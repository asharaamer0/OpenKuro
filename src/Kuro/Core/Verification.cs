using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kuro.AI;
using Kuro.Engine;
using Kuro.UI;

namespace Kuro.Core;
public static class Verification
{
    static readonly List<object> results = [];
    static void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("FAIL: " + label); results.Add(new { test = label, passed = true }); }
    static void Near(double actual, double expected, string label, double tolerance = .0001) => Check(Math.Abs(actual - expected) < tolerance, label);
    public static async Task Run()
    {
        Directory.CreateDirectory("artifacts"); if (File.Exists("artifacts/test-failure.txt")) File.Delete("artifacts/test-failure.txt"); var watch = Stopwatch.StartNew();
        var rational = new FrameRate(30000, 1001); var frame = 10_000_001L;
        Check(rational.Frames(rational.Seconds(frame)) == frame, "Rational frame timing round trip at 92 hours");
        var curve = new Curve(0); curve.Set(0, 10); curve.Set(30, 110); Near(curve.At(15), 60, "Linear interpolation");
        curve.Keys[0].Interpolation = "Hold"; Near(curve.At(29.99), 10, "Hold interpolation until boundary"); Near(curve.At(30), 110, "Exact key boundary");
        curve.Keys[0].Interpolation = "Bezier"; curve.Keys[0].OutY = 0; curve.Keys[1].InY = 1; Near(curve.At(15), 60, "Cubic Bezier evaluation");
        curve.Keys[0].OutY = 2; curve.Keys[1].InY = 2; Check(curve.At(20) > 110, "Bezier overshoot retained");
        Check(double.IsFinite(curve.Speed(15, new())), "Finite speed graph evaluation");
        var editor = new Editor(); editor.Execute(new("create_text", Frame: 0)); var layer = editor.Selected!; layer.Duration = 120; layer.SourceIn = .25;
        editor.Execute(new("add_keyframe", layer.Id, "x", 100, 0)); editor.Execute(new("add_keyframe", layer.Id, "x", 800, 90)); editor.Execute(new("set_interpolation", layer.Id, "x", Text: "Bezier"));
        var before = Data.Clone(editor.Project); var expectedX = layer.Get("x", 75); var expectedSource = layer.SourceTime(75, editor.Comp.Fps);
        editor.Execute(new("split_clip", Frame: 60)); var right = editor.Selected!; var left = editor.Comp.Layers.First(l => l.Id == layer.Id);
        Check(left.End == right.Start && left.Duration + right.Duration == 120, "Split has no frame gap or overlap"); Near(right.SourceTime(75, editor.Comp.Fps), expectedSource, "Split preserves source mapping"); Near(right.Get("x", 75), expectedX, "Split preserves complete animation curve");
        editor.Undo(); Check(editor.Comp.Layers.Count == 1, "Undo restores unsplit layer"); editor.Redo(); Check(editor.Comp.Layers.Count == 2, "Redo restores split");
        right = editor.Selected!; var rev = editor.Project.Revision; var stale = editor.Execute(new("set_property", right.Id, "x", 5, 70, ExpectedRevision: rev - 1)); Check(!stale.Success && editor.Project.Revision == rev, "Stale AI revision rejected without mutation");
        editor.Execute(new("toggle_lock", right.Id)); Check(!editor.Execute(new("set_property", right.Id, "x", 4)).Success, "Locked layer mutation rejected"); Check(!editor.Execute(new("split_clip", right.Id, Frame: 75)).Success, "Locked split rejected"); editor.Execute(new("toggle_lock", right.Id));
        var snapshot = JsonSerializer.Serialize(editor.Project, Data.Json); try { editor.Transaction("Invalid transaction", () => editor.Comp.Layers[0].Duration = -1); } catch (InvalidOperationException) { } Check(JsonSerializer.Serialize(editor.Project, Data.Json) == snapshot, "Invalid transaction rolls back atomically");
        var keysEditor = new Editor(); keysEditor.Execute(new("create_text")); var keyLayer = keysEditor.Selected!;
        foreach (var (at, val) in new[] { (0L, 0.0), (30L, 100.0), (60L, 200.0), (90L, 300.0) }) keysEditor.Execute(new("add_keyframe", keyLayer.Id, "x", val, at));
        var selectedIds = keyLayer.Properties["x"].Keys.Where(k => k.Frame is 30 or 60).Select(k => k.Id).ToArray(); keysEditor.SelectKey(selectedIds[0]); keysEditor.SelectKey(selectedIds[1], true);
        Check(keysEditor.KeyTargets.Count() == 2, "Multiple selected keyframes have stable targets");
        Check(keysEditor.Execute(new("set_selected_interpolation", Text: "Easy Ease")).Success, "Easy Ease operates on selected keys");
        Check(keysEditor.KeyTargets.All(t => t.Key.OutY == 0 && t.Key.InY == 1), "Selected keyframes receive zero-velocity ease handles");
        Check(keysEditor.Comp.Layers[0].Properties["x"].Keys.Last().Interpolation == "Linear", "Easing leaves an unrelated outgoing segment unchanged");
        keysEditor.Undo(); Check(keysEditor.Comp.Layers[0].Properties["x"].Keys.All(k => k.Interpolation == "Linear"), "Selected-key easing is one reversible transaction");
        Check(keysEditor.Execute(new("move_selected_keyframes", Frame: 5)).Success && keysEditor.KeyTargets.Select(t => t.Key.Frame).Order().SequenceEqual(new long[] { 35, 65 }), "Selected keyframes move together with exact offsets");
        var collisionRevision = keysEditor.Project.Revision; Check(!keysEditor.Execute(new("move_selected_keyframes", Frame: 25)).Success && keysEditor.Project.Revision == collisionRevision, "Keyframe collisions are rejected atomically");
        keysEditor.CopyKeys(); Check(keysEditor.PasteKeys(100).Success && keysEditor.KeyTargets.Select(t => t.Key.Frame).Order().SequenceEqual(new long[] { 100, 130 }), "Keyframe paste preserves relative timing and selects new IDs");
        var keyCount = keysEditor.Comp.Layers[0].Properties["x"].Keys.Count; keysEditor.Execute(new("delete_selected_keyframes")); Check(keysEditor.Comp.Layers[0].Properties["x"].Keys.Count == keyCount - 2, "Delete targets selected keyframes only"); keysEditor.Undo();
        keysEditor.SelectedKeys.Clear(); keysEditor.SelectedKeys.UnionWith(selectedIds); var activeKeyLayer = keysEditor.Comp.Layers[0]; keysEditor.Execute(new("toggle_lock", activeKeyLayer.Id)); Check(!keysEditor.Execute(new("set_selected_interpolation", Text: "Hold")).Success, "Locked selected keyframes reject context-menu edits"); keysEditor.Execute(new("toggle_lock", activeKeyLayer.Id));
        string? graphProperty = null; var context = KeyframeMenu.Create(keysEditor, prop => graphProperty = prop, null);
        ((System.Windows.Controls.MenuItem)context.Items[0]).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent)); Check(graphProperty == "x", "Context-menu Graph Editor resolves the selected property");
        ((System.Windows.Controls.MenuItem)context.Items[2]).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent)); Check(keysEditor.KeyTargets.All(t => t.Key.OutY == 0), "Context-menu Easy Ease executes the editor command");
        var assetPath = Path.GetFullPath("artifacts/source.mp4");
        await Processes.Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", assetPath]);
        var source = await Media.Probe(assetPath); Check(source.Width == 320 && source.Height == 180 && source.HasAudio, "Real media probe captures dimensions and audio");
        var waveform = await Media.Waveform(source); Check(waveform.Length >= 190 && waveform.Max() > .05, "Waveforms are generated from source PCM");
        var thumb = await Media.Thumbnail(source); Check(thumb != null && File.Exists(thumb), "Thumbnail generated asynchronously");
        var project = new Project { Name = "Verification edit" }; var c = project.Active; c.Width = 320; c.Height = 180; c.Duration = 60; c.WorkEnd = 60; editor.Load(project);
        editor.Transaction("Import video", () => project.Assets.Add(source)); editor.Execute(new("add_asset", source.Id)); var video = editor.Selected!; video.Properties["x"].Value = 160; video.Properties["y"].Value = 90;
        editor.Execute(new("create_text", Frame: 0)); var title = editor.Selected!; title.Text = "Kuro"; title.Properties["fontSize"].Value = 38; title.Duration = 60;
        editor.Execute(new("add_keyframe", title.Id, "y", 150, 0)); editor.Execute(new("add_keyframe", title.Id, "y", 90, 30)); editor.Execute(new("set_interpolation", title.Id, "y", Text: "Bezier"));
        editor.Execute(new("create_shape", Frame: 0)); var shape = editor.Selected!; shape.Fill = "#A0E28A48"; shape.Properties["width"].Value = 130; shape.Properties["height"].Value = 50; shape.Properties["y"].Value = 145; shape.Duration = 60;
        editor.Select(title.Id); editor.Execute(new("split_clip", Frame: 40));
        var savePath = Path.GetFullPath("artifacts/verification.kuro"); Store.Save(editor.Project, savePath); var reopened = Store.Load(savePath);
        Check(reopened.Active.Layers.Count == 4 && reopened.Assets[0].Path == assetPath, "Project persists stable layers and portable relative media paths");
        Near(reopened.Active.Layers.First(l => l.Id == title.Id).Get("y", 20), title.Get("y", 20), "Curve serialization preserves evaluation");
        Check(!File.ReadAllText(savePath).Contains("API_KEY"), "Project serialization excludes provider credentials");
        Check(ActionRegistry.Valid("Ctrl+Shift+D"), "Required split shortcut accepted"); Check(ActionRegistry.Valid("Ctrl+Alt+K"), "Custom shortcut syntax accepted");
        Check(ActionRegistry.Valid("P") && ActionRegistry.Valid("Space") && ActionRegistry.Valid("F9"), "Unmodified property/play/ease shortcuts accepted"); Check(ActionRegistry.Parse("Ctrl+0", out var fitKey, out _) && fitKey == System.Windows.Input.Key.D0, "Numeric shortcut maps to a physical digit key"); Check(!ActionRegistry.Valid("Ctrl+UnknownKey"), "Invalid shortcut rejected");
        // These checks exercise the optional third-party preset pack.
        var presets = Presets.Discover();
        if (presets.Count == 16)
        {
            Check(presets.All(p => p.Signatures.Length > 0), "All 16 real FFX files have readable property signatures"); Store.AtomicWrite(Path.GetFullPath("artifacts/preset-signatures.json"), JsonSerializer.Serialize(presets, Data.Json));
            Check(presets.All(p => Presets.Motion(p.Name)?.Animation.Keys.Count >= 2), "All 16 True Motion presets have authored editable endpoints");
            var down = Presets.Motion("01 Slide Down By Character")!;
            var motionKeys = down.AtRate(new FrameRate(30), 10, 90);
            Check(down.Y == -85 && motionKeys.Keys.Select(k => (k.Frame, k.Value)).SequenceEqual(new[] { (10L, -100d), (51L, 100d) }), "Slide Down uses the FFX travel and 41-frame selector sweep");
            Check(Presets.Motion("30 Characters Bottom In + Out")!.Animation.Keys.Count == 4, "In + Out preserves all four authored keyframes");
            var motionProject = new Project();
            var motionLayer = new Layer { Kind = "Text", Text = "KURO", Preset = "01 Slide Down By Character", Start = 10 };
            motionLayer.Properties[TextMotion.Property] = motionKeys;
            motionProject.Active.Background = "#00000000";
            motionProject.Active.Layers.Add(motionLayer);
            using (var motionRenderer = new Renderer())
            {
                int Alpha(long frame)
                {
                    var bitmap = motionRenderer.Render(motionProject, motionProject.Active, frame, 320, 180);
                    var pixels = new byte[320 * 180 * 4]; bitmap.CopyPixels(pixels, 320 * 4, 0);
                    var total = 0;
                    for (var i = 3; i < pixels.Length; i += 4) total += pixels[i];
                    return total;
                }
                var first = Alpha(10);
                var finished = Alpha(51);
                motionKeys.Keys[1].Frame = 81;
                Check(first == 0 && finished > 0 && Alpha(51) < finished && Alpha(81) == finished, "Moving a True Motion endpoint changes the actual render timing");
            }
        }
        var linked = new Layer();
        Editor.SetLayerValue(linked, "x", 0, 480, linkAxes: true);
        Editor.SetLayerValue(linked, "scaleY", 0, 125, linkAxes: true);
        Check(linked.Get("y", 0) == 270 && linked.Get("scaleX", 0) == 125, "Position and scale scrub proportionally by default");
        linked.PositionLinked = linked.ScaleLinked = false;
        Editor.SetLayerValue(linked, "y", 0, 90, linkAxes: true);
        Editor.SetLayerValue(linked, "scaleX", 0, 25, linkAxes: true);
        Check(linked.Get("x", 0) == 480 && linked.Get("scaleY", 0) == 125, "Unlinked X and Y scrub independently");
        var shapeEditor = new Editor();
        var circle = Shapes.Bounds("Circle", new(200, 200), new(100, 180), false);
        shapeEditor.DrawShape("Circle", circle);
        Check(shapeEditor.Selected is { Shape: "Circle" } drawn && drawn.Get("x", 0) == 150 && drawn.Get("width", 0) == drawn.Get("height", 0), "Reverse drag creates a circular shape at the drawn bounds");
        var alignLayer = shapeEditor.Selected!;
        alignLayer.Properties["anchorX"].Value = 20;
        alignLayer.Properties["rotation"].Value = 30;
        var originalY = alignLayer.Get("y", 0);
        System.Windows.Rect AlignBounds() => LayerGeometry.Bounds(shapeEditor.Selected!, LayerGeometry.LocalBounds(shapeEditor.Project, shapeEditor.Selected!, 0), 0);
        LayerGeometry.Align(shapeEditor, LayerAlignment.CenterX);
        var centeredX = AlignBounds();
        Check(Math.Abs(centeredX.Left + centeredX.Width / 2 - shapeEditor.Comp.Width / 2.0) < .001 && alignLayer.Get("y", 0) == originalY,
            "Horizontal alignment centers transformed bounds without editing the linked Y axis");
        LayerGeometry.Align(shapeEditor, LayerAlignment.CenterY);
        var centeredY = AlignBounds();
        Near(centeredY.Top + centeredY.Height / 2, shapeEditor.Comp.Height / 2.0, "Vertical alignment centers transformed bounds");
        LayerGeometry.Align(shapeEditor, LayerAlignment.Bottom);
        Near(AlignBounds().Bottom, shapeEditor.Comp.Height, "Bottom alignment meets the composition edge");
        shapeEditor.Undo();
        Near(AlignBounds().Top, centeredY.Top, "Alignment is undoable");
        using (var renderer = new Renderer())
        {
            var bitmap = renderer.Render(reopened, reopened.Active, 30, 320, 180); var bytes = new byte[320 * 180 * 4]; bitmap.CopyPixels(bytes, 320 * 4, 0); Check(bytes.Any(b => b != 0), "Shared renderer produces actual composite pixels");
            foreach (var mode in new[] { "Light", "Dark" })
            {
                Theme.Apply(mode); var themed = renderer.Render(reopened, reopened.Active, 30, 320, 180); var themeBytes = new byte[bytes.Length]; themed.CopyPixels(themeBytes, 320 * 4, 0); Check(themeBytes.SequenceEqual(bytes), mode + " theme does not tint rendered media");
                Check(Contrast(Theme.Color("Text"), Theme.Color("Panel")) >= 7, mode + " primary text contrast ≥ 7:1");
                Check(Contrast(Theme.Color("Muted"), Theme.Color("Panel")) >= 4.5, mode + " secondary text contrast ≥ 4.5:1");
                Check(Contrast(Theme.Color("Accent"), Theme.Color("Panel")) >= 4.5, mode + " accent text contrast ≥ 4.5:1");
                Check(Contrast(Theme.Color("OnPrimary"), Theme.Color("Primary")) >= 4.5, mode + " primary action contrast ≥ 4.5:1");
            }
            using var png = File.Create("artifacts/composite.png"); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(png);
        }
        var still = await Media.Probe(Path.GetFullPath("artifacts/composite.png")); var stillThumbnail = await Media.Thumbnail(still); Check(stillThumbnail != null && File.Exists(stillThumbnail), "Still image thumbnail does not seek past its single frame");
        var previewAudioPath = Path.GetFullPath("artifacts/preview-audio.wav"); await Exporter.PreviewAudio(reopened, previewAudioPath, CancellationToken.None); var audioProbe = await Media.Probe(previewAudioPath); Check(audioProbe.HasAudio && audioProbe.Kind == "Audio", "Shared local audio mix produces a valid PCM preview"); Near(audioProbe.Duration, 2, "Preview audio retains exact composition duration", .01);
        var progressFrames = new List<long>(); var outputPath = Path.GetFullPath("artifacts/verification.mp4");
        var progress = new ImmediateProgress<JobProgress>(p => { if (p.Stage == "Rendering") progressFrames.Add(p.Frame); });
        await Exporter.Export(reopened, new(outputPath, 320, 180), progress, CancellationToken.None);
        var exported = await Media.Probe(outputPath); Check(exported.HasAudio && exported.Width == 320 && exported.Height == 180, "Valid H.264/AAC MP4 with expected dimensions"); Near(exported.Duration, 2, "Export audio/video duration synchronized", .08); Check(progressFrames.Count == 60 && progressFrames.SequenceEqual(Enumerable.Range(1, 60).Select(i => (long)i)), "Progress counts actual rendered frames");
        var probe = await Processes.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=nb_read_frames,codec_name,color_space", "-of", "json", outputPath]); File.WriteAllText("artifacts/export-probe.json", probe); Check(probe.Contains("60") && probe.Contains("h264") && probe.Contains("bt709"), "Frame count and Rec.709 metadata verified");
        var precompEditor = new Editor(); precompEditor.Load(Data.Clone(reopened)); precompEditor.Select(video.Id); precompEditor.Select(shape.Id, true); Check(precompEditor.Execute(new("precompose")).Success, "Precomposition creates stable nested composition"); Check(precompEditor.Project.Compositions.Count == 2, "Nested composition persisted in model");
        var workProject = Data.Clone(precompEditor.Project); workProject.Active.WorkStart = 15; workProject.Active.WorkEnd = 30; var workPath = Path.GetFullPath("artifacts/work-area.mp4"); await Exporter.Export(workProject, new(workPath, 320, 180, WorkArea: true), null, CancellationToken.None); var work = await Media.Probe(workPath); Near(work.Duration, .5, "Work area exports exact frame range with nested audio", .08); Check(work.HasAudio, "Nested source audio reaches export");
        var source4k = Path.GetFullPath("artifacts/source-4k.mp4"); await Processes.Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=3840x2160:rate=30:duration=0.2", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2", "-pix_fmt", "yuv420p", source4k]);
        var asset4k = await Media.Probe(source4k); var p4k = new Project { Name = "4K verification" }; p4k.Active.Width = 3840; p4k.Active.Height = 2160; p4k.Active.Duration = p4k.Active.WorkEnd = 6; var e4k = new Editor(); e4k.Load(p4k); p4k.Assets.Add(asset4k); e4k.Execute(new("add_asset", asset4k.Id)); e4k.Execute(new("create_text")); e4k.Selected!.Text = "Kuro · 4K"; e4k.Selected.Properties["glow"].Value = 5; e4k.Execute(new("add_keyframe", e4k.Selected.Id, "opacity", 25, 0)); e4k.Execute(new("add_keyframe", e4k.Selected.Id, "opacity", 100, 5));
        var out4k = Path.GetFullPath("artifacts/verification-4k.mp4"); var t4k = Stopwatch.StartNew(); await Exporter.Export(p4k, new(out4k, 3840, 2160), null, CancellationToken.None); t4k.Stop(); var check4k = await Media.Probe(out4k); Check(check4k.Width == 3840 && check4k.Height == 2160, "4K source + animated/text composite exports 3840 × 2160");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var canceledPath = Path.GetFullPath("artifacts/canceled.mp4"); try { await Exporter.Export(reopened, new(canceledPath, 320, 180), null, canceled.Token); Check(false, "Cancellation observed"); } catch (OperationCanceledException) { Check(!File.Exists(canceledPath), "Canceled export does not publish incomplete output"); }
        Check(!Directory.GetFiles("artifacts", "*.partial.mp4").Any(), "Export temporary files cleaned on completion and cancellation");
        var trimmed = ClipRange.ForLayer(new Layer { SourceIn = .5, Duration = 6 }, source, new FrameRate(30));
        Near(trimmed.Start, .5, "Clip processing starts at the trim's source in");
        Near(trimmed.Duration, .2, "Clip processing stops at the trim's source out");
        var stabilized = await LocalTools.Stabilize(source, trimmed, null, CancellationToken.None);
        var stableClip = await Media.Probe(stabilized);
        Check(stableClip.HasAudio && stableClip.Duration < source.Duration, "Offline stabilization retains audio and processes the trim only");
        Near(stableClip.Duration, .2, "Stabilized media spans the selected trim", .05);
        if (File.Exists(BackgroundRemoval.ModelPath))
        {
            var foreground = await BackgroundRemoval.Process(source, trimmed, null, CancellationToken.None);
            var matteClip = await Media.Probe(foreground);
            var alpha = await Processes.Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt", "-of", "csv=p=0", foreground]);
            Check(alpha.Trim() == "argb" && matteClip.HasAudio && matteClip.Duration < source.Duration, "Local background removal encodes alpha and audio for the trim only");
            Near(matteClip.Duration, .2, "Foreground media spans the selected trim", .05);
            var matteStable = await LocalTools.Stabilize(matteClip, new ClipRange(0, matteClip.Duration, 0), null, CancellationToken.None);
            var matteRange = await Processes.Run("ffmpeg", ["-v", "error", "-i", matteStable, "-vf", "alphaextract,signalstats,metadata=print:file=-", "-frames:v", "1", "-an", "-f", "null", "NUL"]);
            Check(matteRange.Contains("YMIN=0") && matteRange.Contains("YMAX=255"), "Stabilizing a foreground clip preserves transparent and opaque pixels");
        }
        var reviewProject = File.Exists("examples/First light.kuro") ? Store.Load("examples/First light.kuro") : reopened;
        var oldPreferences = Preferences.Current; Preferences.Current = new Preferences { LayoutVersion = 1 };
        await VerifyChatEdit();
        await VerifyChatFillPreview();
        double previewFramesPerSecond;
        using (var worker = new PreviewWorker())
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            var frameCount = (int)Math.Min(reviewProject.Active.Duration, 60);
            var previewWatch = Stopwatch.StartNew();
            var videoPreview = worker.Play(reviewProject, 0, frameCount, 2, cancel.Token);
            await videoPreview.Ready.WaitAsync(cancel.Token);
            for (var previewFrame = 0; previewFrame < frameCount; previewFrame++)
            {
                while (videoPreview.Take(previewFrame) is not { Frame: var arrived } || arrived != previewFrame)
                {
                    if (videoPreview.Error is { } error) throw new InvalidOperationException(error);
                    await Task.Delay(3, cancel.Token);
                }
            }
            previewFramesPerSecond = frameCount / previewWatch.Elapsed.TotalSeconds;
            Check(true, $"Buffered Half-quality preview warms and delivers {frameCount} ordered video frames");
        }
        foreach (var mode in new[] { "Dark", "Light" })
        {
            Theme.Apply(mode);
            foreach (var (w, h) in new[] { (1366, 738), (1920, 1048) })
            {
                var window = new MainWindow(); window.RenderReview(reviewProject, $"artifacts/ui-{mode.ToLowerInvariant()}-{w}.png", w, h, Math.Min(90, reviewProject.Active.Duration - 1), "Title");
                Check(File.Exists($"artifacts/ui-{mode.ToLowerInvariant()}-{w}.png"), $"Native {mode} workspace rendered at {w} × {h}");
            }
        }
        Theme.Apply("Dark"); new MainWindow().RenderReview(reviewProject, "artifacts/ui-keyframes.png", 1920, 1048, Math.Min(90, reviewProject.Active.Duration - 1), "Title", true); new MainWindow().RenderReview(reviewProject, "artifacts/ui-graph.png", 1920, 1048, Math.Min(90, reviewProject.Active.Duration - 1), "Title", true, true);
        const string sampleResponse = "**Composition reviewed.** The title enters with a *soft ease* at the start.\n\n| Layer | Treatment |\n| --- | --- |\n| Title | **Opacity** ramp |\n| Landscape | *Slow push* |\n\n- Position keys are preserved.\n- Preview range: `0–8s`.";
        var renderedChat = ChatMarkdown.Create(sampleResponse).Document;
        var intro = (System.Windows.Documents.Paragraph)renderedChat.Blocks.FirstBlock;
        Check(intro.Inlines.OfType<System.Windows.Documents.Bold>().Any() && intro.Inlines.OfType<System.Windows.Documents.Italic>().Any() && renderedChat.Blocks.OfType<System.Windows.Documents.Table>().Single().RowGroups[0].Rows.Count == 3, "Chat renders bold, italic and Markdown tables as native document elements");
        var sidebarWindow = new MainWindow();
        sidebarWindow.RenderReview(reviewProject, "artifacts/ui-sidebar.png", 1366, 738, 50, "Title", sidebarView: true, chatMessage: sampleResponse);
        var sidebar = (System.Windows.Controls.Border)sidebarWindow.FindName("AISidebar");
        var workspace = (System.Windows.Controls.Grid)sidebarWindow.FindName("WorkspaceGrid");
        Check(sidebar.ActualWidth >= 286 && sidebar.ActualHeight > workspace.ActualHeight, "AI sidebar extends beside both the viewer and timeline");
        var compactWindow = new MainWindow();
        compactWindow.RenderReview(reviewProject, "artifacts/ui-sidebar-compact.png", 1050, 660, 50, "Title", sidebarView: true);
        var compactViewer = (System.Windows.Controls.Grid)compactWindow.FindName("Viewer");
        Check(compactViewer.ActualWidth >= 290 && compactWindow.FindName("InspectorBody") is System.Windows.FrameworkElement { ActualWidth: > 0 }, "Sidebar layout retains viewer and Properties widths at 1050 pixels");
        Preferences.Current.Recent.Add(Path.GetFullPath("examples/First light.kuro"));
        var welcomeWindow = new MainWindow();
        welcomeWindow.RenderReview(reviewProject, "artifacts/ui-welcome.png", 1366, 738, 50, null, welcomeView: true);
        Check(welcomeWindow.FindName("WelcomeRecent") is System.Windows.Controls.StackPanel recent && recent.Children.OfType<System.Windows.Controls.Button>().Any(), "Welcome displays an openable recent project");
        VerifyPropertyReveals();
        VerifyCompositionPopup();
        Preferences.Current = oldPreferences;
        Theme.Apply("Dark"); context.Style = (System.Windows.Style)System.Windows.Application.Current.FindResource(typeof(System.Windows.Controls.ContextMenu)); context.ApplyTemplate(); context.Measure(new System.Windows.Size(300, 600)); context.Arrange(new System.Windows.Rect(new System.Windows.Point(), context.DesiredSize)); context.UpdateLayout();
        if (context.ActualWidth > 0 && context.ActualHeight > 0) { var menuBitmap = new RenderTargetBitmap((int)Math.Ceiling(context.ActualWidth), (int)Math.Ceiling(context.ActualHeight), 96, 96, PixelFormats.Pbgra32); menuBitmap.Render(context); using var menuFile = File.Create("artifacts/keyframe-menu.png"); var menuPng = new PngBitmapEncoder(); menuPng.Frames.Add(BitmapFrame.Create(menuBitmap)); menuPng.Save(menuFile); }
        Check(context.Template != null && context.ItemContainerStyleSelector != null, "Context menu uses Kuro's custom menu templates");
        var machine = new { os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount, workingSetMb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024, totalSeconds = watch.Elapsed.TotalSeconds, export4kSixFramesSeconds = t4k.Elapsed.TotalSeconds, halfQualityPreviewFramesPerSecond = previewFramesPerSecond, note = "Short sample composition. Preview throughput includes preroll and excludes audio, display, and long-form 4K performance. No outbound media-processing APIs exist in Engine." };
        Store.AtomicWrite(Path.GetFullPath("artifacts/verification-report.json"), JsonSerializer.Serialize(new { results, machine }, Data.Json));
    }

    static async Task VerifyChatEdit()
    {
        var editor = new Editor();
        editor.Execute(new("create_text"));
        var layer = editor.Selected!;
        layer.Duration = 450;
        editor.Playhead = 76;
        var agent = new ChatAgent();
        var adapter = new ScriptedChatProvider(layer.Id[..8], editor.Project.Revision);
        await agent.Run(editor, adapter, "Keep only the first five seconds of this layer.", (_, _) => { }, CancellationToken.None);
        Check(editor.Comp.Layers.Single().Duration == 150 && adapter.SawTarget, "AI trims with the state from the first provider request");
        editor.Undo();
        var wrong = JsonSerializer.SerializeToElement(new { name = "trim_end", target = "ffffffff", frame = 150, expectedRevision = editor.Project.Revision });
        Check(!ChatAgent.ExecuteTool(editor, wrong, editor.Project.Revision).Success && editor.Comp.Layers.Single().Duration == 450, "AI rejects an unknown explicit target without changing the selection");
        var selected = JsonSerializer.SerializeToElement(new { name = "split_clip", expectedRevision = editor.Project.Revision });
        Check(ChatAgent.ExecuteTool(editor, selected, editor.Project.Revision).Success && editor.Comp.Layers.Any(l => l.Start == 76), "AI split uses the current playhead when frame is omitted");
    }

    sealed class ScriptedChatProvider(string target, long revision) : IChatProvider
    {
        int step;
        public bool SawTarget { get; private set; }
        public Task<List<string>> Discover(CancellationToken token) => Task.FromResult<List<string>>([]);
        public Task<JsonElement> Complete(List<object> messages, object[] tools, CancellationToken token, bool requireTool = false)
        {
            var stage = step++;
            if (stage == 1) return Task.FromResult(JsonSerializer.SerializeToElement(new { role = "assistant", content = "Trimmed to five seconds." }));
            SawTarget = JsonSerializer.Serialize(messages).Contains(target, StringComparison.Ordinal);
            const string name = "editor_command";
            var arguments = JsonSerializer.Serialize(new { name = "trim_end", target = target + "…", frame = 150, expectedRevision = revision });
            return Task.FromResult(JsonSerializer.SerializeToElement(new { role = "assistant", tool_calls = new[] { new { id = $"tool-{stage}", type = "function", function = new { name, arguments } } } }));
        }
    }

    static async Task VerifyChatFillPreview()
    {
        var editor = new Editor();
        editor.Comp.Width = 320;
        editor.Comp.Height = 180;
        editor.Execute(new("create_shape"));
        var target = editor.Selected!.Id;
        var revision = editor.Project.Revision;
        CommandResult SetFill(string? text)
        {
            var arguments = text == null
                ? JsonSerializer.SerializeToElement(new { name = "set_fill", target, expectedRevision = revision })
                : JsonSerializer.SerializeToElement(new { name = "set_fill", target, text, expectedRevision = revision });
            return ChatAgent.ExecuteTool(editor, arguments, revision);
        }

        using var worker = new PreviewWorker();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var before = worker.Play(Data.Clone(editor.Project), 0, 30, 1, timeout.Token);
        await before.Ready.WaitAsync(timeout.Token);
        var rejected = new[] { SetFill(null), SetFill(" "), SetFill("#nope") };
        Check(rejected.All(result => !result.Success) && editor.Project.Revision == revision && editor.Selected!.Fill == ProjectColors.DefaultShapeFill,
            "AI rejects missing, blank and invalid fill values without changing the layer");
        Check(SetFill("#FF4D2E").Success, "AI accepts a valid fill color");
        var after = worker.Play(Data.Clone(editor.Project), 0, 30, 1, timeout.Token);
        await after.Ready.WaitAsync(timeout.Token);
        var updated = after.Take(0)!.Value.Image;
        var pixel = new byte[4];
        updated.CopyPixels(new System.Windows.Int32Rect(160, 90, 1, 1), pixel, 4, 0);
        Check(pixel.SequenceEqual(new byte[] { 0x2E, 0x4D, 0xFF, 0xFF }), "Playback restarts with the new fill after an AI edit");

        var legacy = Data.Clone(editor.Project);
        legacy.Active.Layers.Single().Fill = null!;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(legacy, Data.Json));
            Check(Store.Load(path).Active.Layers.Single().Fill == ProjectColors.DefaultShapeFill, "Projects with a legacy null fill reopen with the default shape color");
        }
        finally { File.Delete(path); }
        var recovered = worker.Play(legacy, 0, 30, 1, timeout.Token);
        await recovered.Ready.WaitAsync(timeout.Token);
        Check(recovered.Take(0)?.Image != null, "Preview can render an in-memory layer with a legacy null fill");
    }

    static void VerifyPropertyReveals()
    {
        var editor = new Editor();
        editor.Execute(new("create_text"));
        var layer = editor.Selected!;
        var timeline = new Timeline(editor);
        foreach (var (group, properties) in new (string, string[])[] { ("Scale", ["scaleX", "scaleY"]), ("Position", ["x", "y"]), ("Rotation", ["rotation"]), ("Opacity", ["opacity"]) })
        {
            timeline.ToggleProperties(layer, group);
            Check(timeline.PropertiesFor(layer).SequenceEqual(properties), $"{group} reveal shows only its matching rows");
        }
        editor.Playhead = 10;
        Check(editor.ToggleKeyframe(layer, "opacity").Success && layer.Properties["opacity"].Keys.Single().Frame == 10, "Property diamond creates a keyframe at the playhead");
        timeline.ToggleProperties(layer, "Animated");
        Check(timeline.PropertiesFor(layer).SequenceEqual(new[] { "opacity" }) && timeline.Expanded.Contains(layer.Id), "Animated reveal shows only keyed properties");
        timeline.ToggleProperties(layer, "Animated");
        Check(!timeline.Expanded.Contains(layer.Id), "Animated reveal toggles off");
        var horizontal = new PropertyScrub(new System.Windows.Point(0, 0), 40, "x");
        var vertical = new PropertyScrub(new System.Windows.Point(0, 0), 40, "x");
        Check(horizontal.Update(new System.Windows.Point(9, 0)) && vertical.Update(new System.Windows.Point(0, -9)) && horizontal.Value == vertical.Value, "Numeric fields scrub horizontally and vertically");
    }

    static void VerifyCompositionPopup()
    {
        var composition = new Composition();
        System.Windows.Application.Current.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        (int Width, int Height, FrameRate Rate, long Duration)? ApplySettings(Action<CompositionSettingsDialog> edit)
        {
            (int Width, int Height, FrameRate Rate, long Duration)? applied = null;
            var popup = new CompositionSettingsDialog(null, composition, (_, width, height, rate, duration, _) => applied = (width, height, rate, duration))
            {
                ShowActivated = false,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -2000,
                Top = -2000
            };
            Exception? setupError = null;
            popup.Loaded += (_, _) =>
            {
                try { edit(popup); }
                catch (Exception ex) { setupError = ex; }
                if (applied == null) popup.Close();
            };
            var accepted = popup.ShowDialog();
            if (setupError != null) throw setupError;
            return accepted == true ? applied : null;
        }

        static void Apply(CompositionSettingsDialog popup) =>
            popup.ApplyButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var preset = ApplySettings(popup =>
        {
            Check(popup.DurationField.Text == "00:00:15:000", "Composition duration defaults to hours, minutes, seconds and milliseconds");
            var resolution = popup.Resolution;
            resolution.ApplyTemplate();
            Check(resolution.Template.FindName("PART_Popup", resolution) is System.Windows.Controls.Primitives.Popup, "Resolution selector has the app dropdown template");
            resolution.SelectedIndex = 4;
            popup.Aspect.SelectedIndex = 2;
            popup.FrameRates.SelectedIndex = 3;
            Check(popup.DurationField.Text == "00:00:15:015", "Fractional FPS updates the displayed duration to the nearest millisecond");
            Apply(popup);
        });
        Check(preset == (2160, 3840, new FrameRate(30000, 1001), composition.Duration), "Composition popup applies portrait dimensions and fractional FPS while preserving frame count");

        var timed = ApplySettings(popup =>
        {
            popup.DurationField.Text = "00:01:60:000";
            Apply(popup);
            Check(popup.Error.Visibility == System.Windows.Visibility.Visible, "Composition duration rejects an out-of-range seconds field");
            popup.DurationField.Text = "01:02:03:500";
            Apply(popup);
        });
        Check(timed?.Duration == 111705, "Composition popup converts a duration with hours and milliseconds into frames");
    }
    static double Contrast(Color a, Color b)
    {
        static double L(Color c) { static double Linear(byte v) { var s = v / 255.0; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); } return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B); }
        var x = L(a); var y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
