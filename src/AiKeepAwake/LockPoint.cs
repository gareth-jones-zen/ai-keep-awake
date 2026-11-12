using WinBackground;

namespace AiKeepAwake;

/// <summary>Where Windows would have locked an idle session, from its own settings.
///
/// Keeping the laptop awake must not keep it unlocked. Without the power request, an idle laptop locks when
/// one of these happens first:
/// - it sleeps, and waking needs a sign-in: at the sleep timeout for the current power source, plus the
///   "Require sign-in" grace (0 for "every time"; never, if sign-in on wake is off; no sleep, if sleep is "never");
/// - a secure screensaver starts: active, "on resume, display logon screen", a screensaver chosen, and a timeout
///   (the screensaver still runs while the request is held, so this lock happens anyway);
/// - the machine inactivity limit (a security policy) is reached.
///
/// The watcher locks at the earliest of these, while it's holding the request. If none applies, Windows would
/// never have locked, and neither does the watcher. Nothing here changes a setting.</summary>
/// <param name="Seconds">Null: Windows wouldn't lock, so neither do we.</param>
/// <param name="Why">Which setting gave it.</param>
/// <param name="Considered">Each candidate, as text, for `status`.</param>
public sealed record LockPoint(double? Seconds, string Why, IReadOnlyList<string> Considered)
{
    public static string Minutes(double? seconds)
    {
        if (seconds is not { } s)
            return "never";
        if (s % 60 != 0)
            return $"{Py.Fixed(s, 0)} s";
        return $"{Py.Fixed(s / 60, 0)} min";
    }

    /// <summary>The lock point from Windows' own settings.</summary>
    public static LockPoint Windows(LockSettings settings)
    {
        var candidates = new List<(double Seconds, string Text)>();
        var considered = new List<string>();

        double? sleep = settings.SleepAfter;
        var guessed = "";
        if (sleep is null)   // unreadable: assume it sleeps when the display turns off, or at 5 minutes
        {
            sleep = settings.DisplayOffAfter is { } display && display != 0 ? display : 300;
            guessed = " (sleep timeout unreadable; assumed)";
        }
        var grace = settings.SignInAfterSleep;
        if (sleep == 0)
            considered.Add("sleep: never, so no lock from sleeping");
        else if (grace is null)
            considered.Add($"sleep after {Minutes(sleep)}{guessed}, but sign-in on wake is off, so no lock");
        else
        {
            var when = grace == 0 ? "every time" : $"after {Minutes(grace)} asleep";
            var text = $"sleep after {Minutes(sleep)}{guessed}, sign-in required {when}";
            candidates.Add((sleep.Value + grace.Value, text));
            considered.Add($"{text}: {Minutes(sleep.Value + grace.Value)}");
        }

        var saver = settings.Screensaver;
        if (saver is null)
            considered.Add("screensaver: unreadable");
        else if (saver.Locks)
        {
            var text = $"secure screensaver after {Minutes(saver.Timeout)}{(saver.Policy ? " (policy)" : "")}";
            candidates.Add((saver.Timeout, text));
            considered.Add(text);
        }
        else
        {
            var missing = new[]
            {
                ("active", saver.Active), ("secure", saver.Secure), ("chosen", !string.IsNullOrEmpty(saver.Program)),
                ("timed", saver.Timeout > 0),
            }.Where(check => !check.Item2).Select(check => check.Item1);
            considered.Add($"screensaver: doesn't lock (not {string.Join(", ", missing)})");
        }

        if (settings.InactivityLimit is { } limit && limit != 0)
        {
            var text = $"machine inactivity limit {Minutes(limit)}";
            candidates.Add((limit, text));
            considered.Add(text);
        }

        if (candidates.Count == 0)
            return new LockPoint(null, "Windows wouldn't lock this session when idle", considered);
        var best = candidates[0];
        foreach (var candidate in candidates.Skip(1))
            if (candidate.Seconds < best.Seconds)
                best = candidate;
        return new LockPoint(best.Seconds, best.Text, considered);
    }

    /// <summary>The lock point the watcher uses: the setting "windows" (readSettings() is called), "never", or minutes.</summary>
    public static LockPoint Effective(Config config, Func<LockSettings> readSettings) => Effective(config.Lock, readSettings);

    public static LockPoint Effective(object configLock, Func<LockSettings> readSettings)
    {
        if (configLock is string text)
        {
            if (text.Trim().ToLowerInvariant() == "never")
                return new LockPoint(null, "lock = \"never\" in the settings", []);
            return Windows(readSettings());
        }
        var minutes = Config.Number(configLock) ?? throw new ArgumentException("lock must be text or a number");
        var shown = configLock switch
        {
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => Py.Repr(d),
            _ => Py.Repr(minutes),
        };
        return new LockPoint(minutes * 60, $"lock = {shown} minutes in the settings", []);
    }
}
