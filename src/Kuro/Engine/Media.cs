using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Kuro.Core;

namespace Kuro.Engine;
public static class Processes
{
    public static string Number(double n) => n.ToString("0.#########", CultureInfo.InvariantCulture);
    public static string Locate(string name)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe");
        if (File.Exists(bundled)) return bundled;
        var configured = Environment.GetEnvironmentVariable("KURO_FFMPEG_DIR");
        if (configured != null && File.Exists(Path.Combine(configured, name + ".exe"))) return Path.Combine(configured, name + ".exe");
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", name + ".exe");
        if (File.Exists(winget)) return winget;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (File.Exists(Path.Combine(folder.Trim('"'), name + ".exe"))) return Path.Combine(folder.Trim('"'), name + ".exe");
        throw new FileNotFoundException($"{name}.exe is missing. Install FFmpeg and add its bin folder to PATH, or set KURO_FFMPEG_DIR.");
    }
    public static Process Start(string name, IEnumerable<string> args, bool input = false, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(Locate(name)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input };
        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;
        foreach (var a in args) info.ArgumentList.Add(a);
        return Process.Start(info) ?? throw new IOException("Could not start " + name);
    }
    public static async Task<string> Run(string name, IEnumerable<string> args, CancellationToken token = default, Action<string>? progress = null, string? workingDirectory = null)
    {
        using var process = Start(name, args, workingDirectory: workingDirectory); using var cancel = token.Register(() => Kill(process));
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = new StringBuilder();
        while (await process.StandardError.ReadLineAsync(token).ConfigureAwait(false) is { } line) { if (errors.Length > 8000) errors.Remove(0, 4000); errors.AppendLine(line); progress?.Invoke(line); }
        await process.WaitForExitAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException($"{name} failed: {errors}");
        return await output.ConfigureAwait(false);
    }
    public static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }
}
public static class Media
{
    public static async Task<Asset> Probe(string path, CancellationToken token = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Choose a local media file.", path);
        var json = await Processes.Run("ffprobe", ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_format", "-show_streams", "-of", "json", path], token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var visual = streams.FirstOrDefault(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "video");
        var audio = streams.Any(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "audio");
        if (visual.ValueKind == JsonValueKind.Undefined && !audio) throw new InvalidDataException("This file has no supported video, image, or audio stream.");
        double duration = 0; var format = root.GetProperty("format");
        if (format.TryGetProperty("duration", out var d)) double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out duration);
        var still = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(path).ToLowerInvariant());
        var a = new Asset { Path = Path.GetFullPath(path), Name = Path.GetFileName(path), HasAudio = audio, Duration = still ? 5 : duration, Kind = still ? "Image" : visual.ValueKind == JsonValueKind.Undefined ? "Audio" : "Video" };
        if (visual.ValueKind != JsonValueKind.Undefined)
        {
            a.Width = visual.GetProperty("width").GetInt32(); a.Height = visual.GetProperty("height").GetInt32();
            if (visual.TryGetProperty("duration", out var videoDuration) && double.TryParse(videoDuration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var visualSeconds) && visualSeconds > 0)
                a.VideoDuration = visualSeconds;
            if (visual.TryGetProperty("pix_fmt", out var formatValue) && formatValue.GetString() is { } pixelFormat)
                a.HasAlpha = pixelFormat is "argb" or "abgr" || pixelFormat.StartsWith("rgba") || pixelFormat.StartsWith("bgra")
                    || pixelFormat.StartsWith("yuva") || pixelFormat.StartsWith("gbrap") || pixelFormat.StartsWith("ya");
            if (visual.TryGetProperty("avg_frame_rate", out var fps)) { var p = (fps.GetString() ?? "0/1").Split('/'); if (p.Length == 2 && double.TryParse(p[0], CultureInfo.InvariantCulture, out var n) && double.TryParse(p[1], CultureInfo.InvariantCulture, out var den) && den > 0) a.FrameRate = n / den; }
        }
        if (!still && a.Duration <= 0) throw new InvalidDataException("Could not determine media duration. Live streams are not supported.");
        return a;
    }
    public static string CacheKey(Asset a) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(a.Path + File.GetLastWriteTimeUtc(a.Path).Ticks)))[..24];
    public static async Task<string?> Thumbnail(Asset asset, CancellationToken token = default)
    {
        if (asset.Kind is "Audio" or "Composition" || asset.Missing) return null;
        var path = Path.Combine(Store.Cache, CacheKey(asset) + ".png");
        if (!File.Exists(path))
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            if (asset.Kind == "Video") args.AddRange(["-ss", Processes.Number(Math.Min(asset.Duration * .1, 1))]);
            args.AddRange(["-protocol_whitelist", "file,pipe", "-i", asset.Path, "-frames:v", "1", "-vf", "scale=256:144:force_original_aspect_ratio=decrease,pad=256:144:(ow-iw)/2:(oh-ih)/2", path]);
            await Processes.Run("ffmpeg", args, token);
            if (!File.Exists(path)) throw new InvalidDataException("The source did not produce a thumbnail frame.");
        }
        return path;
    }
    public static async Task<float[]> Waveform(Asset asset, CancellationToken token = default)
    {
        if (!asset.HasAudio || asset.Missing) return [];
        using var p = Processes.Start("ffmpeg", ["-v", "error", "-protocol_whitelist", "file,pipe", "-i", asset.Path, "-vn", "-ac", "1", "-ar", "1000", "-f", "f32le", "pipe:1"]);
        using var registration = token.Register(() => Processes.Kill(p)); var error = p.StandardError.ReadToEndAsync();
        var bins = new List<float>(); var buffer = new byte[4000];
        while (true) { var count = await p.StandardOutput.BaseStream.ReadAtLeastAsync(buffer, buffer.Length, false, token); if (count == 0) break; for (var offset = 0; offset + 4 <= count; offset += 40) { float peak = 0; for (var i = offset; i < Math.Min(count - 3, offset + 40); i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i))); bins.Add(peak); } }
        await p.WaitForExitAsync(token); if (p.ExitCode != 0) throw new IOException(await error); return bins.ToArray();
    }
    public static BitmapSource LoadBitmap(string path)
    {
        using var s = File.OpenRead(path); var b = BitmapFrame.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); b.Freeze(); return b;
    }
}
public sealed class Decoder : IDisposable
{
    Process? process;
    Task<string>? stderr;
    string signature = "";
    double lastTime = double.NegativeInfinity;
    double? videoDuration;
    bool softwareOnly;
    byte[] pixels = [];
    readonly Dictionary<long, BitmapSource> cache = [];
    readonly Queue<long> order = new();

