using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Kuro.AI;
using Kuro.Core;
using Kuro.Engine;
using Kuro.UI;
using Path = System.IO.Path;
using Timeline = Kuro.UI.Timeline;

namespace Kuro;
public class AssetItem : INotifyPropertyChanged
{
    public Asset Asset { get; }
    public string Name => Asset.Name;
    public string Detail => Asset.Bin + " · " + Asset.Summary;
    public string Icon => Asset.Kind == "Audio" ? "♪" : Asset.Kind == "Composition" ? "▣" : "▧";
    ImageSource? thumbnail;
    public ImageSource? Thumbnail { get => thumbnail; set { thumbnail = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); } }
    public AssetItem(Asset asset) { Asset = asset; }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public partial class MainWindow : Window
{
    readonly Editor editor = new(); readonly PreviewWorker preview = new(); readonly ActionRegistry actions = new();
    readonly Timeline timeline; readonly GraphEditor graph; readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(16) }, autosave = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly Stopwatch playback = new(); long playbackStart; long displayedFrame = -1;
    PlaybackBuffer? playbackFrames;
    CancellationTokenSource? playbackCancellation;
    readonly MediaPlayer previewAudio = new(); CancellationTokenSource? audioPreparation; bool audioReady;
    bool playing, graphOpen, uiBuilding, guides, importing, closed;
    bool aiOpen;
    double aiWidth;
    string? projectPath; List<AssetItem> assetItems = []; string assetSignature = ""; Point assetDown;
    readonly HashSet<string> collapsedSections = ["Text", "Appearance", "Timing & audio"]; StackPanel? inspectorTarget; CancellationTokenSource? aiCancellation; readonly CancellationTokenSource lifetime = new();
    readonly List<Action> refreshPropertyFields = [];
    readonly Dictionary<string, TextBox> numericFields = [];
    long lastPropertyPreview;
    bool viewerDragging; Point viewerDown; double originalX, originalY; string? viewerLayer; long lastViewerRequest;
    double viewerZoom;
    Vector viewerPan;
    Point? panStart, shapeStart;
    Vector initialPan;
    string shapeKind = "Rectangle";
    bool shapeTool;
    TextBox? inlineText;
    Action<bool>? finishInlineText;
    string? inlineLayerId;
    public MainWindow()
    {
        InitializeComponent(); timeline = new Timeline(editor); graph = new GraphEditor(editor); TimelineHost.Children.Add(timeline);
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Kuro;component/Assets/Kuro.ico"));
        if (Preferences.Current.LayoutVersion < 1) { Preferences.Current.LeftWidth = 286; Preferences.Current.RightWidth = 318; Preferences.Current.TimelineHeight = 322; Preferences.Current.LayoutVersion = 1; }
        ProjectColumn.Width = new GridLength(Math.Clamp(Preferences.Current.LeftWidth, 200, 450)); InspectorColumn.Width = new GridLength(Math.Clamp(Preferences.Current.RightWidth, 250, 500)); TimelineRow.Height = new GridLength(Math.Clamp(Preferences.Current.TimelineHeight, 170, 600));
        aiWidth = Math.Clamp(Preferences.Current.AIWidth, 286, 480);
        PreviewQuality.SelectedIndex = Preferences.Current.PreviewDivisor == 1 ? 0 : Preferences.Current.PreviewDivisor == 4 ? 2 : 1;
        timeline.Seek += Seek; timeline.SelectionUpdated += () => { RebuildInspector(); UpdateOverlay(); }; editor.Changed += EditorChanged;
        timeline.GraphRequested += OpenGraph;
        timeline.PropertyPreview += PreviewProperty;
        timeline.CancelPropertyPreview += () => RequestPreview();
        timeline.ClipProcessingRequested += ProcessClip;
        Viewer.LostMouseCapture += (_, _) =>
        {
            if (!viewerDragging && shapeStart == null && panStart == null) return;
            viewerDragging = false; shapeStart = null; panStart = null;
            RequestPreview(); UpdateOverlay();
        };
        editor.Activity += Activity;
        preview.Ready += result => { if (!closed) Dispatcher.BeginInvoke(() => ShowFrame(result)); };
        clock.Tick += PlaybackTick; autosave.Tick += (_, _) => { if (editor.Dirty) SaveRecovery(); }; autosave.Start();
        RegisterActions(); BuildMenu(); Closing += OnClosing;
        Loaded += (_, _) => { NativeChrome.Match(this); Refresh(); timeline.Fit(); if (WelcomeScreen.IsVisible) AnimateWelcome(); };
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        RuntimeInfo.Text = "Local engine";
        UpdateChatConnection();
    }
    void RegisterActions()
    {
        actions.Add("project.new", "New project", "Ctrl+N", NewProject);
        actions.Add("project.open", "Open project", "Ctrl+O", OpenProject);
        actions.Add("composition.settings", "Composition Settings…", "Ctrl+K", CompositionSettings);
        actions.Add("project.save", "Save project", "Ctrl+S", () => SaveProject());
        actions.Add("project.saveAs", "Save project as", "Ctrl+Shift+S", () => SaveProject(true));
        actions.Add("media.import", "Import media", "Ctrl+I", () => ImportMedia());
        actions.Add("render.export", "Export composition", "Ctrl+M", () => ShowExport());
        actions.Add("edit.undo", "Undo", "Ctrl+Z", () => { Pause(); editor.Undo(); });
        actions.Add("edit.redo", "Redo", "Ctrl+Shift+Z", () => { Pause(); editor.Redo(); });
        actions.Add("layer.split", "Split selected layers at playhead", "Ctrl+Shift+D", Split);
        actions.Add("layer.duplicate", "Duplicate selected layers", "Ctrl+D", Duplicate);
        actions.Add("layer.delete", "Delete selection", "Delete", DeleteSelection);
        actions.Add("layer.deleteBackspace", "Delete selection", "Backspace", DeleteSelection);
        actions.Add("layer.ripple", "Ripple delete selected layers", "Shift+Delete", () => Command("ripple_delete"));
        actions.Add("layer.precompose", "Precompose selected layers", "Ctrl+Shift+C", () => Command("precompose"));
        actions.Add("layer.text", "New text layer", "Ctrl+Alt+Shift+T", () => Command("create_text"));
        actions.Add("layer.shape", "New shape layer", "Ctrl+Alt+Shift+S", () => Command("create_shape"));
        actions.Add("preview.play", "Play / pause preview", "Space", TogglePlayback);
        actions.Add("preview.previous", "Previous frame", "PageUp", () => Seek(editor.Playhead - 1));
        actions.Add("preview.next", "Next frame", "PageDown", () => Seek(editor.Playhead + 1));
        actions.Add("timeline.start", "Go to composition start", "Home", () => Seek(0));
        actions.Add("timeline.end", "Go to composition end", "End", () => Seek(editor.Comp.Duration - 1));
        actions.Add("timeline.workStart", "Set work area start", "B", WorkStart);
        actions.Add("timeline.workEnd", "Set work area end", "N", WorkEnd);
        actions.Add("timeline.fit", "Fit composition in timeline", "Ctrl+0", () => timeline.Fit());
        actions.Add("timeline.graph", "Toggle graph editor", "Shift+F3", ToggleGraphEditor);
        actions.Add("animation.ease", "Easy Ease selected keyframes", "F9", EasyEase);
        actions.Add("animation.easeIn", "Ease In selected keyframes", "Shift+F9", () => EaseKeys("Ease In"));
        actions.Add("animation.easeOut", "Ease Out selected keyframes", "Ctrl+Shift+F9", () => EaseKeys("Ease Out"));
        actions.Add("animation.copy", "Copy selected keyframes", "Ctrl+C", editor.CopyKeys);
        actions.Add("animation.paste", "Paste keyframes at playhead", "Ctrl+V", () => { if (editor.KeyClipboard.Count > 0) editor.PasteKeys(editor.Playhead); });
        actions.Add("property.position", "Reveal position", "P", () => Reveal("Position"));
        actions.Add("property.scale", "Reveal scale", "S", () => Reveal("Scale"));
        actions.Add("property.rotation", "Reveal rotation", "R", () => Reveal("Rotation"));
        actions.Add("property.opacity", "Reveal opacity", "T", () => Reveal("Opacity"));
        actions.Add("property.anchor", "Reveal anchor point", "A", () => Reveal("Anchor"));
        actions.Add("property.animated", "Reveal animated properties", "U", () => Reveal("Animated"));
        actions.Add("tool.selection", "Selection tool", "V", () => SelectTool(this, new RoutedEventArgs()));
    }
    void BuildMenu()
    {
        MainMenu.Items.Clear();
        MenuItem Menu(string name) { var item = new MenuItem { Header = name }; MainMenu.Items.Add(item); return item; }
        void Item(MenuItem menu, string id) { var action = actions.Actions.First(a => a.Id == id); var item = new MenuItem { Header = action.Name, InputGestureText = actions.Binding(action) }; item.Click += (_, _) => action.Run(); menu.Items.Add(item); }
        var file = Menu("File"); foreach (var id in new[] { "project.new", "project.open", "project.save", "project.saveAs", "media.import", "render.export" }) Item(file, id);
        var recent = new MenuItem { Header = "Recent projects" }; foreach (var path in Preferences.Current.Recent.Where(File.Exists).Take(8)) { var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(path), ToolTip = path }; item.Click += (_, _) => { if (CanDiscard()) LoadProject(path); }; recent.Items.Add(item); } file.Items.Add(recent);
        var edit = Menu("Edit"); foreach (var id in new[] { "edit.undo", "edit.redo", "layer.split", "layer.duplicate", "layer.delete", "layer.ripple", "layer.precompose" }) Item(edit, id);
        var comp = Menu("Composition"); Item(comp, "composition.settings"); comp.Items.Add(new Separator()); Item(comp, "layer.text"); Item(comp, "layer.shape");
        var view = Menu("View"); Item(view, "timeline.graph"); Item(view, "timeline.fit"); var reset = new MenuItem { Header = "Reset workspace" }; reset.Click += (_, _) => { ProjectColumn.Width = new GridLength(264); InspectorColumn.Width = new GridLength(290); TimelineRow.Height = new GridLength(300); }; view.Items.Add(reset);
        foreach (var theme in new[] { "Light", "Dark", "Follow Windows" }) { var item = new MenuItem { Header = theme + " appearance" }; item.Click += (_, _) => SetTheme(theme); view.Items.Add(item); }
        var help = Menu("Help"); var about = new MenuItem { Header = "Build status & limitations" }; about.Click += (_, _) => ShowBuildStatus(); help.Items.Add(about);
    }
    void EditorChanged()
    {
        if (playing) Pause(); Refresh();
    }
    void Refresh()
    {
        if (uiBuilding) return; uiBuilding = true;
        try
        {
            ProjectTitle.Text = editor.Project.Name + (editor.Dirty ? "  •" : ""); Title = $"{editor.Project.Name}{(editor.Dirty ? " *" : "")} — Kuro";
            TimelineTitle.Text = editor.Comp.Name; CompositionMeta.Text = $"{editor.Comp.Width} × {editor.Comp.Height}  ·  {editor.Comp.Fps.Value:0.##} fps";
            CompositionPicker.ItemsSource = editor.Project.Compositions; CompositionPicker.SelectedValue = editor.Comp.Id;
            UpdateAssets(); timeline.Refresh(); graph.Refresh(); RebuildInspector(); UpdateTime();
            EmptyViewer.Visibility = editor.Comp.Layers.Any(l => l.Kind != "Audio") ? Visibility.Collapsed : Visibility.Visible;
            ContextChip.Text = editor.Selection.Count > 0 ? $"{editor.Selection.Count} selected · {editor.Comp.Fps.Timecode(editor.Playhead)}" : editor.Comp.Name;
            RequestPreview(); UpdateViewerZoom();
        }
        finally { uiBuilding = false; }
    }
    void UpdateAssets()
    {
        var signature = string.Join('|', editor.Project.Assets.Select(a => a.Id + a.Name + a.Bin + a.Path));
        if (signature != assetSignature)
        {
            var old = assetItems.ToDictionary(i => i.Asset.Id); assetItems = editor.Project.Assets.Select(a => new AssetItem(a) { Thumbnail = old.TryGetValue(a.Id, out var item) ? item.Thumbnail : null }).ToList(); assetSignature = signature;
        }
        var query = AssetSearch.Text; AssetList.ItemsSource = assetItems.Where(a => string.IsNullOrEmpty(query) || (a.Name + " " + a.Asset.Bin).Contains(query, StringComparison.CurrentCultureIgnoreCase)).OrderBy(a => a.Asset.Bin).ThenBy(a => a.Name).ToList();
        AssetCount.Text = $"{assetItems.Count} items"; EmptyAssets.Visibility = assetItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    void SearchAssets(object sender, TextChangedEventArgs e) { if (timeline != null) UpdateAssets(); }
    void UpdateTime()
    { TimelineTime.Text = editor.Comp.Fps.Timecode(editor.Playhead); if (!ViewerTime.IsKeyboardFocused) ViewerTime.Text = TimelineTime.Text; timeline.CursorOnly(); if (graphOpen) graph.Refresh(); }
    void RequestPreview(bool interrupt = true)
    {
        if (closed) return; FrameNotice.Visibility = Visibility.Visible; FrameNoticeText.Text = "Rendering " + editor.Comp.Fps.Timecode(editor.Playhead);
        var draft = Data.Clone(editor.Project);
        if (draft.Active.Layers.Find(layer => layer.Id == inlineLayerId) is { } editing) editing.Visible = false;
        preview.Request(draft, editor.Playhead, Preferences.Current.PreviewDivisor, interrupt);
    }
    void ShowFrame(FrameResult result)
    {
        if (playing || result.Frame != editor.Playhead) return;
        if (result.Error != null) { FrameNoticeText.Text = result.Error; FrameNotice.Visibility = Visibility.Visible; PreviewImage.Source = null; Status.Text = result.Error; if (playing) Pause(); return; }
        displayedFrame = result.Frame; PreviewImage.Source = result.Image; FrameNotice.Visibility = Visibility.Collapsed;
        UpdateOverlay();
    }
    public void Seek(long frame)
    {
        Pause(); editor.Playhead = Math.Clamp(frame, 0, editor.Comp.Duration - 1);
        UpdateTime();
        if (timeline.Expanded.Count > 0) timeline.Refresh();
        foreach (var refreshField in refreshPropertyFields) refreshField();
        RequestPreview(); UpdateOverlay();
    }
    public void OpenAt(long frame, string? layerName)
    {
        Seek(frame); if (layerName != null) { var layer = editor.Comp.Layers.FirstOrDefault(l => l.Name.StartsWith(layerName, StringComparison.OrdinalIgnoreCase)); if (layer != null) { editor.Select(layer.Id); timeline.Reveal(layer.Id); } }
    }
    public void RenderReview(Project project, string imagePath, int width, int height, long frame, string? selectedName, bool keyframeView = false, bool graphView = false, bool sidebarView = false, bool welcomeView = false, string? chatMessage = null)
    {
        editor.Load(Data.Clone(project)); editor.Playhead = frame;
        if (selectedName != null) editor.Select(editor.Comp.Layers.FirstOrDefault(l => l.Name.StartsWith(selectedName, StringComparison.OrdinalIgnoreCase))?.Id);
        if (keyframeView && editor.Selected is { } selected)
        {
            timeline.Expanded.Add(selected.Id); timeline.Property = "y";
            foreach (var key in selected.Properties["y"].Keys) editor.SelectedKeys.Add(key.Id);
        }
        Refresh();
        var root = (FrameworkElement)Content; root.Width = width; root.Height = height;
        if (height < 800) { ProjectColumn.Width = new GridLength(250); InspectorColumn.Width = new GridLength(290); TimelineRow.Height = new GridLength(260); }
        if (sidebarView) SetAISidebar(true);
        if (welcomeView) ShowWelcome();
        if (chatMessage != null) { AddChat("What changed in this composition?", false, true); AddChat(chatMessage, false); }
        UpdateWorkspaceWidths(width);
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        foreach (var asset in editor.Project.Assets.Where(a => !a.Missing && a.Kind is "Video" or "Image"))
        {
            var cached = Path.Combine(Store.Cache, Media.CacheKey(asset) + ".png");
            if (File.Exists(cached)) { var image = Media.LoadBitmap(cached); timeline.Thumbnails[asset.Id] = image; var item = assetItems.FirstOrDefault(i => i.Asset.Id == asset.Id); if (item != null) item.Thumbnail = image; }
        }
        using var renderer = new Renderer(); PreviewImage.Source = renderer.Render(editor.Project, editor.Comp, frame, editor.Comp.Width / 2, editor.Comp.Height / 2); FrameNotice.Visibility = Visibility.Collapsed;
        timeline.Fit(); if (keyframeView && editor.Selected is { } active) timeline.Reveal(active.Id); if (graphView) OpenGraph("y"); UpdateTime(); UpdateOverlay(); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        using var stream = File.Create(imagePath); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(stream);
        closed = true; lifetime.Cancel(); preview.Dispose(); autosave.Stop(); clock.Stop(); SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
    }
    async void TogglePlayback()
    {
        if (playing) { Seek(editor.Playhead); return; } if (editor.Comp.Layers.Count == 0) return;
        if (editor.Playhead >= editor.Comp.WorkEnd - 1 || editor.Playhead < editor.Comp.WorkStart) editor.Playhead = editor.Comp.WorkStart;
        playing = true;
        playbackStart = editor.Playhead;
        var playbackSnapshot = Data.Clone(editor.Project);
        PlayButton.Content = new Icon { Kind = "Pause", Stroke = Theme.Get("Text") };
        ViewerOverlay.Visibility = Visibility.Hidden;
        var cancel = new CancellationTokenSource();
        playbackCancellation = audioPreparation = cancel;
        var buffer = preview.Play(playbackSnapshot, playbackStart, editor.Comp.WorkEnd, Preferences.Current.PreviewDivisor, cancel.Token);
        playbackFrames = buffer;
        Status.Text = "Buffering preview…";
        try
        {
            if (playbackSnapshot.Assets.Any(a => a.HasAudio))
            {
                Status.Text = "Preparing local audio preview…";
                var output = Path.Combine(Store.Cache, $"{playbackSnapshot.Id}-{playbackSnapshot.Active.Id}-{playbackSnapshot.Revision}-preview.wav");
                if (!File.Exists(output))
                {
                    var partial = output + ".partial.wav";
                    try { await Exporter.PreviewAudio(playbackSnapshot, partial, cancel.Token); cancel.Token.ThrowIfCancellationRequested(); File.Move(partial, output, true); }
                    finally { if (File.Exists(partial)) File.Delete(partial); }
                }
                cancel.Token.ThrowIfCancellationRequested();
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnOpened(object? s, EventArgs e) => opened.TrySetResult();
                void OnFailed(object? s, ExceptionEventArgs e) => opened.TrySetException(e.ErrorException);
                previewAudio.MediaOpened += OnOpened; previewAudio.MediaFailed += OnFailed;
                try { previewAudio.Open(new Uri(output)); await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), cancel.Token); }
                finally { previewAudio.MediaOpened -= OnOpened; previewAudio.MediaFailed -= OnFailed; }
                cancel.Token.ThrowIfCancellationRequested(); audioReady = true;
            }
            await buffer.Ready.WaitAsync(cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            PresentPlaybackFrame(playbackStart);
            if (audioReady)
            {
                previewAudio.Position = TimeSpan.FromSeconds(editor.Comp.Fps.Seconds(playbackStart));
                previewAudio.Play();
            }
            Status.Text = "Playing";
            playback.Restart(); clock.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (playbackCancellation == cancel) { Pause(); Toast("Preview could not start: " + ex.Message); } }
        finally { if (audioPreparation == cancel) audioPreparation = null; if (playbackCancellation != cancel) cancel.Dispose(); }
    }
    void Pause()
    {
        var cancel = playbackCancellation;
        playbackCancellation = null;
        cancel?.Cancel();
        if (cancel != audioPreparation) cancel?.Dispose();
        playbackFrames = null;
        previewAudio.Close(); audioReady = false;
        if (!playing) return;
        playing = false; clock.Stop(); playback.Stop();
        ViewerOverlay.Visibility = Visibility.Visible;
        PlayButton.Content = new Icon { Kind = "Play", Stroke = Theme.Get("Text") };
        Status.Text = "Paused";
        UpdateOverlay();
    }
    void PlaybackTick(object? sender, EventArgs args)
    {
        if (playbackFrames?.Error is { } error) { Pause(); Toast(error); return; }
        var seconds = audioReady ? previewAudio.Position.TotalSeconds : editor.Comp.Fps.Seconds(playbackStart) + playback.Elapsed.TotalSeconds;
        var frame = (long)Math.Floor(seconds * editor.Comp.Fps.Value + .0001);
        if (frame >= editor.Comp.WorkEnd)
        {
            var loop = Preferences.Current.Loop;
            Seek(loop ? editor.Comp.WorkStart : editor.Comp.WorkEnd - 1);
            if (loop) TogglePlayback();
            return;
        }
        if (frame != editor.Playhead) { editor.Playhead = frame; UpdateTime(); }
        PresentPlaybackFrame(frame);
    }
    void PresentPlaybackFrame(long frame)
    {
        if (playbackFrames?.Take(frame) is not { } ready) return;
        displayedFrame = ready.Frame;
        PreviewImage.Source = ready.Image;
        FrameNotice.Visibility = Visibility.Collapsed;
    }
    void Command(string name) { Pause(); var result = editor.Execute(new(name, name is "delete_layers" or "ripple_delete" or "precompose" ? null : editor.Selected?.Id, Frame: editor.Playhead)); if (!result.Success) Toast(result.Summary); }
    void Split() { Pause(); var result = editor.Execute(new("split_clip", Frame: editor.Playhead)); if (!result.Success) Toast(result.Summary); }
    void Duplicate()
    {
        var ids = editor.Selection.ToArray(); if (ids.Length == 0) { Toast("Select a layer to duplicate."); return; }
        foreach (var id in ids) editor.Execute(new("duplicate_layer", id));
    }
    void Reveal(string property)
    {
        if (editor.Selected is not { } active) return;
        if (graphOpen) ToggleGraphEditor();
        foreach (var layer in editor.Comp.Layers.Where(layer => editor.Selection.Contains(layer.Id))) timeline.ToggleProperties(layer, property);
        timeline.Refresh();
        timeline.Reveal(active.Id);
    }

    void PreviewProperty(string layerId, string property, double value)
    {
        if (playing) Pause();
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(lastPropertyPreview, now) < TimeSpan.FromMilliseconds(32)) return;
        lastPropertyPreview = now;
        var draft = Data.Clone(editor.Project);
        var layer = draft.Active.Layers.Find(l => l.Id == layerId);
        if (layer == null || !layer.Properties.ContainsKey(property)) return;
        Editor.SetLayerValue(layer, property, editor.Playhead, value, linkAxes: true);
        preview.Request(draft, editor.Playhead, Preferences.Current.PreviewDivisor);
    }
    void EasyEase() => EaseKeys("Easy Ease");
    void EaseKeys(string type) { if (editor.KeyTargets.Any()) editor.Execute(new("set_selected_interpolation", Text: type)); }
    void OpenGraph(string property) { timeline.Property = property; graph.Choose(property); if (!graphOpen) ToggleGraphEditor(); graph.Refresh(); }
    void RebuildInspector()
    {
        if (InspectorBody == null) return; var oldBuilding = uiBuilding; uiBuilding = true;
        try
        {
            InspectorBody.Children.Clear(); inspectorTarget = InspectorBody; refreshPropertyFields.Clear(); numericFields.Clear(); var l = editor.Selected;
            if (l == null)
            {
                inspectorTarget.Children.Add(Dialogs.Label(editor.Comp.Name, true));
                inspectorTarget.Children.Add(Dialogs.Label("COMPOSITION", true)); inspectorTarget.Children.Add(Dialogs.Label($"{editor.Comp.Name}\n{editor.Comp.Width} × {editor.Comp.Height}\n{editor.Comp.Fps.Value:0.###} frames per second\n{editor.Comp.Fps.Seconds(editor.Comp.Duration):0.##} seconds"));
                inspectorTarget.Children.Add(Dialogs.Button("Composition settings…", CompositionSettings));
                inspectorTarget.Children.Add(Dialogs.Label("LAYERS", true)); inspectorTarget.Children.Add(Dialogs.Button("＋  Add text", () => Command("create_text"))); inspectorTarget.Children.Add(Dialogs.Button("＋  Add shape", () => Command("create_shape"))); return;
            }
            var heading = new TextBlock { Text = l.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 7, 0, 4) }; inspectorTarget.Children.Add(heading);
            inspectorTarget.Children.Add(Dialogs.Label($"{l.Kind} layer · {(l.Locked ? "Locked" : "Editable")} · {editor.Comp.Fps.Seconds(l.Duration):0.##}s"));
            if (editor.Selection.Count > 1) inspectorTarget.Children.Add(Dialogs.Label($"{editor.Selection.Count} layers selected. Inspector shows the uppermost layer."));
            void Section(string name) { var body = new StackPanel { Margin = new Thickness(0, 0, 0, 5) }; var expander = new Expander { Header = name, FontSize = 11, FontWeight = FontWeights.Medium, IsExpanded = !collapsedSections.Contains(name), Content = body }; expander.Collapsed += (_, _) => collapsedSections.Add(name); expander.Expanded += (_, _) => collapsedSections.Remove(name); InspectorBody.Children.Add(expander); inspectorTarget = body; }
            if (editor.Comp.Layers.Any(layer => editor.Selection.Contains(layer.Id) && layer.Kind != "Audio"))
            {
                var alignRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
                var alignLabel = new TextBlock { Text = "Align", FontSize = 11, Width = 43, VerticalAlignment = VerticalAlignment.Center };
                alignLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                alignRow.Children.Add(alignLabel);
                var alignment = new UniformGrid { Rows = 1, Columns = 6 };
                alignment.IsEnabled = editor.Comp.Layers.Any(layer => editor.Selection.Contains(layer.Id) && !layer.Locked && !LayerGeometry.LocalBounds(editor.Project, layer, editor.Playhead).IsEmpty);
                foreach (var (mode, label) in new[]
                {
                    (LayerAlignment.Left, "Align left"), (LayerAlignment.CenterX, "Center horizontally"), (LayerAlignment.Right, "Align right"),
                    (LayerAlignment.Top, "Align top"), (LayerAlignment.CenterY, "Center vertically"), (LayerAlignment.Bottom, "Align bottom")
                })
                {
                    var button = new Button
                    {
                        Content = new Icon { Kind = "Align" + mode, Width = 16, Height = 16 },
                        Height = 26, Padding = new Thickness(0), Margin = new Thickness(mode == LayerAlignment.Top ? 3 : 0, 0, 0, 0),
                        ToolTip = mode is LayerAlignment.CenterX or LayerAlignment.CenterY ? label + " in composition" : label + " to composition edge"
                    };
                    System.Windows.Automation.AutomationProperties.SetName(button, label);
                    button.Click += (_, _) => LayerGeometry.Align(editor, mode);
                    alignment.Children.Add(button);
                }
                var alignmentStrip = new Border { Child = alignment, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(1) };
                alignmentStrip.SetResourceReference(Border.BackgroundProperty, "Elevated");
                alignmentStrip.SetResourceReference(Border.BorderBrushProperty, "Border");
                alignRow.Children.Add(alignmentStrip);
                InspectorBody.Children.Add(alignRow);
            }
            if (l.Kind == "Text")
            {
                Section("Text"); var text = new TextBox { Text = l.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65, MaxHeight = 160, Margin = new Thickness(0, 3, 0, 8), IsEnabled = !l.Locked };
                text.LostKeyboardFocus += (_, _) => { if (!uiBuilding && text.Text != l.Text) editor.Execute(new("set_text", l.Id, Text: text.Text)); }; inspectorTarget.Children.Add(text);
                var font = new ComboBox { ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(x => x).ToList(), SelectedItem = l.Font, IsEnabled = !l.Locked, MaxDropDownHeight = 300 };
                if (font.SelectedIndex < 0) inspectorTarget.Children.Add(Dialogs.Label("Missing font: " + l.Font + ". Choose an installed replacement."));
                font.SelectionChanged += (_, _) => { if (!uiBuilding && font.SelectedItem is string family) editor.Execute(new("set_font", l.Id, Text: family)); }; inspectorTarget.Children.Add(font); Numeric(l, "Font size", "fontSize", "px");
                var weightNames = new Dictionary<string, int> { ["Regular"] = 400, ["Medium"] = 500, ["Semibold"] = 600, ["Bold"] = 700 };
                var weight = Dialogs.Choice(inspectorTarget, "Weight", weightNames.Keys, weightNames.FirstOrDefault(p => p.Value == l.FontWeight).Key ?? "Regular");
                weight.SelectionChanged += (_, _) => { if (!uiBuilding && weight.SelectedItem is string name) editor.Execute(new("set_font_weight", l.Id, Value: weightNames[name])); };
            }
            if (l.Kind != "Audio")
            {
                Section("Transform"); Numeric(l, "Anchor X", "anchorX", "px"); Numeric(l, "Anchor Y", "anchorY", "px"); Numeric(l, "Position X", "x", "px"); Numeric(l, "Position Y", "y", "px"); Numeric(l, "Scale X", "scaleX", "%"); Numeric(l, "Scale Y", "scaleY", "%"); Numeric(l, "Rotation", "rotation", "°"); Numeric(l, "Opacity", "opacity", "%");
                var flips = new StackPanel { Orientation = Orientation.Horizontal }; flips.Children.Add(Dialogs.Button("Flip ↔", () => editor.Execute(new("set_property", l.Id, "scaleX", -l.Get("scaleX", editor.Playhead), editor.Playhead)))); flips.Children.Add(Dialogs.Button("Flip ↕", () => editor.Execute(new("set_property", l.Id, "scaleY", -l.Get("scaleY", editor.Playhead), editor.Playhead)))); inspectorTarget.Children.Add(flips);
            }
            if (l.Kind is "Text" or "Shape")
            {
                Section("Appearance"); var fill = Dialogs.Field(inspectorTarget, "Fill · hexadecimal color", l.Fill); fill.IsEnabled = !l.Locked; fill.LostKeyboardFocus += (_, _) => { if (!uiBuilding && fill.Text != l.Fill) { var r = editor.Execute(new("set_fill", l.Id, Text: fill.Text)); if (!r.Success) Toast("Use a color such as #E28A48 or #FFFFFFFF."); } };
                if (l.Kind == "Shape") { var shape = Dialogs.Choice(inspectorTarget, "Shape", Shapes.Kinds, l.Shape); shape.SelectionChanged += (_, _) => { if (!uiBuilding) editor.Execute(new("set_shape", l.Id, Text: shape.SelectedItem as string)); }; Numeric(l, "Width", "width", "px"); Numeric(l, "Height", "height", "px"); Numeric(l, "Corner radius", "radius", "px"); }
                Numeric(l, "Blur radius", "blur", "px"); Numeric(l, "Glow radius", "glow", "px"); Numeric(l, "Shadow softness", "shadow", "px");
            }
            if (l.Kind == "Text")
            {
                Section("True Motion");
                var names = Presets.Discover().Where(p => Presets.Motion(p.Name) != null).Select(p => p.Name).Prepend("None").ToList();
                var preset = Dialogs.Choice(inspectorTarget, "Text animation", names, l.Preset == "" ? "None" : l.Preset); preset.IsEnabled = !l.Locked;
                preset.SelectionChanged += (_, _) => { if (!uiBuilding && preset.SelectedItem is string name) ApplyTextMotion(l, name); };
                if (l.Properties.ContainsKey(TextMotion.Property))
                    Numeric(l, "Selector", TextMotion.Property, "%");
                else if (Presets.Motion(l.Preset) != null)
                    inspectorTarget.Children.Add(Dialogs.Button("Show animation keyframes", () => ApplyTextMotion(l, l.Preset)));
            }
            Section("Timing & audio"); var rate = Dialogs.Field(inspectorTarget, "Playback speed · 0 = freeze, −1 = reverse", l.Rate.ToString("0.###", CultureInfo.InvariantCulture)); rate.IsEnabled = !l.Locked;
            rate.LostKeyboardFocus += (_, _) => { if (!uiBuilding && double.TryParse(rate.Text, CultureInfo.InvariantCulture, out var value) && value != l.Rate) { var r = editor.Execute(new("set_rate", l.Id, Value: value)); if (!r.Success) Toast(r.Summary); } };
            var source = Dialogs.Field(inspectorTarget, "Source in · seconds", l.SourceIn.ToString("0.###", CultureInfo.InvariantCulture)); source.IsEnabled = !l.Locked; source.LostKeyboardFocus += (_, _) => { if (!uiBuilding && double.TryParse(source.Text, CultureInfo.InvariantCulture, out var value) && value != l.SourceIn) editor.Execute(new("set_source_in", l.Id, Value: value)); };
            var audio = new CheckBox { Content = "Enable source audio in export", IsChecked = l.Audio, IsEnabled = !l.Locked }; audio.Click += (_, _) => editor.Execute(new("toggle_audio", l.Id)); inspectorTarget.Children.Add(audio);
            if (l.Kind == "Video")
            {
                Section("Local tools");
                var remove = Dialogs.Button("Remove Background…", () => ProcessClip(l, true)); remove.IsEnabled = !l.Locked;
                var stabilize = Dialogs.Button("Stabilize…", () => ProcessClip(l, false)); stabilize.IsEnabled = !l.Locked;
                inspectorTarget.Children.Add(remove); inspectorTarget.Children.Add(stabilize);
            }
        }
        finally { uiBuilding = oldBuilding; }
    }
    void Numeric(Layer l, string label, string property, string unit)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3), IsEnabled = !l.Locked }; row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(70) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(27) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(23) });
        var curve = l.Properties[property]; var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 }; title.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var value = new TextBox { Style = (Style)FindResource("NumericField"), Text = PropertyScrub.Format(l.Get(property, editor.Playhead)), Padding = new Thickness(5, 3, 5, 3), FontFamily = new FontFamily("Consolas"), FontSize = 11, HorizontalContentAlignment = HorizontalAlignment.Right, Cursor = Cursors.SizeAll, ToolTip = $"{unit} · Drag to adjust. Click to type." };
        numericFields[property] = value;
        var key = new Button { Content = curve.Keys.Any(k => k.Frame == editor.Playhead) ? "◆" : "◇", ToolTip = "Add / remove keyframe at playhead", Padding = new Thickness(0) }; key.SetResourceReference(Button.ForegroundProperty, curve.Keys.Count > 0 ? "Accent" : "Muted");
        var reset = new Button { Content = "↺", ToolTip = "Reset value", Padding = new Thickness(0) };
        row.Children.Add(title); Grid.SetColumn(value, 1); row.Children.Add(value); Grid.SetColumn(key, 2); row.Children.Add(key); Grid.SetColumn(reset, 3); row.Children.Add(reset); (inspectorTarget ?? InspectorBody).Children.Add(row);
        if (property is "x" or "scaleX")
        {
            var linked = property == "x" ? l.PositionLinked : l.ScaleLinked;
            row.ColumnDefinitions.Insert(2, new() { Width = new GridLength(24) });
            var link = new Button { Content = new Icon { Kind = linked ? "Link" : "Unlink", Width = 13, Height = 13 }, Padding = new Thickness(0), ToolTip = linked ? "Unlink X and Y" : "Link X and Y" };
            link.Click += (_, _) => editor.Execute(new("toggle_axis_link", l.Id, property));
            Grid.SetColumn(link, 2); Grid.SetColumn(key, 3); Grid.SetColumn(reset, 4); row.Children.Add(link);
        }
        System.Windows.Automation.AutomationProperties.SetAutomationId(value, l.Id + "/" + property);
        row.PreviewMouseRightButtonDown += (_, e) =>
        {
            var atPlayhead = curve.Keys.FirstOrDefault(k => k.Frame == editor.Playhead);
            if (!editor.KeyTargets.Any(t => t.Layer.Id == l.Id && t.Property == property)) editor.SelectedKeys.Clear();
            if (atPlayhead != null) editor.SelectedKeys.Add(atPlayhead.Id);
            var menu = KeyframeMenu.Create(editor, OpenGraph, this);
            var addKey = new MenuItem { Header = atPlayhead == null ? "Add Keyframe" : "Remove Keyframe", IsEnabled = !l.Locked };
            addKey.Click += (_, _) => editor.Execute(new(atPlayhead == null ? "add_keyframe" : "remove_keyframe", l.Id, property, l.Get(property, editor.Playhead), editor.Playhead)); menu.Items.Insert(0, addKey);
            var all = new MenuItem { Header = "Select Property Keyframes", IsEnabled = curve.Keys.Count > 0 };
            all.Click += (_, _) => { editor.SelectedKeys.Clear(); editor.SelectedKeys.UnionWith(curve.Keys.Select(k => k.Id)); editor.Notify(); }; menu.Items.Insert(1, all);
            timeline.Property = property; timeline.Refresh(); graph.Refresh(); menu.PlacementTarget = InspectorBody; menu.IsOpen = true; e.Handled = true;
        };
        var baseline = value.Text;
        refreshPropertyFields.Add(() =>
        {
            if (!value.IsKeyboardFocused) value.Text = baseline = PropertyScrub.Format(curve.At(editor.Playhead));
            key.Content = curve.Keys.Any(k => k.Frame == editor.Playhead) ? "◆" : "◇";
        });
        void Commit()
        {
            if (uiBuilding || value.Text == baseline) return;
            if (!double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) { value.Text = baseline; return; }
            baseline = value.Text;
            editor.Execute(new("scrub_property", l.Id, property, number, editor.Playhead));
        }
        value.LostKeyboardFocus += (_, _) => Commit(); value.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        key.Click += (_, _) => { if (editor.ToggleKeyframe(l, property).Success) { timeline.ShowProperty(l, property); graph.Choose(property); } };
        reset.Click += (_, _) => editor.Execute(new("set_property", l.Id, property, property switch { "scaleX" or "scaleY" or "opacity" => 100, "x" => editor.Comp.Width / 2.0, "y" => editor.Comp.Height / 2.0, "fontSize" => 96, _ => 0 }, editor.Playhead));
        PropertyScrub? scrub = null;
        value.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount > 1 && value.IsKeyboardFocused) return;
            scrub = new PropertyScrub(e.GetPosition(this), curve.At(editor.Playhead), property);
            value.CaptureMouse();
            e.Handled = true;
        };
        value.PreviewMouseMove += (_, e) =>
        {
            if (scrub == null || !value.IsMouseCaptured || !scrub.Update(e.GetPosition(this))) return;
            value.Text = PropertyScrub.Format(scrub.Value);
            if (l.LinkedAxis(property) is { } other && numericFields.TryGetValue(other, out var paired))
                paired.Text = PropertyScrub.Format(l.ProportionalValue(property, other, editor.Playhead, scrub.Value));
            PreviewProperty(l.Id, property, scrub.Value);
        };
        value.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (scrub == null) return;
            var gesture = scrub;
            scrub = null;
            value.ReleaseMouseCapture();
            e.Handled = true;
            if (gesture.HasMoved) Commit();
            else { value.Focus(); value.SelectAll(); }
        };
        value.LostMouseCapture += (_, _) =>
        {
            if (scrub == null) return;
            if (scrub.HasMoved) { value.Text = baseline; foreach (var refresh in refreshPropertyFields) refresh(); RequestPreview(); }
            scrub = null;
        };
    }
    void ApplyTextMotion(Layer layer, string name)
    {
        if (layer.Locked) return;
        var motion = Presets.Motion(name);
        editor.Transaction("Apply text animation", () =>
        {
            layer.Preset = motion != null ? name : "";
            layer.Properties.Remove(TextMotion.Property);
            layer.Properties["presetOrigin"] = new(layer.Start);
            if (motion != null)
            {
                layer.PresetDuration = motion.Duration;
                layer.Properties[TextMotion.Property] = motion.AtRate(editor.Comp.Fps, layer.Start, layer.End - 1);
            }
        });
        if (motion != null) timeline.ShowProperty(layer, TextMotion.Property);
    }
    Rect ImageRect()
    {
        var w = Math.Max(0, Viewer.ActualWidth - 48); var h = Math.Max(0, Viewer.ActualHeight - 48);
        var scale = viewerZoom > 0 ? viewerZoom : ViewerFit();
        return new Rect((w - editor.Comp.Width * scale) / 2 + viewerPan.X, (h - editor.Comp.Height * scale) / 2 + viewerPan.Y, editor.Comp.Width * scale, editor.Comp.Height * scale);
    }
    double ViewerFit() => Math.Max(.001, Math.Min((Viewer.ActualWidth - 48) / editor.Comp.Width, (Viewer.ActualHeight - 48) / editor.Comp.Height));
    void UpdateViewerZoom()
    {
        var factor = viewerZoom > 0 ? viewerZoom / ViewerFit() : 1;
        PreviewImage.RenderTransformOrigin = new Point(.5, .5);
        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(factor, factor));
        transforms.Children.Add(new TranslateTransform(viewerPan.X, viewerPan.Y));
        PreviewImage.RenderTransform = transforms;
        ViewerZoomButton.Content = viewerZoom == 0 ? "Fit" : $"{viewerZoom:P0}";
        var narrow = Viewer.ActualWidth < 390;
        GuideButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        PreviewQuality.Width = narrow ? 57 : 85;
        ViewerTime.Width = narrow ? 81 : 105;
        ViewerTime.FontSize = narrow ? 10 : 11;
        UpdateOverlay();
    }
    void ViewerZoomMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ViewerZoomButton, Placement = PlacementMode.Bottom };
        foreach (var zoom in new[] { 0, .125, .25, .5, 1, 2, 4 })
        {
            var item = new MenuItem { Header = zoom == 0 ? "Fit" : $"{zoom:0.#%}", IsCheckable = true, IsChecked = viewerZoom == zoom };
            item.Click += (_, _) => { viewerZoom = zoom; viewerPan = new Vector(); UpdateViewerZoom(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    void ViewerWheel(object sender, MouseWheelEventArgs e)
    {
        if (inlineText != null || Viewer.IsMouseCaptured) return;
        var anchor = e.GetPosition(ViewerOverlay);
        var before = ImageRect();
        var scale = viewerZoom > 0 ? viewerZoom : ViewerFit();
        viewerZoom = Math.Clamp(scale * Math.Pow(1.2, e.Delta / 120.0), .01, 4);
        var after = ImageRect();
        viewerPan += new Vector(anchor.X - after.X - (anchor.X - before.X) * viewerZoom / scale,
            anchor.Y - after.Y - (anchor.Y - before.Y) * viewerZoom / scale);
        UpdateViewerZoom(); e.Handled = true;
    }
    void ViewerMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || inlineText != null || Viewer.IsMouseCaptured) return;
        panStart = e.GetPosition(Viewer); initialPan = viewerPan; Viewer.CaptureMouse(); e.Handled = true;
    }
    void ViewerMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || panStart == null) return;
        panStart = null; Viewer.ReleaseMouseCapture(); e.Handled = true;
    }
    void UpdateOverlay()
    {
        ViewerOverlay.Children.Clear(); var rect = ImageRect(); var scale = rect.Width / editor.Comp.Width;
        if (guides) { foreach (var inset in new[] { .05, .1 }) { var box = new Rectangle { Width = rect.Width * (1 - inset * 2), Height = rect.Height * (1 - inset * 2), Stroke = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), StrokeThickness = .7, StrokeDashArray = [5, 4] }; Canvas.SetLeft(box, rect.X + rect.Width * inset); Canvas.SetTop(box, rect.Y + rect.Height * inset); ViewerOverlay.Children.Add(box); } }
        var l = editor.Selected; if (l == null || l.Kind == "Audio" || editor.Playhead < l.Start || editor.Playhead >= l.End || !l.Visible) return;
        var frame = editor.Playhead;
        var bounds = LayerGeometry.LocalBounds(editor.Project, l, frame);
        if (bounds.IsEmpty) return;
        var center = LayerGeometry.Transform(l, frame).Transform(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        var width = Math.Abs(bounds.Width * l.Get("scaleX", frame) / 100 * scale);
        var height = Math.Abs(bounds.Height * l.Get("scaleY", frame) / 100 * scale);
        var x = rect.X + center.X * scale;
        var y = rect.Y + center.Y * scale;
        var group = new Canvas { Width = Math.Max(1, width), Height = Math.Max(1, height), RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(l.Get("rotation", editor.Playhead)) }; Canvas.SetLeft(group, x - width / 2); Canvas.SetTop(group, y - height / 2);
        group.Children.Add(new Rectangle { Width = Math.Max(1, width), Height = Math.Max(1, height), Stroke = Theme.Get("Accent"), StrokeThickness = .8 });
        foreach (var point in new[] { new Point(0, 0), new Point(width, 0), new Point(width, height), new Point(0, height) }) { var handle = new Rectangle { Width = 5, Height = 5, Fill = Theme.Get("Accent"), Stroke = Brushes.Black, StrokeThickness = .5 }; Canvas.SetLeft(handle, point.X - 2.5); Canvas.SetTop(handle, point.Y - 2.5); group.Children.Add(handle); }
        ViewerOverlay.Children.Add(group); var anchor = new Ellipse { Width = 7, Height = 7, Stroke = Theme.Get("Accent"), StrokeThickness = 1 }; Canvas.SetLeft(anchor, rect.X + l.Get("x", frame) * scale - 3.5); Canvas.SetTop(anchor, rect.Y + l.Get("y", frame) * scale - 3.5); ViewerOverlay.Children.Add(anchor);
    }
    void ViewerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (inlineText != null)
        {
            if (!ViewerEditOverlay.IsMouseOver) { finishInlineText?.Invoke(true); e.Handled = true; }
            return;
        }
        if (panStart != null) return;
        var point = e.GetPosition(ViewerOverlay);
        if (shapeTool)
        {
            if (!ImageRect().Contains(point)) return;
            Pause(); shapeStart = point; Viewer.CaptureMouse(); e.Handled = true; return;
        }
        if (e.ClickCount == 2 && HitText(point) is { } title)
        {
            editor.Select(title.Id); EditViewerText(title); e.Handled = true; return;
        }
        var l = editor.Selected; if (l == null || l.Locked) return;
        if (e.ClickCount == 2 && l.Kind == "Composition") { OpenComposition(l.CompositionId!); return; }
        Pause(); viewerDragging = true; viewerDown = e.GetPosition(Viewer); originalX = l.Get("x", editor.Playhead); originalY = l.Get("y", editor.Playhead); viewerLayer = l.Id; Viewer.CaptureMouse();
    }
    Layer? HitText(Point point)
    {
        var image = ImageRect(); var scale = image.Width / editor.Comp.Width;
        if (!image.Contains(point) || scale <= 0) return null;
        var frame = editor.Playhead;
        var compositionPoint = new Point((point.X - image.X) / scale, (point.Y - image.Y) / scale);
        var solo = editor.Comp.Layers.Any(layer => layer.Solo);
        foreach (var layer in editor.Comp.Layers.Where(l => l.Kind == "Text" && l.Visible && !l.Locked && (!solo || l.Solo) && frame >= l.Start && frame < l.End))
        {
            var matrix = LayerGeometry.Transform(layer, frame);
            if (!matrix.HasInverse) continue;
            matrix.Invert();
            var bounds = LayerGeometry.LocalBounds(editor.Project, layer, frame);
            bounds.Inflate(8 / scale, 8 / scale);
            if (bounds.Contains(matrix.Transform(compositionPoint))) return layer;
        }
        return null;
    }
    void ViewerMouseMove(object sender, MouseEventArgs e)
    {
        if (panStart is { } pan) { viewerPan = initialPan + (e.GetPosition(Viewer) - pan); UpdateViewerZoom(); return; }
        if (shapeStart is { } start)
        {
            UpdateOverlay();
            var bounds = Shapes.Bounds(shapeKind, start, e.GetPosition(ViewerOverlay), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            var outline = new System.Windows.Shapes.Path { Data = Shapes.Geometry(shapeKind, bounds), Stroke = Theme.Get("Accent"), StrokeThickness = 1.3, Fill = Theme.Get("Selection") };
            ViewerOverlay.Children.Add(outline); return;
        }
        if (!viewerDragging) return; var delta = e.GetPosition(Viewer) - viewerDown; Status.Text = $"Move layer · ΔX {delta.X:0} · ΔY {delta.Y:0}";
        if (Environment.TickCount64 - lastViewerRequest < 33) return; lastViewerRequest = Environment.TickCount64;
        var scale = ImageRect().Width / editor.Comp.Width; if (scale <= 0) return;
        var draft = Data.Clone(editor.Project); var layer = draft.Active.Layers.FirstOrDefault(l => l.Id == viewerLayer); if (layer == null) return;
        foreach (var (key, value) in new[] { ("x", originalX + delta.X / scale), ("y", originalY + delta.Y / scale) }) { var curve = layer.Properties[key]; if (curve.Keys.Count > 0) curve.Set(editor.Playhead, value); else curve.Value = value; }
        preview.Request(draft, editor.Playhead, Preferences.Current.PreviewDivisor);
    }
    void ViewerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (shapeStart is { } start)
        {
            shapeStart = null; Viewer.ReleaseMouseCapture();
            var bounds = Shapes.Bounds(shapeKind, start, e.GetPosition(ViewerOverlay), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            var image = ImageRect(); var factor = image.Width / editor.Comp.Width;
            if (bounds.Width >= 3 && bounds.Height >= 3 && factor > 0) editor.DrawShape(shapeKind,
                new Rect((bounds.X - image.X) / factor, (bounds.Y - image.Y) / factor, bounds.Width / factor, bounds.Height / factor));
            UpdateOverlay(); e.Handled = true; return;
        }
        if (!viewerDragging) return; viewerDragging = false; Viewer.ReleaseMouseCapture(); var delta = e.GetPosition(Viewer) - viewerDown; var scale = ImageRect().Width / editor.Comp.Width;
        if (delta.Length < 2 || scale <= 0) { RequestPreview(); return; }
        var id = viewerLayer; editor.Transaction("Move layer in viewer", () => { var l = editor.Comp.Layers.First(l => l.Id == id); if (l.Locked) throw new InvalidOperationException("Layer is locked."); foreach (var (key, value) in new[] { ("x", originalX + delta.X / scale), ("y", originalY + delta.Y / scale) }) { var c = l.Properties[key]; if (c.Keys.Count > 0) c.Set(editor.Playhead, value); else c.Value = value; } });
    }
    void ViewerSizeChanged(object sender, SizeChangedEventArgs e) { if (timeline != null) UpdateViewerZoom(); }
    void EditViewerText(Layer layer)
    {
        Pause();
        var image = ImageRect();
        var scale = image.Width / editor.Comp.Width;
        var fontSize = Math.Clamp(layer.Get("fontSize", editor.Playhead) * scale * Math.Abs(layer.Get("scaleY", editor.Playhead)) / 100, 12, 200);
        var width = Math.Max(110, Math.Min(ViewerEditOverlay.ActualWidth - 10, fontSize * Math.Max(7, layer.Text.Split('\n').Max(line => line.Length)) * .65));
        var box = new TextBox { Text = layer.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = width, FontSize = fontSize, FontFamily = new FontFamily(layer.Font), Padding = new Thickness(5, 2, 5, 2), ToolTip = "Ctrl+Enter to commit · Escape to cancel" };
        box.SetResourceReference(TextBox.BorderBrushProperty, "Accent");
        var x = image.X + layer.Get("x", editor.Playhead) * scale;
        var y = image.Y + layer.Get("y", editor.Playhead) * scale;
        Canvas.SetLeft(box, Math.Clamp(x - width / 2, 0, Math.Max(0, ViewerEditOverlay.ActualWidth - width)));
        Canvas.SetTop(box, Math.Max(0, y - fontSize));
        var composition = editor.Comp.Id;
        void Finish(bool save)
        {
            if (inlineText != box) return;
            inlineText = null; inlineLayerId = null; finishInlineText = null;
            ViewerEditOverlay.Children.Clear(); ViewerEditOverlay.IsHitTestVisible = false;
            var current = editor.Comp.Layers.Find(candidate => candidate.Id == layer.Id);
            if (save && editor.Comp.Id == composition && current is { Locked: false } && box.Text != current.Text)
                editor.Execute(new("set_text", layer.Id, Text: box.Text));
            else RequestPreview();
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape && !(e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) return;
            e.Handled = true; Finish(e.Key != Key.Escape);
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        inlineText = box; inlineLayerId = layer.Id; finishInlineText = Finish;
        ViewerEditOverlay.IsHitTestVisible = true; ViewerEditOverlay.Children.Add(box);
        RequestPreview();
        box.Focus(); box.SelectAll();
    }
    void TimeEntry(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return; var parts = ViewerTime.Text.Split(':'); long frame;
        if (parts.Length == 4 && parts.All(p => int.TryParse(p, out _))) { var fps = (int)Math.Ceiling(editor.Comp.Fps.Value); frame = ((long.Parse(parts[0]) * 3600 + long.Parse(parts[1]) * 60 + long.Parse(parts[2])) * fps) + long.Parse(parts[3]); }
        else if (!long.TryParse(ViewerTime.Text, out frame)) { Toast("Enter a frame number or HH:MM:SS:FF."); return; }
        Seek(frame); Keyboard.ClearFocus(); e.Handled = true;
    }
    void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (WelcomeScreen.IsVisible)
        {
            if (actions.Dispatch(e, "project.new") || actions.Dispatch(e, "project.open")) return;
            return;
        }
        if (aiOpen && e.Key == Key.Escape && AISidebar.IsKeyboardFocusWithin) { SetAISidebar(false); e.Handled = true; return; }
        if (inlineText != null) return;
        if (actions.Dispatch(e, "composition.settings")) return;
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox || Keyboard.FocusedElement is ComboBox) return;
        if (shapeTool && e.Key is Key.Escape or Key.V) { SelectTool(this, e); e.Handled = true; return; }
        actions.Dispatch(e);
    }
    public async void ImportMedia(string[]? paths = null)
    {
        if (paths == null) { var picker = new OpenFileDialog { Title = "Import into Kuro", Multiselect = true, Filter = "Media|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.mts;*.mp3;*.wav;*.flac;*.aac;*.m4a;*.ogg;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|All files|*.*" }; if (picker.ShowDialog(this) != true) return; paths = picker.FileNames; }
        if (importing) { Toast("An import is already running. Wait for it to finish."); return; } importing = true; var projectId = editor.Project.Id; var completed = 0;
        try
        {
            foreach (var path in paths)
            {
                lifetime.Token.ThrowIfCancellationRequested(); if (projectId != editor.Project.Id) break;
                if (editor.Project.Assets.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) { completed++; continue; }
                Status.Text = $"Analyzing {Path.GetFileName(path)} · {completed}/{paths.Length} imported";
                try
                {
                    var asset = await Media.Probe(path, lifetime.Token); if (projectId != editor.Project.Id) break;
                    editor.Transaction("Import " + asset.Name, () => editor.Project.Assets.Add(asset)); completed++;
                    _ = AnalyzeAsset(asset, projectId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Toast("Could not import " + Path.GetFileName(path) + ": " + ex.Message); }
            }
            Status.Text = $"Imported {completed} / {paths.Length}";
        }
        catch (OperationCanceledException) { }
        finally { importing = false; }
    }
    readonly SemaphoreSlim analysisSlots = new(2);
    async Task AnalyzeAsset(Asset asset, string projectId)
    {
        await analysisSlots.WaitAsync(lifetime.Token);
        try
        {
            var thumb = await Media.Thumbnail(asset, lifetime.Token);
            if (projectId != editor.Project.Id) return;
            if (thumb != null) { var b = Media.LoadBitmap(thumb); assetItems.FirstOrDefault(i => i.Asset.Id == asset.Id)!.Thumbnail = b; timeline.Thumbnails[asset.Id] = b; timeline.Refresh(); }
            if (asset.HasAudio) { var waveform = await Media.Waveform(asset, lifetime.Token); if (projectId == editor.Project.Id) { timeline.Waveforms[asset.Id] = waveform; timeline.Refresh(); } }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) Status.Text = "Media preview unavailable: " + ex.Message; }
        finally { analysisSlots.Release(); }
    }
    void WindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 1 && Path.GetExtension(files[0]).Equals(".kuro", StringComparison.OrdinalIgnoreCase)) { if (CanDiscard()) LoadProject(files[0]); }
        else ImportMedia(files); e.Handled = true;
    }
    void AssetMouseDown(object sender, MouseButtonEventArgs e) => assetDown = e.GetPosition(AssetList);
    void AssetMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(AssetList) - assetDown).Length < 6 || AssetList.SelectedItem is not AssetItem item) return;
        DragDrop.DoDragDrop(AssetList, new DataObject("Kuro.Asset", item.Asset.Id), DragDropEffects.Copy);
    }
    void AssetDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AssetList.SelectedItem is not AssetItem item) return; if (item.Asset.Kind == "Composition") { OpenComposition(item.Asset.Id); return; }
        if (item.Asset.Missing) { Relink(item.Asset); return; }
        var result = editor.Execute(new("add_asset", item.Asset.Id, Frame: editor.Playhead)); if (!result.Success) Toast(result.Summary);
    }
    void AssetSelectionChanged(object sender, SelectionChangedEventArgs e) { if (AssetList.SelectedItem is AssetItem item) Status.Text = item.Name + " · " + item.Asset.Summary; }
    void AssetMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu(); void Item(string label, Action action) { var i = new MenuItem { Header = label }; i.Click += (_, _) => action(); menu.Items.Add(i); }
        Item("Place selected asset at playhead", () => { if (AssetList.SelectedItem is AssetItem item) editor.Execute(new("add_asset", item.Asset.Id, Frame: editor.Playhead)); });
        Item("Rename asset…", () => { if (AssetList.SelectedItem is AssetItem item) { var value = Dialogs.Prompt(this, "Rename asset", "Project name (source filename stays unchanged)", item.Name); if (!string.IsNullOrWhiteSpace(value)) editor.Transaction("Rename asset", () => item.Asset.Name = value); } });
        Item("Move selected assets to bin…", () => { var selected = AssetList.SelectedItems.Cast<AssetItem>().ToArray(); var value = Dialogs.Prompt(this, "Move to bin", "Bin name · search this name to filter", "Media"); if (!string.IsNullOrWhiteSpace(value)) editor.Transaction("Organize media bin", () => { foreach (var a in selected) a.Asset.Bin = value; }); });
        Item("Relink media…", () => { if (AssetList.SelectedItem is AssetItem item) Relink(item.Asset); });
        menu.PlacementTarget = sender as FrameworkElement ?? AssetList; menu.IsOpen = true;
    }
    void AssetRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(AssetList, source) is ListBoxItem row && !row.IsSelected) row.IsSelected = true;
        AssetMenuClick(AssetList, e); e.Handled = true;
    }
    async void Relink(Asset asset)
    {
        var picker = new OpenFileDialog { Title = "Relink " + asset.Name }; if (picker.ShowDialog(this) != true) return;
        try { var replacement = await Media.Probe(picker.FileName); if (replacement.Kind != asset.Kind) throw new InvalidOperationException("Choose media of the same type."); editor.Transaction("Relink media", () => { asset.Path = replacement.Path; asset.Width = replacement.Width; asset.Height = replacement.Height; asset.Duration = replacement.Duration; asset.FrameRate = replacement.FrameRate; asset.HasAudio = replacement.HasAudio; asset.HasAlpha = replacement.HasAlpha; }); _ = AnalyzeAsset(asset, editor.Project.Id); } catch (Exception ex) { Toast(ex.Message); }
    }
    bool CanDiscard()
    {
        Pause(); if (!editor.Dirty) return true; var response = MessageBox.Show(this, "Save changes to this project before continuing?", "Kuro", MessageBoxButton.YesNoCancel, MessageBoxImage.Question); return response == MessageBoxResult.No || response == MessageBoxResult.Yes && SaveProject();
    }
    void NewProject()
    {
        if (!CanDiscard()) return;
        projectPath = null;
        assetSignature = "";
        timeline.Thumbnails.Clear(); timeline.Waveforms.Clear();
        timeline.ResetLayerViews();
        if (graphOpen) ToggleGraphEditor();
        editor.Load(new());
        CloseWelcome();
        timeline.Fit();
        Status.Text = "Ready";
    }
    void OpenProject() { if (!CanDiscard()) return; var dialog = new OpenFileDialog { Filter = "Kuro project|*.kuro", Title = "Open project" }; if (dialog.ShowDialog(this) == true) LoadProject(dialog.FileName); }
    public bool LoadProject(string path, bool recovering = false)
    {
        try
        {
            var project = Store.Load(path);
            Pause();
            projectPath = recovering ? null : path;
            assetSignature = "";
            timeline.Thumbnails.Clear(); timeline.Waveforms.Clear();
            timeline.ResetLayerViews();
            if (graphOpen) ToggleGraphEditor();
            editor.Load(project);
            if (recovering) { editor.Dirty = true; Refresh(); }
            else Remember(path);
            CloseWelcome();
            timeline.Fit();
            foreach (var asset in project.Assets) _ = AnalyzeAsset(asset, project.Id);
            Status.Text = recovering ? "Recovered autosaved project" : "Opened " + Path.GetFileName(path);
            return true;
        }
        catch (Exception ex) { Toast("Could not open project: " + ex.Message); return false; }
    }
    bool SaveProject(bool saveAs = false)
    {
        var path = projectPath;
        if (path == null || saveAs) { var dialog = new SaveFileDialog { Title = "Save Kuro project", Filter = "Kuro project|*.kuro", DefaultExt = ".kuro", FileName = editor.Project.Name == "Untitled" ? "Untitled.kuro" : editor.Project.Name + ".kuro" }; if (dialog.ShowDialog(this) != true) return false; path = dialog.FileName; }
        try { editor.Project.Name = Path.GetFileNameWithoutExtension(path); Store.Save(editor.Project, path); projectPath = path; editor.Dirty = false; Remember(path); Refresh(); Status.Text = "Saved · " + Path.GetFileName(path); return true; }
        catch (Exception ex) { Toast("Could not save project: " + ex.Message); return false; }
    }
    void Remember(string path) { Preferences.Current.Recent.Remove(path); Preferences.Current.Recent.Insert(0, path); Preferences.Current.Recent = Preferences.Current.Recent.Take(10).ToList(); Preferences.Current.Save(); BuildMenu(); }
    void SaveRecovery() { try { Store.Save(editor.Project, Path.Combine(Store.Home, "recovery.kuro")); } catch (Exception ex) { Status.Text = "Autosave failed: " + ex.Message; } }
    public void ShowWelcome()
    {
        WelcomeRecent.Children.Clear();
        foreach (var path in Preferences.Current.Recent.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
        {
            var row = new DockPanel { Width = 396 };
            var icon = new Icon { Kind = "Composition", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(icon);
            var description = new StackPanel();
            description.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var directory = new TextBlock { Text = Path.GetDirectoryName(path), FontSize = 10, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            directory.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            description.Children.Add(directory);
            row.Children.Add(description);
            var project = new Button { Content = row, Padding = new Thickness(10, 7, 10, 7), ToolTip = path };
            System.Windows.Automation.AutomationProperties.SetName(project, "Open " + Path.GetFileNameWithoutExtension(path));
            project.Click += (_, _) => LoadProject(path);
            WelcomeRecent.Children.Add(project);
        }
        if (WelcomeRecent.Children.Count == 0) WelcomeRecent.Children.Add(Dialogs.Label("No recent projects"));
        WelcomeRecovery.Visibility = File.Exists(Path.Combine(Store.Home, "recovery.kuro")) ? Visibility.Visible : Visibility.Collapsed;
        WelcomeScreen.Visibility = Visibility.Visible;
    }
    void AnimateWelcome()
    {
        WelcomeNew.Focus();
        if (Preferences.Current.ReduceMotion || !SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(230);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        WelcomeCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
        WelcomeOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = easing });
    }
    void CloseWelcome()
    {
        if (WelcomeScreen.Visibility == Visibility.Collapsed) return;
        WelcomeScreen.Visibility = Visibility.Collapsed;
        if (IsLoaded) timeline.Focus();
    }
    void WelcomeNewClick(object sender, RoutedEventArgs e) => NewProject();
    void WelcomeOpenClick(object sender, RoutedEventArgs e) => OpenProject();
    void WelcomeRecoveryClick(object sender, RoutedEventArgs e) => LoadProject(Path.Combine(Store.Home, "recovery.kuro"), recovering: true);
    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!CanDiscard()) { e.Cancel = true; return; }
        closed = true; aiCancellation?.Cancel(); lifetime.Cancel(); preview.Dispose(); autosave.Stop(); clock.Stop(); SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        Preferences.Current.LeftWidth = ProjectColumn.Width.Value; Preferences.Current.RightWidth = InspectorColumn.Width.Value; Preferences.Current.TimelineHeight = TimelineRow.ActualHeight; Preferences.Current.AIWidth = aiWidth; Preferences.Current.Save();
        var recovery = Path.Combine(Store.Home, "recovery.kuro"); if (WelcomeScreen.Visibility == Visibility.Collapsed && File.Exists(recovery)) File.Delete(recovery);
    }
    void CompositionSettings()
    {
        Pause();
        var composition = editor.Comp;
        var revision = editor.Project.Revision;
        var popup = new CompositionSettingsDialog(this, composition, (name, width, height, rate, duration, background) =>
        {
            if (composition.Name == name && composition.Width == width && composition.Height == height && composition.Fps == rate && composition.Duration == duration && composition.Background == background) return;
            editor.Transaction("Composition settings", () =>
            {
                var workAreaEndsAtDuration = composition.WorkEnd == composition.Duration;
                composition.Name = name;
                composition.Width = width;
                composition.Height = height;
                composition.Fps = rate;
                composition.Duration = duration;
                composition.Background = background;
                composition.WorkEnd = workAreaEndsAtDuration ? duration : Math.Clamp(composition.WorkEnd, 1, duration);
                composition.WorkStart = Math.Clamp(composition.WorkStart, 0, composition.WorkEnd - 1);
                composition.Markers.RemoveAll(frame => frame >= duration);
                foreach (var layer in composition.Layers) layer.Duration = Math.Min(layer.Duration, duration - layer.Start);
                if (editor.Project.Assets.Find(asset => asset.Kind == "Composition" && asset.Id == composition.Id) is { } asset)
                {
                    asset.Name = name;
                    asset.Width = width;
                    asset.Height = height;
                    asset.FrameRate = rate.Value;
                    asset.Duration = rate.Seconds(duration);
                    assetSignature = "";
                }
                editor.Playhead = Math.Min(editor.Playhead, duration - 1);
            }, revision);
            timeline.Fit();
        });
        popup.ShowDialog();
    }
    void OpenComposition(string id) { Pause(); editor.Project.ActiveComposition = id; editor.Selection.Clear(); editor.Playhead = 0; Refresh(); timeline.Fit(); }
    void CompositionChanged(object sender, SelectionChangedEventArgs e) { if (!uiBuilding && CompositionPicker.SelectedValue is string id && editor.Comp.Id != id) OpenComposition(id); }
    void QualityChanged(object sender, SelectionChangedEventArgs e) { if (timeline == null) return; Pause(); Preferences.Current.PreviewDivisor = PreviewQuality.SelectedIndex == 0 ? 1 : PreviewQuality.SelectedIndex == 2 ? 4 : 2; Preferences.Current.Save(); RequestPreview(); }
    void ToggleGraphEditor() { graphOpen = !graphOpen; TimelineHost.Children.Clear(); TimelineHost.Children.Add(graphOpen ? graph : timeline); GraphButton.SetResourceReference(Button.ForegroundProperty, graphOpen ? "Accent" : "Text"); if (graphOpen) { graph.Choose(timeline.Property); graph.Refresh(); } else timeline.Refresh(); }
    void WorkStart() { if (editor.Playhead >= editor.Comp.WorkEnd) { Toast("Work area start must be before its end."); return; } editor.Transaction("Set work area start", () => editor.Comp.WorkStart = editor.Playhead); }
    void WorkEnd() { if (editor.Playhead + 1 <= editor.Comp.WorkStart) { Toast("Work area end must follow its start."); return; } editor.Transaction("Set work area end", () => editor.Comp.WorkEnd = editor.Playhead + 1); }
    void ToggleSnap(object sender, RoutedEventArgs e) { timeline.Snap = !timeline.Snap; SnapButton.SetResourceReference(Button.ForegroundProperty, timeline.Snap ? "Accent" : "Muted"); }
    void ToggleGuides(object sender, RoutedEventArgs e) { guides = !guides; GuideButton.SetResourceReference(Button.ForegroundProperty, guides ? "Accent" : "Muted"); UpdateOverlay(); }
    void SelectTool(object sender, RoutedEventArgs e)
    {
        shapeTool = false;
        if (shapeStart != null) { shapeStart = null; Viewer.ReleaseMouseCapture(); UpdateOverlay(); }
        Viewer.Cursor = Cursors.Arrow;
        ShapeToolButton.ClearValue(Button.BackgroundProperty);
        ShapeToolButton.SetResourceReference(Button.ForegroundProperty, "Muted");
        SelectionTool.SetResourceReference(Button.BackgroundProperty, "Selection");
        SelectionTool.SetResourceReference(Button.ForegroundProperty, "Accent");
    }
    void AddText(object sender, RoutedEventArgs e) => Command("create_text");
    void AddShape(object sender, RoutedEventArgs e)
    {
        shapeTool = true; Viewer.Cursor = Cursors.Cross;
        SelectionTool.ClearValue(Button.BackgroundProperty);
        SelectionTool.SetResourceReference(Button.ForegroundProperty, "Muted");
        ShapeToolButton.SetResourceReference(Button.BackgroundProperty, "Selection");
        ShapeToolButton.SetResourceReference(Button.ForegroundProperty, "Accent");
        ShapeToolButton.ToolTip = $"Draw {shapeKind.ToLowerInvariant()} · right-click for shape types";
    }
    void ShapeToolsMenu(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ShapeToolButton };
        foreach (var kind in Shapes.Kinds)
        {
            var item = new MenuItem { Header = kind, IsCheckable = true, IsChecked = kind == shapeKind };
            item.Click += (_, _) => { shapeKind = kind; AddShape(sender, e); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true; e.Handled = true;
    }
    void UndoClick(object sender, RoutedEventArgs e) { Pause(); editor.Undo(); }
    void RedoClick(object sender, RoutedEventArgs e) { Pause(); editor.Redo(); }
    void SplitClick(object sender, RoutedEventArgs e) => Split();
    void DeleteSelection() => Command(editor.KeyTargets.Any() ? "delete_selected_keyframes" : "delete_layers");
    void PlayClick(object sender, RoutedEventArgs e) => TogglePlayback();
    void PreviousFrame(object sender, RoutedEventArgs e) => Seek(editor.Playhead - 1);
    void NextFrame(object sender, RoutedEventArgs e) => Seek(editor.Playhead + 1);
    void ImportClick(object sender, RoutedEventArgs e) => ImportMedia();
    void ExportClick(object sender, RoutedEventArgs e) => ShowExport();
    void ToggleGraph(object sender, RoutedEventArgs e) => ToggleGraphEditor();
    void WorkStartClick(object sender, RoutedEventArgs e) => WorkStart();
    void WorkEndClick(object sender, RoutedEventArgs e) => WorkEnd();
    void MarkerClick(object sender, RoutedEventArgs e) => editor.Transaction("Add marker", () => { if (!editor.Comp.Markers.Contains(editor.Playhead)) editor.Comp.Markers.Add(editor.Playhead); });
    void ZoomOut(object sender, RoutedEventArgs e) { timeline.PixelsPerFrame = Math.Max(.05, timeline.PixelsPerFrame / 1.3); timeline.Refresh(); }
    void ZoomIn(object sender, RoutedEventArgs e) { timeline.PixelsPerFrame = Math.Min(50, timeline.PixelsPerFrame * 1.3); timeline.Refresh(); }
    void FitTimeline(object sender, RoutedEventArgs e) => timeline.Fit();
    void ToggleAI(object sender, RoutedEventArgs e) => SetAISidebar(!aiOpen);

    void SetAISidebar(bool open)
    {
        aiOpen = open;
        AISidebar.Visibility = AISplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AISplitterColumn.Width = new GridLength(open ? 5 : 0);
        AISidebarColumn.MinWidth = open ? 286 : 0;
        AISidebarColumn.Width = new GridLength(open ? aiWidth : 0);
        AIButton.SetResourceReference(Button.ForegroundProperty, open ? "Accent" : "Muted");
        AIButton.SetResourceReference(Button.BackgroundProperty, open ? "Selection" : "Panel");
        UpdateWorkspaceWidths(AppGrid.ActualWidth);

        if (open)
        {
            if (!Preferences.Current.ReduceMotion && SystemParameters.ClientAreaAnimation)
                AISidebar.BeginAnimation(OpacityProperty, new DoubleAnimation(.55, 1, TimeSpan.FromMilliseconds(125)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!aiOpen || !IsVisible) return;
                Composer.Focus();
                ChatScroll.ScrollToEnd();
            });
        }
        else AIButton.Focus();
    }

    void WorkspaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateWorkspaceWidths(e.NewSize.Width);

    void UpdateWorkspaceWidths(double width)
    {
        if (width <= 0 || AISidebarColumn == null || ProjectColumn == null) return;
        AISidebarColumn.MaxWidth = aiOpen ? Math.Clamp(width - 755, 286, 480) : 0;
        var sidebarWidth = aiOpen ? Math.Min(AISidebarColumn.Width.Value, AISidebarColumn.MaxWidth) + 5 : 0;
        var available = Math.Max(450, width - sidebarWidth - 300);
        var desiredLeft = Math.Clamp(ProjectColumn.Width.Value, 200, 450);
        var desiredRight = Math.Clamp(InspectorColumn.Width.Value, 250, 500);
        var overflow = Math.Max(0, desiredLeft + desiredRight - available);
        var flex = desiredLeft + desiredRight - 450;
        ProjectColumn.MaxWidth = flex > 0 && overflow > 0 ? Math.Max(200, desiredLeft - overflow * (desiredLeft - 200) / flex) : 450;
        InspectorColumn.MaxWidth = flex > 0 && overflow > 0 ? Math.Max(250, desiredRight - overflow * (desiredRight - 250) / flex) : 500;
    }

    void AISplitterCompleted(object sender, DragCompletedEventArgs e)
    {
        aiWidth = AISidebarColumn.Width.Value;
        UpdateWorkspaceWidths(AppGrid.ActualWidth);
    }

    void UpdateChatConnection()
    {
        var p = Preferences.Current;
        ChatConnection.Text = p.ChatVerified ? "Connected · " + p.ChatModel : "Not connected";
        ModelButton.ToolTip = string.IsNullOrEmpty(p.ChatModel) ? "Connection settings" : p.ChatModel + " · model settings";
    }
    async void SendAI(object sender, RoutedEventArgs e)
    {
        if (aiCancellation != null || string.IsNullOrWhiteSpace(Composer.Text)) return;
        Pause();
        var prompt = Composer.Text.Trim();
        Composer.Clear(); AddChat(prompt, false, true);
        aiCancellation = new();
        SendAIButton.Visibility = Visibility.Collapsed;
        StopAIButton.Visibility = Visibility.Visible;
        ChatActivity.Text = "Working…";
        try { await new ChatAgent().Run(editor, prompt, (text, activity) => AddChat(text, activity), aiCancellation.Token); }
        catch (OperationCanceledException) { AddChat("Stopped. Completed edits remain in the timeline and Undo history.", true); }
        catch (Exception ex) { AddChat(ex.Message, true); }
        finally
        {
            aiCancellation.Dispose(); aiCancellation = null;
            ChatActivity.Text = "";
            SendAIButton.Visibility = Visibility.Visible;
            StopAIButton.Visibility = Visibility.Collapsed;
            ComposerChanged(Composer, null);
        }
    }
    void ComposerChanged(object sender, TextChangedEventArgs? e)
    { if (SendAIButton != null) SendAIButton.IsEnabled = aiCancellation == null && !string.IsNullOrWhiteSpace(Composer.Text); }
    void ComposerKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { SendAI(sender, e); e.Handled = true; } }
    void StopAI(object sender, RoutedEventArgs e) => aiCancellation?.Cancel();
    void AddChat(string text, bool activity, bool user = false)
    {
        var follow = user || ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 24;
        if (!user && !activity)
        {
            var message = ChatMarkdown.Create(text);
            message.Margin = new Thickness(0, 7, 0, 5);
            ChatHistory.Children.Add(message);
        }
        else
        {
            var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = activity ? 11 : 12, LineHeight = 18 };
            message.SetResourceReference(TextBlock.ForegroundProperty, activity ? "Muted" : "Text");
            var card = new Border { Child = message, CornerRadius = new CornerRadius(7), Margin = user ? new Thickness(23, 6, 0, 7) : new Thickness(0, 3, 0, 3), Padding = user ? new Thickness(10, 7, 10, 7) : new Thickness(0) };
            if (user) card.SetResourceReference(Border.BackgroundProperty, "Selection");
            ChatHistory.Children.Add(card);
        }
        if (follow) ChatScroll.ScrollToEnd();
    }
    void Activity(CommandEvent e)
    {
        Status.Text = e.Summary; if (aiCancellation == null || e.Stage != "started") return;
        if (e.Target != null && editor.Comp.Layers.Any(l => l.Id == e.Target)) { timeline.Reveal(e.Target); }
    }
    void Toast(string text)
    {
        NotificationText.Text = text; Notification.Visibility = Visibility.Visible;
        if (!Preferences.Current.ReduceMotion) Notification.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) }; timer.Tick += (_, _) => { Notification.Visibility = Visibility.Collapsed; timer.Stop(); }; timer.Start();
    }
    void DismissNotification(object sender, RoutedEventArgs e) => Notification.Visibility = Visibility.Collapsed;
    void SetTheme(string theme) { Preferences.Current.Theme = theme; Preferences.Current.Save(); Theme.Apply(theme); NativeChrome.Match(this); timeline.Refresh(); graph.Refresh(); UpdateOverlay(); }
    void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) { if (Preferences.Current.Theme == "Follow Windows") Dispatcher.BeginInvoke(() => SetTheme("Follow Windows")); }
    void OpenSettings(object sender, RoutedEventArgs e) => ShowSettings();
    void OpenAISettings(object sender, RoutedEventArgs e) => ShowSettings(true);
}
