using System.IO;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Kuro.Core;
public record PresetInfo(string Name, string Path, string Status, string[] Signatures);
public record WigglySelector(double Minimum, double Maximum, double Frequency, int Mode, double Seed)
{
    public double Combine(double range, double seconds, int unit, int layerSeed)
    {
        var position = seconds * Frequency;
        var step = Math.Floor(position);
        var t = position - step;
        t = t * t * (3 - 2 * t);
        var amount = Sample(step) * (1 - t) + Sample(step + 1) * t;
        return Mode == 6 ? Math.Clamp(Math.Abs(range - amount), 0, 1) : Math.Clamp(range * amount, 0, 1);

        double Sample(double tick)
        {
            var noise = Math.Sin(tick * 127.1 + unit * 311.7 + (Seed == 0 ? layerSeed : Seed)) * 43758.5453;
            return (Minimum + (Maximum - Minimum) * (noise - Math.Floor(noise))) / 100;
        }
    }
}
public record TextMotion(Curve Animation, string Channel, double Start, double End, double Offset,
    int Unit, int Shape, double EaseHigh, double EaseLow, double Smoothness, double X, double Y,
    double ScaleX, double ScaleY, double Opacity, double Blur, double Tracking, WigglySelector? Wiggle)
{
    public const double TicksPerSecond = 24000;
    public const string Property = "presetProgress";
    public double Duration => Animation.Keys[^1].Frame / TicksPerSecond;

    public Curve AtRate(FrameRate fps, long firstFrame, long lastFrame)
    {
        var curve = new Curve(Animation.Keys[0].Value);
        var naturalSpan = Math.Max(1, fps.Frames(Duration));
        var span = Math.Min(naturalSpan, lastFrame - firstFrame);
        foreach (var source in Animation.Keys)
        {
            var key = Data.Clone(source);
            key.Id = Data.Id();
            key.Frame = firstFrame + (long)Math.Round(span * source.Frame / (Duration * TicksPerSecond));
            curve.Keys.RemoveAll(k => k.Frame == key.Frame);
            curve.Keys.Add(key);
        }
        return curve;
    }

    public double Weight(double position, double unitWidth, double value)
    {
        var offset = Channel == "Percent Offset" ? value : Offset;
        var start = (Channel == "Percent Start" ? value : Start) + offset;
        var end = End + offset;
        if (Shape == 1)
        {
            var feather = unitWidth * Smoothness / 100;
            if (feather < .001) return position >= start && position <= end ? 1 : 0;
            return Math.Clamp((position - start) / feather + .5, 0, 1) *
                Math.Clamp((end - position) / feather + .5, 0, 1);
        }
        var t = Math.Clamp((position - start) / Math.Max(.001, end - start), 0, 1);
        if (t is 0 or 1 || (EaseLow == 0 && EaseHigh == 0)) return t;
        // Map the selector easing to endpoint tangents while preserving its full range.
        var low = 1 - EaseLow / 100;
        var high = 1 - EaseHigh / 100;
        return Math.Clamp(Curve.Bezier(t, low / 3, 1 - high / 3), 0, 1);
    }
}
public static class Presets
{
    static readonly ConcurrentDictionary<string, TextMotion?> motions = new();
    public static TextMotion? Motion(string name) => motions.GetOrAdd(name, LoadMotion);

