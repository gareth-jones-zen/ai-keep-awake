using System.Text;
using System.Text.Json.Nodes;
using WinBackground;

namespace AiKeepAwake;

/// <summary>A power request the watcher can hold and release.</summary>
public interface IPowerRequest
{
    void Hold(bool on = true);
    void Close();
}

/// <summary>The Windows calls the watcher makes. Tests pass a fake with the same methods.</summary>
public interface ISystem
{
    bool? OnMains();
    double IdleSeconds();
    uint ExecutionState();
    LockSettings LockSettings();
    bool Lock();
    IPowerRequest Request(string reason);
}

/// <summary>The real Windows calls (WinBackground.Power).</summary>
public sealed class WindowsSystem : ISystem
{
    public bool? OnMains() => Power.OnMains();
    public double IdleSeconds() => Power.IdleSeconds();
    public uint ExecutionState() => Power.ExecutionState();
    public LockSettings LockSettings() => Power.LockSettings();
    public bool Lock() => Power.LockWorkStation();
    public IPowerRequest Request(string reason) => new HeldRequest(new SystemRequired(reason));

    private sealed class HeldRequest(SystemRequired request) : IPowerRequest
    {
        public void Hold(bool on = true) => request.Hold(on);
        public void Close() => request.Close();
    }
}

/// <summary>The watcher: holds one named power request while any Claude Code session has a live lease.
///
/// It holds the request only while all of these are true:
/// - a session has a live lease (the hook renews it on every prompt and tool call);
/// - the laptop is on mains, unless mains_only = false;
/// - someone has touched the keyboard or mouse within unattended_hours (3 hours by default);
/// - it isn't paused (Control: pause.json).
///
/// Keep awake now (Control: a lease of kind "manual") counts as a session, under the same rules.
///
/// While it holds the request, it locks the session where Windows would have locked it (LockPoint), unless
/// another program is keeping the display on (a call, a video, a presentation), because then Windows wouldn't
/// have locked either. It never changes a setting and never simulates input.</summary>
public sealed class Watcher
{
    public const double SettingsEvery = 60;   // re-read Windows' settings this often, so a change in Settings applies within a minute
    public const double AliveEvery = 3600;
    public const double StateEvery = 60;
    private const uint EsDisplayRequired = 0x2;

    private readonly Config config;
    private readonly ISystem system;
    private readonly Action<string> log;
    private readonly Action<string, IReadOnlyList<string>> notify;
    private readonly Func<long, bool>? alive;
    private readonly IPowerRequest request;
    private bool locked;          // locked during this idle stretch
    private bool displaySkip;     // logged that another program keeps the display on
    private bool capped;          // notified that the cap let it sleep
    private double pointAt;
    private double aliveAt;
    private double stateAt;
    private (bool? Mains, double Idle, LockPoint Point)? last;
    private (int Sessions, bool Manual, bool Paused)? written;
    private string? writtenWhy;

    public Watcher(Config config, ISystem system, Action<string> log, Action<string, IReadOnlyList<string>> notify,
        Func<long, bool>? alive = null)
    {
        (this.config, this.system, this.log, this.notify, this.alive) = (config, system, log, notify, alive);
        request = system.Request(config.Reason);
    }

    public bool Held { get; private set; }
    public string Why { get; private set; } = "";
    public double? Since { get; private set; }
    public List<Lease> Live { get; private set; } = [];

    /// <summary>The live Claude Code sessions (Live without the manual lease).</summary>
    public List<Lease> Sessions => Live.Where(lease => !lease.IsManual).ToList();

    /// <summary>The live manual lease (Keep awake now), if any.</summary>
    public Lease? Manual => Live.FirstOrDefault(lease => lease.IsManual);

    /// <summary>The pause in force, if any.</summary>
    public Pause? Paused { get; private set; }

    /// <summary>The lock point in use; set to null to make the next step re-read it.</summary>
    public LockPoint? Point { get; set; }

    /// <summary>Whether to hold the request, and why, in words for the log.</summary>
    public static (bool Want, string Why) Decide(Config config, int live, bool? mains, double idle) =>
        Decide(config, live, null, null, mains, idle);

