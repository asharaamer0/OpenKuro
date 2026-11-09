using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Kuro.AI;
using Kuro.Core;
using Kuro.Engine;
using Kuro.UI;

namespace Kuro;
public partial class MainWindow
{
    void ShowSettings(bool chat = false)
    {
        var window = Dialogs.Window("Settings", this, 720, 720); var tabs = new TabControl { Margin = new Thickness(18) };
        StackPanel Tab(string name) { var p = new StackPanel { Margin = new Thickness(12, 8, 12, 20) }; tabs.Items.Add(new TabItem { Header = name, Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }); return p; }
        var appearance = Tab("Appearance"); appearance.Children.Add(Dialogs.Label("Appearance", true));
        var theme = Dialogs.Choice(appearance, "Appearance", ["Light", "Dark", "Follow Windows"], Preferences.Current.Theme); theme.SelectionChanged += (_, _) => { if (theme.SelectedItem is string value) SetTheme(value); };
        var motion = new CheckBox { Content = "Reduce interface motion", IsChecked = Preferences.Current.ReduceMotion }; motion.Click += (_, _) => { Preferences.Current.ReduceMotion = motion.IsChecked == true; Preferences.Current.Save(); }; appearance.Children.Add(motion);
        var loop = new CheckBox { Content = "Loop preview within the work area", IsChecked = Preferences.Current.Loop }; loop.Click += (_, _) => { Preferences.Current.Loop = loop.IsChecked == true; Preferences.Current.Save(); }; appearance.Children.Add(loop);
        appearance.Children.Add(Dialogs.Label("Preview quality changes viewer processing only. Export always uses its own dimensions and the original source."));
        appearance.Children.Add(Dialogs.Label("Workspace", true)); appearance.Children.Add(Dialogs.Button("Balanced layout", () => { ProjectColumn.Width = new GridLength(264); InspectorColumn.Width = new GridLength(290); TimelineRow.Height = new GridLength(300); }));
        appearance.Children.Add(Dialogs.Button("Animation layout", () => { ProjectColumn.Width = new GridLength(220); InspectorColumn.Width = new GridLength(300); TimelineRow.Height = new GridLength(Math.Max(330, ActualHeight * .43)); }));
        appearance.Children.Add(Dialogs.Label("Cache", true)); appearance.Children.Add(Dialogs.Label(Store.Cache)); appearance.Children.Add(Dialogs.Button("Open cache folder", () => Process.Start(new ProcessStartInfo("explorer.exe", Store.Cache) { UseShellExecute = true })));
        appearance.Children.Add(Dialogs.Label("Cache eviction limits and proxy media are planned. Source media is never stored in the disposable cache."));

        var keyboard = Tab("Shortcuts"); keyboard.Children.Add(Dialogs.Label("Keyboard shortcuts", true)); keyboard.Children.Add(Dialogs.Label("After Effects oriented defaults. Click a binding, press a shortcut, then Apply. Backspace clears it. Conflicts are rejected."));
        var search = Dialogs.Field(keyboard, "Search commands", ""); var rows = new StackPanel(); keyboard.Children.Add(rows);
        void RebuildShortcuts()
        {
            rows.Children.Clear(); foreach (var action in actions.Actions.Where(a => a.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(155) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
                var label = Dialogs.Label(action.Name); var binding = new TextBox { Text = actions.Binding(action), ToolTip = "Press a shortcut; Backspace clears it" };
                binding.PreviewKeyDown += (_, e) =>
                {
                    e.Handled = true; var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                    if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift) return;
                    if (key == System.Windows.Input.Key.Back) { binding.Text = ""; return; }
                    var modifiers = System.Windows.Input.Keyboard.Modifiers; var prefix = (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? "Alt+" : "") + (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? "Shift+" : ""); binding.Text = prefix + key;
                };
                var apply = Dialogs.Button("Apply", () =>
                {
                    if (!ActionRegistry.Valid(binding.Text)) { MessageBox.Show(window, "That key combination is not supported by Windows gestures."); return; }
                    var conflict = actions.Actions.FirstOrDefault(a => a.Id != action.Id && actions.Binding(a).Equals(binding.Text, StringComparison.OrdinalIgnoreCase) && binding.Text != "");
                    if (conflict != null) { MessageBox.Show(window, "Already assigned to " + conflict.Name + ". Clear that binding first."); return; }
                    Preferences.Current.Shortcuts[action.Id] = binding.Text; Preferences.Current.Save(); BuildMenu(); Toast("Shortcut updated · " + action.Name);
                }); apply.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(binding, 1); Grid.SetColumn(apply, 2); grid.Children.Add(label); grid.Children.Add(binding); grid.Children.Add(apply); rows.Children.Add(grid);
            }
        }
        search.TextChanged += (_, _) => RebuildShortcuts(); RebuildShortcuts();
        keyboard.Children.Add(Dialogs.Button("Restore defaults", () => { Preferences.Current.Shortcuts.Clear(); Preferences.Current.Save(); BuildMenu(); RebuildShortcuts(); }));
        keyboard.Children.Add(Dialogs.Button("Export preset…", () => { var save = new SaveFileDialog { Filter = "Shortcut preset|*.json", FileName = "Kuro shortcuts.json" }; if (save.ShowDialog(window) == true) Store.AtomicWrite(save.FileName, JsonSerializer.Serialize(actions.Actions.ToDictionary(a => a.Id, actions.Binding), Data.Json)); }));
        keyboard.Children.Add(Dialogs.Button("Import preset…", () =>
        {
            var open = new OpenFileDialog { Filter = "Shortcut preset|*.json" }; if (open.ShowDialog(window) != true) return;
            try { var preset = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(open.FileName), Data.Json)!; if (preset.Any(p => !actions.Actions.Any(a => a.Id == p.Key) || !ActionRegistry.Valid(p.Value))) throw new InvalidDataException("The preset contains an unknown command or invalid shortcut."); var merged = actions.Actions.ToDictionary(a => a.Id, a => preset.GetValueOrDefault(a.Id, actions.Binding(a))); if (merged.Values.Where(v => v != "").GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new InvalidDataException("The preset contains conflicting bindings."); Preferences.Current.Shortcuts = merged; Preferences.Current.Save(); BuildMenu(); RebuildShortcuts(); } catch (Exception ex) { MessageBox.Show(window, ex.Message); }
        }));

        var ai = Tab("AI connection"); ai.Children.Add(Dialogs.Label("Connected chat. Local tools.", true));
        ai.Children.Add(Dialogs.Label("Exa's documented chat compatibility endpoint routes to web answers and its agent API runs research. Custom editor tool calling has not been established for this credential. Configure an inference service that supports Chat Completions function tools, with that service's own credential."));
        ai.Children.Add(Dialogs.Label(Credentials.Read("Exa") != null ? "Exa credential: •••••••• · protected by Windows · not used for editor inference" : "Exa credential is not configured."));
        var endpoint = Dialogs.Field(ai, "API base URL · include /v1 if required by your provider", Preferences.Current.ChatEndpoint);
        var model = Dialogs.Field(ai, "Chat / planning model ID", Preferences.Current.ChatModel);
        ai.Children.Add(Dialogs.Label("Provider credential · saved in Windows Credential Manager")); var secret = new PasswordBox { Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 4), ToolTip = "Leave empty to keep the credential stored for this exact endpoint" }; ai.Children.Add(secret);
        var connection = Dialogs.Label(Preferences.Current.ChatVerified ? "Tool-use connection test passed." : "Not connected. Local editing remains available."); ai.Children.Add(connection);
        void Invalidate() { Preferences.Current.ChatVerified = false; connection.Text = "Configuration changed. Test tool use before autonomous editing."; }
        endpoint.TextChanged += (_, _) => Invalidate(); model.TextChanged += (_, _) => Invalidate(); secret.PasswordChanged += (_, _) => Invalidate();
        ai.Children.Add(Dialogs.Button("Discover models", async () =>
        {
            try { using var provider = new CompatibleProvider(endpoint.Text, model.Text, secret.Password.Length > 0 ? secret.Password : Credentials.Read(Credentials.ChatTarget(endpoint.Text)) ?? ""); var models = await provider.Discover(lifetime.Token); var chooser = new ComboBox { ItemsSource = models, Margin = new Thickness(0, 7, 0, 7) }; chooser.SelectionChanged += (_, _) => { if (chooser.SelectedItem is string selected) model.Text = selected; }; ai.Children.Add(chooser); connection.Text = $"{models.Count} models discovered. Select one, then test its tool support."; } catch (Exception ex) { connection.Text = ex.Message; }
        }));
        ai.Children.Add(Dialogs.Button("Test tool use & save", async () =>
        {
            connection.Text = "Testing a real function-tool round trip…";
            try { if (string.IsNullOrWhiteSpace(model.Text)) throw new InvalidOperationException("Enter a model ID."); using var provider = new CompatibleProvider(endpoint.Text, model.Text, secret.Password.Length > 0 ? secret.Password : Credentials.Read(Credentials.ChatTarget(endpoint.Text)) ?? ""); await ChatAgent.Verify(provider, lifetime.Token); Preferences.Current.ChatEndpoint = endpoint.Text.Trim().TrimEnd('/'); Preferences.Current.ChatModel = model.Text.Trim(); if (secret.Password.Length > 0) Credentials.Write(Credentials.ChatTarget(endpoint.Text), secret.Password); Preferences.Current.ChatVerified = true; Preferences.Current.Save(); connection.Text = "✓ Tool-use verified. Autonomous editing is available."; UpdateChatConnection(); } catch (Exception ex) { Preferences.Current.ChatVerified = false; connection.Text = ex.Message; UpdateChatConnection(); }
        }, true));
        ai.Children.Add(Dialogs.Label("This build uses bounded, non-streaming tool rounds (maximum 12). Stop cancels the active HTTP request and prevents late commands. Project paths, media bytes, and credentials are excluded from tool context. Chat image understanding, streaming text, pause/resume, and request-level grouped undo are not implemented."));

