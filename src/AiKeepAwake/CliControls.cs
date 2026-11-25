using WinBackground;

namespace AiKeepAwake;

public static partial class Cli
{
    /// <summary>`awake 1h|2h|30m|off`: keep the laptop awake by hand for that long, under the same rules as a Claude
    /// Code session. Timed only from here: "until I turn it off" belongs to the tray, whose exit ends it.</summary>
    private static int AwakeCommand(string action, Config config, CliHost host)
    {
        var now = host.Clock();
        if (action == "off")
        {
            Print(host.Out, Control.ClearManual(config) ? "No longer kept awake by hand." : "It wasn't being kept awake by hand.");
            return 0;
        }
        var until = now + Control.Duration(action)!.Value;
        var paused = Control.Paused(Control.ReadPause(config), now);
        try
        {
            Control.SetManual(config, until, now);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Print(host.Err, $"Couldn't write {Control.ManualFile(config)}: {error.Message}");
            return 1;
        }
        Print(host.Out, $"Kept awake by hand until {Control.Clock(until)}{(paused ? " (the pause is over)" : "")}, under the same rules as a "
                        + $"Claude Code session: {(config.MainsOnly ? "on mains only, " : "")}not after {Py.G(config.UnattendedHours)} hours with "
                        + "nobody at the laptop, and locking where Windows would. `ai-keep-awake awake off` stops it.");
        NoWatcherWarning(config, host);
        return 0;
    }

    /// <summary>`pause 1h|on|off`: let the laptop sleep as normal, even while Claude Code works.</summary>
    private static int PauseCommand(string action, Config config, CliHost host)
    {
        var now = host.Clock();
        if (action == "off")
        {
            Print(host.Out, Control.ClearPause(config) ? "Resumed: kept awake again while Claude Code works." : "It wasn't paused.");
            return 0;
        }
        double? until = action == "on" ? null : now + Control.Duration(action)!.Value;
        var byHand = Control.ReadManual(config) is { } manual && manual.Until > now;
        try
        {
            Control.SetPause(config, until, now);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Print(host.Err, $"Couldn't write {Control.PauseFile(config)}: {error.Message}");
            return 1;
        }
        Print(host.Out, (until is { } u ? $"Paused until {Control.Clock(u)}" : "Paused until resumed (or the next logoff)")
                        + $": the laptop sleeps as normal, even while Claude Code works{(byHand ? ", and it's no longer kept awake by hand" : "")}. "
                        + "`ai-keep-awake pause off` resumes.");
        NoWatcherWarning(config, host);
        return 0;
    }

    private static void NoWatcherWarning(Config config, CliHost host)
    {
        if (!Locks.Held(Watcher.LockName(config)))
            Print(host.Out, "The watcher isn't running, so nothing changes until it starts (ai-keep-awake autostart on, or the next logon).");
    }
}
