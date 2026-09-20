using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Assets", "SimRigMode.ico"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using var bitmap = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(bitmap))
{
    g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
    using var tile = new SolidBrush(Color.FromArgb(255, 20, 27, 36)); g.FillRoundedRectangle(tile, new Rectangle(8, 8, 240, 240), 48);
    using var outline = new Pen(Color.FromArgb(255, 70, 87, 105), 7); g.DrawRoundedRectangle(outline, new Rectangle(12, 12, 232, 232), 44);
    using var cyan = new SolidBrush(Color.FromArgb(255, 78, 177, 239)); using var green = new SolidBrush(Color.FromArgb(255, 67, 210, 146));
    g.FillEllipse(cyan, 43, 76, 94, 94); g.FillEllipse(green, 119, 76, 94, 94);
    using var link = new Pen(Color.White, 17) { StartCap = LineCap.Round, EndCap = LineCap.Round }; g.DrawLine(link, 88, 123, 168, 123);
    using var inner = new SolidBrush(Color.FromArgb(255, 20, 27, 36)); g.FillEllipse(inner, 73, 106, 34, 34); g.FillEllipse(inner, 149, 106, 34, 34);
    using var basePen = new Pen(Color.FromArgb(255, 220, 230, 239), 12) { StartCap = LineCap.Round, EndCap = LineCap.Round }; g.DrawLine(basePen, 88, 193, 168, 193);
}
using var png = new MemoryStream(); bitmap.Save(png, ImageFormat.Png); var bytes = png.ToArray();
using var file = File.Create(output); using var writer = new BinaryWriter(file);
writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((uint)bytes.Length); writer.Write((uint)22); writer.Write(bytes);
Console.WriteLine(output);

static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle bounds, int radius) { using var path = Rounded(bounds, radius); g.FillPath(brush, path); }
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle bounds, int radius) { using var path = Rounded(bounds, radius); g.DrawPath(pen, path); }
    private static GraphicsPath Rounded(Rectangle b, int r) { var p = new GraphicsPath(); var d = r * 2; p.AddArc(b.Left, b.Top, d, d, 180, 90); p.AddArc(b.Right - d, b.Top, d, d, 270, 90); p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90); p.AddArc(b.Left, b.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p; }
}
