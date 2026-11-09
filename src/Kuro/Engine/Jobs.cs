using System.Diagnostics;
using System.IO;
using System.Text;
using Kuro.Core;

namespace Kuro.Engine;
public record JobProgress(string Stage, double Fraction, long Frame, long Total, TimeSpan Elapsed)
{
    public string Summary => $"{Stage} · {Frame:N0} / {Total:N0} frames · {Fraction:P0}";
    public string Remaining => Fraction > .02 ? TimeSpan.FromSeconds(Elapsed.TotalSeconds * (1 - Fraction) / Fraction).ToString(@"mm\:ss") : "Estimating…";
}
public record ExportOptions(string Path, int Width, int Height, int Crf = 18, bool WorkArea = false, int AudioKbps = 320);
public static class Exporter
{
    public static Task Export(Project project, ExportOptions options, IProgress<JobProgress>? progress, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { Render(project, options, progress, token); completion.SetResult(); } catch (OperationCanceledException) { completion.SetCanceled(token); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true, Name = "Kuro · export compositor" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    static void Render(Project project, ExportOptions o, IProgress<JobProgress>? progress, CancellationToken token)
    {
        if (o.Width < 2 || o.Height < 2 || o.Width % 2 != 0 || o.Height % 2 != 0 || o.Width > 7680 || o.Height > 4320) throw new InvalidOperationException("MP4 dimensions must be even, between 2 and 7680 × 4320.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(o.Path))!; if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing destination folder.");
        var c = project.Active; var start = o.WorkArea ? c.WorkStart : 0; var end = o.WorkArea ? c.WorkEnd : c.Duration;
        if (end <= start) throw new InvalidOperationException("The export range is empty.");
        var estimate = c.Fps.Seconds(end - start) * (o.Width >= 3840 ? 8_000_000 : 3_000_000);
        var drive = new DriveInfo(Path.GetPathRoot(folder)!); if (drive.IsReady && drive.AvailableFreeSpace < estimate + 100_000_000) throw new IOException("The destination needs more free space for the export and temporary files.");
        var partial = Path.Combine(folder, Path.GetFileNameWithoutExtension(o.Path) + "." + Data.Id()[..8] + ".partial.mp4");
        var silent = Path.Combine(folder, Path.GetFileNameWithoutExtension(partial) + ".video.mp4");
        var watch = Stopwatch.StartNew(); using var renderer = new Renderer();
        try
        {
            using (var process = Processes.Start("ffmpeg", ["-v", "error", "-y", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{o.Width}x{o.Height}", "-framerate", $"{c.Fps.Numerator}/{c.Fps.Denominator}", "-i", "pipe:0", "-an", "-c:v", "libx264", "-preset", "fast", "-crf", o.Crf.ToString(), "-pix_fmt", "yuv420p", "-vf", "scale=out_color_matrix=bt709:out_range=tv,setsar=1", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-movflags", "+faststart", silent], true))
            {
                using var cancel = token.Register(() => Processes.Kill(process)); var errors = process.StandardError.ReadToEndAsync();
                var data = new byte[checked(o.Width * o.Height * 4)];
                for (var frame = start; frame < end; frame++)
                {
                    token.ThrowIfCancellationRequested(); var bitmap = renderer.Render(project, c, frame, o.Width, o.Height, token); bitmap.CopyPixels(data, o.Width * 4, 0);
                    process.StandardInput.BaseStream.Write(data); progress?.Report(new("Rendering", (double)(frame - start + 1) / (end - start) * .94, frame - start + 1, end - start, watch.Elapsed));
                }
                process.StandardInput.Close(); process.WaitForExit(); token.ThrowIfCancellationRequested(); if (process.ExitCode != 0) throw new IOException("Video encoding failed: " + errors.GetAwaiter().GetResult());
            }
            progress?.Report(new("Mixing audio", .95, end - start, end - start, watch.Elapsed));
            MixAudio(project, c, silent, partial, start, end, o.AudioKbps, token);
            token.ThrowIfCancellationRequested(); File.Move(partial, o.Path, true); progress?.Report(new("Complete", 1, end - start, end - start, watch.Elapsed));
        }
        finally { if (File.Exists(silent)) File.Delete(silent); if (File.Exists(partial)) File.Delete(partial); }
    }
    // Recurses precompositions and keeps source audio in the same frame-based time domain.
    public static Task PreviewAudio(Project p, string output, CancellationToken token) => Task.Run(() => MixAudio(p, p.Active, null, output, 0, p.Active.Duration, 320, token), token);
    static void MixAudio(Project p, Composition c, string? video, string output, long start, long end, int bitrate, CancellationToken token)
    {
        var args = new List<string> { "-v", "error", "-y" }; if (video != null) args.AddRange(["-i", video]); var filters = new List<string>(); var labels = new List<string>(); var duration = c.Fps.Seconds(end - start); int input = video == null ? 0 : 1;
        void Collect(Composition comp, double offset, double sourceOffset, double window, int depth)
        {
            if (depth > 12) return; var solo = comp.Layers.Any(l => l.Solo);
            foreach (var l in comp.Layers.Where(l => l.Audio && (!solo || l.Solo)))
            {
                var begin = offset + comp.Fps.Seconds(l.Start) - sourceOffset; var length = comp.Fps.Seconds(l.Duration); var overlapStart = Math.Max(0, begin); var overlapEnd = Math.Min(duration, Math.Min(window + offset, begin + length));
                if (overlapEnd <= overlapStart) continue;
                if (l.Kind == "Composition") { var nested = p.Compositions.Find(x => x.Id == l.CompositionId); if (nested != null && l.Rate == 1) Collect(nested, begin, l.SourceIn, length, depth + 1); continue; }
                var asset = p.Assets.Find(a => a.Id == l.AssetId); if (asset == null || !asset.HasAudio || asset.Missing || l.Rate == 0 || l.Properties.ContainsKey("time")) continue;
                args.AddRange(["-protocol_whitelist", "file,pipe", "-i", asset.Path]); var speed = Math.Abs(l.Rate); var src = l.SourceIn + (overlapStart - begin) * l.Rate; var len = (overlapEnd - overlapStart) * speed;
                var chain = $"[{input}:a]atrim=start={Processes.Number(l.Rate > 0 ? src : Math.Max(0, src - len))}:duration={Processes.Number(len)},asetpts=PTS-STARTPTS";
                if (l.Rate < 0) chain += ",areverse";
                while (speed > 2) { chain += ",atempo=2"; speed /= 2; } while (speed < .5) { chain += ",atempo=0.5"; speed *= 2; }
                chain += $",atempo={Processes.Number(speed)},volume={Processes.Number(l.Volume)},aresample=48000,aformat=channel_layouts=stereo,adelay={Math.Round(overlapStart * 1000)}:all=1[a{input}]";
                filters.Add(chain); labels.Add($"[a{input}]"); input++;
            }
        }
        Collect(c, -c.Fps.Seconds(start), 0, c.Fps.Seconds(end), 0);
        if (labels.Count > 0)
        {
            filters.Add(string.Concat(labels) + $"amix=inputs={labels.Count}:duration=longest:normalize=0,apad,atrim=duration={Processes.Number(duration)}[mix]");
            args.AddRange(["-filter_complex", string.Join(';', filters)]); if (video != null) args.AddRange(["-map", "0:v:0"]); args.AddRange(["-map", "[mix]", "-c:a", video == null ? "pcm_s16le" : "aac", "-ar", "48000"]); if (video != null) args.AddRange(["-b:a", $"{bitrate}k"]);
        }
        else if (video != null) args.AddRange(["-map", "0:v:0", "-an"]);
        else args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-c:a", "pcm_s16le"]);
        if (video != null) args.AddRange(["-c:v", "copy", "-movflags", "+faststart"]);
        args.AddRange(["-t", Processes.Number(duration), output]); Processes.Run("ffmpeg", args, token).GetAwaiter().GetResult();
    }
}
public record ClipRange(double Start, double Duration, double Origin)
{
    public static ClipRange ForLayer(Layer layer, Asset asset, FrameRate fps)
    {
        if (layer.Properties.TryGetValue("time", out var remap) && remap.Keys.Count > 0)
            throw new InvalidOperationException("Local processing needs a clip with a fixed playback rate.");
        var sample = 1 / Math.Max(1, asset.FrameRate > 0 ? asset.FrameRate : fps.Value);
        var first = layer.SourceIn;
        var last = first + fps.Seconds(layer.Duration - 1) * layer.Rate;
        var start = Math.Clamp(Math.Min(first, last), 0, Math.Max(0, asset.Duration - sample));
        var end = Math.Min(asset.Duration, layer.Rate > 0 ? first + fps.Seconds(layer.Duration) * layer.Rate : first + sample);
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start)
            throw new InvalidOperationException("The selected clip has no source frames to process.");
        return new(start, end - start, Math.Max(0, first - start));
    }

    public string[] Input(Asset asset) => ["-ss", Processes.Number(Start), "-t", Processes.Number(Duration), "-protocol_whitelist", "file,pipe", "-i", asset.Path];
}

public static class LocalTools
{
    internal static string OutputPath(Asset asset, ClipRange range, string suffix)
    {
        var folder = Path.Combine(Store.Home, "Processed");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{Media.CacheKey(asset)}-{Processes.Number(range.Start)}-{Processes.Number(range.Duration)}-{suffix}");
    }

