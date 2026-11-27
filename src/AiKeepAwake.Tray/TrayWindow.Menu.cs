using System.Runtime.InteropServices;
using static AiKeepAwake.TrayApp.Win32;

namespace AiKeepAwake.TrayApp;

/// <summary>The right-click menu, and the two buttons' menus. Windows draws a Win32 menu light whatever the app
/// theme, and has no supported switch for that, so these are owner-drawn: each item measured and painted here in
/// the window's theme, over a background brush Windows fills the rest with (SetMenuInfo). They are still Windows'
/// menus: its keys, its submenus, its timing. What owner-drawing takes away is put back by hand: a screen reader
/// gets each item's text from an MSAAMENUINFO, and a typed letter picks the item that starts with it. With a high
/// contrast theme on they aren't owner-drawn at all: Windows' own menu is right there.</summary>
internal static unsafe partial class TrayWindow
{
    private sealed record MenuEntry(string Text, bool Header = false, bool Separator = false, bool Sub = false);

    /// <summary>The open menu's items, by the item data Windows hands back, and by menu in the order shown.</summary>
    private static readonly Dictionary<nint, MenuEntry> menuEntries = [];
    private static readonly Dictionary<nint, List<MenuEntry>> menuLists = [];

    private static nint NewMenu()
    {
        var menu = CreatePopupMenu();
        menuLists[menu] = [];
        return menu;
    }

    private static void Add(nint menu, int id, string text, bool header = false, nint sub = 0)
    {
        var flags = (header ? MF_GRAYED : 0) | (sub != 0 ? MF_POPUP : 0);
        var what = sub != 0 ? (nuint)sub : (nuint)id;
        if (theme.HighContrast)
            AppendMenuW(menu, MF_STRING | flags, what, text);
        else
            AppendMenuData(menu, MF_OWNERDRAW | flags, what, Entry(menu, new MenuEntry(text, header, Sub: sub != 0)));
    }

    private static void AddSeparator(nint menu)
    {
        if (theme.HighContrast)
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
        else
            AppendMenuData(menu, MF_OWNERDRAW | MF_SEPARATOR, 0, Entry(menu, new MenuEntry("", Separator: true)));
    }

    /// <summary>An item's data: an MSAAMENUINFO (what a screen reader reads of an owner-drawn item) and its text
    /// after it, in memory that stays put until the menu closes.</summary>
    private static nint Entry(nint menu, MenuEntry entry)
    {
        var length = entry.Text.Length;
        var info = (MSAAMENUINFO*)NativeMemory.Alloc((nuint)(sizeof(MSAAMENUINFO) + (length + 1) * sizeof(char)));
        var text = (char*)(info + 1);
        entry.Text.CopyTo(new Span<char>(text, length));
        text[length] = '\0';
        *info = new MSAAMENUINFO { dwMSAASignature = MSAA_MENU_SIG, cchWText = (uint)length, pszWText = text };
        menuEntries[(nint)info] = entry;
        menuLists[menu].Add(entry);
        return (nint)info;
    }

    /// <summary>Show a menu and wait for the choice (0: none); the menu and what it held are gone afterwards.</summary>
    private static int ShowMenu(nint menu, uint flags, int x, int y)
    {
        nint back = 0;
        try
        {
            if (!theme.HighContrast)
            {
                back = CreateSolidBrush(Rgb(theme.Back));
                SetMenuInfo(menu, new MENUINFO { cbSize = (uint)sizeof(MENUINFO), fMask = MIM_BACKGROUND | MIM_APPLYTOSUBMENUS, hbrBack = back });
            }
            return TrackPopupMenuEx(menu, flags | TPM_RETURNCMD, x, y, hwnd, 0);
        }
        finally
        {
            DestroyMenu(menu);
            if (back != 0)
                DeleteObject(back);
            foreach (var data in menuEntries.Keys)
                NativeMemory.Free((void*)data);
            menuEntries.Clear();
            menuLists.Clear();
        }
    }

