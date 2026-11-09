using System.Windows.Media;

namespace Kuro.Core;

internal static class ProjectColors
{
    public const string DefaultFill = "#FCFAF6";
    public const string DefaultShapeFill = "#DE8551";
    public const string DefaultBackground = "#161616";

    public static string FillFallback(Layer layer) => layer.Kind == "Shape" ? DefaultShapeFill : DefaultFill;

    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            if (ColorConverter.ConvertFromString(text) is not Color parsed) return false;
            color = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }
}
