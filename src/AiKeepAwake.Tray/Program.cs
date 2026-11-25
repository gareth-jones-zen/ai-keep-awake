using WinBackground;

namespace AiKeepAwake.TrayApp;

/// <summary>ai-keep-awake-tray.exe: the tray icon and its window. Started at logon (the Run key) and by the Start
/// menu shortcut (--show); one per user. A second start shows the first one's window and exits.
///
///     ai-keep-awake-tray.exe [--config CONFIG] [--show] [--version]</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? configPath = null;
        var show = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version":
                    return 0;   // what install runs once, so Defender's first-run scan happens then
                case "--show":
                    show = true;
                    break;
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
            }
        }
        Config config;
        try
        {
            config = Config.Load(configPath);
        }
        catch (Exception error) when (error is ConfigError or IOException or UnauthorizedAccessException)
        {
            Win32.MessageBoxW(0, error.Message, "ai-keep-awake", Win32.MB_ICONWARNING);
            return 2;
        }
        using var instance = Locks.SingleInstance(Tray.LockName(config));
        if (instance is null)
        {
            if (show)
                TrayWindow.ShowExisting();
            return 3;
        }
        return TrayWindow.Run(config, show);
    }
}
