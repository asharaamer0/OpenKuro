using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kuro.Core;

namespace Kuro.UI;
public sealed class GraphEditor : Grid
{
    readonly Editor editor; readonly Graph graph; readonly ComboBox property;
    bool speed;
    public string Property => property.SelectedItem as string ?? "x";
    public GraphEditor(Editor editor)
    {
        this.editor = editor; RowDefinitions.Add(new() { Height = new GridLength(37) }); RowDefinitions.Add(new());
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 3, 12, 3) };
        property = new ComboBox { Width = 130, ItemsSource = new[] { "x", "y", "anchorX", "anchorY", "scaleX", "scaleY", "rotation", "opacity", "fontSize", "blur", "glow", "shadow", "width", "height", "radius", "time", "presetProgress" }, SelectedIndex = 0 };
        bar.Children.Add(property);
        foreach (var name in new[] { "Linear", "Easy Ease", "Hold" }) { var button = new Button { Content = name, Padding = new Thickness(9, 3, 9, 3), ToolTip = name + " · selected keyframes" }; button.Click += (_, _) => { if (editor.KeyTargets.Any()) editor.Execute(new("set_selected_interpolation", Text: name)); Refresh(); }; bar.Children.Add(button); }
        Children.Add(bar); graph = new Graph(this); SetRow(graph, 1); Children.Add(graph);
        property.SelectionChanged += (_, _) => Refresh();
    }
    public void Choose(string name) { if (property.Items.Contains(name)) property.SelectedItem = name; }
    public void Refresh() => graph.InvalidateVisual();
    sealed class Graph : FrameworkElement
    {
        readonly GraphEditor parent; double min, max = 100, zoom = 1; Keyframe? drag; string mode = ""; Curve? draft; string layerId = ""; long dragFrame;
        readonly List<(Point Point, Keyframe Key, string Mode)> targets = [];
        Curve? originalCurve; double dragValue; Rect? marquee; Point down; HashSet<string> marqueeBase = [];
        public Graph(GraphEditor parent)
        {
            this.parent = parent; ClipToBounds = true; Focusable = true; MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up; MouseRightButtonDown += Context; SizeChanged += (_, _) => InvalidateVisual(); MouseWheel += (_, e) => { zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), .25, 8); InvalidateVisual(); };
        }
        Editor E => parent.editor;
        bool Speed => parent.speed;
        double X(double frame) => 70 + frame / E.Comp.Duration * Math.Max(1, ActualWidth - 105);
        double Y(double value) => ActualHeight - 28 - (value - min) / Math.Max(.001, max - min) * Math.Max(1, ActualHeight - 48);
        double Frame(double x) => (x - 70) / Math.Max(1, ActualWidth - 105) * E.Comp.Duration;
        double Value(double y) => min + (ActualHeight - 28 - y) / Math.Max(1, ActualHeight - 48) * (max - min);
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Theme.Get("Workspace"), null, new Rect(RenderSize)); targets.Clear();
            var curve = draft ?? (E.Selected?.Properties.TryGetValue(parent.Property, out var c) == true ? c : null);
            var keys = curve?.Keys.OrderBy(k => k.Frame).ToArray() ?? [];
            if (keys.Length == 0) { Label(dc, "No keyframes", 70, 28, Theme.Get("Muted")); return; }
            if (drag == null)
            {
                var values = Enumerable.Range(0, 200).Select(i => Speed ? curve!.Speed(E.Comp.Duration * i / 199.0, E.Comp.Fps) : curve!.At(E.Comp.Duration * i / 199.0)).ToArray();
                min = Math.Min(0, values.Min()); max = Math.Max(1, values.Max()); var pad = (max - min) * .15; min = (min - pad) / zoom; max = (max + pad) / zoom;
            }
            var pen = new Pen(Theme.Get("Border"), .5);
            for (var i = 0; i <= 4; i++) { var val = min + (max - min) * i / 4; var y = Y(val); dc.DrawLine(pen, new Point(66, y), new Point(ActualWidth - 25, y)); Label(dc, val.ToString("0.##", CultureInfo.InvariantCulture), 12, y - 6, Theme.Get("Muted")); }
            for (var i = 0; i <= 8; i++) { var frame = E.Comp.Duration * i / 8.0; var x = X(frame); dc.DrawLine(pen, new Point(x, 12), new Point(x, ActualHeight - 26)); Label(dc, E.Comp.Fps.Seconds((long)frame).ToString("0.0") + "s", x, ActualHeight - 20, Theme.Get("Muted")); }
            var geometry = new StreamGeometry(); using (var ctx = geometry.Open())
            {
                for (var i = 0; i <= 800; i++) { var frame = E.Comp.Duration * i / 800.0; var point = new Point(X(frame), Y(Speed ? curve!.Speed(frame, E.Comp.Fps) : curve!.At(frame))); if (i == 0) ctx.BeginFigure(point, false, false); else ctx.LineTo(point, true, false); }
            }
            dc.DrawGeometry(null, new Pen(Theme.Get("Accent"), 1.8), geometry);
            if (!Speed)
            {
                for (var i = 0; i < keys.Length; i++)
                {
                    var key = keys[i]; var p = new Point(X(key.Frame), Y(key.Value)); targets.Add((p, key, "key"));
                    if (i < keys.Length - 1 && key.Interpolation == "Bezier")
                    {
                        var next = keys[i + 1]; var span = next.Frame - key.Frame; var delta = next.Value - key.Value;
                        Handle(dc, p, new Point(X(key.Frame + key.OutX * span), Y(key.Value + key.OutY * delta)), key, "out");
                        Handle(dc, new Point(X(next.Frame), Y(next.Value)), new Point(X(key.Frame + next.InX * span), Y(key.Value + next.InY * delta)), next, "in");
                    }
                    var selected = E.SelectedKeys.Contains(key.Id); dc.DrawRectangle(Theme.Get(selected ? "Accent" : "Elevated"), new Pen(Theme.Get(selected ? "Accent" : "Muted"), 1), new Rect(p.X - 4, p.Y - 4, 8, 8));
                }
            }
            dc.DrawLine(new Pen(Theme.Get("Accent"), 1), new Point(X(E.Playhead), 0), new Point(X(E.Playhead), ActualHeight));
            if (marquee is { } selectionBox) { var fill = Theme.Get("Accent").Clone(); fill.Opacity = .08; dc.DrawRectangle(fill, new Pen(Theme.Get("Accent"), .7), selectionBox); }
        }
        void Handle(DrawingContext dc, Point origin, Point handle, Keyframe key, string kind) { dc.DrawLine(new Pen(Theme.Get("Muted"), .8), origin, handle); dc.DrawEllipse(Theme.Get("Workspace"), new Pen(Theme.Get("Accent"), 1.3), handle, 4, 4); targets.Add((handle, key, kind)); }
        static void Label(DrawingContext dc, string s, double x, double y, Brush b) => dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, b, 1), new Point(x, y));
        void Down(object sender, MouseButtonEventArgs e)
        {
            Focus();
            if (Speed || E.Selected is not { Locked: false } layer) return; var p = e.GetPosition(this); down = p; var hit = targets.OrderBy(t => (t.Point - p).Length).FirstOrDefault();
            if (hit.Key == null || (hit.Point - p).Length > 12) { marqueeBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? E.SelectedKeys.ToHashSet() : []; marquee = new Rect(p, p); CaptureMouse(); return; }
            var add = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (add || !E.SelectedKeys.Contains(hit.Key.Id)) E.SelectKey(hit.Key.Id, add);
            if (!E.SelectedKeys.Contains(hit.Key.Id)) { InvalidateVisual(); return; }
            originalCurve = Data.Clone(layer.Properties[parent.Property]); draft = Data.Clone(originalCurve); drag = draft.Keys.First(k => k.Id == hit.Key.Id); mode = hit.Mode; dragFrame = drag.Frame; dragValue = drag.Value; layerId = layer.Id; CaptureMouse();
        }
        void Move(object sender, MouseEventArgs e)
        {
            if (!IsMouseCaptured) return; var p = e.GetPosition(this);
            if (marquee != null)
            {
                marquee = new Rect(down, p); E.SelectedKeys.Clear(); E.SelectedKeys.UnionWith(marqueeBase);
                foreach (var target in targets.Where(t => t.Mode == "key" && marquee.Value.Contains(t.Point))) E.SelectedKeys.Add(target.Key.Id);
                InvalidateVisual(); return;
            }
            if (drag == null || draft == null || originalCurve == null) return;
            var keys = draft.Keys.OrderBy(k => k.Frame).ToArray(); var index = Array.IndexOf(keys, drag);
            if (mode == "key")
            {
                var selected = originalCurve.Keys.Where(k => E.SelectedKeys.Contains(k.Id)).ToArray();
                if (selected.Length == 0) return;
                var delta = Math.Clamp((long)Math.Round(Frame(p.X)) - dragFrame, -selected.Min(k => k.Frame), E.Comp.Duration - 1 - selected.Max(k => k.Frame));
                if (selected.Any(k => originalCurve.Keys.Any(other => !E.SelectedKeys.Contains(other.Id) && other.Frame == k.Frame + delta))) return;
                foreach (var original in selected) { var key = draft.Keys.First(k => k.Id == original.Id); key.Frame = original.Frame + delta; var value = original.Value + Value(p.Y) - dragValue; key.Value = parent.Property == "opacity" ? Math.Clamp(value, 0, 100) : value; }
            }
            else
            {
                var a = mode == "in" ? keys[index - 1] : drag; var b = mode == "in" ? drag : keys[index + 1]; var dx = b.Frame - a.Frame; var dy = b.Value - a.Value;
                var x = Math.Clamp((Frame(p.X) - a.Frame) / dx, 0, 1); var y = Math.Abs(dy) < .0001 ? x : (Value(p.Y) - a.Value) / dy;
                if (mode == "in") { drag.InX = x; drag.InY = y; } else { drag.OutX = x; drag.OutY = y; }
            }
            InvalidateVisual();
        }
        void Up(object sender, MouseButtonEventArgs e)
        {
            ReleaseMouseCapture(); if (marquee != null) { marquee = null; E.Notify(); InvalidateVisual(); return; }
            if (draft != null && drag != null && (e.GetPosition(this) - down).Length > 2) { var copy = draft; E.Transaction("Edit animation curve", () => { var l = E.Comp.Layers.First(x => x.Id == layerId); if (l.Locked) throw new InvalidOperationException("The layer is locked."); l.Properties[parent.Property] = copy; }); }
            draft = null; originalCurve = null; drag = null; InvalidateVisual();
        }
        void Context(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this); var hit = targets.Where(t => t.Mode == "key").OrderBy(t => (t.Point - p).Length).FirstOrDefault();
            if (hit.Key != null && (hit.Point - p).Length <= 12 && !E.SelectedKeys.Contains(hit.Key.Id)) E.SelectKey(hit.Key.Id);
            var menu = KeyframeMenu.Create(E, property => parent.Choose(property), Window.GetWindow(this));
            foreach (var (label, speed) in new[] { ("Value Graph", false), ("Speed Graph", true) })
            {
                var mode = new MenuItem { Header = label, IsCheckable = true, IsChecked = Speed == speed };
                mode.Click += (_, _) => { parent.speed = speed; InvalidateVisual(); };
                menu.Items.Insert(speed ? 1 : 0, mode);
            }
            menu.Items.Insert(2, new Separator()); menu.PlacementTarget = this; menu.IsOpen = true; e.Handled = true;
        }
    }
}
