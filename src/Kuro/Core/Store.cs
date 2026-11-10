using System.IO;
using System.Text.Json;

namespace Kuro.Core;
public static class Store
{
    public const string ProjectExtension = ".kr";
    public const string ProjectFilter = "Kuro projects (*.kr;*.kuro)|*.kr;*.kuro";
    public static bool IsProjectPath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".kr" or ".kuro";
    public static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kuro");
    public static string Cache { get { var p = Path.Combine(Home, "Cache"); Directory.CreateDirectory(p); return p; } }
    public static void AtomicWrite(string path, string content)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream)) { writer.Write(content); writer.Flush(); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Save(Project project, string path)
    {
        var copy = Data.Clone(project); var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var asset in copy.Assets.Where(a => a.Kind != "Composition")) asset.Path = Path.GetRelativePath(folder, asset.Path);
        AtomicWrite(path, JsonSerializer.Serialize(copy, Data.Json));
    }
    public static Project Load(string path)
    {
        var p = JsonSerializer.Deserialize<Project>(File.ReadAllText(path), Data.Json) ?? throw new InvalidDataException("The project is empty.");
        if (p.Version != 1) throw new InvalidDataException($"Project version {p.Version} is not supported by this build.");
        if (p.Compositions.Count == 0 || p.Compositions.Any(c => c.Width < 2 || c.Height < 2 || c.Fps.Numerator <= 0 || c.Fps.Denominator <= 0 || c.Duration < 1)) throw new InvalidDataException("Invalid composition settings.");
        foreach (var comp in p.Compositions)
        {
            if (!ProjectColors.TryParse(comp.Background, out _)) comp.Background = ProjectColors.DefaultBackground;
            foreach (var layer in comp.Layers.Where(layer => layer.Kind is "Text" or "Shape"))
                if (!ProjectColors.TryParse(layer.Fill, out _)) layer.Fill = ProjectColors.FillFallback(layer);
        }
        foreach (var a in p.Assets.Where(a => a.Kind != "Composition")) a.Path = Path.GetFullPath(a.Path, Path.GetDirectoryName(Path.GetFullPath(path))!);
        return p;
    }
}
public class Preferences
{
    public static Preferences Current { get; set; } = new();
    public string Theme { get; set; } = "Dark";
    public int DesignVersion { get; set; }
    public bool ReduceMotion { get; set; }
    public int LayoutVersion { get; set; }
    public double LeftWidth { get; set; } = 286;
    public double RightWidth { get; set; } = 318;
    public double AIWidth { get; set; } = 340;
    public double TimelineHeight { get; set; } = 322;
    public int PreviewDivisor { get; set; } = 2;
    public bool HardwarePreview { get; set; } = true;
    public bool Loop { get; set; } = true;
    public string ChatEndpoint { get; set; } = "";
    public string ChatModel { get; set; } = "";
    public bool ChatVerified { get; set; }
    public List<string> Recent { get; set; } = [];
    public Dictionary<string, string> Shortcuts { get; set; } = [];
    public void Save() => Store.AtomicWrite(Path.Combine(Store.Home, "settings.json"), JsonSerializer.Serialize(this, Data.Json));
    public static Preferences Load()
    { try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Store.Home, "settings.json")), Data.Json) ?? new(); } catch { return new(); } }
}
