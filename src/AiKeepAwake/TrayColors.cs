namespace AiKeepAwake;

/// <summary>The colour sums behind the tray window's theme: the person's accent colour out of Windows' palette, and
/// enough contrast whatever colour they chose. Colours are ARGB; alpha is ignored. Kept here, away from the window,
/// so they can be tested without Windows.</summary>
public static class TrayColors
{
    public const uint Black = 0xFF000000, White = 0xFFFFFFFF;

    /// <summary>Text needs 4.5:1 on what's behind it; a control's shape needs 3:1 (WCAG 2.2, 1.4.3 and 1.4.11).</summary>
    public const double TextContrast = 4.5, ShapeContrast = 3;

    /// <summary>Relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(uint color)
    {
        static double Channel(uint value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel((color >> 16) & 0xFF) + 0.7152 * Channel((color >> 8) & 0xFF) + 0.0722 * Channel(color & 0xFF);
    }

    /// <summary>The contrast ratio of two colours, 1 to 21.</summary>
    public static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>from, with that share of to mixed in (0: from; 1: to).</summary>
    public static uint Mix(uint from, uint to, double share)
    {
        uint Channel(int shift) => (uint)Math.Round(((from >> shift) & 0xFF) * (1 - share) + ((to >> shift) & 0xFF) * share);
        return 0xFF000000 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    /// <summary>Black or white, whichever reads better on a fill.</summary>
    public static uint On(uint fill) => Contrast(fill, Black) >= Contrast(fill, White) ? Black : White;

    /// <summary>color, moved towards another (the text colour) only as far as it takes to reach the ratio on back:
    /// a pale accent on a light window, or a deep one on a dark window, would otherwise be unreadable.</summary>
    public static uint Readable(uint color, uint back, uint towards, double ratio = TextContrast)
    {
        for (var step = 0; step <= 10; step++)
        {
            var mixed = Mix(color, towards, step / 10.0);
            if (Contrast(mixed, back) >= ratio)
                return mixed;
        }
        return towards;
    }

    /// <summary>The accent for filled controls and for links, out of Windows' accent palette (AccentPalette under
    /// Explorer\Accent: eight colours as R, G, B, A bytes, lightest first, the accent itself fourth). As Windows 11's
    /// own controls do: a shade darker (fills) and two darker (text) on a light window, two and three lighter on a
    /// dark one. Null if the palette isn't there or isn't that shape.</summary>
    public static (uint Fill, uint Text)? Accent(byte[]? palette, bool dark)
    {
        if (palette is not { Length: >= 28 })
            return null;
        uint At(int index) => 0xFF000000 | (uint)palette[index * 4] << 16 | (uint)palette[index * 4 + 1] << 8 | palette[index * 4 + 2];
        return dark ? (At(1), At(0)) : (At(4), At(5));
    }
}