    public BitmapSource Read(Asset asset, double seconds, int width, int height, double fps, CancellationToken token,
        bool preview = false, bool hardware = false, bool scrubbing = false)
    {
        var key = $"{asset.Path}|{width}|{height}|{fps:R}|{hardware}";
        if (signature != key)
        {
            CloseStream();
            signature = key;
            videoDuration = asset.VideoDuration;
            softwareOnly = !hardware || asset.HasAlpha;
            cache.Clear();
            order.Clear();
        }

        var sourceStep = 1 / (asset.FrameRate > 0 ? asset.FrameRate : fps);
        var maxTime = Math.Max(0, (videoDuration ?? asset.Duration) - sourceStep);
        seconds = Math.Clamp(seconds, 0, maxTime);
        token.ThrowIfCancellationRequested();
        if (cache.TryGetValue(FrameKey(seconds), out var hit)) return hit;

        var length = checked(width * height * 4);
        if (pixels.Length != length) pixels = new byte[length];
        var capacity = preview ? Math.Clamp(10 * 1024 * 1024 / length, 2, 48) : 1;
        var scale = $"scale={width}:{height}" + (preview ? ":flags=fast_bilinear" : "");
        var fpsFilter = $"fps={Processes.Number(fps)}:eof_action=pass";
        var filter = preview ? fpsFilter + "," + scale : scale + "," + fpsFilter;
        var distance = (seconds - lastTime) * fps;
        var steps = distance is >= .99 and <= 24.01 ? (int)Math.Round(distance) : 0;
        var first = lastTime + 1 / fps;
        if (process == null || steps == 0 || Math.Abs(distance - steps) > .015)
        {
            var preroll = scrubbing && seconds < lastTime && lastTime - seconds < 1
                ? Math.Min(6, (int)Math.Floor(seconds * fps)) : 0;
            first = seconds - preroll / fps;
            Start(first, filter, !softwareOnly);
            steps = preroll + 1;
        }

        var frame = ReadFrames(first, steps);
        if (frame != null) return frame;
        var error = stderr?.IsCompletedSuccessfully == true ? stderr.Result.Trim() : "";

        // Restart on an interrupted pipe or rejected hardware session. Keep the next stream open for sequential frames.
        softwareOnly = true;
        Start(seconds, filter, hardwareDecode: false);
        frame = ReadFrames(seconds, 1);
        if (frame != null) return frame;

        // The container can outlast its video stream. Old projects have only the container duration.
        videoDuration ??= Media.Probe(asset.Path, token).GetAwaiter().GetResult().VideoDuration ?? asset.Duration;
        var tail = Math.Max(0, videoDuration.Value - sourceStep);
        var seek = Math.Min(seconds, tail);
        Start(seek, scale, hardwareDecode: false, singleFrame: true);
        frame = ReadFrames(seek, 1);
        if (frame == null && seconds >= tail - .001)
        {
            // Variable-rate sources may have a longer final sample. Decode the tail once and hold its last complete frame.
            var begin = Math.Max(0, videoDuration.Value - 1);
            Start(begin, scale, hardwareDecode: false);
            while (ReadPixels())
                frame = MakeBitmap();
        }
        CloseStream();
        if (frame == null)
            throw new IOException($"Could not decode {asset.Name} at {seconds:0.###}s." + (error.Length > 0 ? " " + error : ""));
        Remember(FrameKey(Math.Min(seconds, tail)), frame);
        return frame;

        void Start(double at, string filters, bool hardwareDecode, bool singleFrame = false)
        {
            CloseStream();
            string[] accelerator = hardwareDecode ? ["-hwaccel", "auto"] : [];
            string[] limit = singleFrame ? ["-frames:v", "1"] : [];
            process = Processes.Start("ffmpeg", ["-v", "error", "-threads", "4", "-filter_threads", "2", .. accelerator,
                "-ss", Processes.Number(at), "-protocol_whitelist", "file,pipe", "-i", asset.Path, "-map", "0:v:0",
                "-an", "-sn", "-vf", filters, .. limit, "-threads", "2", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"]);
            stderr = process.StandardError.ReadToEndAsync();
        }

        bool ReadPixels()
        {
            var current = process!;
            using var cancel = token.Register(() => Processes.Kill(current));
            try
            {
                token.ThrowIfCancellationRequested();
                var read = current.StandardOutput.BaseStream.ReadAtLeast(pixels, length, false);
                token.ThrowIfCancellationRequested();
                return read == length;
            }
            catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or IOException)
            {
                CloseStream();
                throw new OperationCanceledException(token);
            }
        }

        BitmapSource? ReadFrames(double time, int count)
        {
            BitmapSource? bitmap = null;
            for (var i = 0; i < count; i++)
            {
                if (!ReadPixels()) return null;
                lastTime = time + i / fps;
                if (!preview && i < count - 1) continue;
                bitmap = MakeBitmap();
                Remember(FrameKey(lastTime), bitmap);
            }
            return bitmap;
        }

        BitmapSource MakeBitmap()
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        void Remember(long timestamp, BitmapSource bitmap)
        {
            if (cache.TryAdd(timestamp, bitmap)) order.Enqueue(timestamp);
            while (order.Count > capacity) cache.Remove(order.Dequeue());
        }
    }

    static long FrameKey(double seconds) => (long)Math.Round(seconds * 1_000_000);

    void CloseStream()
    {
        if (process != null)
        {
            Processes.Kill(process);
            process.Dispose();
            process = null;
        }
        lastTime = double.NegativeInfinity;
        stderr = null;
    }

    public void Dispose()
    {
        CloseStream();
        cache.Clear();
        order.Clear();
    }
}
