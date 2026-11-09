using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace Kuro.UI;

internal sealed class PropertyScrub
{
    readonly Point origin;
    readonly double start;
    readonly string property;
    bool? horizontal;
    public double Value { get; private set; }
    public PropertyScrub(Point origin, double start, string property)
    {
        this.origin = origin;
        this.start = start;
        this.property = property;
        Value = start;
    }
    public bool HasMoved => horizontal.HasValue;
    public bool Update(Point point)
    {
        var delta = point - origin;
        if (!horizontal.HasValue && delta.Length < 4) return false;
        horizontal ??= Math.Abs(delta.X) >= Math.Abs(delta.Y);
        var speed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .1 : 1;
        var next = start + (horizontal.Value ? delta.X : -delta.Y) * speed;
        Value = property == "opacity" ? Math.Clamp(next, 0, 100) : next;
        return true;
    }
    public static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    public static string Label(string key) => key switch
    {
        "x" => "Position X", "y" => "Position Y", "anchorX" => "Anchor X", "anchorY" => "Anchor Y",
        "scaleX" => "Scale X", "scaleY" => "Scale Y", "fontSize" => "Font size", "presetProgress" => "True Motion", _ => char.ToUpperInvariant(key[0]) + key[1..]
    };
}
