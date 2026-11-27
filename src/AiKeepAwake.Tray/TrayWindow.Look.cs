using Microsoft.Win32;
using static AiKeepAwake.TrayApp.Win32;

namespace AiKeepAwake.TrayApp;

/// <summary>How the window looks: its colours (light, dark or high contrast, with the person's accent colour), its
/// type, its layout and its drawing. Windows 11's own measures: 14 px body text and 12 px captions in Segoe UI
/// Variable, a 16 px margin, 32 px controls with 4 px corners, the icon font for the tick and the chevrons.</summary>
internal static unsafe partial class TrayWindow
{
    /// <summary>The window's width at 100%, before the display's scaling and the person's text size.</summary>
    private const int Width = 360;

    private const string ChevronDown = "\uE972", ChevronRight = "\uE974", Tick = "\uE73E";

    private enum Kind { Text, Icon, Button, Link, Check, Fill }

    /// <param name="On">A button filled with the accent colour (it's on, or it's the one thing to do); a ticked box.</param>
    /// <param name="Drops">A button that opens a menu: a chevron after its text.</param>
    /// <param name="Dim">A control that's busy: drawn quieter.</param>
    private sealed record Item(Kind Kind, RECT Rect, string Text, int Id = 0, nint Font = 0, uint Color = 0, uint Flags = 0, bool On = false,
        TrayState State = TrayState.NotNeeded, bool Drops = false, bool Dim = false);

    /// <param name="Back">The window, and a menu.</param>
    /// <param name="Footer">The strip at the bottom of the window.</param>
    /// <param name="Fill">The accent, for a filled control; OnFill is the text on it.</param>
    /// <param name="Outline">The edge of an empty tick box (3:1 on Back).</param>
    /// <param name="Subtle">Under the pointer in a menu.</param>
    private sealed record Theme(bool Dark, uint Back, uint Footer, uint Divider, uint Text, uint Secondary, uint Fill, uint OnFill, uint Link,
        uint Control, uint ControlText, uint ControlBorder, uint ControlHover, uint ControlPressed, uint Outline, uint Subtle, uint Warning,
        bool HighContrast = false);

    private static readonly Theme Light = new(false, 0xFFF9F9F9, 0xFFF3F3F3, 0xFFE5E5E5, 0xFF1A1A1A, 0xFF5C5C5C, 0xFF0067C0, 0xFFFFFFFF, 0xFF003E92,
        0xFFFFFFFF, 0xFF1A1A1A, 0xFFD1D1D1, 0xFFF5F5F5, 0xFFEDEDED, 0xFF868686, 0xFFEDEDED, 0xFF9D5D00);

    private static readonly Theme Dark = new(true, 0xFF2C2C2C, 0xFF202020, 0xFF3A3A3A, 0xFFFFFFFF, 0xFFC8C8C8, 0xFF60CDFF, 0xFF000000, 0xFF99EBFF,
        0xFF373737, 0xFFFFFFFF, 0xFF474747, 0xFF3E3E3E, 0xFF323232, 0xFF9A9A9A, 0xFF3A3A3A, 0xFFFCE100);

    private static Theme theme = Light;
    private static double textScale = 1;
    private static nint fontNormal, fontBold, fontSmall, fontSymbol;
    private static readonly List<Item> items = [];
    private static int contentHeight = 300;
    private static int? hover, pressed, focus;

    /// <summary>The focus ring shows once the keyboard is used, not for a click.</summary>
    private static bool focusShown;

    // ---- Light, dark, high contrast ------------------------------------------------------------------------------

    /// <summary>AI_KEEP_AWAKE_THEME=light or dark: that theme with Windows' default accent and text size, whatever
    /// the person's settings (for screenshots, and for looking at the other theme without changing Windows).</summary>
    private static bool? ForcedDark() => Environment.GetEnvironmentVariable("AI_KEEP_AWAKE_THEME") switch
    {
        "dark" => true,
        "light" => false,
        _ => null,
    };

