using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Kuro.Core;

internal enum LayerAlignment { Left, CenterX, Right, Top, CenterY, Bottom }

internal static class LayerGeometry
{
    public static Matrix Transform(Layer layer, long frame)
    {
        var matrix = Matrix.Identity;
        matrix.Translate(-layer.Get("anchorX", frame), -layer.Get("anchorY", frame));
        matrix.Scale(layer.Get("scaleX", frame) / 100, layer.Get("scaleY", frame) / 100);
        matrix.Rotate(layer.Get("rotation", frame));
        matrix.Translate(layer.Get("x", frame), layer.Get("y", frame));
        return matrix;
    }

    public static FormattedText Text(Layer layer, long frame, Brush fill) => new(layer.Text, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, new Typeface(new FontFamily(layer.Font), FontStyles.Normal,
            FontWeight.FromOpenTypeWeight(layer.FontWeight), FontStretches.Normal),
        Math.Clamp(layer.Get("fontSize", frame), 1, 1200), fill, 1);

    public static Rect LocalBounds(Project project, Layer layer, long frame)
    {
        switch (layer.Kind)
        {
            case "Video":
            case "Image":
                var asset = project.Assets.Find(a => a.Id == layer.AssetId);
                return asset is { Width: > 0, Height: > 0 } ? Centered(asset.Width, asset.Height) : Rect.Empty;
            case "Text":
                var text = Text(layer, frame, Brushes.Transparent);
                return Centered(text.Width, text.Height);
            case "Shape":
                var rect = Centered(Math.Max(0, layer.Get("width", frame)), Math.Max(0, layer.Get("height", frame)));
                return Shapes.Geometry(layer.Shape, rect, layer.Get("radius", frame)).Bounds;
            case "Composition":
                var child = project.Compositions.Find(c => c.Id == layer.CompositionId);
                return child == null ? Rect.Empty : Centered(child.Width, child.Height);
            default:
                return Rect.Empty;
        }
    }

    public static void Align(Editor editor, LayerAlignment alignment)
    {
        var frame = editor.Playhead;
        var targets = editor.Comp.Layers.Where(layer => editor.Selection.Contains(layer.Id) && !layer.Locked)
            .Select(layer => (Layer: layer, Bounds: LocalBounds(editor.Project, layer, frame)))
            .Where(target => !target.Bounds.IsEmpty).ToArray();
        if (targets.Length == 0) return;
        var property = alignment is LayerAlignment.Left or LayerAlignment.CenterX or LayerAlignment.Right ? "x" : "y";
        var changes = targets.Select(target =>
        {
            var bounds = Bounds(target.Layer, target.Bounds, frame);
            var delta = alignment switch
            {
                LayerAlignment.Left => -bounds.Left,
                LayerAlignment.CenterX => (editor.Comp.Width - bounds.Width) / 2 - bounds.Left,
                LayerAlignment.Right => editor.Comp.Width - bounds.Right,
                LayerAlignment.Top => -bounds.Top,
                LayerAlignment.CenterY => (editor.Comp.Height - bounds.Height) / 2 - bounds.Top,
                _ => editor.Comp.Height - bounds.Bottom
            };
            return (target.Layer, Delta: delta);
        }).Where(target => Math.Abs(target.Delta) > 1e-6).ToArray();
        if (changes.Length == 0) return;
        editor.Transaction("Align layers", () =>
        {
            foreach (var (layer, delta) in changes)
                Editor.SetLayerValue(layer, property, frame, layer.Get(property, frame) + delta);
        });
    }

    public static Rect Bounds(Layer layer, Rect localBounds, long frame)
    {
        var matrix = Transform(layer, frame);
        if (layer.Kind != "Shape") return Rect.Transform(localBounds, matrix);
        var box = Centered(Math.Max(0, layer.Get("width", frame)), Math.Max(0, layer.Get("height", frame)));
        var shape = Shapes.Geometry(layer.Shape, box, layer.Get("radius", frame));
        var offset = new Vector(matrix.OffsetX, matrix.OffsetY);
        matrix.OffsetX = matrix.OffsetY = 0;
        shape.Transform = new MatrixTransform(matrix);
        var bounds = shape.Bounds;
        bounds.Offset(offset);
        return bounds;
    }

    static Rect Centered(double width, double height) => new(-width / 2, -height / 2, width, height);
}
