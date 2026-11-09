using System.Windows;
using System.Windows.Media;

namespace Kuro.UI;
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata("Select", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Icon() { Width = Height = 16; IsHitTestVisible = false; SetResourceReference(StrokeProperty, "Muted"); }
    static readonly Dictionary<string, string> Paths = new()
    {
        ["Select"] = "M4,2 L4,16 L8,12 L11,18 L14,16 L11,10 L17,10 Z",
        ["Text"] = "M3,4 L17,4 M10,4 L10,17 M6,17 L14,17 M3,4 L3,7 M17,4 L17,7",
        ["Shape"] = "M5,3 L15,3 Q17,3 17,5 L17,15 Q17,17 15,17 L5,17 Q3,17 3,15 L3,5 Q3,3 5,3",
        ["Undo"] = "M7,5 L3,9 L7,13 M3,9 L12,9 Q17,9 17,14 L17,16",
        ["Redo"] = "M13,5 L17,9 L13,13 M17,9 L8,9 Q3,9 3,14 L3,16",
        ["Split"] = "M10,2 L10,18 M3,5 L7,5 L7,15 L3,15 M17,5 L13,5 L13,15 L17,15",
        ["Snap"] = "M4,4 L4,11 Q4,17 10,17 Q16,17 16,11 L16,4 L12,4 L12,11 Q12,13 10,13 Q8,13 8,11 L8,4 Z M4,8 L8,8 M12,8 L16,8",
        ["AI"] = "M3,7 L3,4 L7,4 M13,4 L17,4 L17,7 M3,13 L3,16 L7,16 M13,16 L17,16 L17,13 M10,5 L10,15 M5,10 L7,10 L8,8 L12,12 L13,10 L15,10",
        ["Link"] = "M8,7 L12,3 Q14,1 17,4 Q19,6 17,8 L13,12 M12,13 L8,17 Q6,19 3,16 Q1,14 3,12 L7,8 M7,13 L13,7",
        ["Unlink"] = "M11,5 L13,3 Q15,1 17,4 Q19,6 17,8 L15,10 M9,15 L7,17 Q5,19 3,16 Q1,14 3,12 L5,10 M4,4 L16,16",
        ["Export"] = "M10,13 L10,2 M6,6 L10,2 L14,6 M3,12 L3,17 L17,17 L17,12",
        ["Import"] = "M10,2 L10,13 M6,9 L10,13 L14,9 M3,13 L3,18 L17,18 L17,13",
        ["Settings"] = "M8,2 L12,2 L13,5 L16,5 L18,9 L16,11 L16,14 L12,17 L10,16 L7,18 L3,15 L4,12 L2,10 L4,6 L7,6 Z M7,10 A3,3 0 1 0 13,10 A3,3 0 1 0 7,10",
        ["Grid"] = "M3,3 L17,3 L17,17 L3,17 Z M3,10 L17,10 M10,3 L10,17",
        ["Play"] = "M6,3 L16,10 L6,17 Z",
        ["Pause"] = "M6,4 L6,16 M14,4 L14,16",
        ["Previous"] = "M4,4 L4,16 M15,4 L7,10 L15,16 Z",
        ["Next"] = "M16,4 L16,16 M5,4 L13,10 L5,16 Z",
        ["Search"] = "M14,8 A6,6 0 1 0 2,8 A6,6 0 1 0 14,8 M12,13 L18,19",
        ["Chevron"] = "M6,8 L10,12 L14,8",
        ["ChevronRight"] = "M8,6 L12,10 L8,14",
        ["Folder"] = "M2,6 L2,16 L18,16 L18,5 L10,5 L8,3 L2,3 Z",
        ["Video"] = "M3,3 L17,3 L17,17 L3,17 Z M3,7 L17,7 M3,13 L17,13 M7,3 L7,7 M13,3 L13,7 M7,13 L7,17 M13,13 L13,17",
        ["Image"] = "M3,3 L17,3 L17,17 L3,17 Z M3,14 L8,9 L12,13 L15,10 L17,12 M12,6 L13,6",
        ["Audio"] = "M3,8 L3,12 M7,4 L7,16 M11,2 L11,18 M15,6 L15,14 M19,8 L19,12",
        ["Composition"] = "M2,6 L14,6 L14,18 L2,18 Z M6,6 L6,2 L18,2 L18,14 L14,14",
        ["Graph"] = "M3,3 L3,17 L18,17 M4,14 C8,14 8,5 12,5 C14,5 15,12 18,8",
        ["More"] = "M3,10 L4,10 M9,10 L10,10 M15,10 L16,10",
        ["Close"] = "M5,5 L15,15 M15,5 L5,15",
        ["Minimize"] = "M4,10 L16,10",
        ["Maximize"] = "M5,5 L15,5 L15,15 L5,15 Z",
        ["List"] = "M3,4 L4,4 M7,4 L17,4 M3,10 L4,10 M7,10 L17,10 M3,16 L4,16 M7,16 L17,16",
        ["Fit"] = "M3,7 L3,3 L7,3 M13,3 L17,3 L17,7 M17,13 L17,17 L13,17 M7,17 L3,17 L3,13",
        ["Eye"] = "M2,10 Q10,-1 18,10 Q10,21 2,10 Z M7,10 A3,3 0 1 0 13,10 A3,3 0 1 0 7,10",
        ["EyeOff"] = "M3,3 L17,17 M4,8 L2,10 Q6,16 11,15 M8,5 Q13,4 18,10 L16,12",
        ["Lock"] = "M5,9 L15,9 L15,17 L5,17 Z M7,9 L7,6 Q7,2 10,2 Q13,2 13,6 L13,9",
        ["Unlock"] = "M5,9 L15,9 L15,17 L5,17 Z M7,9 L7,6 Q7,2 10,2 Q13,2 13,5",
        ["Volume"] = "M3,8 L6,8 L11,4 L11,16 L6,12 L3,12 Z M14,6 Q18,10 14,14",
        ["VolumeOff"] = "M3,8 L6,8 L11,4 L11,16 L6,12 L3,12 Z M14,8 L18,12 M18,8 L14,12",
        ["Solo"] = "M14,5 L8,5 Q5,5 5,8 Q5,10 8,10 L12,10 Q15,10 15,12 Q15,15 12,15 L6,15",
        ["AlignLeft"] = "M3,3 L3,17 M6,7 L16,7 M6,13 L12,13",
        ["AlignCenterX"] = "M10,3 L10,17 M4,7 L16,7 M6,13 L14,13",
        ["AlignRight"] = "M17,3 L17,17 M4,7 L14,7 M8,13 L14,13",
        ["AlignTop"] = "M3,3 L17,3 M7,6 L7,16 M13,6 L13,12",
        ["AlignCenterY"] = "M3,10 L17,10 M7,4 L7,16 M13,6 L13,14",
        ["AlignBottom"] = "M3,17 L17,17 M7,4 L7,14 M13,8 L13,14",
        ["Marker"] = "M6,3 L14,3 L14,17 L10,14 L6,17 Z"
    };
    static readonly Dictionary<string, Geometry> Geometries = Paths.ToDictionary(entry => entry.Key, entry =>
    {
        var geometry = Geometry.Parse(entry.Value);
        geometry.Freeze();
        return geometry;
    });
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth / 20, ActualHeight / 20));
        Draw(dc, Kind, Stroke ?? Theme.Get("Muted")); dc.Pop();
    }
    internal static void Draw(DrawingContext dc, string kind, Brush stroke)
    {
        if (Geometries.TryGetValue(kind, out var geometry))
            dc.DrawGeometry(null, new Pen(stroke, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
    }
}
