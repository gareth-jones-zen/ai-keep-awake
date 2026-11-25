using System.Buffers.Binary;
using System.IO.Compression;

namespace AiKeepAwake;

/// <summary>The tray's icons, drawn in code at any size: a filled disc in the accent colour while it keeps the
/// laptop awake (with clock hands when that's by hand), a grey ring when it doesn't (with a pause mark, a dot when
/// something stops it, or an amber dot when something's wrong). Pixels are ARGB, top row first, alpha not
/// premultiplied, as CreateIconIndirect and PNG both take them. The program icon (app.ico) is the "awake" art,
/// written by Ico(); a test checks the committed file is what this draws.</summary>
public static class IconArt
{
    private const uint Accent = 0xFF0067C0, AccentDark = 0xFF4CC2FF, White = 0xFFFFFFFF, Amber = 0xFFF7A800;
    private const uint GreyOnLight = 0xFF5A5A5A, GreyOnDark = 0xFFD0D0D0;

    private delegate bool Shape(double x, double y);

    private static Shape Disc(double cx, double cy, double r) => (x, y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;

    private static Shape Ring(double cx, double cy, double r, double width) => (x, y) =>
    {
        var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        return d <= r && d >= r - width;
    };

    private static Shape Box(double x0, double y0, double x1, double y1) => (x, y) => x >= x0 && x <= x1 && y >= y0 && y <= y1;

    /// <summary>A segment from (x0, y0) to (x1, y1) with round ends.</summary>
    private static Shape Line(double x0, double y0, double x1, double y1, double width) => (x, y) =>
    {
        double dx = x1 - x0, dy = y1 - y0;
        var t = Math.Clamp(((x - x0) * dx + (y - y0) * dy) / (dx * dx + dy * dy), 0, 1);
        double px = x0 + t * dx - x, py = y0 + t * dy - y;
        return px * px + py * py <= width * width / 4;
    };

    /// <summary>The icon for a state at size x size pixels; dark: the taskbar is dark (so the grey is light).</summary>
    public static uint[] Render(TrayState state, int size, bool dark = false)
    {
        var accent = dark ? AccentDark : Accent;
        var grey = dark ? GreyOnDark : GreyOnLight;
        var hole = dark ? 0xFF202020u : 0xFFF3F3F3u;   // the taskbar behind a mark that overlaps the ring
        // Thicker lines at small sizes, so the ring stays visible at 16 px.
        var ring = size <= 20 ? 0.14 : size <= 32 ? 0.11 : 0.09;
        var layers = new List<(Shape Shape, uint Color)>();
        switch (state)
        {
            case TrayState.Awake:
                layers.Add((Disc(0.5, 0.5, 0.44), accent));
                layers.Add((Disc(0.5, 0.5, 0.15), White));
                break;
            case TrayState.Manual:
                layers.Add((Disc(0.5, 0.5, 0.44), accent));
                layers.Add((Line(0.5, 0.5, 0.5, 0.24, 0.12), White));
                layers.Add((Line(0.5, 0.5, 0.7, 0.5, 0.12), White));
                break;
            case TrayState.NotNeeded:
                layers.Add((Ring(0.5, 0.5, 0.44, ring), grey));
                break;
            case TrayState.Blocked:
                layers.Add((Ring(0.5, 0.5, 0.44, ring), grey));
                layers.Add((Disc(0.5, 0.5, 0.14), grey));
                break;
            case TrayState.Paused:
                layers.Add((Ring(0.5, 0.5, 0.44, ring), grey));
                layers.Add((Box(0.36, 0.32, 0.45, 0.68), grey));
                layers.Add((Box(0.55, 0.32, 0.64, 0.68), grey));
                break;
            case TrayState.Problem:
                layers.Add((Ring(0.5, 0.5, 0.44, ring), grey));
                layers.Add((Disc(0.74, 0.74, 0.27), hole));
                layers.Add((Disc(0.74, 0.74, 0.21), Amber));
                break;
        }
        return Draw(size, layers);
    }

    /// <summary>Each pixel sampled 4 x 4 times, and the layers painted over each other in order.</summary>
    private static uint[] Draw(int size, List<(Shape Shape, uint Color)> layers)
    {
        const int n = 4;
        var pixels = new uint[size * size];
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                double a = 0, r = 0, g = 0, b = 0;   // premultiplied while compositing
                foreach (var (shape, color) in layers)
                {
                    var inside = 0;
                    for (var sy = 0; sy < n; sy++)
                        for (var sx = 0; sx < n; sx++)
                            if (shape((px + (sx + 0.5) / n) / size, (py + (sy + 0.5) / n) / size))
                                inside++;
                    if (inside == 0)
                        continue;
                    var ca = inside / (double)(n * n) * ((color >> 24) / 255.0);
                    r = ((color >> 16) & 0xFF) / 255.0 * ca + r * (1 - ca);
                    g = ((color >> 8) & 0xFF) / 255.0 * ca + g * (1 - ca);
                    b = (color & 0xFF) / 255.0 * ca + b * (1 - ca);
                    a = ca + a * (1 - ca);
                }
                if (a <= 0)
                    continue;
                static uint Byte(double v) => (uint)Math.Clamp(Math.Round(v * 255), 0, 255);
                pixels[py * size + px] = Byte(a) << 24 | Byte(r / a) << 16 | Byte(g / a) << 8 | Byte(b / a);
            }
        }
        return pixels;
    }

    /// <summary>The program icon: the "awake" art at each size, as PNGs in an .ico.</summary>
    public static byte[] Ico(params int[] sizes)
    {
        if (sizes.Length == 0)
            sizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var images = sizes.Select(size => Png(Render(TrayState.Awake, size), size)).ToList();
        using var ico = new MemoryStream();
        using var w = new BinaryWriter(ico);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images)
            w.Write(image);
        w.Flush();
        return ico.ToArray();
    }

    /// <summary>A PNG of ARGB pixels (8-bit RGBA).</summary>
    public static byte[] Png(uint[] argb, int size)
    {
        var raw = new byte[size * (size * 4 + 1)];
        for (var y = 0; y < size; y++)
        {
            var row = y * (size * 4 + 1);
            raw[row] = 0;   // no filter
            for (var x = 0; x < size; x++)
            {
                var p = argb[y * size + x];
                var at = row + 1 + x * 4;
                raw[at] = (byte)(p >> 16);
                raw[at + 1] = (byte)(p >> 8);
                raw[at + 2] = (byte)p;
                raw[at + 3] = (byte)(p >> 24);
            }
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(raw);
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;   // bit depth
        header[9] = 6;   // RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typed));
        png.Write(number);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
