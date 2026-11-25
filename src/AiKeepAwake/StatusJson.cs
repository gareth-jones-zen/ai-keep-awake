using System.Text.Json.Nodes;
using WinBackground;

namespace AiKeepAwake;

public static partial class Cli
{
    /// <summary>The version of `status --json`'s shape. Fields are only ever added within a version.</summary>
    public const int StatusFormat = 1;

    /// <summary>`status --json`: the same facts as `status`, for the tray and for right-hand's check. Times are Unix
    /// seconds; durations seconds; null where Windows or a file didn't say.</summary>
    private static int StatusJson(Config config, CliHost host)
    {
        Print(host.Out, PyJson.Dump(StatusObject(config, host), 2, ensureAscii: false));
        return 0;
    }

    public static JsonObject StatusObject(Config config, CliHost host)
    {
        var now = host.Clock();
        var events = Install.Installed(config.ClaudeSettings);
        var old = Install.Installed(config.ClaudeSettings, old: true);
        var running = Locks.Held(Watcher.LockName(config));
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

        JsonObject state = [];
        try
        {
            state = PyJson.Parse(PyJson.ReadStrict(config.StateFile)) as JsonObject ?? [];
        }
        catch (Exception)
        {
        }
        JsonObject? request = null;
        if (running && state.Count > 0)
        {
            request = new JsonObject
            {
                ["held"] = Py.Truthy(state["held"]),
                ["why"] = state.TryGetPropertyValue("why", out var why) ? Py.Str(why) : "",
                ["since"] = Py.Truthy(state["since"]) ? Py.Float(state["since"]) : null,
                ["updated"] = state.ContainsKey("updated") ? Py.Float(state["updated"]) : null,
                ["lock_after"] = state["lock_after"]?.DeepClone(),
                ["lock_why"] = state["lock_why"]?.DeepClone(),
            };
        }

        var pause = Control.ReadPause(config);
        var leases = new JsonArray();
        JsonObject? manual = null;
        foreach (var lease in Leases.Read(config.LeasesDir))
        {
            var live = lease.Until > now;
            bool? alive = lease.Pid is { } pid && pid != 0 ? Leases.PidAlive(pid) : null;
            if (lease.IsManual)
            {
                if (live && alive != false)
                    manual = new JsonObject { ["until"] = lease.Until, ["until_off"] = lease.UntilOff, ["pid"] = lease.Pid };
                continue;
            }
            leases.Add((JsonNode)new JsonObject
            {
                ["session"] = lease.Session,
                ["kind"] = lease.Kind,
                ["live"] = live && alive != false,
                ["until"] = lease.Until,
                ["background"] = lease.Bg,
                ["event"] = lease.Event,
                ["pid"] = lease.Pid,
                ["pid_running"] = alive,
                ["cwd"] = lease.Cwd,
            });
        }

        var system = host.System();
        var mains = system.OnMains();
        var idle = system.IdleSeconds();
        var es = system.ExecutionState();
        var settings = system.LockSettings();
        var point = LockPoint.Effective(config.Lock, () => settings);
        var saver = settings.Screensaver;

        return new JsonObject
        {
            ["format"] = StatusFormat,
            ["version"] = Version,
            ["now"] = now,
            ["settings"] = new JsonObject { ["path"] = config.Path, ["exists"] = File.Exists(config.Path) },
            ["hooks"] = new JsonObject
            {
                ["installed"] = events.Count > 0,
                ["events"] = Strings(events),
                ["file"] = config.ClaudeSettings,
                ["old_events"] = Strings(old),
            },
            ["programs"] = new JsonObject
            {
                ["dir"] = config.InstallDir,
                ["installed"] = Programs.Names.All(name => File.Exists(Path.Combine(config.InstallDir, name))),
            },
            ["watcher"] = new JsonObject
            {
                ["running"] = running,
                ["task"] = task is null ? null : TaskObject(task),
                ["task_error"] = unread,
            },
            ["request"] = request,
            ["paused"] = Control.Paused(pause, now) ? new JsonObject { ["until"] = pause!.Until } : null,
            ["manual"] = manual,
            ["leases"] = leases,
            ["power"] = new JsonObject
            {
                ["mains"] = mains,
                ["idle"] = idle,
                ["asking"] = Strings(new[] { (0x1u, "system awake"), (0x2u, "display on") }.Where(pair => (es & pair.Item1) != 0).Select(pair => pair.Item2)),
            },
            ["lock"] = new JsonObject
            {
                ["after"] = point.Seconds,
                ["why"] = point.Why,
                ["considered"] = Strings(point.Considered),
            },
            ["windows"] = new JsonObject
            {
                ["on_mains"] = settings.OnMains,
                ["sleep_after"] = settings.SleepAfter,
                ["sleep_source"] = settings.SleepSource,
                ["display_off_after"] = settings.DisplayOffAfter,
                ["sign_in_after_sleep"] = settings.SignInAfterSleep,
                ["sign_in_source"] = settings.SignInSource,
                ["screensaver"] = saver is null ? null : new JsonObject
                {
                    ["active"] = saver.Active,
                    ["secure"] = saver.Secure,
                    ["timeout"] = saver.Timeout,
                    ["chosen"] = !string.IsNullOrEmpty(saver.Program),
                    ["policy"] = saver.Policy,
                },
                ["inactivity_limit"] = settings.InactivityLimit,
                ["modern_standby"] = settings.ModernStandby,
            },
            ["rules"] = new JsonObject
            {
                ["mains_only"] = config.MainsOnly,
                ["unattended_hours"] = config.UnattendedHours,
                ["lease_minutes"] = config.LeaseMinutes,
                ["background_lease_minutes"] = config.BackgroundLeaseMinutes,
            },
            ["log_file"] = config.LogFile,
            ["state_file"] = config.StateFile,
        };
    }

    private static JsonObject TaskObject(IReadOnlyDictionary<string, string> task)
    {
        var result = new JsonObject();
        foreach (var name in new[] { "state", "last_run", "last_result", "next_run", "command" })
            result[name] = task.TryGetValue(name, out var value) ? value : null;
        return result;
    }

    private static JsonArray Strings(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add((JsonNode)item);
        return array;
    }
}
