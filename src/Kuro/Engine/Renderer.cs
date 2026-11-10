using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Kuro.Core;

namespace Kuro.Engine;
// One evaluator is used for viewer frames and final output; no UI controls are read here.
public sealed class Renderer(bool preview = false) : IDisposable
{
    internal bool HardwareDecode { get; set; }
    internal bool Scrubbing { get; set; }
    readonly Dictionary<string, Decoder> decoders = [];
    readonly Dictionary<string, BitmapSource> images = [];
    public BitmapSource Render(Project project, Composition composition, long frame, int width, int height, CancellationToken token = default, int depth = 0)
    {
        if (depth > 12) throw new InvalidOperationException("Composition nesting exceeds the supported depth of 12.");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brush(composition.Background, ProjectColors.DefaultBackground), null, new Rect(0, 0, width, height));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            dc.PushTransform(new ScaleTransform((double)width / composition.Width, (double)height / composition.Height));
            var solo = composition.Layers.Any(l => l.Solo);
            foreach (var l in composition.Layers.AsEnumerable().Reverse())
            {
                token.ThrowIfCancellationRequested();
                if (!l.Visible || solo && !l.Solo || l.Kind == "Audio" || frame < l.Start || frame >= l.End) continue;
                var opacity = Math.Clamp(l.Get("opacity", frame) / 100, 0, 1); if (opacity == 0) continue;
                var matrix = LayerGeometry.Transform(l, frame);
                dc.PushOpacity(opacity); dc.PushTransform(new MatrixTransform(matrix));
                if (l.Kind is "Video" or "Image")
                {
                    var a = project.Assets.Find(a => a.Id == l.AssetId);
                    if (a == null || a.Missing) throw new InvalidOperationException($"Missing media: {l.Name}. Relink it in the Project panel.");
                    BitmapSource image;
                    if (l.Kind == "Image") { if (!images.TryGetValue(a.Path, out image!)) { image = Media.LoadBitmap(a.Path); if (images.Count >= 12) images.Clear(); images[a.Path] = image; } }
                    else
                    {
                        if (!decoders.TryGetValue(l.Id, out var decoder)) { if (decoders.Count >= 8) { var first = decoders.First(); first.Value.Dispose(); decoders.Remove(first.Key); } decoders[l.Id] = decoder = new Decoder(); }
                        var factor = Math.Min(1.0, Math.Max((double)width / composition.Width, (double)height / composition.Height));
                        if (preview && l.Properties["scaleX"].Keys.Count == 0 && l.Properties["scaleY"].Keys.Count == 0)
                        {
                            var scale = Math.Max(Math.Abs(l.Properties["scaleX"].Value), Math.Abs(l.Properties["scaleY"].Value)) / 100;
                            factor *= Math.Clamp(scale, .04, 1);
                        }
                        var w = Math.Max(2, (int)Math.Round(a.Width * factor)); var h = Math.Max(2, (int)Math.Round(a.Height * factor));
                        var samplingFps = l.Rate > 0 ? composition.Fps.Value / l.Rate : composition.Fps.Value;
                        image = decoder.Read(a, l.SourceTime(frame, composition.Fps), w, h, samplingFps, token, preview, HardwareDecode, Scrubbing);
                    }
                    dc.DrawImage(image, new Rect(-a.Width / 2.0, -a.Height / 2.0, a.Width, a.Height));
                }
                else if (l.Kind == "Composition")
                {
                    var child = project.Compositions.Find(c => c.Id == l.CompositionId);
                    if (child != null) { var nested = Render(project, child, child.Fps.Frames(l.SourceTime(frame, composition.Fps)), Math.Max(2, (int)(child.Width * (double)width / composition.Width)), Math.Max(2, (int)(child.Height * (double)height / composition.Height)), token, depth + 1); dc.DrawImage(nested, new Rect(-child.Width / 2.0, -child.Height / 2.0, child.Width, child.Height)); }
                }
                else
                {
                    var drawing = new DrawingGroup(); using (var content = drawing.Open())
                    {
                        if (l.Kind == "Text") DrawText(content, l, composition, frame);
                        if (l.Kind == "Shape")
                        {
                            var w = Math.Max(0, l.Get("width", frame)); var h = Math.Max(0, l.Get("height", frame)); var rect = new Rect(-w / 2, -h / 2, w, h);
                            content.DrawGeometry(Brush(l.Fill, ProjectColors.DefaultShapeFill), null, Shapes.Geometry(l.Shape, rect, l.Get("radius", frame)));
                        }
                    }
                    var blur = Math.Clamp(l.Get("blur", frame), 0, 100); var glow = Math.Clamp(l.Get("glow", frame), 0, 100); var shadow = Math.Clamp(l.Get("shadow", frame), 0, 100);
                    if (glow > 0) DrawEffect(dc, drawing, new BlurEffect { Radius = glow }, .65, 0, 0);
                    if (shadow > 0) DrawEffect(dc, drawing, new DropShadowEffect { Color = Colors.Black, BlurRadius = shadow, ShadowDepth = shadow / 2, Opacity = .7 }, 1, 0, 0);
                    if (blur > 0) DrawEffect(dc, drawing, new BlurEffect { Radius = blur }, 1, 0, 0); else dc.DrawDrawing(drawing);
                }
                dc.Pop(); dc.Pop();
            }
            dc.Pop(); dc.Pop();
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); target.Render(visual); target.Freeze(); return target;
    }
    static void DrawEffect(DrawingContext dc, Drawing drawing, Effect effect, double opacity, double x, double y, int pad = 220)
    {
        var bounds = drawing.Bounds; if (bounds.IsEmpty) return;
        var w = Math.Clamp((int)Math.Ceiling(bounds.Width) + pad * 2, 1, 8192); var h = Math.Clamp((int)Math.Ceiling(bounds.Height) + pad * 2, 1, 8192);
        var element = new DrawingElement(drawing, -bounds.X + pad, -bounds.Y + pad) { Effect = effect, Width = w, Height = h };
        element.Measure(new Size(w, h)); element.Arrange(new Rect(0, 0, w, h));
        var b = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); b.Render(element); b.Freeze();
        dc.PushOpacity(opacity); dc.DrawImage(b, new Rect(bounds.X - pad + x, bounds.Y - pad + y, w, h)); dc.Pop();
    }
    sealed class DrawingElement(Drawing drawing, double x, double y) : FrameworkElement
    { protected override void OnRender(DrawingContext dc) { dc.PushTransform(new TranslateTransform(x, y)); dc.DrawDrawing(drawing); dc.Pop(); } }
    static void DrawText(DrawingContext dc, Layer l, Composition c, long frame)
    {
        var fontSize = Math.Clamp(l.Get("fontSize", frame), 1, 1200); var typeface = new Typeface(new FontFamily(l.Font), FontStyles.Normal, FontWeight.FromOpenTypeWeight(l.FontWeight), FontStretches.Normal);
        var brush = Brush(l.Fill);
        var text = LayerGeometry.Text(l, frame, brush);
        var motion = Presets.Motion(l.Preset);
        if (motion == null)
        {
            dc.DrawText(text, new Point(-text.Width / 2, -text.Height / 2));
            return;
        }
        var origin = l.Properties.TryGetValue("presetOrigin", out var o) ? o.Value : l.Start;
        var phase = Math.Max(0, (frame - origin) / c.Fps.Value);
        var value = l.Properties.TryGetValue(TextMotion.Property, out var timing)
            ? timing.At(frame)
            : motion.Animation.At(phase / Math.Max(.05, l.PresetDuration) * motion.Duration * TextMotion.TicksPerSecond);
        var animatedOpacity = motion.Channel == "Opacity";
        if ((animatedOpacity || motion.Channel is "Percent Offset" or "Percent Start") && value >= 100)
        {
            dc.DrawText(text, new Point(-text.Width / 2, -text.Height / 2));
            return;
        }
        var lines = l.Text.Replace("\r\n", "\n").Split('\n');
        var units = lines.Select(line => motion.Unit == 3
            ? System.Text.RegularExpressions.Regex.Matches(line, @"\S+|\s+").Select(m => m.Value).ToArray()
            : StringInfo.ParseCombiningCharacters(line).Select(start => StringInfo.GetNextTextElement(line, start)).ToArray()).ToArray();
        var count = units.Sum(line => line.Count(unit => motion.Unit == 1 || !string.IsNullOrWhiteSpace(unit)));
        if (count == 0) return;
        var index = 0;
        for (var line = 0; line < lines.Length; line++)
        {
            var measured = new FormattedText(lines[line], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, brush, 1);
            var x = -measured.WidthIncludingTrailingWhitespace / 2;
            var y = -text.Height / 2 + line * text.Height / lines.Length;
            foreach (var word in units[line])
            {
                var unit = new FormattedText(word, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, brush, 1);
                var advance = unit.WidthIncludingTrailingWhitespace;
                if (motion.Unit == 1 || !string.IsNullOrWhiteSpace(word))
                {
                    var weight = motion.Weight((index + .5) * 100 / count, 100.0 / count, value);
                    if (motion.Wiggle is { } wiggle) weight = wiggle.Combine(weight, phase, index, l.Seed);
                    var alpha = Math.Clamp(1 - weight * (1 - (animatedOpacity ? value : motion.Opacity) / 100), 0, 1);
                    if (alpha > .001)
                    {
                        var center = new Point(x + advance / 2, y + unit.Height / 2);
                        dc.PushTransform(new TranslateTransform(weight * motion.X, weight * motion.Y));
                        dc.PushTransform(new ScaleTransform(1 + weight * (motion.ScaleX / 100 - 1), 1 + weight * (motion.ScaleY / 100 - 1), center.X, center.Y));
                        dc.PushOpacity(alpha);
                        var blur = weight * motion.Blur;
                        if (blur >= 1 && !string.IsNullOrWhiteSpace(word))
                        {
                            var glyph = new DrawingGroup();
                            using (var glyphContext = glyph.Open()) glyphContext.DrawText(unit, new Point(x, y));
                            DrawEffect(dc, glyph, new BlurEffect { Radius = blur }, 1, 0, 0, (int)Math.Ceiling(blur * 2));
                        }
                        else dc.DrawText(unit, new Point(x, y));
                        dc.Pop(); dc.Pop(); dc.Pop();
                    }
                    advance += motion.Tracking * fontSize / 1000 * weight;
                    index++;
                }
                x += advance;
            }
        }
    }
    public static SolidColorBrush Brush(string? color, string fallback = ProjectColors.DefaultFill)
    {
        var brush = new SolidColorBrush(ProjectColors.TryParse(color, out var parsed) ? parsed : (Color)ColorConverter.ConvertFromString(fallback));
        brush.Freeze();
        return brush;
    }
    public void Dispose() { foreach (var d in decoders.Values) d.Dispose(); decoders.Clear(); images.Clear(); }
}
public record FrameResult(long Frame, BitmapSource? Image, string? Error, long Request);
public sealed class PreviewWorker : IDisposable
{
    record Work(Project Project, long Frame, int Divisor, long Request, CancellationTokenSource Cancel,
        PlaybackBuffer? Playback = null, long End = 0, bool Scrubbing = false, bool Hardware = false);
    readonly BlockingCollection<Work> queue = new(1);
    readonly Thread thread; CancellationTokenSource? active; long serial; bool disposed;
    public event Action<FrameResult>? Ready;
    public PreviewWorker()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "Kuro · preview compositor" }; thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public long Request(Project p, long frame, int divisor, bool interrupt = true, bool scrubbing = false, bool hardware = false)
    {
        if (disposed) return 0;
        var request = Interlocked.Increment(ref serial);
        if (interrupt) CancelActive();
        Enqueue(new(p, frame, divisor, request, new(), Scrubbing: scrubbing, Hardware: hardware));
        return request;
    }
    public PlaybackBuffer Play(Project project, long start, long end, int divisor, CancellationToken token, bool hardware = false)
    {
        var c = project.Active;
        var frameBytes = (long)Math.Max(2, c.Width / divisor) * Math.Max(2, c.Height / divisor) * 4;
        var capacity = (int)Math.Clamp((40L * 1024 * 1024) / frameBytes, 2, 10);
        var buffer = new PlaybackBuffer(capacity, (int)Math.Min(Math.Min(capacity, 5), end - start), start);
        CancelActive();
        Enqueue(new(project, start, divisor, Interlocked.Increment(ref serial), CancellationTokenSource.CreateLinkedTokenSource(token), buffer, end, Hardware: hardware));
        return buffer;
    }
    void Enqueue(Work work)
    {
        while (queue.TryTake(out var old)) { old.Playback?.Cancel(); old.Cancel.Dispose(); }
        if (!queue.TryAdd(work)) { work.Playback?.Cancel(); work.Cancel.Dispose(); }
    }
    void CancelActive()
    {
        try { active?.Cancel(); } catch (ObjectDisposedException) { }
    }
    void Loop()
    {
        using var renderer = new Renderer(preview: true);
        foreach (var w in queue.GetConsumingEnumerable())
        {
            active = w.Cancel;
            try
            {
                var c = w.Project.Active;
                renderer.HardwareDecode = w.Hardware;
                renderer.Scrubbing = w.Scrubbing;
                BitmapSource Render(long frame) => renderer.Render(w.Project, c, frame, Math.Max(2, c.Width / w.Divisor), Math.Max(2, c.Height / w.Divisor), w.Cancel.Token);
                if (w.Playback is { } buffer)
                {
                    for (var frame = w.Frame; frame < w.End; frame++)
                    {
                        buffer.WaitForSpace(w.Cancel.Token);
                        if (buffer.WantedFrame > frame + 2) frame = Math.Min(w.End - 1, buffer.WantedFrame);
                        buffer.Add(frame, Render(frame));
                    }
                }
                else
                {
                    var result = Render(w.Frame);
                    if ((w.Scrubbing || w.Request == Interlocked.Read(ref serial)) && !w.Cancel.IsCancellationRequested) Ready?.Invoke(new(w.Frame, result, null, w.Request));
                }
            }
            catch (Exception ex)
            {
                if (w.Cancel.IsCancellationRequested) w.Playback?.Cancel();
                else if (w.Playback is { } buffer) buffer.Fail(ex);
                else if (w.Request == Interlocked.Read(ref serial)) Ready?.Invoke(new(w.Frame, null, ex.Message, w.Request));
            }
            finally { active = null; w.Cancel.Dispose(); }
        }
    }
    public void Dispose() { disposed = true; CancelActive(); queue.CompleteAdding(); }
}