        var local = Tab("Local processing"); PopulateLocalModels(local);
        window.Content = tabs; if (chat) tabs.SelectedIndex = 2; window.ShowDialog(); UpdateChatConnection();
    }
    void PopulateLocalModels(StackPanel panel)
    {
        panel.Children.Add(Dialogs.Label("Local processing", true));
        try { panel.Children.Add(Dialogs.Label("FFmpeg: " + Processes.Locate("ffmpeg"))); panel.Children.Add(Dialogs.Label("✓ Decode, waveforms, composition rendering, audio mixing, H.264/AAC export, and block-matching stabilization run locally.")); } catch (Exception ex) { panel.Children.Add(Dialogs.Label(ex.Message)); }
        panel.Children.Add(Dialogs.Label("Segmentation & tracking", true)); panel.Children.Add(Dialogs.Label("Remove Background runs U²-Netp on the CPU. The first use downloads the foreground model. Each frame is processed locally."));
        panel.Children.Add(Dialogs.Label("Compute", true)); panel.Children.Add(Dialogs.Label("Native WPF workspace uses Windows graphics acceleration when available. The shared composition renderer currently rasterizes on a dedicated CPU/STA worker. FFmpeg uses bounded decoding threads; exports use software H.264. GPU compositing and hardware encoding remain planned."));
        panel.Children.Add(Dialogs.Label("Preview limitation", true)); panel.Children.Add(Dialogs.Label("Preview prepares a local PCM audio mix, then uses Windows audio playback as its clock. Video requests may drop frames while showing displayed/requested timing. Sustained realtime 4K, low-latency mixing, proxies, and resource scheduling remain planned."));
    }
    void ShowLocalModels() { var window = Dialogs.Window("Local processing availability", this, 600, 600); var panel = new StackPanel { Margin = new Thickness(24) }; PopulateLocalModels(panel); window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; window.ShowDialog(); }
    void ShowBuildStatus()
    {
        var window = Dialogs.Window("Build status", this, 660, 650); var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(Dialogs.Label("Kuro 0.1 · working foundation", true));
        panel.Children.Add(Dialogs.Label("Native Windows editor with real file import, searchable bins, clip placement, layer stacking, trim/move/split, transform animation, value/speed graphs, text, shapes, precompositions, atomic save/recovery, shortcuts, local stabilization, and H.264/AAC MP4 export."));
        panel.Children.Add(Dialogs.Label("Not production complete", true)); panel.Children.Add(Dialogs.Label("The full requested scope is tracked in docs/ROADMAP.md. Current limits include low-latency audio mixing, GPU composition, masks/pen paths, blend modes beyond Normal, linked AV, parenting, advanced graph selection, text selectors and typography, faithful AE preset parity, local object tracking, render queue, temporal effects, and AI streaming/activity cursor. Controls for these are not presented as implemented."));
        panel.Children.Add(Dialogs.Label("Preset status", true)); panel.Children.Add(Dialogs.Label("True Motion reads authored text animator values and keyframes from supported FFX files in the optional True Motion folder. Selector rendering and flicker use Kuro implementations. After Effects visual parity is unverified."));
        panel.Children.Add(Dialogs.Button("Open project folder", () => Process.Start(new ProcessStartInfo("explorer.exe", Presets.Root()) { UseShellExecute = true }))); window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; window.ShowDialog();
    }
    void ShowExport()
    {
        Pause(); var window = Dialogs.Window("Export composition", this, 520, 730); var panel = new StackPanel { Margin = new Thickness(24) }; var snapshot = Data.Clone(editor.Project);
        panel.Children.Add(Dialogs.Label("Export", true)); panel.Children.Add(Dialogs.Label("MP4 · H.264 video + AAC audio · Rec.709 SDR · square pixels"));
        var size = Dialogs.Choice(panel, "Output resolution", ["Composition", "3840 × 2160", "1920 × 1080", "1280 × 720", "Custom"], "Composition");
        var width = Dialogs.Field(panel, "Width · pixels", snapshot.Active.Width.ToString()); var height = Dialogs.Field(panel, "Height · pixels", snapshot.Active.Height.ToString());
        size.SelectionChanged += (_, _) => { var s = size.SelectedItem as string; var dimensions = s switch { "3840 × 2160" => (3840, 2160), "1920 × 1080" => (1920, 1080), "1280 × 720" => (1280, 720), _ => (snapshot.Active.Width, snapshot.Active.Height) }; if (s != "Custom") { width.Text = dimensions.Item1.ToString(); height.Text = dimensions.Item2.ToString(); } };
        var range = Dialogs.Choice(panel, "Export range", ["Full composition", "Work area"], "Full composition"); var quality = Dialogs.Choice(panel, "Encoding quality", ["Master · CRF 16", "High · CRF 18", "Balanced · CRF 22"], "High · CRF 18");
        panel.Children.Add(Dialogs.Label($"Frame rate: {snapshot.Active.Fps.Value:0.###} fps · matches composition. Audio: 48 kHz stereo, 320 kbps. MP4 does not preserve transparency; transparent areas render over black."));
        var output = Dialogs.Field(panel, "Destination", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), snapshot.Name + ".mp4")); panel.Children.Add(Dialogs.Button("Choose destination…", () => { var picker = new SaveFileDialog { Filter = "H.264 MP4|*.mp4", DefaultExt = ".mp4", FileName = Path.GetFileName(output.Text) }; if (picker.ShowDialog(window) == true) output.Text = picker.FileName; }));
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 12, 0, 8), Height = 4 }; panel.Children.Add(progress); var status = Dialogs.Label("Uses original sources and the shared composition renderer."); panel.Children.Add(status);
        CancellationTokenSource? cancel = null; Button? start = null; var stop = Dialogs.Button("Cancel export", () => cancel?.Cancel()); stop.IsEnabled = false;
        start = Dialogs.Button("Export MP4", async () =>
        {
            if (!int.TryParse(width.Text, out var x) || !int.TryParse(height.Text, out var y)) { status.Text = "Enter valid output dimensions."; return; }
            if (File.Exists(output.Text) && MessageBox.Show(window, "Replace the existing export?", "Kuro", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            cancel = new(); start!.IsEnabled = false; stop.IsEnabled = true; var opts = new ExportOptions(output.Text, x, y, quality.SelectedIndex == 0 ? 16 : quality.SelectedIndex == 2 ? 22 : 18, range.SelectedIndex == 1);
            try { await Exporter.Export(snapshot, opts, new Progress<JobProgress>(p => { progress.Value = p.Fraction; status.Text = p.Summary + $"\nElapsed {p.Elapsed:mm\\:ss} · remaining {p.Remaining}"; }), cancel.Token); status.Text = "✓ Export complete · " + output.Text; Toast("Export complete · " + Path.GetFileName(output.Text)); }
            catch (OperationCanceledException) { status.Text = "Canceled. Incomplete output was removed."; }
            catch (Exception ex) { status.Text = "Export failed: " + ex.Message; }
            finally { cancel.Dispose(); cancel = null; start.IsEnabled = true; stop.IsEnabled = false; }
        }, true);
        panel.Children.Add(start); panel.Children.Add(stop); window.Closing += (_, e) => { if (cancel != null) { cancel.Cancel(); e.Cancel = true; status.Text = "Canceling… close this window when the job has stopped."; } }; window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; window.ShowDialog();
    }
    async void ProcessClip(Layer layer, bool removeBackground)
    {
        var asset = editor.Project.Assets.Find(a => a.Id == layer.AssetId);
        if (asset is not { Kind: "Video", Missing: false } || layer.Locked) { Toast("Select an unlocked video layer with available media."); return; }
        ClipRange range;
        try { range = ClipRange.ForLayer(layer, asset, editor.Comp.Fps); }
        catch (InvalidOperationException ex) { Toast(ex.Message); return; }

        Pause();
        var projectId = editor.Project.Id;
        var composition = editor.Comp.Id;
        var timing = (layer.AssetId, layer.Start, layer.Duration, layer.SourceIn, layer.Rate, editor.Comp.Fps);
        var name = removeBackground ? "Remove Background" : "Stabilize";
        var window = Dialogs.Window(name, this, 470, 225);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Dialogs.Label(name, true));
        var status = Dialogs.Label($"{layer.Name} · {range.Duration:0.##}s of source");
        panel.Children.Add(status);
        var progress = new ProgressBar { Maximum = 1, Height = 4, Margin = new Thickness(0, 14, 0, 14) };
        panel.Children.Add(progress);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var done = false;
        var button = Dialogs.Button("Cancel", () => { if (done) window.Close(); else cancel.Cancel(); });
        panel.Children.Add(button);
        window.Content = panel;
        window.Closing += (_, e) => { if (!done) { cancel.Cancel(); e.Cancel = true; } };
        window.Show();
        try
        {
            var reporting = new Progress<JobProgress>(p =>
            {
                if (done) return;
                progress.Value = p.Fraction;
                status.Text = p.Total > 0 ? p.Summary : p.Stage + "…";
            });
            var output = removeBackground
                ? await BackgroundRemoval.Process(asset, range, reporting, cancel.Token)
                : await LocalTools.Stabilize(asset, range, reporting, cancel.Token);
            var media = await Media.Probe(output, cancel.Token);
            var current = editor.Comp.Layers.Find(candidate => candidate.Id == layer.Id);
            if (editor.Project.Id != projectId || editor.Comp.Id != composition || current is not { Locked: false }
                || timing != (current.AssetId, current.Start, current.Duration, current.SourceIn, current.Rate, editor.Comp.Fps)
                || current.Properties.TryGetValue("time", out var remap) && remap.Keys.Count > 0)
            {
                status.Text = "The target clip changed while processing.";
                return;
            }
            media.Name = Path.GetFileNameWithoutExtension(asset.Name) + (removeBackground ? " · foreground" : " · stabilized");
            var existing = editor.Project.Assets.Find(candidate => candidate.Path.Equals(media.Path, StringComparison.OrdinalIgnoreCase));
            editor.Transaction(name, () =>
            {
                if (existing == null) editor.Project.Assets.Add(media);
                current.AssetId = (existing ?? media).Id;
                current.SourceIn = range.Origin;
            });
            if (existing == null) _ = AnalyzeAsset(media, projectId);
            progress.Value = 1;
            status.Text = removeBackground ? "Background removed from the selected clip." : "Selected clip stabilized.";
        }
        catch (OperationCanceledException) { status.Text = "Canceled."; }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { done = true; button.Content = "Close"; }
    }
}