    /// <summary>Whether to hold the request, and why: as for sessions alone, with a pause (nothing is held) and Keep
    /// awake now (a manual lease, under the same rules as a session) taken into account.</summary>
    public static (bool Want, string Why) Decide(Config config, int sessions, Lease? manual, Pause? pause, bool? mains, double idle)
    {
        if (pause is not null)
            return (false, pause.Until is { } until ? $"paused until {Control.Clock(until)}" : "paused until resumed");
        if (sessions == 0 && manual is null)
            return (false, "no Claude Code session is working");
        if (config.MainsOnly && mains == false)
            return (false, "on battery");
        if (idle >= config.UnattendedSeconds)
            return (false, $"nobody at the laptop for {Py.Fixed(idle / 3600, 1)} hours (the cap is {Py.G(config.UnattendedHours)})");
        var byHand = manual is null ? ""
            : manual.UntilOff ? "kept awake by hand until turned off" : $"kept awake by hand until {Control.Clock(manual.Until)}";
        if (sessions == 0)
            return (true, byHand);
        return (true, $"{sessions} session{(sessions != 1 ? "s" : "")} working" + (byHand.Length > 0 ? $", and {byHand}" : ""));
    }

    public LockPoint LockPointAt(double now)
    {
        if (Point is null || now - pointAt >= SettingsEvery)
        {
            var point = LockPoint.Effective(config.Lock, system.LockSettings);
            if (Point is not null && (point.Seconds != Point.Seconds || point.Why != Point.Why))
                log($"lock point now {LockPoint.Minutes(point.Seconds)}: {point.Why}");
            (Point, pointAt) = (point, now);
        }
        return Point;
    }

    public void Step(double now)
    {
        Live = Leases.Live(config.LeasesDir, now, config.CheckPid, alive);
        var pause = Control.ReadPause(config);
        if (pause is not null && !Control.Paused(pause, now))
        {
            Control.ClearPause(config, wake: false);
            log("pause ended");
            pause = null;
        }
        if (pause is not null && Paused is null)
            log(pause.Until is { } until ? $"paused until {Control.Clock(until)}" : "paused until resumed");
        else if (pause is null && Paused is not null && (Paused.Until is null || Paused.Until > now))
            log("resumed");
        Paused = pause;
        var sessions = Sessions;
        var manual = Manual;
        var mains = system.OnMains();
        var idle = system.IdleSeconds();
        var (want, why) = Decide(config, sessions.Count, manual, pause, mains, idle);
        var changed = want != Held;
        if (changed)
        {
            request.Hold(want);
            (Held, Since) = (want, now);
            var mainsText = mains switch { true => "mains", false => "battery", null => "power source unknown" };
            log($"{(want ? "holding" : "released")}: {why}; idle {Py.Fixed(idle / 60, 0)} min, {mainsText}");
        }
        Why = why;

        // The cap: say so once, if sessions are still working when it lets the laptop sleep (not while paused, when
        // the person asked for that).
        if (sessions.Count > 0 && pause is null && idle >= config.UnattendedSeconds)
        {
            if (!capped)
            {
                capped = true;
                notify("Claude Code may now let the laptop sleep",
                [
                    $"{sessions.Count} session(s) still working, but nobody has used the laptop for "
                    + $"{Py.G(config.UnattendedHours)} hours, so it's no longer kept awake.",
                ]);
            }
        }
        else if (idle < config.UnattendedSeconds)
            capped = false;

        var point = LockPointAt(now);
        if (point.Seconds is null || idle < point.Seconds)
            locked = displaySkip = false;
        else if (Held && !locked)
        {
            if ((system.ExecutionState() & EsDisplayRequired) != 0)
            {
                if (!displaySkip)
                {
                    displaySkip = true;
                    log("not locking: another program is keeping the display on, so Windows wouldn't lock either");
                }
            }
            else
            {
                locked = true;
                var done = system.Lock();
                log($"locked: idle {Py.Fixed(idle / 60, 0)} min while Claude Code works ({point.Why})"
                    + (done ? "" : "; Windows refused"));
            }
        }

        if (now - aliveAt >= AliveEvery)
        {
            aliveAt = now;
            log($"alive: {(Held ? "holding" : "not holding")}, {Live.Count} live lease(s)");
            Leases.Tidy(config.LeasesDir, now);
        }
        last = (mains, idle, point);
        // Written at once on a change the tray shows (held, sessions, by hand, paused, why), else each minute.
        var shown = (sessions.Count, manual is not null, pause is not null);
        if (changed || shown != written || why != writtenWhy || now - stateAt >= StateEvery)
        {
            (stateAt, written, writtenWhy) = (now, shown, why);
            WriteState(now, mains, idle, point);
        }
    }

