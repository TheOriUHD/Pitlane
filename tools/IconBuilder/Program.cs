using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Renders the Pitlane icons: a graphite tile with a monoline "P". The stem is the main straight;
// the loop is the pit lane, leaving the straight and rejoining it. The loop's colour marks the
// variant: orange for the app itself, blue for Desktop mode and orange for Sim Rig mode in the tray.
//
// Usage: dotnet run --project tools/IconBuilder [preview.png]
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var assets = Path.Combine(root, "Assets");
Directory.CreateDirectory(Path.Combine(assets, "Tray"));

var rig = new Variant(Color.FromArgb(255, 255, 146, 72), Color.FromArgb(255, 255, 84, 56));
var desktop = new Variant(Color.FromArgb(255, 92, 156, 255), Color.FromArgb(255, 108, 100, 255));

int[] appSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
int[] traySizes = [16, 20, 24, 32, 40, 48, 64];
WriteIco(Path.Combine(assets, "Pitlane.ico"), rig, appSizes);
WriteIco(Path.Combine(assets, "Tray", "Desktop.ico"), desktop, traySizes);
WriteIco(Path.Combine(assets, "Tray", "SimRig.ico"), rig, traySizes);
Console.WriteLine(assets);

if (args.Length > 0)
{
    WritePreview(args[0], desktop, rig);
    Console.WriteLine(args[0]);
}

static Bitmap Render(int size, Variant variant)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    g.Clear(Color.Transparent);

    var k = size / 256f;
    var small = size <= 32;

    // The tile. Full-bleed at tray sizes, where every pixel counts.
    var margin = small ? (size >= 32 ? 1f : 0f) : 12 * k;
    var radius = small ? size * 0.25f : 56 * k;
    var tile = new RectangleF(margin, margin, size - 2 * margin, size - 2 * margin);
    using (var path = Rounded(tile, radius))
    using (var fill = new LinearGradientBrush(tile, Color.FromArgb(255, 46, 51, 64), Color.FromArgb(255, 15, 17, 23), LinearGradientMode.Vertical))
        g.FillPath(fill, path);

    if (size >= 48)
    {
        // A hairline of light along the top edge, fading out before the middle. The brush spans
        // the whole tile: GDI+ tiles a gradient beyond its rectangle instead of clamping it.
        var inset = 1.5f * Math.Max(1f, k * 2);
        var edge = new RectangleF(tile.X + inset / 2, tile.Y + inset / 2, tile.Width - inset, tile.Height - inset);
        using var path = Rounded(edge, radius - inset / 2);
        using var light = new LinearGradientBrush(new RectangleF(edge.X, edge.Y - 1, edge.Width, edge.Height + 2), Color.White, Color.White, LinearGradientMode.Vertical)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(80, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)],
                Positions = [0f, 0.42f, 1f]
            }
        };
        using var pen = new Pen(light, inset);
        g.DrawPath(pen, path);
    }

    // The glyph, designed on a 256 grid and mapped to device pixels - a little larger when small.
    // At tray sizes the stroke is a whole number of pixels and the straight edges are snapped to
    // the pixel grid, so the stem and the bars stay crisp instead of smearing across two pixels.
    var glyphScale = size <= 20 ? 1.22f : size <= 32 ? 1.12f : 1f;
    var s = k * glyphScale;
    var c = size / 2f;
    float X(float v) => c + (v - 128) * s;
    float Y(float v) => c + (v - 128) * s;
    var stroke = small ? Math.Max(2f, MathF.Round(30 * s)) : 30 * s;
    float Snap(float v) => !small ? v : stroke % 2 == 0 ? MathF.Round(v) : MathF.Floor(v) + 0.5f;

    var stemX = Snap(X(89));
    var top = Snap(Y(69));
    var bottom = Snap(Y(145));
    var r = (bottom - top) / 2;
    // The loop's outer edge lands on a pixel boundary too.
    var arcX = small ? MathF.Round(X(129) + r + stroke / 2) - r - stroke / 2 : X(129);
    var stemEnd = Y(187);

    using (var loop = new GraphicsPath())
    {
        loop.AddLine(stemX, bottom, arcX, bottom);
        loop.AddArc(arcX - r, top, 2 * r, 2 * r, 90, -180);
        loop.AddLine(arcX, top, stemX, top);
        var bounds = new RectangleF(stemX - stroke, top - stroke, arcX + r - stemX + 2 * stroke, bottom - top + 2 * stroke);
        using var brush = new LinearGradientBrush(bounds, variant.Top, variant.Bottom, LinearGradientMode.Vertical);
        using var pen = new Pen(brush, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawPath(pen, loop);
    }

    using (var stem = new Pen(Color.FromArgb(255, 246, 248, 252), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        g.DrawLine(stem, stemX, top, stemX, stemEnd);

    return bitmap;
}

static void WriteIco(string path, Variant variant, int[] sizes)
{
    var images = sizes.Select(size =>
    {
        using var bitmap = Render(size, variant);
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return (size, bytes: png.ToArray());
    }).ToList();

    using var file = File.Create(path);
    using var writer = new BinaryWriter(file);
    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)images.Count);
    var offset = 6 + 16 * images.Count;
    foreach (var (size, bytes) in images)
    {
        writer.Write((byte)(size >= 256 ? 0 : size)); writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write((uint)bytes.Length); writer.Write((uint)offset);
        offset += bytes.Length;
    }
    foreach (var (_, bytes) in images) writer.Write(bytes);
}

/// <summary>A sheet for checking the icons by eye: each tray size at 1x and blown up 4x without
/// smoothing, on a dark and a light taskbar colour, plus the app icon at 256 and 64.</summary>
static void WritePreview(string path, Variant desktop, Variant rig)
{
    int[] sizes = [16, 20, 24, 32];
    using var sheet = new Bitmap(1200, 560, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(sheet);
    g.Clear(Color.FromArgb(255, 32, 32, 32));
    using (var light = new SolidBrush(Color.FromArgb(255, 238, 238, 238))) g.FillRectangle(light, 0, 280, 900, 280);
    g.InterpolationMode = InterpolationMode.NearestNeighbor;
    g.PixelOffsetMode = PixelOffsetMode.Half;

    for (var band = 0; band < 2; band++)
    {
        var y0 = band * 280 + 24;
        var x = 24;
        foreach (var variant in new[] { desktop, rig })
        {
            foreach (var size in sizes)
            {
                using var icon = Render(size, variant);
                g.DrawImage(icon, x, y0, size, size);
                g.DrawImage(icon, x, y0 + 44, size * 4, size * 4);
                x += size * 4 + 14;
            }
            x += 24;
        }
    }

    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    using var big = Render(256, rig);
    g.DrawImage(big, 924, 24, 256, 256);
    using var mid = Render(64, rig);
    g.DrawImage(mid, 1020, 330, 64, 64);
    sheet.Save(path, ImageFormat.Png);
}

static GraphicsPath Rounded(RectangleF b, float r)
{
    var p = new GraphicsPath();
    var d = r * 2;
    p.AddArc(b.Left, b.Top, d, d, 180, 90);
    p.AddArc(b.Right - d, b.Top, d, d, 270, 90);
    p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
    p.AddArc(b.Left, b.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

record Variant(Color Top, Color Bottom);