    private static void ReadTheme()
    {
        if (ForcedDark() is { } forced)
        {
            (theme, textScale) = (forced ? Dark : Light, 1);
            return;
        }
        textScale = Math.Clamp((Setting(@"Software\Microsoft\Accessibility", "TextScaleFactor") as int? ?? 100) / 100.0, 1, 2);
        if (HighContrastOn())
        {
            theme = SystemColours();
            return;
        }
        var basis = AppsDark() ? Dark : Light;
        var accent = TrayColors.Accent(Setting(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette") as byte[], basis.Dark)
                     ?? (basis.Fill, basis.Link);
        var fill = TrayColors.Readable(accent.Fill, basis.Back, basis.Text, TrayColors.ShapeContrast);
        var link = TrayColors.Readable(TrayColors.Readable(accent.Text, basis.Footer, basis.Text), basis.Back, basis.Text);
        theme = basis with { Fill = fill, OnFill = TrayColors.On(fill), Link = link };
    }

    /// <summary>A high contrast theme is on: the window takes Windows' colours for it, and the menus are Windows' own.</summary>
    private static Theme SystemColours()
    {
        static uint Sys(int index) => 0xFF000000 | Rgb(GetSysColor(index));
        uint window = Sys(COLOR_WINDOW), text = Sys(COLOR_WINDOWTEXT);
        return new Theme(TrayColors.Luminance(window) < 0.5, window, window, text, text, text, Sys(COLOR_HIGHLIGHT), Sys(COLOR_HIGHLIGHTTEXT),
            Sys(COLOR_HOTLIGHT), Sys(COLOR_BTNFACE), Sys(COLOR_BTNTEXT), Sys(COLOR_BTNTEXT), Sys(COLOR_HIGHLIGHT), Sys(COLOR_HIGHLIGHT), text,
            Sys(COLOR_HIGHLIGHT), text, HighContrast: true);
    }

    private static bool HighContrastOn()
    {
        var contrast = new HIGHCONTRASTW { cbSize = (uint)sizeof(HIGHCONTRASTW) };
        return SystemParametersInfoW(SPI_GETHIGHCONTRAST, contrast.cbSize, ref contrast, 0) && (contrast.dwFlags & HCF_HIGHCONTRASTON) != 0;
    }

    private static bool AppsDark() => ForcedDark() ?? Setting(Personalize, "AppsUseLightTheme") as int? == 0;

    private static bool TaskbarDark() => ForcedDark() ?? Setting(Personalize, "SystemUsesLightTheme") as int? != 1;

    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static object? Setting(string key, string name)
    {
        try
        {
            using var opened = Registry.CurrentUser.OpenSubKey(key);
            return opened?.GetValue(name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The window's frame, from Windows: dark or light, and Windows 11's rounded corners (and their shadow).</summary>
    private static void Chrome()
    {
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, theme.Dark ? 1 : 0, 4);
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND, 4);
    }

    // ---- Type and measures ---------------------------------------------------------------------------------------

    /// <summary>A measure at 100%, in this display's pixels at the person's text size.</summary>
    private static int S(double value) => (int)Math.Round(value * dpi / 96.0 * textScale);

    private static void Fonts()
    {
        foreach (var font in new[] { fontNormal, fontBold, fontSmall, fontSymbol })
            if (font != 0)
                DeleteObject(font);
        var eleven = Environment.OSVersion.Version.Build >= 22000;   // Segoe UI Variable and Segoe Fluent Icons came with Windows 11
        nint Font(int size, int weight, string face) => CreateFontW(-S(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, face);
        fontNormal = Font(14, 400, eleven ? "Segoe UI Variable Text" : "Segoe UI");
        fontBold = Font(16, 600, eleven ? "Segoe UI Variable Text" : "Segoe UI");
        fontSmall = Font(12, 400, eleven ? "Segoe UI Variable Small" : "Segoe UI");
        fontSymbol = Font(12, 400, eleven ? "Segoe Fluent Icons" : "Segoe MDL2 Assets");
    }

    private static int TextWidth(string text, nint font) => Measure(text, font, 0).Width;

    private static int LineHeight(nint font) => Measure("Ag", font, 0).Height;

    /// <summary>How many lines a text takes in that width.</summary>
    private static int WrappedLines(string text, nint font, int width) =>
        (int)Math.Round(Measure(text, font, width).Height / (double)Math.Max(1, LineHeight(font)));

    /// <summary>A text's size on one line, or (wrap: a width) broken into lines of that width.</summary>
    private static RECT Measure(string text, nint font, int wrap)
    {
        var dc = CreateCompatibleDC(0);
        try
        {
            var old = SelectObject(dc, font);
            var rect = new RECT { Right = wrap > 0 ? wrap : S(1000), Bottom = S(100) };
            DrawTextW(dc, text, text.Length, ref rect, DT_CALCRECT | DT_NOPREFIX | (wrap > 0 ? DT_WORDBREAK : DT_SINGLELINE));
            SelectObject(dc, old);
            return rect;
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    // ---- Layout --------------------------------------------------------------------------------------------------

    /// <summary>Lay the window out from the view: what it's doing (or the problem and the one thing to do), the
    /// sessions, when it locks, the two controls and start at logon; then, in the footer strip, the watcher's
    /// line and the links.</summary>
    private static void Layout()
    {
        items.Clear();
        int left = S(16), right = S(Width - 16), y = S(16);
        int body = S(20), row = S(22), caption = S(16), control = S(32);
        const uint line = DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER;

        var top = y;
        items.Add(new Item(Kind.Icon, new RECT { Left = left, Top = y, Right = left + S(32), Bottom = y + S(32) }, "", State: view.State));
        var textLeft = left + S(44);
        // A line of text; one too long for the width goes onto a second line (a third, at a large text size)
        // rather than being cut short.
        void Line(string text, nint font, uint color, int height, int x0 = -1)
        {
            var start = x0 < 0 ? textLeft : x0;
            var lines = Math.Clamp(WrappedLines(text, font, right - start), 1, 4);
            if (lines == 1)
                items.Add(new Item(Kind.Text, new RECT { Left = start, Top = y, Right = right, Bottom = y + height }, text, Font: font, Color: color,
                    Flags: line | DT_END_ELLIPSIS));
            else
                items.Add(new Item(Kind.Text, new RECT { Left = start, Top = y + (height - LineHeight(font)) / 2, Right = right, Bottom = y + lines * height },
                    text, Font: font, Color: color, Flags: DT_WORDBREAK | DT_NOPREFIX));
            y += lines * height;
        }
        Line(view.Headline, fontBold, theme.Text, S(22));
        y += S(2);
        if (view.Problem is { } problem)
        {
            Line(problem.Text, fontNormal, theme.Warning, body);
            if (problem.Action is { } action)
            {
                y += S(8);
                items.Add(new Item(Kind.Button, new RECT { Left = textLeft, Top = y, Right = textLeft + TextWidth(action, fontNormal) + S(32), Bottom = y + control },
                    action, ProblemAction, fontNormal, On: true));
                y += control;
            }
        }
        foreach (var detail in view.Detail)
            Line(detail, fontNormal, theme.Secondary, body);
        y = Math.Max(y, top + S(32)) + S(16);

        // The sessions, as a table: the last two columns as wide as their text, the name has the rest.
        int untilWidth = view.Sessions.Select(session => TextWidth(session.Until, fontNormal)).DefaultIfEmpty().Max();
        int whatWidth = view.Sessions.Select(session => TextWidth(session.What, fontNormal)).DefaultIfEmpty().Max();
        int untilLeft = right - untilWidth, whatLeft = untilLeft - S(16) - whatWidth;
        foreach (var session in view.Sessions)
        {
            void Cell(string text, int from, int to, uint color, uint flags) =>
                items.Add(new Item(Kind.Text, new RECT { Left = from, Top = y, Right = to, Bottom = y + row }, text, Font: fontNormal, Color: color,
                    Flags: line | flags));
            Cell(session.Name, left, whatLeft - S(16), theme.Text, DT_END_ELLIPSIS);
            Cell(session.What, whatLeft, untilLeft - S(16), theme.Secondary, 0);
            Cell(session.Until, untilLeft, right, theme.Secondary, DT_RIGHT);
            y += row;
        }
        if (view.MoreSessions > 0)
            Line($"and {view.MoreSessions} more", fontNormal, theme.Secondary, row, left);
        if (view.Sessions.Count > 0)
            y += S(12);
        foreach (var text in view.LockLines)
            Line(text, fontSmall, theme.Secondary, caption, left);
        if (view.LockLines.Count > 0)
            y += S(16);

        // The two controls: side by side, or one above the other when a label ("Stop keeping awake (until 16:00)")
        // wouldn't fit in half the width.
        string awake = view.AwakeButton, pause = view.PauseButton;
        var half = (right - left - S(8)) / 2;
        int Needs(string text, bool drops) => TextWidth(text, fontNormal) + S(24) + (drops ? S(20) : 0);
        var stacked = Needs(awake, !view.AwakeOn) > half || Needs(pause, !view.PausedOn) > half;
        items.Add(new Item(Kind.Button, new RECT { Left = left, Top = y, Right = stacked ? right : left + half, Bottom = y + control },
            awake, AwakeMenu, fontNormal, On: view.AwakeOn, Drops: !view.AwakeOn));
        if (stacked)
            y += control + S(8);
        items.Add(new Item(Kind.Button, new RECT { Left = stacked ? left : right - half, Top = y, Right = right, Bottom = y + control },
            pause, PauseMenu, fontNormal, On: view.PausedOn, Drops: !view.PausedOn));
        y += control + S(8);

        var logon = busy ?? (atLogon is null ? "Start at logon (checking...)" : "Start at logon");
        items.Add(new Item(Kind.Check, new RECT { Left = left, Top = y, Right = left + S(30) + TextWidth(logon, fontNormal), Bottom = y + control },
            logon, AtLogon, fontNormal, On: atLogon == true, Dim: busy is not null || atLogon is null));
        y += control + S(8);

        // The footer strip.
        items.Add(new Item(Kind.Fill, new RECT { Top = y, Right = S(Width), Bottom = y + S(2000) }, "", Color: theme.Footer));
        items.Add(new Item(Kind.Fill, new RECT { Top = y, Right = S(Width), Bottom = y + Math.Max(1, S(1)) }, "", Color: theme.Divider));
        y += S(12);
        Line(view.Footer, fontSmall, theme.Secondary, caption, left);
        if (view.FooterNote is { } note)
            Line(note, fontSmall, theme.Secondary, caption, left);
        y += S(2);
        var x = left;
        foreach (var (text, id) in new[] { ("Full status", FullStatus), ("Log", OpenLog), ("Settings file", SettingsFile) })
        {
            var width = TextWidth(text, fontSmall);
            items.Add(new Item(Kind.Link, new RECT { Left = x, Top = y, Right = x + width, Bottom = y + S(24) }, text, id, fontSmall));
            x += width + S(16);
        }
        y += S(24);
        if (view.ReleaseLine is { } releaseLine)
        {
            items.Add(new Item(Kind.Link, new RECT { Left = left, Top = y, Right = left + TextWidth(releaseLine, fontSmall), Bottom = y + S(24) },
                releaseLine, ReleaseLink, fontSmall));
            y += S(24);
        }
        contentHeight = y + S(10);
        if (focus is { } kept && !items.Any(item => item.Id == kept))
            focus = null;
    }

    private static Item? ItemAt(int x, int y)
    {
        foreach (var item in items)
            if (item.Id != 0 && item.Rect.Contains(x, y))
                return item;
        return null;
    }

    private static int? HitTest(int x, int y) => ItemAt(x, y)?.Id;

    // ---- Drawing -------------------------------------------------------------------------------------------------

    private static void Paint()
    {
        var paint = BeginPaint(hwnd, out var ps);
        try
        {
            GetClientRect(hwnd, out var client);
            var dc = CreateCompatibleDC(paint);
            var bitmap = CreateCompatibleBitmap(paint, client.Width, client.Height);
            var oldBitmap = SelectObject(dc, bitmap);
            Fill(dc, client, theme.Back);
            SetBkMode(dc, TRANSPARENT);
            foreach (var item in items)
                Draw(dc, item);
            if (focusShown && focus is { } id && items.FirstOrDefault(item => item.Id == id) is { } focused)
                FocusRing(dc, focused);
            BitBlt(paint, 0, 0, client.Width, client.Height, dc, 0, 0, 0x00CC0020);   // SRCCOPY
            SelectObject(dc, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
        finally
        {
            EndPaint(hwnd, ps);
        }
    }

    private static void Fill(nint dc, RECT rect, uint color)
    {
        var brush = CreateSolidBrush(Rgb(color));
        FillRect(dc, rect, brush);
        DeleteObject(brush);
    }

    /// <summary>A rounded box: filled, with a one-pixel edge.</summary>
    private static void Rounded(nint dc, RECT rect, int radius, uint fill, uint edge)
    {
        var brush = CreateSolidBrush(Rgb(fill));
        var pen = CreatePen(0, 1, Rgb(edge));
        var oldBrush = SelectObject(dc, brush);
        var oldPen = SelectObject(dc, pen);
        RoundRect(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius * 2, radius * 2);
        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        DeleteObject(brush);
        DeleteObject(pen);
    }

    /// <summary>Two pixels in the text colour, just outside the control: where the keyboard is.</summary>
    private static void FocusRing(nint dc, Item item)
    {
        var rect = item.Rect;
        int outset = S(3), side = item.Kind is Kind.Link or Kind.Check ? S(5) : outset;
        if (item.Kind == Kind.Link)
            outset = 0;
        var pen = CreatePen(PS_INSIDEFRAME, S(2), Rgb(theme.Text));
        var oldPen = SelectObject(dc, pen);
        var oldBrush = SelectObject(dc, GetStockObject(NULL_BRUSH));
        RoundRect(dc, rect.Left - side, rect.Top - outset, rect.Right + side, rect.Bottom + outset, S(12), S(12));
        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        DeleteObject(pen);
    }

    private static void Draw(nint dc, Item item)
    {
        var rect = item.Rect;
        var hovered = hover == item.Id && item.Id != 0 && !item.Dim;
        var down = hovered && pressed == item.Id;
        switch (item.Kind)
        {
            case Kind.Fill:
                Fill(dc, rect, item.Color);
                break;
            case Kind.Text:
                SelectObject(dc, item.Font);
                SetTextColor(dc, Rgb(item.Color));
                DrawTextW(dc, item.Text, item.Text.Length, ref rect, item.Flags);
                break;
            case Kind.Icon:
                var size = rect.Width;
                var art = MakeIcon(item.State, size, theme.Dark);
                DrawIconEx(dc, rect.Left, rect.Top, art, size, size, 0, 0, 3);   // DI_NORMAL
                DestroyIcon(art);
                break;
            case Kind.Link:
                SelectObject(dc, item.Font);
                SetTextColor(dc, Rgb(theme.Link));
                DrawTextW(dc, item.Text, item.Text.Length, ref rect, DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER);
                if (hovered)
                {
                    var under = (rect.Top + rect.Bottom) / 2 + S(8);
                    Fill(dc, new RECT { Left = rect.Left, Top = under, Right = rect.Right, Bottom = under + Math.Max(1, S(1)) }, theme.Link);
                }
                break;
            case Kind.Button:
                {
                    uint fill, edge, text;
                    if (item.On)
                    {
                        fill = edge = TrayColors.Mix(theme.Fill, theme.Back, down ? 0.2 : hovered ? 0.1 : 0);
                        text = theme.OnFill;
                    }
                    else
                    {
                        fill = down ? theme.ControlPressed : hovered ? theme.ControlHover : theme.Control;
                        edge = theme.ControlBorder;
                        text = theme.HighContrast && hovered ? theme.OnFill : theme.ControlText;
                    }
                    Rounded(dc, rect, S(4), fill, edge);
                    SelectObject(dc, item.Font);
                    SetTextColor(dc, Rgb(text));
                    // The text and its chevron, centred together.
                    var chevron = item.Drops ? S(8) + S(12) : 0;
                    var measured = new RECT { Right = S(1000), Bottom = rect.Height };
                    DrawTextW(dc, item.Text, item.Text.Length, ref measured, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
                    var width = Math.Min(measured.Width, rect.Width - S(24) - chevron);
                    var start = rect.Left + (rect.Width - width - chevron) / 2;
                    var label = new RECT { Left = start, Top = rect.Top, Right = start + width, Bottom = rect.Bottom - S(1) };
                    DrawTextW(dc, item.Text, item.Text.Length, ref label, DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER | DT_END_ELLIPSIS);
                    if (item.Drops)
                    {
                        var glyph = new RECT { Left = label.Right + S(8), Top = rect.Top, Right = label.Right + chevron, Bottom = rect.Bottom };
                        SelectObject(dc, fontSymbol);
                        DrawTextW(dc, ChevronDown, 1, ref glyph, DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER | DT_CENTER);
                    }
                    break;
                }
            case Kind.Check:
                {
                    var edge = S(20);
                    var boxTop = rect.Top + (rect.Height - edge) / 2;
                    var box = new RECT { Left = rect.Left, Top = boxTop, Right = rect.Left + edge, Bottom = boxTop + edge };
                    var accent = TrayColors.Mix(theme.Fill, theme.Back, item.Dim ? 0.5 : down ? 0.2 : hovered ? 0.1 : 0);
                    Rounded(dc, box, S(4), item.On ? accent : down ? theme.ControlPressed : hovered ? theme.ControlHover : theme.Back,
                        item.On ? accent : item.Dim ? theme.ControlBorder : theme.Outline);
                    if (item.On)
                    {
                        SelectObject(dc, fontSymbol);
                        SetTextColor(dc, Rgb(theme.OnFill));
                        DrawTextW(dc, Tick, 1, ref box, DT_SINGLELINE | DT_CENTER | DT_VCENTER | DT_NOPREFIX);
                    }
                    var label = new RECT { Left = rect.Left + S(30), Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom - S(1) };
                    SelectObject(dc, item.Font);
                    SetTextColor(dc, Rgb(item.Dim ? theme.Secondary : theme.Text));
                    DrawTextW(dc, item.Text, item.Text.Length, ref label, DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER);
                    break;
                }
        }
    }
}
