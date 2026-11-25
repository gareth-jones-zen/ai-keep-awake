using WinBackground;

namespace AiKeepAwake;

/// <summary>The watcher at logon, through Windows Task Scheduler.
///
/// The task runs the installed ai-keep-awake-background.exe --config SETTINGS watch directly, so there's no
/// window, and stopping the task stops the watcher itself (and Windows drops its power request with it).
/// Restarted if it crashes; never stopped for time.</summary>
public static class AutoStart
{
    public const string TaskName = "ai-keep-awake watch";

    /// <summary>The Python claude-keep-awake's task, which install and uninstall remove.</summary>
    public const string OldTaskName = "claude-keep-awake watch";

    private static readonly Dictionary<string, string> Results = new() { ["3"] = "another watcher was already running" };

    public static ITask Create(string name) =>
        new ScheduledTask(name, $"Keeps the laptop awake while Claude Code is working ({name}).", Results);

    public static (string Exe, List<string> Args) Command(string exe, string configPath) =>
        (exe, ["--config", PyPath.Resolve(configPath), "watch"]);
}

/// <summary>Copying the three programs into the install folder, so the hooks and the task don't depend on where
/// the download was unzipped, and an upgrade is "run the new one's install".</summary>
public static class Programs
{
    public static readonly string[] Names = [Install.ConsoleExe, Install.BackgroundExe, Install.TrayExe];

    /// <summary>The names as a sentence: "a, b and c".</summary>
    public static string Listed(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Join("", names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    /// <summary>Whether the running program is the installed copy (so there's nothing to copy).</summary>
    public static bool RunningFrom(string programDir, string installDir) => PyPath.Same(programDir, installDir);

    /// <summary>The programs missing from programDir, if any (so install can't copy them).</summary>
    public static List<string> Missing(string programDir) =>
        Names.Where(name => !File.Exists(Path.Combine(programDir, name))).ToList();

    /// <summary>Copy the programs into installDir, replacing older copies, even while one is running.
    ///
    /// Every hook call runs the installed ai-keep-awake-background.exe, from every open session and every
    /// headless run, and a running exe can't be overwritten (nor can one Defender is scanning). It can be
    /// renamed, though. So each program is copied in as NAME.new first; then each old one is renamed aside to
    /// NAME.old and the new one moved into its place, retrying briefly. A failure puts back anything already
    /// swapped, so the install is never left half old and half new. The .old files are deleted on the next
    /// install, once nothing is running them. Throws IOException or UnauthorizedAccessException if it can't.</summary>
    public static void Copy(string programDir, string installDir, int attempts = 10, TimeSpan? pause = null)
    {
        var wait = pause ?? TimeSpan.FromMilliseconds(300);
        Directory.CreateDirectory(installDir);
        Tidy(installDir);
        var swapped = new List<(string Target, string? Aside)>();
        try
        {
            foreach (var name in Names)
            {
                var source = Path.Combine(programDir, name);
                var staged = Path.Combine(installDir, name + ".new");
                Retry(() => File.Copy(source, staged, overwrite: true), attempts, wait);
            }
            foreach (var name in Names)
            {
                var target = Path.Combine(installDir, name);
                swapped.Add((target, Retry(() => Swap(target), attempts, wait)));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            foreach (var (target, aside) in Enumerable.Reverse(swapped))
                PutBack(target, aside);
            foreach (var name in Names)
                TryDelete(Path.Combine(installDir, name + ".new"));
            throw;
        }
        foreach (var (_, aside) in swapped)
            if (aside is not null)
                TryDelete(aside);   // fails, harmlessly, while something is still running it; the next install tidies it
    }

    /// <summary>Rename the old program aside and move the staged one into its place. Returns where the old one
    /// went (null if there wasn't one). If the move in fails, the old one is put back before it throws.</summary>
    private static string? Swap(string target)
    {
        string? aside = null;
        if (File.Exists(target))
        {
            aside = AsideName(target);
            File.Move(target, aside);
        }
        try
        {
            File.Move(target + ".new", target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (aside is not null)
                TryMove(aside, target);
            throw;
        }
        return aside;
    }

    /// <summary>Undo one swap: the new program aside (it may be running already), the old one back.</summary>
    private static void PutBack(string target, string? aside)
    {
        if (aside is null || !File.Exists(aside))
            return;
        var newAside = AsideName(target, skip: aside);
        if (File.Exists(target) && !TryMove(target, newAside))
            return;
        TryMove(aside, target);
        TryDelete(newAside);
    }

    /// <summary>NAME.old, or NAME.2.old and so on while an earlier one is still running and can't be deleted.</summary>
    private static string AsideName(string target, string? skip = null)
    {
        for (var n = 1; ; n++)
        {
            var aside = n == 1 ? target + ".old" : $"{target}.{n}.old";
            if (string.Equals(aside, skip, StringComparison.OrdinalIgnoreCase))
                continue;   // the old program, waiting to be put back
            if (!File.Exists(aside) || TryDelete(aside))
                return aside;
        }
    }

    /// <summary>Delete what earlier installs left: old programs set aside, and copies staged but never moved in.
    /// One still running is left for next time.</summary>
    private static void Tidy(string installDir)
    {
        var options = new EnumerationOptions { MatchType = MatchType.Simple, MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var name in Names)
            foreach (var pattern in new[] { name + ".old", name + ".*.old", name + ".new" })
                foreach (var path in Directory.GetFiles(installDir, pattern, options))
                    TryDelete(path);
    }

    private static T Retry<T>(Func<T> action, int attempts, TimeSpan pause)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < attempts)
            {
                Thread.Sleep(pause);
            }
        }
    }

    private static void Retry(Action action, int attempts, TimeSpan pause) =>
        Retry(() => { action(); return 0; }, attempts, pause);

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The background program the task should run: the installed copy if there is one, else the one
    /// beside the running program.</summary>
    public static string BackgroundFor(string programDir, string installDir)
    {
        var installed = Path.Combine(installDir, Install.BackgroundExe);
        return File.Exists(installed) ? installed : Path.Combine(programDir, Install.BackgroundExe);
    }
}
