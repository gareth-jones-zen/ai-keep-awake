using System.Runtime.InteropServices;

namespace AiKeepAwake.TrayApp;

/// <summary>The Windows calls the tray makes, as source-generated imports (Native AOT needs nothing at run time).</summary>
internal static unsafe partial class Win32
{
    internal const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_ACTIVATE = 0x0006, WM_PAINT = 0x000F, WM_CLOSE = 0x0010,
        WM_ERASEBKGND = 0x0014, WM_SETTINGCHANGE = 0x001A, WM_SETCURSOR = 0x0020, WM_KEYDOWN = 0x0100, WM_COMMAND = 0x0111,
        WM_TIMER = 0x0113, WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B,
        WM_DPICHANGED = 0x02E0, WM_NULL = 0x0000, WM_MOUSELEAVE = 0x02A3, WM_APP = 0x8000;
    internal const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
    internal const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    internal const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    internal const uint NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_RESPECT_QUIET_TIME = 0x80;
    internal const uint NOTIFYICON_VERSION_4 = 4;
    internal const uint WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_CLIPCHILDREN = 0x02000000;
    internal const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    internal const int SW_HIDE = 0, SW_SHOW = 5;
    internal const uint MF_STRING = 0, MF_GRAYED = 1, MF_DISABLED = 2, MF_CHECKED = 8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    internal const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTALIGN = 8, TPM_TOPALIGN = 0;
    internal const uint DT_LEFT = 0, DT_CENTER = 1, DT_VCENTER = 4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000,
        DT_CALCRECT = 0x400;
    internal const int TRANSPARENT = 1;
    internal const int IDC_ARROW = 32512, IDC_HAND = 32649;
    internal const uint SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    internal const int VK_ESCAPE = 0x1B;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const int SM_CXSMICON = 49;
    internal const uint MB_OK = 0, MB_OKCANCEL = 1, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40, MB_TOPMOST = 0x40000;
    internal const int IDOK = 1;
    internal const uint TME_LEAVE = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        public int fIcon;
        public int xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACKMOUSEEVENT
    {
        public uint cbSize, dwFlags;
        public nint hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    // user32
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(in WNDCLASSEXW wc);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width,
        int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    internal static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(in MSG msg);

    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(in MSG msg);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint func);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    internal static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    internal static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    internal static partial nint BeginPaint(nint hwnd, out PAINTSTRUCT ps);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndPaint(nint hwnd, in PAINTSTRUCT ps);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(nint hdc, in RECT rect, nint brush);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DrawTextW(nint hdc, string text, int length, ref RECT rect, uint format);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    internal static partial nint LoadCursorW(nint instance, nint name);

    [LibraryImport("user32.dll")]
    internal static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(nint hwnd, string text, string caption, uint type);

    [LibraryImport("user32.dll")]
    internal static partial nint CreateIconIndirect(in ICONINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClientToScreen(nint hwnd, ref POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustWindowRectExForDpi(ref RECT rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    // gdi32
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [LibraryImport("gdi32.dll")]
    internal static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll")]
    internal static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreatePen(int style, int width, uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RoundRect(nint hdc, int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("gdi32.dll")]
    internal static partial nint GetStockObject(int obj);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateDIBSection(nint hdc, in BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DrawIconEx(nint hdc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);

    // shell32, dwmapi, kernel32
    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [LibraryImport("shell32.dll")]
    internal static partial int Shell_NotifyIconGetRect(in NOTIFYICONIDENTIFIER identifier, out RECT rect);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint ShellExecuteW(nint hwnd, string? verb, string file, string? parameters, string? directory, int show);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, in int value, uint size);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? name);

    internal static int LoWord(nint value) => (short)((long)value & 0xFFFF);
    internal static int HiWord(nint value) => (short)(((long)value >> 16) & 0xFFFF);
    internal static uint Rgb(uint argb) => ((argb & 0xFF) << 16) | (argb & 0xFF00) | ((argb >> 16) & 0xFF);   // COLORREF is 0x00BBGGRR

    internal static void Copy(string text, char* buffer, int size)
    {
        var n = Math.Min(text.Length, size - 1);
        for (var i = 0; i < n; i++)
            buffer[i] = text[i];
        buffer[n] = '\0';
    }
}
