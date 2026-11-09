using System.Text.Json;

namespace Kuro.Core;

public record CommandRequest(string Name, string? Target = null, string? Property = null, double Value = 0, long Frame = 0, string? Text = null, long? ExpectedRevision = null);
public record CommandEvent(string Stage, string Name, string? Target, string Summary);
public record CommandResult(bool Success, string Summary, long Revision);
public record KeyTarget(Layer Layer, string Property, Keyframe Key);
public record CopiedKey(string LayerId, string Property, Keyframe Key);
public class Editor
{
    public Project Project { get; private set; } = new();
    public HashSet<string> Selection { get; } = [];
    public HashSet<string> SelectedKeys { get; } = [];
    public List<CopiedKey> KeyClipboard { get; private set; } = [];
    public IEnumerable<KeyTarget> KeyTargets => Comp.Layers.SelectMany(l => l.Properties.SelectMany(p => p.Value.Keys.Where(k => SelectedKeys.Contains(k.Id)).Select(k => new KeyTarget(l, p.Key, k))));
    public long Playhead { get; set; }
    public event Action? Changed;
    public event Action<CommandEvent>? Activity;
    readonly Stack<(Project State, string Summary)> undo = new(), redo = new();
    public bool Dirty { get; set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string UndoLabel => undo.TryPeek(out var u) ? u.Summary : "";
    public Composition Comp => Project.Active;
    public Layer? Selected => Comp.Layers.FirstOrDefault(l => Selection.Contains(l.Id));
    public void Load(Project p) { Project = p; Selection.Clear(); SelectedKeys.Clear(); undo.Clear(); redo.Clear(); Playhead = 0; Dirty = false; Changed?.Invoke(); }
    public void Notify() => Changed?.Invoke();
    public void Select(string? id, bool add = false) { SelectedKeys.Clear(); if (!add) Selection.Clear(); if (id != null) { if (add && Selection.Contains(id)) Selection.Remove(id); else Selection.Add(id); } Notify(); }
    public void SelectKey(string id, bool add = false)
    {
        if (!add) { SelectedKeys.Clear(); Selection.Clear(); }
        if (add && SelectedKeys.Contains(id)) SelectedKeys.Remove(id); else SelectedKeys.Add(id);
        foreach (var target in KeyTargets) Selection.Add(target.Layer.Id);
        Notify();
    }
    public void CopyKeys() { KeyClipboard = KeyTargets.Select(t => new CopiedKey(t.Layer.Id, t.Property, Data.Clone(t.Key))).ToList(); }
    public CommandResult PasteKeys(long frame) => Execute(new("paste_keyframes", Frame: frame));
    public CommandResult ToggleKeyframe(Layer layer, string property)
    {
        var curve = layer.Properties[property];
        var command = curve.Keys.Any(key => key.Frame == Playhead) ? "remove_keyframe" : "add_keyframe";
        return Execute(new(command, layer.Id, property, curve.At(Playhead), Playhead));
    }
    public void Undo() { if (!undo.TryPop(out var entry)) return; redo.Push((Data.Clone(Project), entry.Summary)); var revision = Project.Revision + 1; Project = entry.State; Project.Revision = revision; Dirty = true; Notify(); }
    public void Redo() { if (!redo.TryPop(out var entry)) return; undo.Push((Data.Clone(Project), entry.Summary)); var revision = Project.Revision + 1; Project = entry.State; Project.Revision = revision; Dirty = true; Notify(); }
    public void Transaction(string label, Action action, long? expectedRevision = null)
    {
        if (expectedRevision.HasValue && Project.Revision != expectedRevision) throw new InvalidOperationException("The project changed while this operation was being planned. Inspect the current state and retry.");
        var before = Data.Clone(Project);
        try { action(); Validate(); }
        catch { Project = before; throw; }
        undo.Push((before, label)); while (undo.Count > 80) { var items = undo.Take(80).Reverse().ToArray(); undo.Clear(); foreach (var item in items) undo.Push(item); }
        redo.Clear(); Project.Revision++; Dirty = true; Notify();
    }
    public CommandResult Execute(CommandRequest request)
    {
        Activity?.Invoke(new("started", request.Name, request.Target, request.Name.Replace('_', ' ')));
        try
        {
            Transaction(request.Name.Replace('_', ' '), () => Apply(request), request.ExpectedRevision);
            Activity?.Invoke(new("completed", request.Name, request.Target, "Completed · " + request.Name.Replace('_', ' ')));
            return new(true, request.Name.Replace('_', ' '), Project.Revision);
        }
        catch (Exception ex) { Activity?.Invoke(new("failed", request.Name, request.Target, ex.Message)); return new(false, ex.Message, Project.Revision); }
    }
    Layer Target(CommandRequest r)
    {
        var l = Comp.Layers.Find(l => l.Id == r.Target) ?? throw new InvalidOperationException("The target layer no longer exists.");
        if (l.Locked && r.Name != "toggle_lock") throw new InvalidOperationException($"{l.Name} is locked. Unlock it explicitly first.");
        return l;
    }
    void Apply(CommandRequest r)
    {
        if (r.Name is "set_selected_interpolation" or "delete_selected_keyframes" or "set_selected_keyframe_value" or "move_selected_keyframes")
        {
            var targets = KeyTargets.ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("No keyframes selected.");
            if (targets.Any(t => t.Layer.Locked)) throw new InvalidOperationException("The selection contains locked keyframes.");
            if (r.Name == "delete_selected_keyframes") { foreach (var t in targets) t.Layer.Properties[t.Property].Keys.Remove(t.Key); SelectedKeys.Clear(); return; }
            if (r.Name == "move_selected_keyframes")
            {
                var delta = r.Frame;
                foreach (var t in targets) if (t.Key.Frame + delta < 0 || t.Key.Frame + delta >= Comp.Duration || t.Layer.Properties[t.Property].Keys.Any(k => !SelectedKeys.Contains(k.Id) && k.Frame == t.Key.Frame + delta)) throw new InvalidOperationException("That position overlaps another keyframe or falls outside the composition.");
                foreach (var t in targets) t.Key.Frame += delta;
                return;
            }
            if (r.Name == "set_selected_keyframe_value")
            { if (!double.IsFinite(r.Value)) throw new InvalidOperationException("Values must be finite."); foreach (var t in targets) t.Key.Value = t.Property == "opacity" ? Math.Clamp(r.Value, 0, 100) : r.Value; return; }
            if (r.Text is not ("Linear" or "Hold" or "Easy Ease" or "Ease In" or "Ease Out")) throw new InvalidOperationException("Unsupported keyframe interpolation.");
            foreach (var t in targets)
            {
                var key = t.Key;
                if (r.Text is "Linear" or "Hold") { key.Interpolation = r.Text; key.OutX = key.OutY = 1.0 / 3; key.InX = key.InY = 2.0 / 3; continue; }
                if (r.Text is "Easy Ease" or "Ease Out") { key.Interpolation = "Bezier"; key.OutX = 1.0 / 3; key.OutY = 0; }
                if (r.Text is "Easy Ease" or "Ease In")
                {
                    key.InX = 2.0 / 3; key.InY = 1;
                    var previous = t.Layer.Properties[t.Property].Keys.Where(k => k.Frame < key.Frame).OrderBy(k => k.Frame).LastOrDefault();
                    if (previous != null) previous.Interpolation = "Bezier";
                }
            }
            return;
        }
        if (r.Name == "paste_keyframes")
        {
            if (KeyClipboard.Count == 0) throw new InvalidOperationException("No keyframes copied.");
            var first = KeyClipboard.Min(k => k.Key.Frame); var targetLayer = Selection.Count == 1 ? Selected : null;
            var plan = KeyClipboard.Select(copy => new { Layer = targetLayer ?? Comp.Layers.Find(l => l.Id == copy.LayerId), copy.Property, Key = Data.Clone(copy.Key) }).ToArray();
            foreach (var item in plan) if (item.Layer == null || item.Layer.Locked || !item.Layer.Properties.ContainsKey(item.Property) || r.Frame + item.Key.Frame - first < 0 || r.Frame + item.Key.Frame - first >= Comp.Duration) throw new InvalidOperationException("The keyframes cannot be pasted at this target/time.");
            SelectedKeys.Clear();
            foreach (var item in plan) { var curve = item.Layer!.Properties[item.Property]; item.Key.Frame = r.Frame + item.Key.Frame - first; item.Key.Id = Data.Id(); curve.Keys.RemoveAll(k => k.Frame == item.Key.Frame); curve.Keys.Add(item.Key); SelectedKeys.Add(item.Key.Id); }
            return;
        }
        if (r.Name == "split_clip") { Split(r.Target == null ? Selection : [r.Target], r.Frame); return; }
        if (r.Name == "delete_layers" || r.Name == "ripple_delete")
        {
            var targets = Comp.Layers.Where(l => (r.Target == null ? Selection.Contains(l.Id) : l.Id == r.Target) && !l.Locked).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("Select an unlocked layer first.");
            var start = targets.Min(l => l.Start); var end = targets.Max(l => l.End);
            Comp.Layers.RemoveAll(l => targets.Contains(l));
            if (r.Name == "ripple_delete") foreach (var l in Comp.Layers.Where(l => l.Start >= end && !l.Locked)) Move(l, l.Start - (end - start));
            return;
        }
        if (r.Name == "create_text" || r.Name == "create_shape")
        {
            var l = new Layer { Name = r.Name == "create_text" ? "Text" : "Shape", Kind = r.Name == "create_text" ? "Text" : "Shape", Start = r.Frame, Duration = Math.Min(Comp.Fps.Frames(5), Comp.Duration - r.Frame), Text = r.Text ?? "Your next story.", Fill = r.Name == "create_text" ? ProjectColors.DefaultFill : ProjectColors.DefaultShapeFill, Color = r.Name == "create_text" ? "#918C83" : "#768789" };
            l.Properties["x"].Value = Comp.Width / 2.0; l.Properties["y"].Value = Comp.Height / 2.0;
            Comp.Layers.Insert(0, l); Selection.Clear(); Selection.Add(l.Id); return;
        }
        if (r.Name == "add_asset")
        {
            var a = Project.Assets.Find(a => a.Id == r.Target) ?? throw new InvalidOperationException("Asset no longer exists.");
            var l = new Layer { Name = a.Name, Kind = a.Kind, AssetId = a.Id, Start = Math.Clamp(r.Frame, 0, Comp.Duration - 1), Duration = a.Kind == "Image" ? Comp.Fps.Frames(5) : Math.Max(1, Comp.Fps.Frames(a.Duration)), Color = a.Kind == "Audio" ? "#788A85" : "#757B8A" };
            l.Duration = Math.Min(l.Duration, Comp.Duration - l.Start); l.Properties["x"].Value = Comp.Width / 2.0; l.Properties["y"].Value = Comp.Height / 2.0;
            var scale = a.Width > 0 ? Math.Min((double)Comp.Width / a.Width, (double)Comp.Height / a.Height) * 100 : 100;
            l.Properties["scaleX"].Value = scale; l.Properties["scaleY"].Value = scale;
            Comp.Layers.Insert(Math.Clamp((int)r.Value, 0, Comp.Layers.Count), l); Selection.Clear(); Selection.Add(l.Id); return;
        }
        if (r.Name == "precompose") { Precompose(); return; }
        var layer = Target(r);
        switch (r.Name)
        {
            case "move_layer": Move(layer, Math.Max(0, Math.Min(r.Frame, Comp.Duration - layer.Duration))); break;
            case "reorder_layer": Comp.Layers.Remove(layer); Comp.Layers.Insert(Math.Clamp((int)r.Value, 0, Comp.Layers.Count), layer); break;
            case "trim_start":
                var newStart = Math.Clamp(r.Frame, 0, layer.End - 1);
                var sourceIn = layer.SourceIn + Comp.Fps.Seconds(newStart - layer.Start) * layer.Rate;
                if (sourceIn < 0) { newStart = layer.Start - Comp.Fps.Frames(layer.SourceIn / Math.Max(.001, layer.Rate)); sourceIn = 0; }
                layer.Duration = layer.End - newStart; layer.SourceIn = sourceIn; layer.Start = newStart; break;
            case "trim_end": layer.Duration = Math.Clamp(r.Frame - layer.Start, 1, Comp.Duration - layer.Start); break;
            case "set_property":
            case "scrub_property":
                SetLayerValue(layer, r.Property, r.Frame, r.Value, r.Name == "scrub_property"); break;
            case "toggle_axis_link":
                if (r.Property is "x" or "y") layer.PositionLinked = !layer.PositionLinked;
                else if (r.Property is "scaleX" or "scaleY") layer.ScaleLinked = !layer.ScaleLinked;
                else throw new InvalidOperationException("This property has no linked axis.");
                break;
            case "add_keyframe":
                if (r.Property == null || !layer.Properties.TryGetValue(r.Property, out var animated)) throw new InvalidOperationException("Unknown animation property.");
                if (r.Frame < 0 || r.Frame >= Comp.Duration || !double.IsFinite(r.Value)) throw new InvalidOperationException("A keyframe needs a valid frame and value.");
                animated.Set(r.Frame, r.Property == "opacity" ? Math.Clamp(r.Value, 0, 100) : r.Value); break;
            case "remove_keyframe":
                var keys = layer.Properties[r.Property!].Keys;
                SelectedKeys.ExceptWith(keys.Where(k => k.Frame == r.Frame).Select(k => k.Id));
                keys.RemoveAll(k => k.Frame == r.Frame);
                break;
            case "set_interpolation":
                if (r.Text is not ("Linear" or "Bezier" or "Hold")) throw new InvalidOperationException("Interpolation must be Linear, Bezier or Hold.");
                foreach (var k in layer.Properties[r.Property!].Keys) { k.Interpolation = r.Text; if (r.Text == "Bezier") { k.OutX = .333333; k.OutY = 0; k.InX = .666667; k.InY = 1; } } break;
            case "set_text": layer.Text = r.Text ?? ""; break;
            case "rename_layer": layer.Name = r.Text ?? layer.Name; break;
            case "set_font": layer.Font = r.Text ?? "Segoe UI"; break;
            case "set_font_weight": layer.FontWeight = Math.Clamp((int)r.Value, 100, 900); break;
            case "set_fill":
                if (!ProjectColors.TryParse(r.Text, out _)) throw new InvalidOperationException("Enter a valid fill color in text, such as #FF4D2E.");
                layer.Fill = r.Text!.Trim();
                break;
            case "set_shape": if (!Shapes.Kinds.Contains(r.Text)) throw new InvalidOperationException("Unknown shape."); layer.Shape = r.Text!; break;
            case "set_rate":
                if (!double.IsFinite(r.Value) || Math.Abs(r.Value) > 16) throw new InvalidOperationException("Speed must be between −16 and 16.");
                layer.Rate = r.Value; break;
            case "set_source_in": layer.SourceIn = Math.Max(0, r.Value); break;
            case "toggle_visibility": layer.Visible = !layer.Visible; break;
            case "toggle_lock": layer.Locked = !layer.Locked; break;
            case "toggle_audio": layer.Audio = !layer.Audio; break;
            case "toggle_solo": layer.Solo = !layer.Solo; break;
            case "duplicate_layer":
                var copy = Data.Clone(layer); copy.Id = Data.Id(); copy.Name += " copy"; foreach (var c in copy.Properties.Values) foreach (var k in c.Keys) k.Id = Data.Id(); Comp.Layers.Insert(Comp.Layers.IndexOf(layer), copy); Selection.Clear(); Selection.Add(copy.Id); break;
            default: throw new InvalidOperationException($"Command '{r.Name}' is not implemented.");
        }
    }
    public static void SetLayerValue(Layer layer, string? property, long frame, double value, bool linkAxes = false)
    {
        if (property == null || !layer.Properties.TryGetValue(property, out var curve)) throw new InvalidOperationException("Unknown property.");
        if (!double.IsFinite(value)) throw new InvalidOperationException("Values must be finite.");
        var linked = linkAxes ? layer.LinkedAxis(property) : null;
        if (linked != null)
        {
            var other = layer.Properties[linked];
            var next = layer.ProportionalValue(property, linked, frame, value);
            if (!double.IsFinite(next)) throw new InvalidOperationException("Linked value is out of range.");
            Set(other, next, curve.Keys.Count > 0);
        }
        Set(curve, property == "opacity" ? Math.Clamp(value, 0, 100) : value);

        void Set(Curve axis, double number, bool animated = false)
        {
            if (animated || axis.Keys.Count > 0) axis.Set(frame, number);
            else axis.Value = number;
        }
    }

    public void DrawShape(string shape, System.Windows.Rect bounds)
    {
        if (!Shapes.Kinds.Contains(shape) || bounds.Width < 1 || bounds.Height < 1) return;
        Transaction("Draw " + shape.ToLowerInvariant(), () =>
        {
            var layer = new Layer { Kind = "Shape", Shape = shape, Name = shape, Fill = "#E8E8EC", Color = "#768789", Start = Playhead, Duration = Math.Min(Comp.Fps.Frames(5), Comp.Duration - Playhead) };
            layer.Properties["x"].Value = bounds.X + bounds.Width / 2;
            layer.Properties["y"].Value = bounds.Y + bounds.Height / 2;
            layer.Properties["width"].Value = bounds.Width;
            layer.Properties["height"].Value = bounds.Height;
            Comp.Layers.Insert(0, layer);
            Selection.Clear(); SelectedKeys.Clear(); Selection.Add(layer.Id);
        });
    }
    public void Split(IEnumerable<string> ids, long frame)
    {
        var targets = Comp.Layers.Where(l => ids.Contains(l.Id) && !l.Locked && l.Start < frame && l.End > frame).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("The playhead must be inside a selected, unlocked layer.");
        Selection.Clear();
        foreach (var layer in targets)
        {
            var right = Data.Clone(layer); right.Id = Data.Id(); right.Name = layer.Name; right.SourceIn = layer.SourceTime(frame, Comp.Fps); right.Duration = layer.End - frame; right.Start = frame;
            if (right.Kind == "Composition") right.SourceIn = layer.SourceTime(frame, Comp.Fps);
            foreach (var c in right.Properties.Values) foreach (var k in c.Keys) k.Id = Data.Id();
            // Preset animation uses an explicit time origin so a split does not restart it.
            if (!layer.Properties.ContainsKey("presetOrigin")) layer.Properties["presetOrigin"] = new(layer.Start);
            right.Properties["presetOrigin"] = Data.Clone(layer.Properties["presetOrigin"]);
            layer.Duration = frame - layer.Start; Comp.Layers.Insert(Comp.Layers.IndexOf(layer), right); Selection.Add(right.Id);
        }
    }
    public static void Move(Layer layer, long frame)
    {
        var delta = frame - layer.Start; layer.Start = frame;
        foreach (var curve in layer.Properties.Values) foreach (var k in curve.Keys) k.Frame += delta;
        if (layer.Properties.TryGetValue("presetOrigin", out var o)) o.Value += delta;
    }
    void Precompose()
    {
        var selected = Comp.Layers.Where(l => Selection.Contains(l.Id)).ToList();
        if (selected.Count == 0 || selected.Any(l => l.Locked)) throw new InvalidOperationException("Select unlocked layers to precompose.");
        var parent = Comp; var child = new Composition { Name = "Precomposition " + Project.Compositions.Count, Width = parent.Width, Height = parent.Height, Fps = parent.Fps, Duration = parent.Duration, WorkEnd = parent.Duration, Background = "#00000000", Layers = selected };
        var index = parent.Layers.IndexOf(selected[0]); parent.Layers.RemoveAll(selected.Contains);
        var outer = new Layer { Name = child.Name, Kind = "Composition", CompositionId = child.Id, Duration = parent.Duration, Color = "#847E91" };
        outer.Properties["x"].Value = parent.Width / 2.0; outer.Properties["y"].Value = parent.Height / 2.0;
        parent.Layers.Insert(index, outer); Project.Compositions.Add(child); Project.Assets.Add(new Asset { Id = child.Id, Kind = "Composition", Name = child.Name, Width = child.Width, Height = child.Height, Duration = child.Fps.Seconds(child.Duration), FrameRate = child.Fps.Value }); Selection.Clear(); Selection.Add(outer.Id);
    }
    void Validate()
    {
        foreach (var c in Project.Compositions)
        {
            if (c.Layers.Any(l => l.Duration < 1 || l.Start < 0 || !double.IsFinite(l.Rate))) throw new InvalidOperationException("Invalid layer timing.");
            if (c.Layers.Select(l => l.Id).Distinct().Count() != c.Layers.Count) throw new InvalidOperationException("Duplicate layer identifier.");
        }
        void Visit(Composition c, HashSet<string> parents)
        {
            if (!parents.Add(c.Id)) throw new InvalidOperationException("Circular composition references are not allowed.");
            foreach (var l in c.Layers.Where(l => l.Kind == "Composition")) { var child = Project.Compositions.Find(x => x.Id == l.CompositionId); if (child != null) Visit(child, new(parents)); }
        }
        foreach (var c in Project.Compositions) Visit(c, []);
    }
}
