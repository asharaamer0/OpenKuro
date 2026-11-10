using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Kuro.Core;

namespace Kuro.UI;
public sealed class Timeline : ScrollViewer
{
    const double Header = 280, Ruler = 36, LayerHeight = 32, PropertyHeight = 22;
    static readonly string[] Transforms = ["anchorX", "anchorY", "x", "y", "scaleX", "scaleY", "rotation", "opacity"];
    readonly Surface surface;
    readonly Dictionary<string, string> propertyViews = [];
    public double PixelsPerFrame { get; set; } = 2.4;
    public bool Snap { get; set; } = true;
    public HashSet<string> Expanded { get; } = [];
    public string Property { get; set; } = "x";
    public event Action<long, bool>? Seek;
    public event Action? SelectionUpdated;
    public event Action<string>? GraphRequested;
    public event Action<string, string, double>? PropertyPreview;
    public event Action? CancelPropertyPreview;
    public event Action<Layer, bool>? ClipProcessingRequested;
    public Dictionary<string, ImageSource> Thumbnails { get; } = [];
    public Dictionary<string, float[]> Waveforms { get; } = [];
    public Editor Editor { get; }
    public string[] PropertiesFor(Layer layer)
    {
        var animated = layer.Properties.Where(p => p.Value.Keys.Count > 0).Select(p => p.Key);
        string[] properties = propertyViews.GetValueOrDefault(layer.Id) switch
        {
            "Position" => ["x", "y"], "Scale" => ["scaleX", "scaleY"], "Rotation" => ["rotation"],
            "Opacity" => ["opacity"], "Anchor" => ["anchorX", "anchorY"], "Animated" => animated.ToArray(),
            _ => Transforms.Concat(animated).Distinct().ToArray()
        };
        return properties.Where(layer.Properties.ContainsKey).ToArray();
    }
    public void ToggleProperties(Layer layer, string view)
    {
        if (Expanded.Contains(layer.Id) && propertyViews.GetValueOrDefault(layer.Id) == view) { Expanded.Remove(layer.Id); return; }
        propertyViews[layer.Id] = view;
        Expanded.Add(layer.Id);
        if (PropertiesFor(layer).FirstOrDefault() is { } property) Property = property;
    }
    public void ShowProperty(Layer layer, string property)
    {
        if (!Expanded.Contains(layer.Id) || !PropertiesFor(layer).Contains(property))
        {
            propertyViews[layer.Id] = "Animated";
            Expanded.Add(layer.Id);
        }
        Property = property;
        Refresh();
    }
    public void ResetLayerViews()
    {
        Expanded.Clear();
        propertyViews.Clear();
        Property = "x";
        ScrollToHome();
    }
    public double RowHeight(Layer layer) => LayerHeight + (Expanded.Contains(layer.Id) ? Math.Max(1, PropertiesFor(layer).Length) * PropertyHeight : 0);
    public Timeline(Editor editor)
    {
        Editor = editor; surface = new Surface(this); Content = surface;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        CanContentScroll = false; Focusable = true; AllowDrop = true;
        ScrollChanged += (_, _) => surface.Refresh();
    }
    public void Refresh() { surface.Width = Math.Max(ViewportWidth, Header + Editor.Comp.Duration * PixelsPerFrame + 24); surface.Height = Math.Max(ViewportHeight, Ruler + 8 + Editor.Comp.Layers.Sum(RowHeight)); surface.Refresh(); }
    public void CursorOnly() => surface.DrawCursor();
    public void Fit() { PixelsPerFrame = Math.Max(.08, (Math.Max(400, ActualWidth) - Header - 48) / Editor.Comp.Duration); ScrollToHorizontalOffset(0); Refresh(); }
    public void Reveal(string id) { var y = surface.Rows().FirstOrDefault(r => r.Layer.Id == id).Y; ScrollToVerticalOffset(Math.Max(0, y - Ruler)); }
    sealed class Surface : FrameworkElement
    {
        readonly Timeline owner; readonly VisualCollection visuals; readonly DrawingVisual backdrop = new(), cursor = new();
        const double SwitchWidth = 21, SwitchStart = 25, TypeStart = 117, NameStart = 140;
        const double KeyStart = 30, PropertyStart = 54, LinkStart = 176, ValueStart = 200, ValueEnd = 268;
        static readonly string[] switchCommands = ["toggle_visibility", "toggle_lock", "toggle_audio", "toggle_solo"];
        Layer? dragLayer; string dragMode = ""; Point down; long original, proposed; int proposedIndex; string? dragProperty; Keyframe? dragKey; bool dragging;
        (long Frame, int Index)? drop;
        Rect? marquee; HashSet<string> marqueeBase = [];
        PropertyScrub? valueScrub;
        Popup? numericPopup;
        (string? Id, int Cell) hover;
        public Surface(Timeline timeline)
        {
            owner = timeline; visuals = new(this) { backdrop, cursor }; Focusable = true;
            MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up; MouseRightButtonDown += Context;
            MouseLeave += (_, _) => SetHover((null, -1));
            ToolTip = new ToolTip();
            ToolTipService.SetInitialShowDelay(this, 500);
            ToolTipOpening += (_, e) =>
            {
                var label = TooltipAt(Mouse.GetPosition(this));
                if (label == null) e.Handled = true;
                else ((ToolTip)ToolTip).Content = label;
            };
            LostMouseCapture += (_, _) =>
            {
                if (dragMode == "scrub")
                {
                    dragMode = "";
                    owner.Seek?.Invoke(E.Playhead, false);
                    return;
                }
                if (valueScrub == null) return;
                var moved = valueScrub.HasMoved;
                valueScrub = null; dragLayer = null; dragMode = "";
                if (moved) owner.CancelPropertyPreview?.Invoke();
                Refresh();
            };
            DragOver += Drag; DragLeave += (_, _) => { drop = null; Refresh(); }; Drop += Dropped;
        }
        protected override int VisualChildrenCount => visuals.Count;
        protected override Visual GetVisualChild(int index) => visuals[index];
        Editor E => owner.Editor;
        public IEnumerable<(Layer Layer, double Y)> Rows() { double y = Ruler; foreach (var l in E.Comp.Layers) { yield return (l, y); y += owner.RowHeight(l); } }
        (Layer? Layer, double Y) RowAt(double y) => y < owner.VerticalOffset + Ruler
            ? default
            : Rows().FirstOrDefault(r => y >= r.Y && y < r.Y + owner.RowHeight(r.Layer));
        string? PropertyAt(Layer layer, double localY)
        {
            if (localY < LayerHeight || !owner.Expanded.Contains(layer.Id)) return null;
            var properties = owner.PropertiesFor(layer);
            return properties.ElementAtOrDefault((int)((localY - LayerHeight) / PropertyHeight));
        }
        static double KeyY(double rowY, int propertyIndex) => rowY + LayerHeight + (propertyIndex + .5) * PropertyHeight;
        int LayerIndexAt(double y) => Rows().TakeWhile(row => y >= row.Y + owner.RowHeight(row.Layer)).Count();
        double X(long frame) => Header + frame * owner.PixelsPerFrame;
        long Frame(double x) => Math.Clamp((long)Math.Round((x - Header) / owner.PixelsPerFrame), 0, E.Comp.Duration - 1);
        static void Text(DrawingContext dc, string text, double x, double y, Brush color, double size = 11, bool bold = false, double width = 0, TextAlignment alignment = TextAlignment.Left)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, color, 1);
            if (width > 0)
            {
                formatted.MaxTextWidth = width;
                formatted.MaxLineCount = 1;
                formatted.Trimming = TextTrimming.CharacterEllipsis;
                formatted.TextAlignment = alignment;
            }
            dc.DrawText(formatted, new Point(x, y));
        }

        public void Refresh()
        {
            var left = owner.HorizontalOffset;
            var top = owner.VerticalOffset;
            var right = Math.Min(Width, left + Math.Max(Header, owner.ViewportWidth));
            var bottom = Math.Min(Height, top + Math.Max(Ruler, owner.ViewportHeight));
            var split = left + Header;
            var panel = Theme.Get("Panel");
            var border = new Pen(Theme.Get("Border"), .6);
            var accent = Theme.Get("Accent");
            var rows = Rows().TakeWhile(r => r.Y < bottom).Where(r => r.Y + owner.RowHeight(r.Layer) > top + Ruler).ToArray();
            using (var dc = backdrop.RenderOpen())
            {
                dc.DrawRectangle(Theme.Get("Workspace"), null, new Rect(0, 0, Math.Max(1, Width), Math.Max(1, Height)));
                var spacing = E.Comp.Fps.Value;
                while (spacing * owner.PixelsPerFrame < 70) spacing *= 2;
                while (spacing * owner.PixelsPerFrame > 180 && spacing > 1) spacing /= 2;

                dc.PushClip(new RectangleGeometry(new Rect(split, top + Ruler, Math.Max(0, right - split), Math.Max(0, bottom - top - Ruler))));
                var first = Math.Max(0, Math.Floor((split - Header) / owner.PixelsPerFrame / spacing) * spacing);
                for (var frame = first; frame < E.Comp.Duration; frame += spacing)
                {
                    var x = X((long)frame);
                    if (x > right) break;
                    dc.DrawLine(border, new Point(x, top + Ruler), new Point(x, bottom));
                }
                foreach (var (layer, y) in rows)
                {
                    DrawClip(dc, layer, y, right, split);
                    dc.DrawLine(border, new Point(split, y + LayerHeight), new Point(right, y + LayerHeight));
                    if (!owner.Expanded.Contains(layer.Id)) continue;
                    var properties = owner.PropertiesFor(layer);
                    for (var i = 0; i < properties.Length; i++)
                    {
                        var keyY = KeyY(y, i);
                        dc.DrawLine(border, new Point(split, keyY + PropertyHeight / 2), new Point(right, keyY + PropertyHeight / 2));
                        foreach (var key in layer.Properties[properties[i]].Keys)
                        {
                            var selected = E.SelectedKeys.Contains(key.Id);
                            var frame = key.Frame + (dragMode == "key" && dragging && selected ? proposed - original : 0);
                            var x = X(frame);
                            if (x >= split - 5 && x <= right + 5) Diamond(dc, x, keyY, selected ? accent : panel, selected ? accent : Theme.Get("Muted"));
                        }
                    }
                }
                if (drop is { } d)
                {
                    var y = Ruler + E.Comp.Layers.Take(d.Index).Sum(owner.RowHeight);
                    dc.DrawRoundedRectangle(Theme.Get("Selection"), new Pen(accent, 1), new Rect(X(d.Frame), y + 3, Math.Min(150, E.Comp.Duration - d.Frame) * owner.PixelsPerFrame, LayerHeight - 6), 3, 3);
                }
                if (marquee is { } selectionBox)
                {
                    var fill = accent.Clone(); fill.Opacity = .08;
                    dc.DrawRectangle(fill, new Pen(accent, .7), selectionBox);
                }
                if (E.Comp.Layers.Count == 0) Text(dc, "No layers", split + 16, top + Ruler + 14, Theme.Get("Muted"));
                dc.Pop();

                dc.DrawRectangle(panel, null, new Rect(left, top, Header, Math.Max(0, bottom - top)));
                dc.PushClip(new RectangleGeometry(new Rect(left, top + Ruler, Header, Math.Max(0, bottom - top - Ruler))));
                foreach (var (layer, y) in rows) DrawLayer(dc, layer, left, y, border);
                dc.Pop();
                dc.DrawLine(border, new Point(split, top), new Point(split, bottom));
                DrawRuler(dc, left, top, right, spacing, first, border);
            }
            DrawCursor();
        }

        void DrawRuler(DrawingContext dc, double left, double top, double right, double spacing, double first, Pen border)
        {
            var split = left + Header;
            var muted = Theme.Get("Muted");
            dc.DrawRectangle(Theme.Get("Elevated"), null, new Rect(left, top, Header, Ruler));
            Text(dc, "LAYERS", left + 13, top + 11, muted, 10, true);
            Text(dc, E.Comp.Layers.Count.ToString(CultureInfo.CurrentCulture), left + Header - 52, top + 11, muted, 10, width: 38, alignment: TextAlignment.Right);
            dc.PushClip(new RectangleGeometry(new Rect(split, top, Math.Max(0, right - split), Ruler)));
            dc.DrawRectangle(Theme.Get("Panel"), null, new Rect(split, top, Math.Max(0, right - split), Ruler));
            var workArea = muted.Clone();
            workArea.Opacity = .55;
            dc.DrawRoundedRectangle(workArea, null, new Rect(X(E.Comp.WorkStart), top + 30, Math.Max(1, (E.Comp.WorkEnd - E.Comp.WorkStart) * owner.PixelsPerFrame), 2), 1, 1);
            for (var frame = first; frame < E.Comp.Duration; frame += spacing)
            {
                var x = X((long)frame);
                if (x > right) break;
                Text(dc, E.Comp.Fps.Seconds((long)frame).ToString("0.##", CultureInfo.InvariantCulture) + "s", x + 6, top + 5, muted, 10);
                for (var j = 0; j < 4; j++)
                {
                    var tick = x + spacing * owner.PixelsPerFrame * j / 4;
                    dc.DrawLine(border, new Point(tick, top + (j == 0 ? 18 : 21)), new Point(tick, top + 26));
                }
            }
            foreach (var marker in E.Comp.Markers)
                Diamond(dc, X(marker), top + 31, Theme.Get("Accent"), Theme.Get("Accent"), 3);
            dc.Pop();
            dc.DrawLine(border, new Point(left, top + Ruler), new Point(right, top + Ruler));
        }

        void DrawClip(DrawingContext dc, Layer layer, double y, double right, double split)
        {
            var start = layer == dragLayer && dragging && dragMode == "move" ? proposed : layer.Start;
            var from = layer == dragLayer && dragging && dragMode == "start" ? proposed : start;
            var to = layer == dragLayer && dragging && dragMode == "end" ? proposed : start + layer.Duration;
            var rect = new Rect(X(from) + 1, y + 4, Math.Max(2, (to - from) * owner.PixelsPerFrame - 2), LayerHeight - 8);
            if (rect.Right < split || rect.Left > right) return;
            var selected = E.Selection.Contains(layer.Id);
            var muted = Theme.Get("Muted");
            dc.DrawRoundedRectangle(Theme.Get("Elevated"), new Pen(selected ? Theme.Get("Accent") : Theme.Get("Border"), selected ? 1 : .7), rect, 3, 3);
            dc.PushClip(new RectangleGeometry(rect, 3, 3));
            dc.DrawRectangle(RendererBrush(layer.Color, Theme.Dark ? .16 : .12), null, rect);
            dc.DrawRectangle(RendererBrush(layer.Color, .9), null, new Rect(rect.X, rect.Y, 2, rect.Height));
            var labelStart = Math.Max(rect.X + 8, split + 5);
            var labelWidth = Math.Min(rect.Right, right) - labelStart - 6;
            if (layer.AssetId != null && layer.Kind != "Audio" && owner.Thumbnails.TryGetValue(layer.AssetId, out var thumb) && rect.Width > 120)
            {
                dc.PushOpacity(.22);
                var x = Math.Max(rect.X + 4, split - 36);
                for (; x < Math.Min(rect.Right, right); x += 37)
                    dc.DrawImage(thumb, new Rect(x, rect.Bottom - 9, 36, 9));
                dc.Pop();
            }
            if (layer.AssetId != null && layer.Audio && owner.Waveforms.TryGetValue(layer.AssetId, out var waveform) && waveform.Length > 0)
            {
                var seconds = E.Comp.Fps.Seconds(layer.Duration);
                var step = Math.Max(2, rect.Width / 800);
                var pen = new Pen(RendererBrush(layer.Color, .7), 1);
                for (var x = Math.Max(rect.X + 3, split); x < Math.Min(rect.Right, right); x += step)
                {
                    var time = layer.SourceIn + seconds * layer.Rate * (x - rect.X) / rect.Width;
                    var index = Math.Clamp((int)(time * 100), 0, waveform.Length - 1);
                    var amplitude = Math.Min(3.5, waveform[index] * 13);
                    dc.DrawLine(pen, new Point(x, rect.Bottom - 4 - amplitude), new Point(x, rect.Bottom - 4 + amplitude));
                }
            }
            if (labelWidth > 10)
                Text(dc, layer.Name, labelStart, rect.Y + 3, layer.Visible ? Theme.Get("Text") : muted, 10.5, selected, labelWidth);
            dc.Pop();
        }

        void DrawLayer(DrawingContext dc, Layer layer, double x, double y, Pen border)
        {
            var accent = Theme.Get("Accent");
            var muted = Theme.Get("Muted");
            var selected = E.Selection.Contains(layer.Id);
            var expanded = owner.Expanded.Contains(layer.Id);
            var center = y + LayerHeight / 2;
            if (selected)
            {
                dc.DrawRectangle(Theme.Get("Selection"), null, new Rect(x, y, Header, LayerHeight));
                dc.DrawRoundedRectangle(accent, null, new Rect(x + 1, y + 7, 2, LayerHeight - 14), 1, 1);
            }
            if (hover.Id == layer.Id && hover.Cell == 0)
                dc.DrawRoundedRectangle(Theme.Get("Hover"), null, new Rect(x + 5, y + 5, 18, 22), 3, 3);
            Glyph(dc, expanded ? "Chevron" : "ChevronRight", x + 14, center, muted, 12);
            var silent = Theme.Get("Muted").Clone();
            silent.Opacity = .62;
            for (var i = 0; i < switchCommands.Length; i++)
            {
                if (i == 2 && !HasAudioSwitch(layer)) continue;
                var sx = x + SwitchStart + i * SwitchWidth;
                if (hover.Id == layer.Id && hover.Cell == i + 1 && (i == 1 || !layer.Locked))
                    dc.DrawRoundedRectangle(Theme.Get("Hover"), null, new Rect(sx + 1, y + 5, SwitchWidth - 2, 22), 3, 3);
                var color = i switch { 1 when layer.Locked => accent, 3 when layer.Solo => accent, 0 when layer.Visible => muted, 2 when layer.Audio => muted, _ => silent };
                var icon = i switch { 0 => layer.Visible ? "Eye" : "EyeOff", 1 => layer.Locked ? "Lock" : "Unlock", 2 => layer.Audio ? "Volume" : "VolumeOff", _ => "Solo" };
                Glyph(dc, icon, sx + SwitchWidth / 2, center, color, 13);
            }
            dc.DrawRoundedRectangle(RendererBrush(layer.Color, .19), null, new Rect(x + TypeStart, y + 7, 18, 18), 4, 4);
            Glyph(dc, layer.Kind, x + TypeStart + 9, center, muted, 12);
            Text(dc, layer.Name, x + NameStart, y + 8, layer.Visible ? Theme.Get("Text") : muted, 11, selected, Header - NameStart - 12);
            dc.DrawLine(border, new Point(x, y + LayerHeight), new Point(x + Header, y + LayerHeight));
            if (!expanded) return;

            var properties = owner.PropertiesFor(layer);
            var propertyY = y + LayerHeight;
            dc.DrawRectangle(Theme.Get("Workspace"), null, new Rect(x, propertyY, Header, Math.Max(1, properties.Length) * PropertyHeight));
            dc.DrawLine(border, new Point(x + 20, propertyY), new Point(x + 20, propertyY + Math.Max(1, properties.Length) * PropertyHeight));
            for (var i = 0; i < properties.Length; i++)
            {
                var key = properties[i];
                var curve = layer.Properties[key];
                var py = propertyY + i * PropertyHeight;
                var animated = curve.Keys.Count > 0;
                var atPlayhead = curve.Keys.Any(k => k.Frame == E.Playhead);
                var value = curve.At(E.Playhead);
                if (valueScrub is { HasMoved: true } scrub && layer == dragLayer && dragProperty != null)
                {
                    if (dragProperty == key) value = scrub.Value;
                    else if (layer.LinkedAxis(dragProperty) == key) value = layer.ProportionalValue(dragProperty, key, E.Playhead, scrub.Value);
                }
                Diamond(dc, x + KeyStart + 10, py + PropertyHeight / 2, atPlayhead ? accent : null, animated ? accent : muted, 3.6);
                Text(dc, PropertyScrub.Label(key), x + PropertyStart, py + 4, muted, 10.5, width: LinkStart - PropertyStart - 6);
                if (key is "x" or "scaleX")
                {
                    var linked = key == "x" ? layer.PositionLinked : layer.ScaleLinked;
                    Glyph(dc, linked ? "Link" : "Unlink", x + LinkStart + 11, py + PropertyHeight / 2, linked ? accent : muted, 13);
                }
                dc.DrawRoundedRectangle(Theme.Get("Elevated"), null, new Rect(x + ValueStart, py + 2, ValueEnd - ValueStart, PropertyHeight - 4), 3, 3);
                Text(dc, PropertyScrub.Format(value), x + ValueStart + 5, py + 4, Theme.Get("Text"), 10.5, width: ValueEnd - ValueStart - 10, alignment: TextAlignment.Right);
                dc.DrawLine(border, new Point(x + 21, py + PropertyHeight), new Point(x + Header, py + PropertyHeight));
            }
            if (properties.Length == 0) Text(dc, "No keyframes", x + PropertyStart, propertyY + 4, muted, 10);
        }

        static void Diamond(DrawingContext dc, double x, double y, Brush? fill, Brush stroke, double radius = 4)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new Point(x, y - radius), true, true);
                path.PolyLineTo([new Point(x + radius, y), new Point(x, y + radius), new Point(x - radius, y)], true, false);
            }
            dc.DrawGeometry(fill, new Pen(stroke, .85), geometry);
        }

        static void Glyph(DrawingContext dc, string icon, double x, double y, Brush stroke, double size)
        {
            dc.PushTransform(new TranslateTransform(x - size / 2, y - size / 2));
            dc.PushTransform(new ScaleTransform(size / 20, size / 20));
            Icon.Draw(dc, icon, stroke);
            dc.Pop(); dc.Pop();
        }

        static bool HasAudioSwitch(Layer layer) => layer.Kind is "Video" or "Audio" or "Composition";
        static int SwitchAt(double x) => x >= SwitchStart && x < SwitchStart + SwitchWidth * switchCommands.Length
            ? (int)((x - SwitchStart) / SwitchWidth) : -1;
        void SetHover((string? Id, int Cell) target)
        {
            if (hover == target) return;
            hover = target;
            if (ToolTip is ToolTip { IsOpen: true } tip) tip.IsOpen = false;
            Refresh();
        }
        string? TooltipAt(Point p)
        {
            var x = p.X - owner.HorizontalOffset;
            if (x < 0 || x >= Header) return null;
            var (layer, y) = RowAt(p.Y);
            if (layer == null) return null;
            var property = PropertyAt(layer, p.Y - y);
            if (property != null)
            {
                if (x is >= KeyStart and < PropertyStart) return "Toggle keyframe at playhead";
                if (x is >= LinkStart and < ValueStart && property is ("x" or "scaleX"))
                    return layer.LinkedAxis(property) != null ? "Unlink X and Y" : "Link X and Y";
                if (x is >= ValueStart and <= ValueEnd) return "Drag to adjust · Double-click to type";
                return null;
            }
            if (p.Y >= y + LayerHeight) return null;
            if (x < SwitchStart) return owner.Expanded.Contains(layer.Id) ? "Collapse properties" : "Expand properties";
            return SwitchAt(x) switch
            {
                0 => layer.Visible ? "Hide layer" : "Show layer",
                1 => layer.Locked ? "Unlock layer" : "Lock layer",
                2 when HasAudioSwitch(layer) => layer.Audio ? "Mute audio" : "Enable audio",
                3 => layer.Solo ? "Disable solo" : "Solo layer",
                _ when x >= TypeStart => layer.Name + "  ·  " + layer.Kind + (layer.Rate != 1 ? $"  ·  {layer.Rate:0.##}×" : ""),
                _ => null
            };
        }
        public void DrawCursor()
        {
            using var dc = cursor.RenderOpen(); var x = X(E.Playhead); if (x < owner.HorizontalOffset + Header) return;
            var top = owner.VerticalOffset; var accent = Theme.Get("Accent"); dc.DrawLine(new Pen(accent, 1), new Point(x, top + 20), new Point(x, Math.Max(1, Height)));
            dc.DrawGeometry(accent, null, Geometry.Parse(FormattableString.Invariant($"M {x - 4},{top + 16} L {x + 4},{top + 16} L {x + 4},{top + 21} L {x},{top + 26} L {x - 4},{top + 21} Z")));
        }
        static Brush RendererBrush(string hex, double opacity) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity }; b.Freeze(); return b; }
        long SnapFrame(long f, Layer? ignore)
        {
            if (!owner.Snap) return f; var candidates = new List<long> { 0, E.Playhead, E.Comp.Duration }; candidates.AddRange(E.Comp.Markers);
            foreach (var l in E.Comp.Layers.Where(l => l != ignore)) { candidates.Add(l.Start); candidates.Add(l.End); candidates.AddRange(l.Properties.Values.SelectMany(c => c.Keys).Select(k => k.Frame)); }
            return candidates.Where(v => Math.Abs(v - f) * owner.PixelsPerFrame < 8).OrderBy(v => Math.Abs(v - f)).Cast<long?>().FirstOrDefault() ?? f;
        }
        void Down(object sender, MouseButtonEventArgs e)
        {
            Focus(); var p = e.GetPosition(this); down = p; dragLayer = null; dragKey = null; dragProperty = null;
            if (p.Y < Ruler + owner.VerticalOffset && p.X > Header + owner.HorizontalOffset) { dragMode = "scrub"; owner.Seek?.Invoke(Frame(p.X), true); CaptureMouse(); return; }
            var row = RowAt(p.Y);
            if (row.Layer == null) { E.Select(null); owner.SelectionUpdated?.Invoke(); return; }
            var l = row.Layer; var localX = p.X - owner.HorizontalOffset;
            var property = PropertyAt(l, p.Y - row.Y);
            if (localX < Header)
            {
                if (p.Y < row.Y + LayerHeight && localX < SwitchStart) { if (!owner.Expanded.Remove(l.Id)) owner.ToggleProperties(l, "All"); owner.Refresh(); return; }
                if (p.Y >= row.Y + LayerHeight)
                {
                    if (property == null) return;
                    owner.Property = property;
                    if (localX is >= KeyStart and < PropertyStart && !l.Locked) { E.ToggleKeyframe(l, property); owner.Refresh(); return; }
                    if (localX is >= LinkStart and < ValueStart && !l.Locked && property is ("x" or "scaleX"))
                    {
                        E.Execute(new("toggle_axis_link", l.Id, property)); return;
                    }
                    if (localX is >= ValueStart and <= ValueEnd && !l.Locked)
                    {
                        if (!E.Selection.Contains(l.Id)) E.Select(l.Id);
                        if (e.ClickCount == 2) { EditNumber(l, property); return; }
                        dragLayer = l;
                        dragProperty = property;
                        dragMode = "value";
                        valueScrub = new PropertyScrub(p, l.Get(property, E.Playhead), property);
                        CaptureMouse();
                        return;
                    }
                    E.Select(l.Id); owner.SelectionUpdated?.Invoke(); return;
                }
                var cell = SwitchAt(localX);
                if (cell >= 0 && (cell != 2 || HasAudioSwitch(l)))
                {
                    if (!l.Locked || cell == 1) E.Execute(new(switchCommands[cell], l.Id));
                    return;
                }
                E.Select(l.Id, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); owner.SelectionUpdated?.Invoke(); return;
            }
            if (p.Y >= row.Y + LayerHeight)
            {
                if (property == null) return;
                dragProperty = owner.Property = property;
                dragKey = l.Properties[dragProperty].Keys.FirstOrDefault(k => Math.Abs(X(k.Frame) - p.X) < 8);
                if (dragKey != null)
                {
                    var add = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                    if (add || !E.SelectedKeys.Contains(dragKey.Id)) E.SelectKey(dragKey.Id, add);
                    if (!l.Locked && E.SelectedKeys.Contains(dragKey.Id)) { dragLayer = l; original = proposed = dragKey.Frame; dragMode = "key"; CaptureMouse(); }
                }
                else { dragMode = "marquee"; marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? E.SelectedKeys.ToHashSet() : []; marquee = new Rect(p, p); CaptureMouse(); }
                owner.SelectionUpdated?.Invoke(); return;
            }
            if (p.X < X(l.Start) - 5 || p.X > X(l.End) + 5) { dragMode = "scrub"; owner.Seek?.Invoke(Frame(p.X), true); CaptureMouse(); return; }
            if (!E.Selection.Contains(l.Id)) E.Select(l.Id, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            owner.SelectionUpdated?.Invoke(); if (l.Locked) return;
            dragLayer = l; dragMode = Math.Abs(p.X - X(l.Start)) < 8 ? "start" : Math.Abs(p.X - X(l.End)) < 8 ? "end" : "move";
            original = proposed = dragMode == "start" ? l.Start : dragMode == "end" ? l.End : l.Start; proposedIndex = E.Comp.Layers.IndexOf(l); CaptureMouse();
        }
        void Move(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (!IsMouseCaptured)
            {
                var x = p.X - owner.HorizontalOffset;
                var (layer, y) = RowAt(p.Y);
                var value = layer is { Locked: false } && PropertyAt(layer, p.Y - y) != null && x is >= ValueStart and <= ValueEnd;
                var cell = SwitchAt(x);
                SetHover(layer != null && p.Y < y + LayerHeight && x >= 0 && x < Header
                    ? (layer.Id, x < SwitchStart ? 0 : cell >= 0 ? cell + 1 : 5) : (null, -1));
                Cursor = value ? Cursors.SizeAll : x >= Header && p.Y < owner.VerticalOffset + Ruler ? Cursors.IBeam : Cursors.Arrow;
                return;
            }
            if (dragMode == "scrub") { owner.Seek?.Invoke(Frame(p.X), true); return; }
            if (valueScrub != null && dragLayer != null && dragProperty != null)
            {
                if (valueScrub.Update(p)) { owner.PropertyPreview?.Invoke(dragLayer.Id, dragProperty, valueScrub.Value); Refresh(); }
                return;
            }
            if (dragMode == "marquee")
            {
                marquee = new Rect(down, p); E.SelectedKeys.Clear(); E.SelectedKeys.UnionWith(marqueeBase);
                foreach (var (l, y) in Rows().Where(r => owner.Expanded.Contains(r.Layer.Id)))
                {
                    var props = owner.PropertiesFor(l);
                    for (var i = 0; i < props.Length; i++) foreach (var k in l.Properties[props[i]].Keys) if (marquee.Value.Contains(new Point(X(k.Frame), KeyY(y, i)))) { E.SelectedKeys.Add(k.Id); E.Selection.Add(l.Id); }
                }
                Refresh(); return;
            }
            if (dragLayer == null) return;
            if ((p - down).Length < 3 && !dragging) return; dragging = true;
            var delta = (long)Math.Round((p.X - down.X) / owner.PixelsPerFrame); proposed = SnapFrame(original + delta, dragLayer);
            proposed = dragMode switch { "start" => Math.Clamp(proposed, 0, dragLayer.End - 1), "end" => Math.Clamp(proposed, dragLayer.Start + 1, E.Comp.Duration), "move" => Math.Clamp(proposed, 0, Math.Max(0, E.Comp.Duration - dragLayer.Duration)), _ => Math.Clamp(proposed, 0, E.Comp.Duration - 1) };
            proposedIndex = Math.Abs(p.Y - down.Y) > LayerHeight / 2
                ? Math.Min(LayerIndexAt(p.Y), E.Comp.Layers.Count - 1) : E.Comp.Layers.IndexOf(dragLayer);
            Refresh();
        }
        void Up(object sender, MouseButtonEventArgs e)
        {
            if (dragMode == "scrub")
            {
                dragMode = "";
                ReleaseMouseCapture();
                owner.Seek?.Invoke(Frame(e.GetPosition(this).X), false);
                return;
            }
            if (valueScrub is { } scrub && dragLayer is { } layer && dragProperty is { } property)
            {
                valueScrub = null; dragLayer = null; dragMode = "";
                ReleaseMouseCapture();
                if (scrub.HasMoved) E.Execute(new("scrub_property", layer.Id, property, scrub.Value, E.Playhead));
                Refresh();
                return;
            }
            ReleaseMouseCapture(); if (dragMode == "marquee") { marquee = null; E.Notify(); dragMode = ""; Refresh(); return; } if (dragLayer != null && dragging)
            {
                var id = dragLayer.Id; var target = proposed;
                if (dragMode == "key" && dragKey != null && dragProperty != null)
                {
                    E.Execute(new("move_selected_keyframes", Frame: target - original));
                }
                else
                {
                    var ids = E.Selection.ToArray(); var shift = target - original;
                    if (dragMode == "move" && ids.Length > 1) E.Transaction("Move selected layers", () => { foreach (var l in E.Comp.Layers.Where(l => ids.Contains(l.Id) && !l.Locked)) Editor.Move(l, Math.Max(0, l.Start + shift)); });
                    else E.Execute(new(dragMode == "start" ? "trim_start" : dragMode == "end" ? "trim_end" : "move_layer", id, Frame: target));
                    if (dragMode == "move" && proposedIndex != E.Comp.Layers.FindIndex(candidate => candidate.Id == id))
                        E.Execute(new("reorder_layer", id, Value: proposedIndex));
                }
            }
            dragLayer = null; dragging = false; dragMode = ""; Refresh();
        }
        void EditNumber(Layer layer, string property)
        {
            var frame = E.Playhead;
            var box = new TextBox { Width = 78, Text = PropertyScrub.Format(layer.Get(property, frame)), FontFamily = new FontFamily("Consolas") };
            var popup = new Popup { Child = box, PlacementTarget = this, Placement = PlacementMode.MousePoint, AllowsTransparency = true, StaysOpen = false };
            numericPopup = popup;
            popup.Closed += (_, _) => { if (numericPopup == popup) numericPopup = null; };
            box.KeyDown += (_, key) =>
            {
                if (key.Key == Key.Enter && double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    E.Execute(new("scrub_property", layer.Id, property, number, frame));
                else if (key.Key != Key.Escape) return;
                key.Handled = true;
                popup.IsOpen = false;
                Focus();
            };
            popup.IsOpen = true;
            box.Focus();
            box.SelectAll();
        }
        void Context(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this); var row = RowAt(p.Y); if (row.Layer == null) return;
            Focus();
            if (p.Y >= row.Y + LayerHeight)
            {
                var prop = PropertyAt(row.Layer, p.Y - row.Y);
                if (prop == null) return;
                owner.Property = prop;
                var key = p.X >= owner.HorizontalOffset + Header
                    ? row.Layer.Properties[prop].Keys.FirstOrDefault(k => Math.Abs(X(k.Frame) - p.X) < 9) : null;
                if (key != null && !E.SelectedKeys.Contains(key.Id)) E.SelectKey(key.Id);
                var keyMenu = KeyframeMenu.Create(E, property => owner.GraphRequested?.Invoke(property), Window.GetWindow(this));
                var selectKeys = new MenuItem { Header = "Select Property Keyframes", IsEnabled = row.Layer.Properties[prop].Keys.Count > 0 }; selectKeys.Click += (_, _) => { E.SelectedKeys.Clear(); E.SelectedKeys.UnionWith(row.Layer.Properties[prop].Keys.Select(k => k.Id)); E.Selection.Add(row.Layer.Id); E.Notify(); }; keyMenu.Items.Insert(0, selectKeys);
                keyMenu.PlacementTarget = this; keyMenu.IsOpen = true; e.Handled = true; return;
            }
            if (!E.Selection.Contains(row.Layer.Id)) E.Select(row.Layer.Id); var menu = new ContextMenu();
            void Item(string name, string cmd, string shortcut = "", string? icon = null) { var item = new MenuItem { Header = name, InputGestureText = shortcut }; if (icon != null) item.Icon = new Icon { Kind = icon }; item.Click += (_, _) => E.Execute(new(cmd, cmd is "split_clip" or "delete_layers" or "ripple_delete" or "precompose" ? null : row.Layer.Id, Frame: E.Playhead)); menu.Items.Add(item); }
            Item("Split at Playhead", "split_clip", "Ctrl+Shift+D", "Split"); Item("Duplicate", "duplicate_layer", "Ctrl+D"); menu.Items.Add(new Separator()); Item("Precompose…", "precompose", "Ctrl+Shift+C", "Composition");
            if (row.Layer.Kind == "Video")
            {
                foreach (var (label, removeBackground) in new[] { ("Remove Background…", true), ("Stabilize…", false) })
                {
                    var action = new MenuItem { Header = label, IsEnabled = !row.Layer.Locked };
                    action.Click += (_, _) => owner.ClipProcessingRequested?.Invoke(row.Layer, removeBackground);
                    menu.Items.Add(action);
                }
            }
            menu.Items.Add(new Separator()); Item("Delete", "delete_layers", Preferences.Current.Shortcuts.GetValueOrDefault("layer.deleteBackspace", "Backspace"), "Close"); Item("Ripple Delete", "ripple_delete", "Shift+Delete"); menu.PlacementTarget = this; menu.IsOpen = true; e.Handled = true;
        }
        void Drag(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("Kuro.Asset")) return; var p = e.GetPosition(this); drop = (SnapFrame(Frame(p.X), null), LayerIndexAt(p.Y)); e.Effects = DragDropEffects.Copy; e.Handled = true; Refresh();
        }
        void Dropped(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("Kuro.Asset")) return;
            var id = (string)e.Data.GetData("Kuro.Asset"); var p = e.GetPosition(this); var pos = drop ?? (Frame(p.X), 0);
            var asset = E.Project.Assets.Find(a => a.Id == id);
            if (asset?.Kind == "Composition")
            {
                E.Transaction("Add precomposition", () => { if (id == E.Comp.Id) throw new InvalidOperationException("A composition cannot contain itself."); var l = new Layer { Kind = "Composition", CompositionId = id, Name = asset.Name, Start = pos.Item1, Duration = Math.Min(E.Comp.Duration - pos.Item1, E.Comp.Fps.Frames(asset.Duration)) }; l.Properties["x"].Value = E.Comp.Width / 2.0; l.Properties["y"].Value = E.Comp.Height / 2.0; E.Comp.Layers.Insert(pos.Item2, l); });
            }
            else E.Execute(new("add_asset", id, Value: pos.Item2, Frame: pos.Item1));
            drop = null; e.Handled = true; owner.SelectionUpdated?.Invoke(); Refresh();
        }
    }
}
