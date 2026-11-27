using System.Buffers.Binary;
using System.IO.Compression;

namespace AiKeepAwake;

/// <summary>The tray's icons, drawn in code at any size. The mark is an eye: open and filled in blue while it keeps
/// the laptop awake (with a bar under it when that's by hand), closed and grey when it isn't needed, and open but
/// hollow and grey when it isn't keeping awake though it might (a pupil when something stops it, a pause mark, or
/// an amber dot when something's wrong). The iris is cut out, so the taskbar shows through on light and on dark.
/// Pixels are ARGB, top row first, alpha not premultiplied, as CreateIconIndirect and PNG both take them. The
/// program icon (app.ico) is the "awake" art, written by Ico(); a test checks the committed file is what this
/// draws.</summary>
public static class IconArt
{
    private const uint Accent = 0xFF0067C0, AccentDark = 0xFF4CC2FF, Amber = 0xFFF7A800;
    private const uint GreyOnLight = 0xFF5A5A5A, GreyOnDark = 0xFFD0D0D0;

    /// <summary>The program icon's blue: one colour for a light and a dark Start menu (3.6:1 or better on both).</summary>
    private const uint AccentAnywhere = 0xFF1479D6;

    /// <summary>A layer that rubs out what's under it, rather than painting.</summary>
    private const uint Erase = 0;

    /// <summary>Whether a point, in pixels from the top left, is inside.</summary>
    private delegate bool Shape(double x, double y);

    private static Shape Disc(double cx, double cy, double r) => (x, y) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;

    private static Shape Box(double x0, double y0, double x1, double y1) => (x, y) => x >= x0 && x <= x1 && y >= y0 && y <= y1;

    /// <summary>A segment from (x0, y0) to (x1, y1) with round ends.</summary>
    private static Shape Line(double x0, double y0, double x1, double y1, double width) => (x, y) =>
    {
        double dx = x1 - x0, dy = y1 - y0;
        var t = Math.Clamp(((x - x0) * dx + (y - y0) * dy) / (dx * dx + dy * dy), 0, 1);
        double px = x0 + t * dx - x, py = y0 + t * dy - y;
        return px * px + py * py <= width * width / 4;
    };

    /// <summary>The eye's outline: where two equal circles overlap, halfWidth to each corner and halfHeight to each
    /// lid; inset draws it that much smaller all round (for the hollow eye).</summary>
    private static Shape Almond(double cx, double cy, double halfWidth, double halfHeight, double inset = 0)
    {
        var r = (halfWidth * halfWidth + halfHeight * halfHeight) / (2 * halfHeight);
        var d = r - halfHeight;
        var inner = r - inset;
        return (x, y) => (x - cx) * (x - cx) + (y - cy - d) * (y - cy - d) <= inner * inner
                         && (x - cx) * (x - cx) + (y - cy + d) * (y - cy + d) <= inner * inner;
    }

    private static Shape Hollow(double cx, double cy, double halfWidth, double halfHeight, double stroke)
    {
        Shape outer = Almond(cx, cy, halfWidth, halfHeight), inner = Almond(cx, cy, halfWidth, halfHeight, stroke);
        return (x, y) => outer(x, y) && !inner(x, y);
    }

    /// <summary>The icon for a state at size x size pixels; dark: the taskbar is dark (so the colours are lighter).</summary>
    public static uint[] Render(TrayState state, int size, bool dark = false) =>
        Render(state, size, dark ? AccentDark : Accent, dark ? GreyOnDark : GreyOnLight);

