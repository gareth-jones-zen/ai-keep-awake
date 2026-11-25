using System.Text.Json.Nodes;
using WinBackground;

namespace AiKeepAwake;

public static partial class Cli
{
    private static string Clock(double? epoch) =>
        epoch is { } e && e != 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(e * 1000)).ToLocalTime().ToString("HH:mm:ss")
            : "-";

    private static string Minutes(double? seconds) => LockPoint.Minutes(seconds);

    /// <summary>What's installed, running and held, and the lock point, as the Python version's status printed it.</summary>
    private static int Status(Config config, CliHost host)
    {
        var now = host.Clock();
        var output = host.Out;
        var events = Install.Installed(config.ClaudeSettings);
        Print(output, $"Settings:   {config.Path}{(File.Exists(config.Path) ? "" : " (not created; defaults apply)")}");
        Print(output, $"Hooks:      {(events.Count > 0 ? $"installed, {events.Count} events: {string.Join(", ", events)}" : "not installed")}"
                      + $" ({config.ClaudeSettings})");
        var old = Install.Installed(config.ClaudeSettings, old: true);
        if (old.Count > 0)
            Print(output, $"Old hooks:  the Python claude-keep-awake's, {old.Count} events (install replaces them)");
        var installed = Programs.Names.All(name => File.Exists(Path.Combine(config.InstallDir, name)));
        Print(output, $"Programs:   {config.InstallDir}{(installed ? "" : " (not installed there)")}");

        var running = Locks.Held(Watcher.LockName(config));   // looked at, never taken, so a starting watcher isn't put off
        IReadOnlyDictionary<string, string>? task = null;
        string? unread = null;
        try
        {
            task = host.Tasks(AutoStart.TaskName).Status();
        }
        catch (Exception error) when (IsTaskError(error))
        {
            unread = error.Message;
        }
        Print(output, $"Watcher:    {(running ? "running" : "not running")}; "
                      + (task is not null
                          ? $"task '{AutoStart.TaskName}' {task.GetValueOrDefault("state", "?")}, last run {task.GetValueOrDefault("last_run", "?")}"
                            + $" ({task.GetValueOrDefault("last_result", "?")})"
                          : unread is not null
                              ? $"task '{AutoStart.TaskName}' couldn't be read ({unread})"
                              : "no logon task (ai-keep-awake install)"));
        var desktop = host.Desktop;
        var trayRunning = Tray.Running(config);
        var trayAtLogon = TrayRunEntry(desktop, config);
        Print(output, $"Tray:       {(trayRunning ? "running" : "not running")}; {(trayAtLogon ? "starts at logon" : "not started at logon")}"
                      + (InstallRecord.Read(config) is { } record
                          ? $"; installed {(record.RemovedByPerson ? "then removed by you" : $"by {record.ManagedBy}")}"
                          : ""));

        JsonObject state = [];
        try
        {
            state = PyJson.Parse(PyJson.ReadStrict(config.StateFile)) as JsonObject ?? [];
        }
        catch (Exception)
        {
        }
        if (running && state.Count > 0)
        {
            var held = Py.Truthy(state["held"]) ? "held" : "not held";
            var since = Py.Truthy(state["since"]) ? Py.Float(state["since"]) : (double?)null;
            var updated = state.ContainsKey("updated") ? Py.Float(state["updated"]) : now;
            var why = state.TryGetPropertyValue("why", out var whyNode) ? Py.Str(whyNode) : "";
            Print(output, $"Request:    {held} since {Clock(since)}: {why} (as of {Py.Fixed(now - updated, 0)} s ago)");
        }
        else
            Print(output, "Request:    not held (no watcher)");
        var pause = Control.ReadPause(config);
        if (Control.Paused(pause, now))
            Print(output, $"Paused:     {(pause!.Until is { } pausedUntil ? $"until {Clock(pausedUntil)}" : "until resumed")} (ai-keep-awake pause off)");

        var all = Leases.Read(config.LeasesDir);
        foreach (var manual in all.Where(lease => lease.IsManual && lease.Until > now))
            Print(output, $"By hand:    kept awake {(manual.UntilOff ? "until turned off (the tray)" : $"until {Clock(manual.Until)}")} (ai-keep-awake awake off)");
        var found = all.Where(lease => !lease.IsManual).ToList();
        if (found.Count == 0)
            Print(output, "Leases:     none");
        foreach (var lease in found)
        {
            var live = lease.Until > now;
            bool? alive = lease.Pid is { } pid && pid != 0 ? Leases.PidAlive(pid) : null;
            var session = lease.Session.Length > 8 ? lease.Session[..8] : lease.Session;
            Print(output, $"Lease:      {session} {(live ? "live" : "expired")} until {Clock(lease.Until)}"
                          + $"{(lease.Bg ? " (background)" : "")}, last {(string.IsNullOrEmpty(lease.Event) ? "?" : lease.Event)}, "
                          + $"Claude Code pid {(lease.Pid is { } p ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) : "None")}"
                          + $" {(alive == true ? "running" : alive == false ? "gone" : "")}, {lease.Cwd ?? ""}");
        }

        var system = host.System();
        var mains = system.OnMains();
        var idle = system.IdleSeconds();
        var es = system.ExecutionState();
        var asking = new[] { (0x1u, "system awake"), (0x2u, "display on") }.Where(pair => (es & pair.Item1) != 0).Select(pair => pair.Item2).ToList();
        Print(output, $"Power:      {(mains == true ? "mains" : mains == false ? "battery" : "unknown")}; idle {Py.Fixed(idle / 60, 1)} min; "
                      + $"programs are asking for: {(asking.Count > 0 ? string.Join(", ", asking) : "nothing")}");

        var settings = system.LockSettings();
        var point = LockPoint.Effective(config.Lock, () => settings);
        Print(output, $"Lock point: {Minutes(point.Seconds)} idle: {point.Why}");
        foreach (var line in point.Considered)
            Print(output, $"              {line}");
        var saver = settings.Screensaver;
        var signIn = settings.SignInAfterSleep;
        Print(output, $"Windows ({(settings.OnMains ? "mains" : "battery")}):");
        Print(output, $"  sleep after            {(settings.SleepAfter is { } s && s != 0 ? Minutes(s) : settings.SleepAfter == 0 ? "never" : "unreadable")} ({settings.SleepSource})");
        Print(output, $"  display off after      {(settings.DisplayOffAfter is { } d && d != 0 ? Minutes(d) : "never")}");
        Print(output, $"  sign-in after sleep    {(signIn is null ? "never" : signIn == 0 ? "every time" : "after " + Minutes(signIn))} ({settings.SignInSource})");
        if (saver is not null)
            Print(output, $"  screensaver            {(saver.Active ? "on" : "off")}, {(saver.Secure ? "secure" : "not secure")}, "
                          + $"{Minutes(saver.Timeout)}, {(string.IsNullOrEmpty(saver.Program) ? "none chosen" : "chosen")}"
                          + (saver.Policy ? ", set by policy" : ""));
        Print(output, $"  inactivity limit       {(settings.InactivityLimit is { } limit && limit != 0 ? Minutes(limit) : "not set")}");
        Print(output, $"  Modern Standby         {(settings.ModernStandby == true ? "yes" : settings.ModernStandby == false ? "no" : "unknown")}");
        Print(output, $"Holds when: a lease is live{(config.MainsOnly ? ", on mains" : "")}, and idle under "
                      + $"{Py.G(config.UnattendedHours)} h. Log: {config.LogFile}");
        return 0;
    }
}