    public void WriteState(double now, bool? mains, double idle, LockPoint point)
    {
        JsonNode? lockAfter = point.Seconds is not { } seconds
            ? null
            : config.Lock is string && seconds == Math.Floor(seconds) ? PyJson.Int((long)seconds) : PyJson.Float(seconds);
        var sessions = new JsonArray();
        foreach (var lease in Live.Where(lease => !lease.IsManual))
            sessions.Add((JsonNode)lease.Session);
        var manual = Manual;
        var state = new JsonObject
        {
            ["held"] = Held,
            ["why"] = Why,
            ["since"] = Since is { } since ? PyJson.Float(since) : null,
            ["updated"] = PyJson.Float(now),
            ["pid"] = PyJson.Int(Environment.ProcessId),
            ["sessions"] = sessions,
            ["mains"] = mains is { } m ? m : null,
            ["idle"] = PyJson.Float(idle),
            ["lock_after"] = lockAfter,
            ["lock_why"] = point.Why,
            ["manual_until"] = manual is null ? null : PyJson.Float(manual.Until),
            ["manual_until_off"] = manual?.UntilOff ?? false,
            ["paused"] = Paused is not null,
            ["paused_until"] = Paused?.Until is { } pausedUntil ? PyJson.Float(pausedUntil) : null,
            ["version"] = Cli.Version,
        };
        var path = config.StateFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = Path.ChangeExtension(path, ".tmp");
            File.WriteAllText(temp, PyJson.Dump(state), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Close(Func<double>? clock = null)
    {
        try
        {
            request.Close();
        }
        finally
        {
            var wasHeld = Held;
            (Held, Why) = (false, "the watcher stopped");
            if (wasHeld)
                log("released: the watcher stopped");
            if (last is { } l)
                WriteState((clock ?? Hook.Now)(), l.Mains, l.Idle, l.Point);
        }
    }

    public static string LockName(Config config) => $"ai-keep-awake-watch-{Locks.Key(config.LeasesDir)}";

    /// <summary>The watcher loop. 3 if another watcher is already running.</summary>
    public static int Run(Config config, Action<string> log, Action<string, IReadOnlyList<string>> notify,
        ISystem? system = null, Action<double>? sleep = null, int? steps = null, Func<double>? clock = null)
    {
        var now = clock ?? Hook.Now;
        using var instance = Locks.SingleInstance(LockName(config));
        if (instance is null)
        {
            log("another watcher is already running; not starting a second");
            return 3;
        }
        // Set by `awake`, `pause` and the tray, so a change applies at once rather than at the next check.
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, Control.WakeName(config));
        sleep ??= seconds => wake.WaitOne(TimeSpan.FromSeconds(seconds));
        // A pause never outlives a logoff (nor a restart of the watcher).
        if (Control.ClearPause(config, wake: false))
            log("pause ended: the watcher started");
        var watcher = new Watcher(config, system ?? new WindowsSystem(), log, notify);
        var point = watcher.LockPointAt(now());
        log($"started: keeps the laptop awake while Claude Code works; lock point {LockPoint.Minutes(point.Seconds)} ({point.Why})");
        var failures = 0;
        try
        {
            while (steps is null || steps > 0)
            {
                try
                {
                    watcher.Step(now());
                    failures = 0;
                }
                catch (Exception error)   // keep watching; say so if it keeps failing
                {
                    failures++;
                    var text = $"{error.GetType().Name}: {error.Message}";
                    log($"error: {text}");
                    if (failures == 3)
                        notify("ai-keep-awake is failing", [text.Length > 300 ? text[..300] : text, $"Log: {config.LogFile}"]);
                }
                if (steps is not null)
                {
                    steps--;
                    if (steps == 0)
                        break;
                }
                sleep(config.CheckSeconds);
            }
        }
        finally
        {
            watcher.Close(now);
        }
        return 0;
    }
}