    private static uint[] Render(TrayState state, int size, uint accent, uint grey)
    {
        // Drawn on a 16-unit grid. Up to 32 px the measures are whole pixels, so the lids, the iris and the bars
        // sit on pixel edges and stay sharp at 16, 20 and 24.
        var u = size / 16.0;
        double Px(double units) => size <= 32 ? Math.Max(1, Math.Round(units * u, MidpointRounding.AwayFromZero)) : units * u;
        double cx = size / 2.0, cy = size / 2.0;
        double halfWidth = 7.5 * u, halfHeight = Px(5), iris = Px(3), pupil = Px(1);
        var stroke = size <= 32 ? Math.Max(1.5, Math.Round(3 * u, MidpointRounding.AwayFromZero) / 2) : 1.5 * u;
        var layers = new List<(Shape Shape, uint Color)>();

        void Open(double y)
        {
            layers.Add((Almond(cx, y, halfWidth, halfHeight), accent));
            layers.Add((Disc(cx, y, iris), Erase));
            layers.Add((Disc(cx, y, pupil), accent));
            if (size >= 48)
                layers.Add((Disc(cx + pupil * 0.42, y - pupil * 0.42, pupil * 0.34), Erase));   // a glint
        }

        switch (state)
        {
            case TrayState.Awake:
                Open(cy);
                break;
            case TrayState.Manual:
                {
                    // The same eye, held open: a bar under it.
                    var bar = Px(2);
                    Open(cy - Px(2));
                    layers.Add((Line(cx - Px(4), size - Px(1) - bar / 2, cx + Px(4), size - Px(1) - bar / 2, bar), accent));
                    break;
                }
            case TrayState.NotNeeded:
                {
                    // Closed: the lower lid, and three lashes.
                    var lid = Px(2);
                    var top = cy - Px(3);
                    var r = (halfWidth * halfWidth + halfHeight * halfHeight) / (2 * halfHeight);
                    var centre = top + halfHeight - r;
                    layers.Add(((x, y) =>
                    {
                        var distance = Math.Sqrt((x - cx) * (x - cx) + (y - centre) * (y - centre));
                        return y >= top && Math.Abs(distance - r) <= lid / 2;
                    }, grey));
                    var reach = r + Px(3) - lid / 2;
                    foreach (var angle in new[] { -0.6, 0, 0.6 })
                    {
                        double sin = Math.Sin(angle), cos = Math.Cos(angle);
                        layers.Add((Line(cx + r * sin, centre + r * cos, cx + reach * sin, centre + reach * cos, lid), grey));
                    }
                    break;
                }
            case TrayState.Blocked:
                layers.Add((Hollow(cx, cy, halfWidth, halfHeight, stroke), grey));
                layers.Add((Disc(cx, cy, Px(2)), grey));
                break;
            case TrayState.Paused:
                {
                    double bar = Px(2), half = Px(1), tall = Px(1.5);
                    layers.Add((Hollow(cx, cy, halfWidth, halfHeight, stroke), grey));
                    layers.Add((Box(cx - half - bar, cy - tall, cx - half, cy + tall), grey));
                    layers.Add((Box(cx + half, cy - tall, cx + half + bar, cy + tall), grey));
                    break;
                }
            case TrayState.Problem:
                {
                    var dot = Px(3);
                    layers.Add((Hollow(cx, cy, halfWidth, halfHeight, stroke), grey));
                    layers.Add((Disc(cx, cy, Px(2)), grey));
                    layers.Add((Disc(size - dot, size - dot, dot + Px(1)), Erase));
                    layers.Add((Disc(size - dot, size - dot, dot), Amber));
                    break;
                }
        }
        return Draw(size, layers);
    }

    /// <summary>Each pixel sampled 8 x 8 times (4 x 4 above 64 px), and the layers painted over each other in order.</summary>
    private static uint[] Draw(int size, List<(Shape Shape, uint Color)> layers)
    {
        var n = size <= 64 ? 8 : 4;
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
                            if (shape(px + (sx + 0.5) / n, py + (sy + 0.5) / n))
                                inside++;
                    if (inside == 0)
                        continue;
                    var cover = inside / (double)(n * n);
                    if (color == Erase)
                    {
                        (a, r, g, b) = (a * (1 - cover), r * (1 - cover), g * (1 - cover), b * (1 - cover));
                        continue;
                    }
                    var ca = cover * ((color >> 24) / 255.0);
                    r = ((color >> 16) & 0xFF) / 255.0 * ca + r * (1 - ca);
                    g = ((color >> 8) & 0xFF) / 255.0 * ca + g * (1 - ca);
                    b = (color & 0xFF) / 255.0 * ca + b * (1 - ca);
                    a = ca + a * (1 - ca);
                }
                if (a <= 0.002)
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
        var images = sizes.Select(size => Png(Render(TrayState.Awake, size, AccentAnywhere, GreyOnLight), size)).ToList();
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

    /// <summary>A PNG of ARGB pixels (8-bit RGBA): size x size, or size wide and height tall.</summary>
    public static byte[] Png(uint[] argb, int size, int height = 0)
    {
        if (height == 0)
            height = size;
        var raw = new byte[height * (size * 4 + 1)];
        for (var y = 0; y < height; y++)
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
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
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
