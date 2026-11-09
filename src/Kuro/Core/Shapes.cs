using System.Windows;
using System.Windows.Media;

namespace Kuro.Core;

public static class Shapes
{
    public static readonly string[] Kinds = ["Rectangle", "Rounded rectangle", "Square", "Ellipse", "Circle", "Triangle", "Star"];

    public static Rect Bounds(string kind, Point start, Point end, bool constrain)
    {
        if (constrain || kind is "Circle" or "Square")
        {
            var size = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
            end = new Point(start.X + (end.X < start.X ? -size : size), start.Y + (end.Y < start.Y ? -size : size));
        }
        return new Rect(start, end);
    }

    public static Geometry Geometry(string kind, Rect bounds, double radius = 20)
    {
        if (kind is "Circle" or "Ellipse") return new EllipseGeometry(bounds);
        if (kind is not ("Star" or "Triangle"))
        {
            radius = kind == "Rounded rectangle" ? Math.Max(0, radius) : 0;
            return new RectangleGeometry(bounds, radius, radius);
        }
        var path = new StreamGeometry();
        using var context = path.Open();
        var count = kind == "Star" ? 10 : 3;
        for (var i = 0; i < count; i++)
        {
            var angle = Math.PI * (2.0 * i / count - .5);
            var inset = count == 10 && i % 2 == 1 ? .45 : 1;
            var point = new Point(bounds.X + bounds.Width * (1 + inset * Math.Cos(angle)) / 2,
                bounds.Y + bounds.Height * (1 + inset * Math.Sin(angle)) / 2);
            if (i == 0) context.BeginFigure(point, true, true);
            else context.LineTo(point, true, false);
        }
        return path;
    }
}