public sealed class PlaybackBuffer(int capacity, int preroll, long start)
{
    readonly object gate = new();
    readonly Queue<(long Frame, BitmapSource Image)> frames = new();
    readonly TaskCompletionSource warm = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long wanted = start;
    public Task Ready => warm.Task;
    public string? Error { get; private set; }
    public long WantedFrame => Interlocked.Read(ref wanted);

    internal void WaitForSpace(CancellationToken token)
    {
        lock (gate)
        {
            while (frames.Count >= capacity && !token.IsCancellationRequested) Monitor.Wait(gate, 30);
            token.ThrowIfCancellationRequested();
        }
    }
    internal void Add(long frame, BitmapSource bitmap)
    {
        lock (gate)
        {
            frames.Enqueue((frame, bitmap));
            if (frames.Count >= preroll) warm.TrySetResult();
        }
    }
    public (long Frame, BitmapSource Image)? Take(long frame)
    {
        Interlocked.Exchange(ref wanted, frame);
        lock (gate)
        {
            (long Frame, BitmapSource Image)? latest = null;
            while (frames.TryPeek(out var item) && item.Frame <= frame) latest = frames.Dequeue();
            if (latest.HasValue) Monitor.Pulse(gate);
            return latest;
        }
    }
    internal void Fail(Exception ex) { Error = ex.Message; warm.TrySetException(ex); }
    internal void Cancel() => warm.TrySetCanceled();
}
