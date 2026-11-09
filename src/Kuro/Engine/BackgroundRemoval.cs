using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Kuro.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kuro.Engine;

public static class BackgroundRemoval
{
    const int Size = 320;
    const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx";
    const string ModelHash = "309C8469258DDA742793DCE0EBEA8E6DD393174F89934733ECC8B14C76F4DDD8";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };
    static readonly SemaphoreSlim modelLock = new(1);
    static readonly float[] means = [.485f, .456f, .406f], deviations = [.229f, .224f, .225f];
    public static string ModelPath => Path.Combine(Store.Home, "Models", "u2netp.onnx");

    public static async Task<string> Process(Asset asset, ClipRange range, IProgress<JobProgress>? progress, CancellationToken token)
    {
        var output = LocalTools.OutputPath(asset, range, "foreground.mov");
        token.ThrowIfCancellationRequested();
        if (File.Exists(output)) return output;
        if (!File.Exists(ModelPath)) progress?.Report(new("Downloading foreground model", 0, 0, 0, TimeSpan.Zero));
        await EnsureModel(token);
        await Task.Run(() => Render(asset, range, output, progress, token), token);
        return output;
    }

    static async Task EnsureModel(CancellationToken token)
    {
        await modelLock.WaitAsync(token);
        var partial = ModelPath + "." + Data.Id() + ".partial";
        try
        {
            if (File.Exists(ModelPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var file = File.Create(partial))
                await response.Content.CopyToAsync(file, token);
            using (var file = File.OpenRead(partial))
                if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != ModelHash)
                    throw new InvalidDataException("The downloaded foreground model failed its checksum.");
            File.Move(partial, ModelPath, true);
        }
        finally
        {
            modelLock.Release();
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    static void Render(Asset asset, ClipRange range, string output, IProgress<JobProgress>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var partial = output + "." + Data.Id() + ".partial.mov";
        var fps = Processes.Number(asset.FrameRate > 0 ? asset.FrameRate : 30);
        var expected = Math.Max(1, (long)Math.Ceiling(range.Duration * (asset.FrameRate > 0 ? asset.FrameRate : 30)));
        using var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        using var inference = new InferenceSession(ModelPath, options);
        var input = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        var feed = new[] { NamedOnnxValue.CreateFromTensor(inference.InputMetadata.Keys.First(), input) };
        using var decoder = Processes.Start("ffmpeg", ["-v", "error", "-noautorotate", "-threads", "2", .. range.Input(asset),
            "-map", "0:v:0", "-vf", $"fps={fps}:eof_action=pass", "-threads", "2", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"]);
        using var stopDecoder = token.Register(() => Processes.Kill(decoder));
        var decoderError = decoder.StandardError.ReadToEndAsync();
        try
        {
            using var encoder = Processes.Start("ffmpeg", ["-v", "error", "-y", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{asset.Width}x{asset.Height}",
                "-framerate", fps, "-i", "pipe:0", .. range.Input(asset), "-map", "0:v:0", "-map", "1:a:0?",
                "-c:v", "qtrle", "-pix_fmt", "argb", "-c:a", "pcm_s16le", partial], true);
            using var stopEncoder = token.Register(() => Processes.Kill(encoder));
            var encoderError = encoder.StandardError.ReadToEndAsync();
            try
            {
                var pixels = new byte[checked(asset.Width * asset.Height * 4)];
                var frames = 0L;
                var raw = decoder.StandardOutput.BaseStream;
                int bytes;
                while ((bytes = raw.ReadAtLeast(pixels, pixels.Length, false)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (bytes != pixels.Length) throw new IOException("The source returned an incomplete frame.");
                    Prepare(pixels, asset.Width, asset.Height, input.Buffer.Span);
                    using var masks = inference.Run(feed);
                    var mask = masks.First().AsTensor<float>().ToArray();
                    Apply(pixels, asset.Width, asset.Height, mask);
                    encoder.StandardInput.BaseStream.Write(pixels);
                    frames++;
                    progress?.Report(new("Removing background", Math.Min(.99, (double)frames / expected), frames, expected, watch.Elapsed));
                }
                decoder.WaitForExit();
                token.ThrowIfCancellationRequested();
                if (decoder.ExitCode != 0 || frames == 0) throw new IOException("Could not decode the selected source range. " + decoderError.GetAwaiter().GetResult());
                encoder.StandardInput.Close(); encoder.WaitForExit();
                token.ThrowIfCancellationRequested();
                if (encoder.ExitCode != 0) throw new IOException("Could not encode transparent footage. " + encoderError.GetAwaiter().GetResult());
                File.Move(partial, output, true);
                progress?.Report(new("Complete", 1, frames, frames, watch.Elapsed));
            }
            finally { Processes.Kill(encoder); }
        }
        catch (IOException) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); }
        finally
        {
            Processes.Kill(decoder);
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    static void Prepare(byte[] bgra, int width, int height, Span<float> input)
    {
        var max = 1f;
        for (var y = 0; y < Size; y++)
        {
            var sourceY = Math.Clamp((y + .5) * height / Size - .5, 0, height - 1);
            var y0 = (int)sourceY;
            var y1 = Math.Min(height - 1, y0 + 1);
            for (var x = 0; x < Size; x++)
            {
                var sourceX = Math.Clamp((x + .5) * width / Size - .5, 0, width - 1);
                var x0 = (int)sourceX;
                var x1 = Math.Min(width - 1, x0 + 1);
                for (var channel = 0; channel < 3; channel++)
                {
                    var offset = 2 - channel;
                    var value = Interpolate(bgra[(y0 * width + x0) * 4 + offset], bgra[(y0 * width + x1) * 4 + offset],
                        bgra[(y1 * width + x0) * 4 + offset], bgra[(y1 * width + x1) * 4 + offset], sourceX - x0, sourceY - y0);
                    input[(channel * Size + y) * Size + x] = value;
                    max = Math.Max(max, value);
                }
            }
        }
        for (var channel = 0; channel < 3; channel++)
        {
            var plane = input.Slice(channel * Size * Size, Size * Size);
            for (var i = 0; i < plane.Length; i++) plane[i] = (plane[i] / max - means[channel]) / deviations[channel];
        }
    }

    static void Apply(byte[] bgra, int width, int height, float[] mask)
    {
        var min = mask.Min();
        var span = Math.Max(.00001f, mask.Max() - min);
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp((y + .5) * Size / height - .5, 0, Size - 1);
            var y0 = (int)sourceY;
            var y1 = Math.Min(Size - 1, y0 + 1);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Clamp((x + .5) * Size / width - .5, 0, Size - 1);
                var x0 = (int)sourceX;
                var x1 = Math.Min(Size - 1, x0 + 1);
                var matte = Interpolate(mask[y0 * Size + x0], mask[y0 * Size + x1],
                    mask[y1 * Size + x0], mask[y1 * Size + x1], sourceX - x0, sourceY - y0);
                var alpha = (y * width + x) * 4 + 3;
                bgra[alpha] = (byte)Math.Round(bgra[alpha] * Math.Clamp((matte - min) / span, 0, 1));
            }
        }
    }

    static float Interpolate(float a, float b, float c, float d, double x, double y) =>
        (float)((a + (b - a) * x) * (1 - y) + (c + (d - c) * x) * y);
}
