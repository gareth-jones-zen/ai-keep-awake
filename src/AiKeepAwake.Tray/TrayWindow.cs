using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using WinBackground;
using static AiKeepAwake.TrayApp.Win32;

namespace AiKeepAwake.TrayApp;

/// <summary>The tray icon and its small window. One window does both jobs: hidden, it receives the icon's messages
/// (and Explorer's TaskbarCreated, to put the icon back after a restart); shown, it's the window by the clock: no
/// title bar, as Windows 11's own are, closed by Escape, Alt+F4 or a click anywhere else. How it looks is in
/// TrayWindow.Look.cs, and its menus in TrayWindow.Menu.cs.
///
/// It reads what the watcher and the hooks write (state.json, the leases, the pause file) every two seconds, works
/// out what to show with TrayView, and acts only through the same files and commands as the command line (Control,
/// Cli.Run in-process). The watcher never waits on it: if the tray crashes or is closed, nothing changes but the
/// icon, and a "until I turn it off" keep-awake ends with it.</summary>
internal static unsafe partial class TrayWindow
{
    private const uint WmTray = WM_APP + 1, WmShow = WM_APP + 2, WmRefresh = WM_APP + 3, WmQuit = WM_APP + 4, WmDone = WM_APP + 5;
    private const uint IconId = 1;
    private const nuint RefreshTimer = 1;

    // Menu and window commands.
    private const int AwakeHour = 101, AwakeTwo = 102, AwakeOff = 103, StopAwake = 104, PauseHour = 111, PauseOn = 112, Resume = 113,
        OpenWindow = 121, FullStatus = 122, OpenLog = 123, SettingsFile = 124, CloseTray = 131, AwakeMenu = 201, PauseMenu = 202,
        AtLogon = 203, ProblemAction = 204, ReleaseLink = 205;

    private static Config config = null!;
    private static Action<string> log = _ => { };
    private static nint hwnd;
    private static uint taskbarCreated;
    private static nint icon;
    private static (TrayState State, bool Dark, int Size)? iconKey;
    private static string tip = "";
    private static TrayView view = new();
    private static TrayFacts facts = new();
    private static int hookEvents = -1;
    private static double hooksReadAt;
    private static bool? atLogon;
    private static bool taskRegistered;
    private static double logonCheckedAt = double.MinValue;
    private static InstallRecord? record;
    private static double recordReadAt = double.MinValue;
    private static ReleaseInfo? release;
    private static double noticeSeen;
    private static bool untilOffMine;
    private static long hiddenAt;
    private static bool modal;
    private static string? busy;
    private static string? workerMessage;
    private static uint dpi = 96;
    private static string title = "ai-keep-awake";

    /// <summary>A plain popup: Windows rounds its corners and gives it a shadow (Chrome).</summary>
    private const uint WindowStyle = WS_POPUP, WindowExStyle = WS_EX_TOOLWINDOW | WS_EX_TOPMOST;