    static TextMotion? LoadMotion(string name)
    {
        if (name.Length == 0 || Path.GetFileName(name) != name) return null;
        var path = Path.Combine(Root(), "True Motion", name + ".ffx");
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var raw = Encoding.Latin1.GetString(bytes);
        if (!raw.StartsWith("RIFX", StringComparison.Ordinal)) return null;
        var properties = Regex.Matches(raw, "tdmn....([^\x00]+)", RegexOptions.Singleline);
        var constants = new Dictionary<string, double[]>();
        var animations = new Dictionary<string, Curve>();
        for (var i = 0; i < properties.Count; i++)
        {
            var match = properties[i];
            const string prefix = "ADBE Text ";
            if (!match.Groups[1].Value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var property = match.Groups[1].Value[prefix.Length..];
            var limit = i + 1 < properties.Count ? properties[i + 1].Index : raw.Length;
            int Find(string chunk) => raw.IndexOf(chunk, match.Index + match.Length, limit - match.Index - match.Length, StringComparison.Ordinal);
            var data = Find("cdat");
            if (data >= 0 && data + 8 <= limit)
            {
                var length = ReadInt(data + 4);
                if (length > 0 && length <= limit - data - 8 && length % 8 == 0)
                    constants[property] = Enumerable.Range(0, length / 8).Select(n => ReadDouble(data + 8 + n * 8)).ToArray();
            }
            var list = Find("ldat");
            var header = Find("lhd3");
            if (list < 0 || header < 0 || header + 28 > limit || list + 8 > limit) continue;
            var stride = ReadInt(header + 24);
            var size = ReadInt(list + 4);
            if (stride != 48 || size <= 0 || size > limit - list - 8 || size % stride != 0) continue;
            var curve = new Curve();
            for (var at = list + 8; at < list + 8 + size; at += stride)
            {
                var bezier = bytes[at + 5] == 2;
                curve.Keys.Add(new Keyframe
                {
                    Frame = ReadInt(at), Value = ReadDouble(at + 8), Interpolation = bezier ? "Bezier" : "Linear",
                    OutX = bezier ? Math.Clamp(ReadDouble(at + 40), 0, 1) : 1.0 / 3,
                    InX = bezier ? 1 - Math.Clamp(ReadDouble(at + 24), 0, 1) : 2.0 / 3,
                    OutY = 0, InY = 1
                });
            }
            animations[property] = curve;
        }
        var channel = new[] { "Percent Offset", "Percent Start", "Opacity" }.FirstOrDefault(animations.ContainsKey);
        if (channel == null || animations[channel].Keys.Count < 2 || animations[channel].Keys[^1].Frame <= 0) return null;
        var wiggle = constants.ContainsKey("Wiggly Max Amount")
            ? new WigglySelector(Get("Wiggly Min Amount"), Get("Wiggly Max Amount"), Get("Temporal Freq"), (int)Get("Selector Mode"), Get("Wiggly Random Seed"))
            : null;
        return new TextMotion(animations[channel], channel, Get("Percent Start"), Get("Percent End", 100), Get("Percent Offset"),
            (int)Get("Range Type2", 1), (int)Get("Range Shape", 1), Get("Levels Max Ease"), Get("Levels Min Ease"),
            Get("Selector Smoothness", 100), Get("Position 3D"), Get("Position 3D", axis: 1),
            Get("Scale 3D", 100), Get("Scale 3D", 100, 1), Get("Opacity", 100),
            Math.Max(Get("Blur"), Get("Blur", axis: 1)), Get("Tracking Amount"), wiggle);

        int ReadInt(int at) => BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at, 4));
        double ReadDouble(int at) => BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(at, 8));
        double Get(string property, double fallback = 0, int axis = 0) =>
            constants.TryGetValue(property, out var values) && axis < values.Length && double.IsFinite(values[axis]) ? values[axis] : fallback;
    }
    public static string Root()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent) if (Directory.Exists(System.IO.Path.Combine(d.FullName, "True Motion"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
    public static List<PresetInfo> Discover()
    {
        var folder = System.IO.Path.Combine(Root(), "True Motion"); if (!Directory.Exists(folder)) return [];
        return Directory.GetFiles(folder, "*.ffx").Order().Select(path =>
        {
            var bytes = File.ReadAllBytes(path); var valid = bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFX" && Encoding.ASCII.GetString(bytes, 8, 4) == "FaFX";
            var signatures = Regex.Matches(Encoding.ASCII.GetString(bytes), "[ -~]{10,}").Select(m => m.Value).Where(v => v.Contains("ADBE") || v.Contains("random") || v.Contains("wiggle") || v.Contains("expression")).Distinct().ToArray();
            var name = Path.GetFileNameWithoutExtension(path);
            return new PresetInfo(name, path, valid && Motion(name) != null ? "Authored keyframes · native rendering" : "Unsupported preset", signatures);
        }).ToList();
    }
}
