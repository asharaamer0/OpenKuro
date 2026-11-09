using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kuro.Engine;

namespace Kuro.Core;
public static class Example
{
    // An actual editable project: original procedural artwork, real video and native title animation.
    public static async Task Create()
    {
        var folder = Path.GetFullPath("examples"); Directory.CreateDirectory(folder);
        var picture = Path.Combine(folder, "First light.png"); var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(Color.FromRgb(196, 173, 146), Color.FromRgb(226, 202, 168), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, 1920, 1080));
            dc.DrawEllipse(Renderer.Brush("#D87849"), null, new Point(1390, 360), 145, 145);
            dc.DrawGeometry(Renderer.Brush("#878877"), null, Geometry.Parse("M0,680 C190,630 320,720 520,610 C730,500 900,610 1110,590 C1310,570 1460,650 1640,565 L1920,635 L1920,1080 L0,1080 Z"));
            dc.DrawGeometry(Renderer.Brush("#545F55"), null, Geometry.Parse("M0,760 C170,590 280,600 420,640 C570,680 650,570 810,655 C1010,770 1130,640 1310,710 C1500,780 1660,630 1920,790 L1920,1080 L0,1080 Z"));
            dc.DrawGeometry(Renderer.Brush("#293E39"), null, Geometry.Parse("M0,830 C170,820 270,930 460,870 C650,810 800,840 940,915 C1190,1040 1340,775 1520,815 C1730,860 1820,910 1920,890 L1920,1080 L0,1080 Z"));
            dc.DrawGeometry(Renderer.Brush("#1A302B"), null, Geometry.Parse("M0,935 C220,890 350,980 560,1020 C800,1080 1020,945 1200,980 C1420,1030 1620,1010 1920,955 L1920,1080 L0,1080 Z"));
        }
        var bmp = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32); bmp.Render(visual);
        using (var stream = File.Create(picture)) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); png.Save(stream); }
        var videoPath = Path.Combine(folder, "First light.mp4");
        await Processes.Run("ffmpeg", ["-v", "error", "-y", "-loop", "1", "-i", picture, "-vf", "scale=1920:1080,zoompan=z='1+0.0002*on':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s=1920x1080:fps=30", "-t", "8", "-c:v", "libx264", "-preset", "fast", "-crf", "18", "-threads", "2", "-pix_fmt", "yuv420p", videoPath]);
        var video = await Media.Probe(videoPath); video.Name = "First light.mp4"; video.Bin = "Footage";
        var image = await Media.Probe(picture); image.Bin = "Artwork";
        var p = new Project { Name = "First light" }; var c = p.Active; c.Name = "First light · Main"; c.Duration = c.WorkEnd = 240; var e = new Editor(); e.Load(p); p.Assets.AddRange([video, image]);
        e.Execute(new("add_asset", video.Id)); e.Selected!.Name = "Landscape · slow push";
        void Text(string name, string text, double size, double x, double y, long start, long length)
        {
            e.Execute(new("create_text", Frame: start)); var l = e.Selected!; l.Name = name; l.Text = text; l.Duration = length; l.Font = "Segoe UI"; l.FontWeight = 600; l.Properties["fontSize"].Value = size; l.Properties["x"].Value = x; l.Properties["y"].Value = y;
        }
        Text("Title · Small moments", "Small moments.\nLasting stories.", 108, 660, 470, 0, 240); var title = e.Selected!; title.Fill = "#FAF3E6"; title.Color = "#918C83";
        e.Execute(new("add_keyframe", title.Id, "y", 550, 0)); e.Execute(new("add_keyframe", title.Id, "y", 470, 42)); e.Execute(new("set_interpolation", title.Id, "y", Text: "Bezier"));
        e.Execute(new("add_keyframe", title.Id, "opacity", 0, 0)); e.Execute(new("add_keyframe", title.Id, "opacity", 100, 32)); e.Execute(new("set_interpolation", title.Id, "opacity", Text: "Bezier"));
        Text("Eyebrow · First light", "F I R S T   L I G H T", 24, 443, 282, 0, 240); e.Selected!.Color = "#918C83";
        Text("Caption · A film by you", "A quiet beginning.  A film by you.", 25, 499, 775, 24, 216); e.Selected!.Color = "#918C83";
        e.Execute(new("create_shape", Frame: 24)); var rule = e.Selected!; rule.Name = "Accent · persimmon rule"; rule.Duration = 216; rule.Fill = "#E59A65"; rule.Properties["width"].Value = 54; rule.Properties["height"].Value = 3; rule.Properties["x"].Value = 295; rule.Properties["y"].Value = 710;
        c.Markers.AddRange([42, 120, 210]); Store.Save(p, Path.Combine(folder, "First light.kuro"));
    }
}