    private static void Menu(int x, int y)
    {
        var menu = NewMenu();
        Add(menu, 1, view.MenuHeader, header: true);
        AddSeparator(menu);
        AddAwake(menu);
        AddPause(menu);
        AddSeparator(menu);
        Add(menu, OpenWindow, "Open ai-keep-awake");
        Add(menu, FullStatus, "Full status...");
        Add(menu, OpenLog, "Open the log");
        AddSeparator(menu);
        Add(menu, CloseTray, "Close the tray icon");
        SetForegroundWindow(hwnd);
        var chosen = ShowMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, x, y);
        PostMessageW(hwnd, WM_NULL, 0, 0);
        if (chosen != 0)
            Command(chosen);
    }

    private static void AwakeChoices(nint menu)
    {
        Add(menu, AwakeHour, "for 1 hour");
        Add(menu, AwakeTwo, "for 2 hours");
        Add(menu, AwakeOff, "until I turn it off");
    }

    private static void PauseChoices(nint menu)
    {
        Add(menu, PauseHour, "for 1 hour");
        Add(menu, PauseOn, "until I resume");
    }

    private static void AddAwake(nint menu)
    {
        if (view.AwakeOn)
        {
            Add(menu, StopAwake, view.AwakeButton);
            return;
        }
        var sub = NewMenu();
        AwakeChoices(sub);
        Add(menu, 0, "Keep awake now", sub: sub);
    }

    private static void AddPause(nint menu)
    {
        if (view.PausedOn)
        {
            Add(menu, Resume, $"Resume ({view.Headline.ToLowerInvariant()})");
            return;
        }
        var sub = NewMenu();
        PauseChoices(sub);
        Add(menu, 0, "Pause", sub: sub);
    }

    /// <summary>A button's own menu, under it.</summary>
    private static void ButtonMenu(int id)
    {
        var item = items.First(i => i.Id == id);
        var menu = NewMenu();
        if (id == AwakeMenu)
            AwakeChoices(menu);
        else
            PauseChoices(menu);
        var point = new POINT { X = item.Rect.Left, Y = item.Rect.Bottom + S(2) };
        ClientToScreen(hwnd, ref point);
        modal = true;
        int chosen;
        try
        {
            chosen = ShowMenu(menu, TPM_TOPALIGN, point.X, point.Y);
        }
        finally
        {
            modal = false;
        }
        if (chosen != 0)
            Command(chosen);
    }

    // ---- Drawing them --------------------------------------------------------------------------------------------

    /// <summary>WM_MEASUREITEM: how big one of the menu's items is.</summary>
    private static bool MeasureMenuItem(nint lParam)
    {
        var measure = (MEASUREITEMSTRUCT*)lParam;
        if (measure->CtlType != ODT_MENU || !menuEntries.TryGetValue((nint)measure->itemData, out var entry))
            return false;
        if (entry.Separator)
        {
            (measure->itemWidth, measure->itemHeight) = (0, (uint)S(9));
            return true;
        }
        var width = S(16) + (entry.Header ? S(24) : 0) + TextWidth(entry.Text, fontNormal) + (entry.Sub ? S(28) : 0) + S(16);
        (measure->itemWidth, measure->itemHeight) = ((uint)Math.Max(width, S(180)), (uint)S(30));
        return true;
    }

    /// <summary>WM_DRAWITEM: paint one of the menu's items, in the window's theme.</summary>
    private static bool DrawMenuItem(nint lParam)
    {
        var draw = (DRAWITEMSTRUCT*)lParam;
        if (draw->CtlType != ODT_MENU || !menuEntries.TryGetValue((nint)draw->itemData, out var entry))
            return false;
        var dc = draw->hDC;
        var rect = draw->rcItem;
        Fill(dc, rect, theme.Back);
        if (entry.Separator)
        {
            var middle = (rect.Top + rect.Bottom) / 2;
            Fill(dc, new RECT { Left = rect.Left, Top = middle, Right = rect.Right, Bottom = middle + Math.Max(1, S(1)) }, theme.Divider);
            return true;
        }
        if ((draw->itemState & ODS_SELECTED) != 0 && !entry.Header)
            Rounded(dc, new RECT { Left = rect.Left + S(4), Top = rect.Top + S(1), Right = rect.Right - S(4), Bottom = rect.Bottom - S(1) }, S(4),
                theme.Subtle, theme.Subtle);
        SetBkMode(dc, TRANSPARENT);
        var oldFont = SelectObject(dc, fontNormal);
        var left = rect.Left + S(16);
        if (entry.Header)
        {
            // What it's doing, with the tray icon's own mark for it.
            var size = S(16);
            var art = MakeIcon(view.State, size, theme.Dark);
            DrawIconEx(dc, left, (rect.Top + rect.Bottom - size) / 2, art, size, size, 0, 0, 3);   // DI_NORMAL
            DestroyIcon(art);
            left += S(24);
        }
        SetTextColor(dc, Rgb(entry.Header ? theme.Secondary : theme.Text));
        var text = new RECT { Left = left, Top = rect.Top, Right = rect.Right - S(16) - (entry.Sub ? S(20) : 0), Bottom = rect.Bottom - S(1) };
        DrawTextW(dc, entry.Text, entry.Text.Length, ref text, DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER | DT_END_ELLIPSIS);
        if (entry.Sub)
        {
            var glyph = new RECT { Left = rect.Right - S(16) - S(12), Top = rect.Top, Right = rect.Right - S(16), Bottom = rect.Bottom };
            SelectObject(dc, fontSymbol);
            SetTextColor(dc, Rgb(theme.Secondary));
            DrawTextW(dc, ChevronRight, 1, ref glyph, DT_SINGLELINE | DT_NOPREFIX | DT_VCENTER | DT_CENTER);
        }
        SelectObject(dc, oldFont);
        // Windows draws its own submenu arrow after this, in its own (light theme) colour: not over this item.
        if (entry.Sub)
            ExcludeClipRect(dc, rect.Left, rect.Top, rect.Right, rect.Bottom);
        return true;
    }

    /// <summary>WM_MENUCHAR: a letter typed in a menu. Owner-drawn items have no text Windows can match, so match it
    /// here: the one item that starts with the letter is chosen; of several, the first is selected.</summary>
    private static nint MenuChar(nint wParam, nint lParam)
    {
        if (!menuLists.TryGetValue(lParam, out var list))
            return 0;
        var typed = char.ToUpperInvariant((char)(ushort)LoWord(wParam));
        var matches = Enumerable.Range(0, list.Count)
            .Where(i => !list[i].Header && !list[i].Separator && list[i].Text.Length > 0 && char.ToUpperInvariant(list[i].Text[0]) == typed)
            .ToList();
        return matches.Count == 0 ? 0 : (matches.Count == 1 ? MNC_EXECUTE : MNC_SELECT) << 16 | matches[0];
    }
}
