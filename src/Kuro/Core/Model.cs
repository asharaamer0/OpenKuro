using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuro.Core;

public static class Data
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    public static string Id() => Guid.NewGuid().ToString("N");
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}
public record FrameRate(int Numerator = 30, int Denominator = 1)
{
    [JsonIgnore] public double Value => (double)Numerator / Denominator;
    public double Seconds(long frame) => (double)frame * Denominator / Numerator;
    public long Frames(double seconds) => (long)Math.Round(seconds * Numerator / Denominator, MidpointRounding.AwayFromZero);
    public string Timecode(long frame)
    {
        var fps = (int)Math.Ceiling(Value); var f = Math.Abs(frame);
        return $"{(frame < 0 ? "−" : "")}{f / (fps * 3600):00}:{f / (fps * 60) % 60:00}:{f / fps % 60:00}:{f % fps:00}";
    }
}
public class Keyframe
{
    public string Id { get; set; } = Data.Id();
    public long Frame { get; set; }
    public double Value { get; set; }
    public string Interpolation { get; set; } = "Linear";
    public double OutX { get; set; } = .333333;
    public double OutY { get; set; } = .333333;
    public double InX { get; set; } = .666667;
    public double InY { get; set; } = .666667;
}
public class Curve
{
    public double Value { get; set; }
    public List<Keyframe> Keys { get; set; } = [];
    public Curve() { }
    public Curve(double value) { Value = value; }
    public double At(double frame)
    {
        if (Keys.Count == 0) return Value;
        var ordered = Keys.OrderBy(k => k.Frame).ToArray();
        if (frame <= ordered[0].Frame) return ordered[0].Value;
        if (frame >= ordered[^1].Frame) return ordered[^1].Value;
        var right = Array.FindIndex(ordered, k => k.Frame > frame); var a = ordered[right - 1]; var b = ordered[right];
        if (a.Interpolation == "Hold") return a.Value;
        var x = (frame - a.Frame) / (b.Frame - a.Frame);
        if (a.Interpolation == "Bezier")
        {
            double lo = 0, hi = 1, t = x;
            for (var i = 0; i < 30; i++) { t = (lo + hi) * .5; if (Bezier(t, Math.Clamp(a.OutX, 0, 1), Math.Clamp(b.InX, 0, 1)) < x) lo = t; else hi = t; }
            x = Bezier(t, a.OutY, b.InY);
        }
        return a.Value + (b.Value - a.Value) * x;
    }
    public double Speed(double frame, FrameRate rate) => (At(frame + .01) - At(frame - .01)) / .02 * rate.Value;
    public static double Bezier(double t, double a, double b) => 3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;
    public void Set(long frame, double value)
    {
        var key = Keys.Find(k => k.Frame == frame);
        if (key == null) Keys.Add(new Keyframe { Frame = frame, Value = value }); else key.Value = value;
        Keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
    }
}
public class Asset
{
    public string Id { get; set; } = Data.Id();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "Video";
    public string Bin { get; set; } = "Media";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Duration { get; set; }
    public double FrameRate { get; set; }
    public bool HasAudio { get; set; }
    public bool HasAlpha { get; set; }
    [JsonIgnore] public bool Missing => Kind != "Composition" && !File.Exists(Path);
    [JsonIgnore] public string Summary => Missing ? "Missing · relink source" : Kind == "Audio" ? $"Audio · {Duration:0.0}s" : $"{Width} × {Height} · {(Kind == "Image" ? "Still" : $"{FrameRate:0.##} fps · {Duration:0.0}s")}";
}
public class Layer
{
    public string Id { get; set; } = Data.Id();
    public string Name { get; set; } = "Layer";
    public string Kind { get; set; } = "Video";
    public string? AssetId { get; set; }
    public string? CompositionId { get; set; }
    public long Start { get; set; }
    public long Duration { get; set; } = 150;
    public double SourceIn { get; set; }
    public double Rate { get; set; } = 1;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public bool Audio { get; set; } = true;
    public bool Solo { get; set; }
    public string Color { get; set; } = "#757B8A";
    public string Text { get; set; } = "Your next story.";
    public string Font { get; set; } = "Segoe UI";
    public int FontWeight { get; set; } = 400;
    public string Fill { get; set; } = ProjectColors.DefaultFill;
    public string Shape { get; set; } = "Rectangle";
    public string Blend { get; set; } = "Normal";
    public string Preset { get; set; } = "";
    public double PresetDuration { get; set; } = 1;
    public bool PositionLinked { get; set; } = true;
    public bool ScaleLinked { get; set; } = true;
    public int Seed { get; set; } = 42;
    public double Volume { get; set; } = 1;
    public Dictionary<string, Curve> Properties { get; set; } = new()
    {
        ["x"] = new(960), ["y"] = new(540), ["anchorX"] = new(0), ["anchorY"] = new(0),
        ["scaleX"] = new(100), ["scaleY"] = new(100), ["rotation"] = new(0), ["opacity"] = new(100),
        ["fontSize"] = new(96), ["blur"] = new(0), ["glow"] = new(0), ["shadow"] = new(0),
        ["width"] = new(480), ["height"] = new(240), ["radius"] = new(16)
    };
    [JsonIgnore] public long End => Start + Duration;
    public double Get(string property, double frame) => Properties.TryGetValue(property, out var c) ? c.At(frame) : 0;
    public string? LinkedAxis(string? property) => property switch
    {
        "x" when PositionLinked => "y",
        "y" when PositionLinked => "x",
        "scaleX" when ScaleLinked => "scaleY",
        "scaleY" when ScaleLinked => "scaleX",
        _ => null
    };
    public double ProportionalValue(string property, string other, long frame, double value)
    {
        var current = Get(property, frame);
        var baseline = Get(other, frame);
        return Math.Abs(current) < 1e-8 ? baseline + value - current : baseline * (value / current);
    }
    public double SourceTime(double frame, FrameRate fps)
    {
        if (Properties.TryGetValue("time", out var c) && c.Keys.Count > 0) return c.At(frame);
        return SourceIn + fps.Seconds((long)frame - Start) * Rate;
    }
}
public class Composition
{
    public override string ToString() => Name;
    public string Id { get; set; } = Data.Id();
    public string Name { get; set; } = "Composition 01";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public FrameRate Fps { get; set; } = new();
    public long Duration { get; set; } = 450;
    public long WorkStart { get; set; }
    public long WorkEnd { get; set; } = 450;
    public string Background { get; set; } = ProjectColors.DefaultBackground;
    public List<Layer> Layers { get; set; } = [];
    public List<long> Markers { get; set; } = [];
}
public class Project
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Data.Id();
    public string Name { get; set; } = "Untitled";
    public long Revision { get; set; }
    public List<Asset> Assets { get; set; } = [];
    public List<Composition> Compositions { get; set; } = [new()];
    public string ActiveComposition { get; set; } = "";
    [JsonIgnore] public Composition Active => Compositions.Find(c => c.Id == ActiveComposition) ?? Compositions[0];
}