    public static int Run(Config settings, bool show)
    {
        config = settings;
        log = text => Logger.Create(config.LogFile)($"tray: {text}");
        SetCurrentProcessExplicitAppUserModelID(WindowsDesktop.AppId);
        noticeSeen = Hook.Now();   // only notices from now on
        record = InstallRecord.Read(config);
        ReadTheme();

        var instance = GetModuleHandleW(null);
        fixed (char* className = Tray.WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = LoadCursorW(0, IDC_ARROW),
                lpszClassName = className,
            };
            if (RegisterClassExW(wc) == 0)
                return 1;
        }
        hwnd = CreateWindowExW(WindowExStyle, Tray.WindowClass, title, WindowStyle, 0, 0, Width, 300, 0, 0, instance, 0);
        if (hwnd == 0)
            return 1;
        dpi = Math.Max(96, GetDpiForWindow(hwnd));
        Chrome();
        Fonts();
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        Refresh();
        AddIcon();
        SetTimer(hwnd, RefreshTimer, 2000, 0);
        WatchQuit();
        CheckLogon();
        StartReleaseChecks();
        if (show)
            Show();
        log($"started ({Cli.Version})");

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }
        if (untilOffMine)
            Control.ClearManual(config);
        RemoveIcon();
        log("closed");
        return 0;
    }

    /// <summary>A second start (the Start menu shortcut) asks the running tray to show its window.</summary>
    public static void ShowExisting()
    {
        var existing = FindWindowW(Tray.WindowClass, null);
        if (existing != 0)
        {
            AllowSetForegroundWindow(-1);
            PostMessageW(existing, WmShow, 0, 0);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int pid);

    [UnmanagedCallersOnly]
    private static nint WndProc(nint window, uint msg, nint wParam, nint lParam)
    {
        try
        {
            return Handle(window, msg, wParam, lParam);
        }
        catch (Exception error)
        {
            log($"error: {error.GetType().Name}: {error.Message}");
            return DefWindowProcW(window, msg, wParam, lParam);
        }
    }

    private static nint Handle(nint window, uint msg, nint wParam, nint lParam)
    {
        if (msg == taskbarCreated && taskbarCreated != 0)
        {
            iconKey = null;
            AddIcon();
            return 0;
        }
        switch (msg)
        {
            case WmTray:
                switch ((uint)LoWord(lParam))
                {
                    case NIN_SELECT or NIN_KEYSELECT:
                        if (IsWindowVisible(hwnd))
                            Hide();
                        else if (Environment.TickCount64 - hiddenAt > 400)   // the click that hid it by taking focus away
                            Show();
                        break;
                    case WM_CONTEXTMENU:
                        Menu(LoWord(wParam), HiWord(wParam));
                        break;
                    case NIN_BALLOONUSERCLICK:
                        Show();
                        break;
                }
                return 0;
            case WmShow:
                Show();
                return 0;
            case WmRefresh:
                Refresh();
                return 0;
            case WmDone:
                busy = null;
                if (workerMessage is { } text)
                {
                    workerMessage = null;
                    Message(text, warning: true);
                }
                CheckLogon(force: true);
                Refresh();
                return 0;
            case WmQuit:
                PostQuitMessage(0);
                return 0;
            case WM_TIMER:
                Refresh();
                return 0;
            case WM_ACTIVATE:
                if (LoWord(wParam) == 0 && !modal && IsWindowVisible(hwnd))
                    Hide();
                return 0;
            case WM_CLOSE:
                Hide();   // Alt+F4: the window goes, the tray stays
                return 0;
            case WM_KEYDOWN:
                Key((int)wParam);
                return 0;
            case WM_MEASUREITEM:
                if (MeasureMenuItem(lParam))
                    return 1;
                break;
            case WM_DRAWITEM:
                if (DrawMenuItem(lParam))
                    return 1;
                break;
            case WM_MENUCHAR:
                return MenuChar(wParam, lParam);
            case WM_ERASEBKGND:
                return 1;
            case WM_PAINT:
                Paint();
                return 0;
            case WM_MOUSEMOVE:
                {
                    var at = HitTest(LoWord(lParam), HiWord(lParam));
                    if (at != hover)
                    {
                        hover = at;
                        InvalidateRect(hwnd, 0, false);
                        var track = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = hwnd };
                        TrackMouseEvent(ref track);
                    }
                    return 0;
                }
            case WM_MOUSELEAVE:
                hover = null;
                InvalidateRect(hwnd, 0, false);
                return 0;
            case WM_SETCURSOR:
                if (GetCursorPos(out var cursor))
                {
                    var point = cursor;
                    ScreenToClient(hwnd, ref point);
                    if (ItemAt(point.X, point.Y) is { Kind: Kind.Link })   // a hand for a link, as Windows has it; the arrow for a button
                    {
                        SetCursor(LoadCursorW(0, IDC_HAND));
                        return 1;
                    }
                }
                break;
            case WM_LBUTTONDOWN:
                pressed = HitTest(LoWord(lParam), HiWord(lParam));
                focusShown = false;
                if (pressed is not null)
                {
                    focus = pressed;
                    SetCapture(hwnd);
                }
                InvalidateRect(hwnd, 0, false);
                return 0;
            case WM_LBUTTONUP:
                {
                    // A click is a press and a release on the same control: sliding off it lets go.
                    var was = pressed;
                    pressed = null;
                    ReleaseCapture();
                    InvalidateRect(hwnd, 0, false);
                    if (was is { } id && HitTest(LoWord(lParam), HiWord(lParam)) == id)
                        Click(id);
                    return 0;
                }
            case WM_DPICHANGED:
                dpi = (uint)HiWord(wParam);
                Fonts();
                iconKey = null;
                Refresh();
                if (IsWindowVisible(hwnd))
                    Place();
                return 0;
            case WM_SETTINGCHANGE or WM_THEMECHANGED or WM_SYSCOLORCHANGE or WM_DWMCOLORIZATIONCOLORCHANGED:
                // Light or dark, the accent colour, a high contrast theme, the text size: all take effect at once.
                ReadTheme();
                Chrome();
                Fonts();
                iconKey = null;
                Refresh();
                return 0;
        }
        return DefWindowProcW(window, msg, wParam, lParam);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint hwnd, ref POINT point);

    /// <summary>The keyboard: Tab and Shift+Tab (or the arrows) go round the controls, Enter or Space presses the
    /// one in focus, Escape closes the window.</summary>
    private static void Key(int key)
    {
        switch (key)
        {
            case VK_ESCAPE:
                Hide();
                break;
            case VK_TAB or VK_DOWN or VK_UP:
                {
                    var stops = items.Where(item => item.Id != 0).Select(item => item.Id).ToList();
                    if (stops.Count == 0)
                        break;
                    var back = key == VK_UP || key == VK_TAB && GetKeyState(VK_SHIFT) < 0;
                    var at = focus is { } id && focusShown ? stops.IndexOf(id) : -1;
                    focus = stops[at < 0 ? back ? stops.Count - 1 : 0 : (at + (back ? stops.Count - 1 : 1)) % stops.Count];
                    focusShown = true;
                    InvalidateRect(hwnd, 0, false);
                    break;
                }
            case VK_RETURN or VK_SPACE:
                if (focus is { } chosen && items.Any(item => item.Id == chosen))
                {
                    focusShown = true;
                    Click(chosen);
                }
                break;
        }
    }

    // ---- What's shown --------------------------------------------------------------------------------------------

    private static void Refresh()
    {
        var now = Hook.Now();
        if (now - hooksReadAt >= 10 || hookEvents < 0)
        {
            hookEvents = Install.Installed(config.ClaudeSettings).Count;
            hooksReadAt = now;
        }
        if (now - recordReadAt >= 60)
        {
            record = InstallRecord.Read(config);
            recordReadAt = now;
        }
        JsonObject? state = null;
        try
        {
            state = PyJson.Parse(PyJson.ReadStrict(config.StateFile)) as JsonObject;
        }
        catch (Exception)
        {
        }
        var pause = Control.ReadPause(config);
        facts = new TrayFacts
        {
            Now = now,
            WatcherRunning = Locks.Held(Watcher.LockName(config)),
            State = state,
            Live = Leases.Live(config.LeasesDir, now, config.CheckPid),
            Pause = Control.Paused(pause, now) ? pause : null,
            HookEvents = hookEvents,
            AtLogon = atLogon,
            TaskRegistered = taskRegistered,
            Version = Cli.Version,
            Install = record,
            Release = release,
        };
        view = TrayView.From(facts);
        if (title != view.MenuHeader)
            SetWindowTextW(hwnd, title = view.MenuHeader);   // what a screen reader says when the window opens
        RenewUntilOff(now);
        UpdateIcon();
        ShowNotice();
        if (IsWindowVisible(hwnd))
        {
            Layout();
            Place(keepPosition: true);
            InvalidateRect(hwnd, 0, false);
        }
    }

    /// <summary>"Until I turn it off" is a short lease tied to this process, renewed here each minute.</summary>
    private static void RenewUntilOff(double now)
    {
        if (!untilOffMine)
            return;
        var manual = Control.ReadManual(config);
        if (manual is null || !manual.UntilOff || manual.Pid != Environment.ProcessId)
        {
            untilOffMine = false;   // turned off elsewhere (awake off, a pause)
            return;
        }
        if (manual.Until - now < Control.UntilOffLease - 60)
            Act(() => Control.SetManual(config, now + Control.UntilOffLease, now, Environment.ProcessId, untilOff: true), quiet: true);
    }

    private static void ShowNotice()
    {
        if (Notice.Read(config) is not { } notice || notice.At <= noticeSeen)
            return;
        noticeSeen = notice.At;
        var data = IconData(NIF_INFO);
        Copy(notice.Title, data.szInfoTitle, 64);
        Copy(string.Join("\n", notice.Lines), data.szInfo, 256);
        data.dwInfoFlags = NIIF_INFO | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    // ---- The icon --------------------------------------------------------------------------------------------------

    private static NOTIFYICONDATAW IconData(uint flags)
    {
        var data = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = hwnd, uID = IconId, uFlags = flags };
        return data;
    }

    private static void AddIcon()
    {
        UpdateIcon(force: true);
        var data = IconData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.uCallbackMessage = WmTray;
        data.hIcon = icon;
        Copy(tip, data.szTip, 128);
        Shell_NotifyIconW(NIM_DELETE, ref data);   // a leftover from a crash
        if (Shell_NotifyIconW(NIM_ADD, ref data))
        {
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, ref data);
        }
    }

    private static void RemoveIcon()
    {
        var data = IconData(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
    }

    private static void UpdateIcon(bool force = false)
    {
        var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        var key = (view.State, TaskbarDark(), size);
        var changed = force || iconKey != key || tip != view.Tooltip;
        if (!changed)
            return;
        if (iconKey != key || icon == 0)
        {
            var old = icon;
            icon = MakeIcon(view.State, size, key.Item2);
            iconKey = key;
            if (old != 0)
                DestroyIcon(old);
        }
        tip = view.Tooltip;
        if (force)
            return;
        var data = IconData(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.hIcon = icon;
        Copy(tip, data.szTip, 128);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private static nint MakeIcon(TrayState state, int size, bool dark)
    {
        var pixels = IconArt.Render(state, size, dark);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = size,
            biHeight = -size,   // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = CreateDIBSection(0, header, 0, out var bits, 0, 0);
        var mask = CreateBitmap(size, size, 1, 1, 0);
        try
        {
            fixed (uint* source = pixels)
                Buffer.MemoryCopy(source, (void*)bits, pixels.Length * 4L, pixels.Length * 4L);
            return CreateIconIndirect(new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color });
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    // ---- Actions ---------------------------------------------------------------------------------------------------

    private static void Click(int id)
    {
        switch (id)
        {
            case AwakeMenu:
                if (view.AwakeOn)
                    Command(StopAwake);
                else
                    ButtonMenu(AwakeMenu);
                break;
            case PauseMenu:
                if (view.PausedOn)
                    Command(Resume);
                else
                    ButtonMenu(PauseMenu);
                break;
            case AtLogon:
                ToggleAtLogon();
                break;
            case ProblemAction:
                if (view.Problem?.ActionId == TrayView.StartWatcher)
                    StartWatcher();
                else if (view.Problem?.ActionId == TrayView.PutHooksBack)
                    Worker("Putting the hooks back", () => RunCli("install", "--hooks-only"));
                break;
            case ReleaseLink:
                if (view.ReleaseUrl is { } url && url.StartsWith("https://github.com/", StringComparison.Ordinal))
                    ShellExecuteW(hwnd, "open", url, null, null, SW_SHOW);
                break;
            default:
                Command(id);
                break;
        }
    }

    private static void Command(int id)
    {
        var now = Hook.Now();
        switch (id)
        {
            case AwakeHour or AwakeTwo:
                untilOffMine = false;
                Act(() => Control.SetManual(config, now + (id == AwakeHour ? 3600 : 7200), now));
                break;
            case AwakeOff:
                Act(() => Control.SetManual(config, now + Control.UntilOffLease, now, Environment.ProcessId, untilOff: true));
                untilOffMine = true;
                break;
            case StopAwake:
                untilOffMine = false;
                Act(() => Control.ClearManual(config));
                break;
            case PauseHour or PauseOn:
                untilOffMine = false;
                Act(() => Control.SetPause(config, id == PauseHour ? now + 3600 : null, now));
                break;
            case Resume:
                Act(() => Control.ClearPause(config));
                break;
            case OpenWindow:
                Show();
                break;
            case FullStatus:
                var console = Path.Combine(AppContext.BaseDirectory, Install.ConsoleExe);
                ShellExecuteW(hwnd, "open", "cmd.exe", $"/k \"\"{console}\" --config \"{config.Path}\" status\"", null, SW_SHOW);
                break;
            case OpenLog:
                OpenFile(config.LogFile);
                break;
            case SettingsFile:
                if (!File.Exists(config.Path))
                    Act(() =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(config.Path)!);
                        File.WriteAllText(config.Path, Config.Template.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                    });
                OpenFile(config.Path);
                break;
            case CloseTray:
                PostMessageW(hwnd, WmQuit, 0, 0);
                break;
        }
        Refresh();
    }

    /// <summary>Do a quick change to the control files; say so if it fails.</summary>
    private static void Act(Action action, bool quiet = false)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log($"couldn't change it: {error.Message}");
            if (!quiet)
                Message($"That couldn't be changed: {error.Message}", warning: true);
        }
    }

    private static void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            Message($"{path} isn't there yet.", warning: false);
            return;
        }
        if (ShellExecuteW(hwnd, "open", path, null, null, SW_SHOW) <= 32)   // no program for .toml or .log
            ShellExecuteW(hwnd, "open", "notepad.exe", $"\"{path}\"", null, SW_SHOW);
    }

    private static void ToggleAtLogon()
    {
        if (busy is not null)
            return;
        if (atLogon == true)
        {
            if (!Confirm("Stop starting ai-keep-awake at logon?\n\nThis removes the watcher's logon task, which stops the watcher now: "
                         + "the laptop then sleeps as normal even while Claude works, until it's turned on again."))
                return;
            Worker("Turning off start at logon", () => RunCli("autostart", "off"));
        }
        else
            Worker("Turning on start at logon", () => RunCli("autostart", "on"));
    }

    private static void StartWatcher()
    {
        Worker("Starting the watcher", () =>
        {
            if (taskRegistered && AutoStart.Create(AutoStart.TaskName).Start())
                return null;
            var background = Path.Combine(config.InstallDir, Install.BackgroundExe);
            if (!File.Exists(background))
                background = Path.Combine(AppContext.BaseDirectory, Install.BackgroundExe);
            new WindowsDesktop().Start(background, ["--config", PyPath.Resolve(config.Path), "watch"], Path.GetDirectoryName(config.Path));
            Thread.Sleep(1500);
            return taskRegistered ? null : "The watcher has started, until you log off. To start it at every logon too, tick Start at logon.";
        });
    }

    /// <summary>A command, in this process, its output kept; null if it worked, else what it said.</summary>
    private static string? RunCli(params string[] args)
    {
        var output = new StringWriter();
        var host = new CliHost { Out = output, Err = output };
        var code = Cli.Run(["--config", config.Path, .. args], host);
        return code == 0 ? null : output.ToString().Trim();
    }

    /// <summary>Run something slow (Task Scheduler takes a second or two) off the window's thread.</summary>
    private static void Worker(string what, Func<string?> work)
    {
        if (busy is not null)
            return;
        busy = what;
        InvalidateRect(hwnd, 0, false);
        Task.Run(() =>
        {
            try
            {
                workerMessage = work();
            }
            catch (Exception error)
            {
                workerMessage = $"{what} didn't work: {error.Message}";
            }
            PostMessageW(hwnd, WmDone, 0, 0);
        });
    }

    /// <summary>Whether the watcher's task and the tray's Run entry are there: Task Scheduler is slow, so asked off
    /// the window's thread, at start, when the window opens and after a change.</summary>
    private static void CheckLogon(bool force = false)
    {
        var now = Hook.Now();
        if (!force && now - logonCheckedAt < 30)
            return;
        logonCheckedAt = now;
        Task.Run(() =>
        {
            try
            {
                var info = AutoStart.Create(AutoStart.TaskName).Status();
                taskRegistered = info is not null;
                var run = new WindowsDesktop().RunEntry();
                atLogon = taskRegistered && run is not null && run.Contains(Tray.Exe(config), StringComparison.OrdinalIgnoreCase)
                          || taskRegistered && run is not null && run.Contains(Path.Combine(AppContext.BaseDirectory, Install.TrayExe), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
            {
                atLogon = null;
            }
            PostMessageW(hwnd, WmRefresh, 0, 0);
        });
    }

    private static void StartReleaseChecks() =>
        ReleaseLoop.Start(config, () => record, found =>
        {
            if (found != release)
            {
                release = found;
                PostMessageW(hwnd, WmRefresh, 0, 0);
            }
        });

    /// <summary>The installer's quit event: install and uninstall set it to close this tray before replacing it.</summary>
    private static void WatchQuit()
    {
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, Tray.QuitName(AppContext.BaseDirectory));
        new Thread(() =>
        {
            quit.WaitOne();
            PostMessageW(hwnd, WmQuit, 0, 0);
        }) { IsBackground = true, Name = "quit" }.Start();
    }

    private static void Message(string text, bool warning)
    {
        modal = true;
        MessageBoxW(hwnd, text, "ai-keep-awake", (warning ? MB_ICONWARNING : MB_ICONINFORMATION) | MB_TOPMOST);
        modal = false;
    }

    private static bool Confirm(string text)
    {
        modal = true;
        var answer = MessageBoxW(hwnd, text, "ai-keep-awake", MB_OKCANCEL | MB_ICONWARNING | MB_TOPMOST);
        modal = false;
        return answer == IDOK;
    }

    // ---- The window ------------------------------------------------------------------------------------------------

    private static void Show()
    {
        (focus, focusShown, pressed) = (null, false, null);
        CheckLogon();
        Refresh();
        Layout();
        Place();
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
    }

    private static void Hide()
    {
        ShowWindow(hwnd, SW_HIDE);
        hiddenAt = Environment.TickCount64;
        (hover, pressed) = (null, null);
    }

    /// <summary>Size the window to its contents and put it by the clock (or where the taskbar is).</summary>
    private static void Place(bool keepPosition = false)
    {
        var rect = new RECT { Right = S(Width), Bottom = contentHeight };
        AdjustWindowRectExForDpi(ref rect, WindowStyle, false, WindowExStyle, dpi);
        int width = rect.Width, height = rect.Height;
        if (keepPosition)
        {
            if (GetWindowRect(hwnd, out var current) && current.Height != height)
                SetWindowPos(hwnd, 0, current.Left, current.Bottom - height, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
            return;
        }
        var anchor = new POINT();
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER), hWnd = hwnd, uID = IconId };
        if (Shell_NotifyIconGetRect(id, out var iconRect) == 0 && iconRect.Width > 0)
            anchor = new POINT { X = iconRect.Left + iconRect.Width / 2, Y = iconRect.Top };
        else
            GetCursorPos(out anchor);
        var monitor = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfoW(MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST), ref monitor);
        var work = monitor.rcWork;
        var gap = S(12);
        int x = Math.Clamp(anchor.X - width / 2, work.Left + gap, work.Right - width - gap);
        int y = work.Bottom - height - gap;                      // taskbar at the bottom (the usual)
        if (work.Top > monitor.rcMonitor.Top)
            y = work.Top + gap;                                  // at the top
        else if (work.Left > monitor.rcMonitor.Left)
            x = work.Left + gap;                                 // on the left
        else if (work.Right < monitor.rcMonitor.Right)
            x = work.Right - width - gap;                        // on the right
        SetWindowPos(hwnd, 0, x, y, width, height, SWP_NOZORDER);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);
}
