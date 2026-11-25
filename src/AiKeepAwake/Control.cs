using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinBackground;

namespace AiKeepAwake;

/// <summary>A pause: the laptop sleeps as normal until then (null: until resumed), even while Claude Code works.</summary>
public sealed record Pause(double? Until, double At);

/// <summary>The two controls a person (or Claude) has, as files the watcher reads on every step:
///
/// - **Keep awake now**: a lease of its own kind, <c>manual.json</c> in the leases folder, so the watcher treats it as
///   it treats a Claude Code session (mains only, the unattended cap, locking where Windows would). Timed from the
///   command line; from the tray it can also be "until I turn it off", when it records the tray's process and is
///   renewed every minute, so closing the tray ends it.
/// - **Pause**: <c>pause.json</c> beside the state file. While it's there and unexpired nothing is held, whatever is
///   working. The watcher deletes it when it ends, and when the watcher starts (so it never outlives a logoff).
///
/// Each turns the other off: keeping it awake by hand ends a pause, and pausing ends keeping it awake by hand.
/// After either, the watcher's wake event is set so it acts at once rather than at its next 15-second check.</summary>
public static partial class Control
{
    public const string ManualName = "manual";

    /// <summary>The longest a timed control may run.</summary>
    public const double MaxSeconds = 24 * 3600;

    /// <summary>How long an "until I turn it off" lease lasts between the tray's renewals (each minute).</summary>
    public const double UntilOffLease = 150;

    public static string PauseFile(Config config) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.StateFile))!, "pause.json");

    public static string ManualFile(Config config) => Path.Combine(config.LeasesDir, ManualName + ".json");

    /// <summary>The pause, if there is one (expired or not: the caller compares Until with the time).</summary>
    public static Pause? ReadPause(Config config)
    {
        try
        {
            if (PyJson.Parse(PyJson.ReadStrict(PauseFile(config))) is not JsonObject data)
                return null;
            double? until = data.TryGetPropertyValue("until", out var u) && u is not null ? Py.Float(u) : null;
            var at = data.TryGetPropertyValue("at", out var a) && a is not null ? Py.Float(a) : 0;
            return new Pause(until, at);
        }
        catch (Exception)
        {
            return null;   // none, or unreadable: not paused
        }
    }

    /// <summary>Whether a pause is in force at now.</summary>
    public static bool Paused(Pause? pause, double now) => pause is not null && (pause.Until is null || pause.Until > now);

    /// <summary>Pause until then (null: until resumed). Ends keeping it awake by hand.</summary>
    public static void SetPause(Config config, double? until, double now)
    {
        ClearManual(config);
        WriteJson(PauseFile(config), new JsonObject
        {
            ["until"] = until is { } u ? PyJson.Float(u) : null,
            ["at"] = PyJson.Float(now),
        });
        Wake(config);
    }

    /// <summary>End a pause. False if there wasn't one.</summary>
    public static bool ClearPause(Config config, bool wake = true)
    {
        var gone = Delete(PauseFile(config));
        if (gone && wake)
            Wake(config);
        return gone;
    }

    /// <summary>The manual lease, if there is one (live or not).</summary>
    public static Lease? ReadManual(Config config) =>
        Leases.Read(config.LeasesDir).FirstOrDefault(lease => lease.IsManual && lease.Session == ManualName);

    /// <summary>Keep it awake by hand until then. pid: the process whose exit ends it (the tray's, for "until I turn
    /// it off"); null for a timed one from the command line. Ends a pause.</summary>
    public static void SetManual(Config config, double until, double now, long? pid = null, bool untilOff = false)
    {
        Delete(PauseFile(config));
        Directory.CreateDirectory(config.LeasesDir);
        WriteJson(ManualFile(config), new JsonObject
        {
            ["until"] = PyJson.Float(until),
            ["bg"] = false,
            ["pid"] = pid is { } p ? PyJson.Int(p) : null,
            ["kind"] = Lease.Manual,
            ["until_off"] = untilOff,
            ["at"] = PyJson.Float(now),
            ["agent"] = ManualName,
        });
        Wake(config);
    }

    /// <summary>Stop keeping it awake by hand. False if it wasn't.</summary>
    public static bool ClearManual(Config config, bool wake = true)
    {
        var gone = Delete(ManualFile(config));
        if (gone && wake)
            Wake(config);
        return gone;
    }

    /// <summary>The watcher's wake event: set it, and the watcher steps at once.</summary>
    public static string WakeName(Config config) => $@"Local\ai-keep-awake-wake-{Locks.Key(config.LeasesDir)}";

    /// <summary>Set the watcher's wake event, if a watcher has made it. Never throws.</summary>
    public static void Wake(Config config)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(WakeName(config), out var handle))
            {
                using (handle)
                    handle.Set();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
        }
    }

    /// <summary>A duration such as 30m, 1h, 1.5h or 90min, in seconds; null if it isn't one (or is over 24 hours).</summary>
    public static double? Duration(string text)
    {
        var match = DurationRx().Match(text.Trim().ToLowerInvariant());
        if (!match.Success)
            return null;
        var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = match.Groups[2].Value.StartsWith('h') ? number * 3600 : number * 60;
        return seconds > 0 && seconds <= MaxSeconds ? seconds : null;
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(h|hr|hrs|hour|hours|m|min|mins|minute|minutes)$")]
    private static partial Regex DurationRx();

    /// <summary>A time as people read it: 16:00 (local).</summary>
    public static string Clock(double epoch) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(epoch * 1000)).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static void WriteJson(string path, JsonObject data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, PyJson.Dump(data), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static bool Delete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