    public static async Task<string> Stabilize(Asset asset, ClipRange range, IProgress<JobProgress>? progress, CancellationToken token)
    {
        if (asset.Kind != "Video") throw new InvalidOperationException("Select a video clip to stabilize.");
        token.ThrowIfCancellationRequested();
        var extension = asset.HasAlpha ? "mov" : "mp4";
        var output = OutputPath(asset, range, "stabilized." + extension);
        if (File.Exists(output)) return output;
        var id = Data.Id();
        var temp = output + "." + id + ".partial." + extension;
        var transforms = "stabilize-" + id + ".trf";
        var transformPath = Path.Combine(Store.Cache, transforms);
        var watch = Stopwatch.StartNew();
        var frames = Math.Max(1, (long)Math.Ceiling(range.Duration * asset.FrameRate));
        string[] input = ["-hide_banner", "-nostats", "-y", "-noautorotate", .. range.Input(asset)];
        try
        {
            var filter = "deshake=rx=32:ry=32:edge=mirror";
            string[] codec = ["-c:v", "libx264", "-crf", "18", "-preset", "fast", "-c:a", "aac"];
            if (asset.HasAlpha)
            {
                // deshake negotiates an opaque format. vidstab can transform planar alpha.
                progress?.Report(new("Analyzing motion", 0, 0, frames, watch.Elapsed));
                await Processes.Run("ffmpeg", [.. input, "-map", "0:v:0", "-vf", $"format=yuva420p,vidstabdetect=result={transforms}", "-an", "-f", "null", "NUL"], token,
                    workingDirectory: Store.Cache);
                filter = $"format=yuva420p,vidstabtransform=input={transforms}";
                codec = ["-c:v", "qtrle", "-pix_fmt", "argb", "-c:a", "pcm_s16le"];
            }
            await Processes.Run("ffmpeg", [.. input, "-map", "0:v:0", "-map", "0:a:0?", "-vf", filter, .. codec, "-progress", "pipe:2", temp], token,
                line => { if (line.StartsWith("frame=") && long.TryParse(line[6..].Trim(), out var f)) progress?.Report(new("Stabilizing locally", Math.Clamp((double)f / frames, 0, 1), f, frames, watch.Elapsed)); },
                workingDirectory: Store.Cache);
            token.ThrowIfCancellationRequested();
            File.Move(temp, output, true); return output;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(transformPath)) File.Delete(transformPath);
        }
    }
}
