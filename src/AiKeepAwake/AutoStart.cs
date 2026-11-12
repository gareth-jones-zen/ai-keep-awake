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

/// <summary>Copying the two programs into the install folder, so the hooks and the task don't depend on where
/// the download was unzipped, and an upgrade is "run the new one's install".</summary>
public static class Programs
{
    public static readonly string[] Names = [Install.ConsoleExe, Install.BackgroundExe];

    /// <summary>Whether the running program is the installed copy (so there's nothing to copy).</summary>
    public static bool RunningFrom(string programDir, string installDir) => PyPath.Same(programDir, installDir);

    /// <summary>The programs missing from programDir, if any (so install can't copy them).</summary>
    public static List<string> Missing(string programDir) =>
        Names.Where(name => !File.Exists(Path.Combine(programDir, name))).ToList();

    /// <summary>Copy both programs into installDir, replacing older copies. Throws IOException if one is in use.</summary>
    public static void Copy(string programDir, string installDir)
    {
        Directory.CreateDirectory(installDir);
        foreach (var name in Names)
            File.Copy(Path.Combine(programDir, name), Path.Combine(installDir, name), overwrite: true);
    }

    /// <summary>The background program the task should run: the installed copy if there is one, else the one
    /// beside the running program.</summary>
    public static string BackgroundFor(string programDir, string installDir)
    {
        var installed = Path.Combine(installDir, Install.BackgroundExe);
        return File.Exists(installed) ? installed : Path.Combine(programDir, Install.BackgroundExe);
    }
}
