using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace VpnClient;

/// <summary>Иконка приложения: скруглённый квадрат с градиентом и белым символом питания.</summary>
public static class IconArt
{
    public static readonly (Color, Color) Blue = (Color.FromArgb(0x4F, 0x7C, 0xFF), Color.FromArgb(0x7C, 0x5C, 0xFF));
    public static readonly (Color, Color) GreenC = (Color.FromArgb(0x2B, 0xD4, 0x7D), Color.FromArgb(0x10, 0xA8, 0x6A));
    public static readonly (Color, Color) GrayC = (Color.FromArgb(0x6B, 0x74, 0x86), Color.FromArgb(0x48, 0x50, 0x60));

    public static Bitmap Render(int size, (Color a, Color b) colors)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float r = size * 0.24f, pad = size * 0.03f, s = size - pad * 2;
        using var path = new GraphicsPath();
        path.AddArc(pad, pad, r * 2, r * 2, 180, 90);
        path.AddArc(pad + s - r * 2, pad, r * 2, r * 2, 270, 90);
        path.AddArc(pad + s - r * 2, pad + s - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(pad, pad + s - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        using (var bg = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), colors.a, colors.b))
            g.FillPath(bg, path);

        // символ питания
        float w = Math.Max(1.6f, size * 0.095f);
        using var pen = new Pen(Color.White, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float c = size / 2f, rad = size * 0.25f;
        g.DrawArc(pen, c - rad, c - rad + size * 0.04f, rad * 2, rad * 2, -55, 290);
        g.DrawLine(pen, c, size * 0.2f, c, size * 0.5f);
        return bmp;
    }

    public static Icon TrayIcon(bool connected)
    {
        using var bmp = Render(32, connected ? GreenC : GrayC);
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Запись .ico с PNG-кадрами нескольких размеров.</summary>
    public static void SaveIco(string path, params int[] sizes)
    {
        var pngs = sizes.Select(sz =>
        {
            using var b = Render(sz, Blue);
            using var ms = new MemoryStream();
            b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();

        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }
}
