namespace AiKeepAwake;

public static partial class Cli
{
    private static int InstallCommand(Options options, Config config, CliHost host)
    {
        var path = SettingsPath(options, config, host);
        var hookExe = Install.HookExe(config.InstallDir);
        ChangeReport report;
        try
        {
            // Read and check the settings before anything else is touched, so a bad file stops everything.
            report = Install.Change(path, s => Install.WithOurs(s, config, hookExe), dryRun: true);
        }
        catch (Exception error) when (error is SettingsError or IOException or UnauthorizedAccessException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        var copy = !Programs.RunningFrom(host.ProgramDir, config.InstallDir);
        var missing = copy ? Programs.Missing(host.ProgramDir) : [];
        if (missing.Count > 0)
        {
            Print(host.Err, $"{string.Join(" and ", missing)} isn't beside this program (in {PyPath.Pure(host.ProgramDir)}), so it can't be installed.");
            return 1;
        }
        var (exe, taskArgs) = AutoStart.Command(Install.HookExe(config.InstallDir), config.Path);

        if (options.DryRun)
        {
            Print(host.Out, copy
                ? $"Would copy {string.Join(" and ", Programs.Names)} to {config.InstallDir}"
                  + (options.NoAutostart ? "." : ", stopping the watcher first.")
                : $"Running from {config.InstallDir} already; nothing to copy.");
            Print(host.Out, report.Changed ? report.Diff : $"{path}: the hooks are already in place; no change.");
            if (!options.NoAutostart)
            {
                Print(host.Out, $"\nWould register the Task Scheduler task '{AutoStart.TaskName}' (at every logon, kept running, and "
                                + $"started now):\n  {exe} {WinBackground.Shell.ListToCommandLine(taskArgs)}");
                if (OldTaskExists(host))
                    Print(host.Out, $"Would remove the old task '{AutoStart.OldTaskName}' (the Python claude-keep-awake), and stop its watcher.");
            }
            return 0;
        }

        // Install stops the watcher before replacing its program. Whatever happens after that, it must not be
        // left stopped: if install doesn't get as far as registering (and so starting) the task, it's started
        // again as it was. Otherwise a failed copy or settings write left no watcher until the next logon.
        var task = host.Tasks(AutoStart.TaskName);
        var stopped = false;
        var enabled = false;
        try
        {
            if (copy)
            {
                if (!options.NoAutostart)
                {
                    try
                    {
                        stopped = true;   // if stopping fails part way, it may still have stopped: start it again to be sure
                        stopped = task.Stop();
                    }
                    catch (Exception error) when (IsTaskError(error))
                    {
                        Print(host.Err, $"The watcher couldn't be stopped, so the programs weren't replaced: {error.Message}");
                        return 1;
                    }
                }
                try
                {
                    Programs.Copy(host.ProgramDir, config.InstallDir, pause: host.CopyPause);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Print(host.Err, $"The programs couldn't be copied to {config.InstallDir}: {error.Message}");
                    Print(host.Err, "The programs there were left as they were, and nothing else was changed. "
                                    + "Something may be scanning or holding them; try again in a minute.");
                    return 1;
                }
                Print(host.Out, $"Copied {string.Join(" and ", Programs.Names)} to {config.InstallDir}.");
            }

            try
            {
                report = Install.Change(path, s => Install.WithOurs(s, config, hookExe));
            }
            catch (Exception error) when (error is SettingsError or IOException or UnauthorizedAccessException)
            {
                Print(host.Err, error.Message);
                return 2;
            }
            if (report.Changed)
            {
                Print(host.Out, $"Hooks added to {path} ({Install.Events.Length} events)."
                                + (report.Backup is not null ? $" Backup: {report.Backup}" : ""));
                Print(host.Out, "Claude Code sessions already open pick the hooks up when they next read their settings; "
                                + "a new session certainly will.");
            }
            else
                Print(host.Out, $"The hooks were already in {path}; no change.");

            if (!options.NoAutostart)
            {
                try
                {
                    if (host.Tasks(AutoStart.OldTaskName).Disable())
                        Print(host.Out, $"The old task '{AutoStart.OldTaskName}' is removed (and its watcher stopped).");
                    Directory.CreateDirectory(Path.GetDirectoryName(config.Path)!);
                    var command = task.Enable(exe, taskArgs, Path.GetDirectoryName(config.Path));
                    enabled = true;
                    Print(host.Out, $"The watcher runs at every logon ('{AutoStart.TaskName}'), and has started now.\n  {command}");
                }
                catch (Exception error) when (IsTaskError(error))
                {
                    Print(host.Err, $"The watcher's task couldn't be registered: {error.Message}");
                    return 1;
                }
            }
            return 0;
        }
        finally
        {
            if (stopped && !enabled)
                StartAgain(task, host);
        }
    }

    /// <summary>What a Task Scheduler call can throw: PowerShell failing (InvalidOperationException), timing out,
    /// or not starting at all.</summary>
    private static bool IsTaskError(Exception error) =>
        error is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception;

    /// <summary>Start the watcher's task again after install stopped it and then failed. Never throws.</summary>
    private static void StartAgain(WinBackground.ITask task, CliHost host)
    {
        try
        {
            if (task.Start())
                Print(host.Err, "The watcher was started again, as it was.");
        }
        catch (Exception error) when (IsTaskError(error))
        {
            Print(host.Err, $"The watcher couldn't be started again ({error.Message}). It starts at the next logon, "
                            + "or now with: ai-keep-awake autostart on");
        }
    }

    private static int UninstallCommand(Options options, Config config, CliHost host)
    {
        var path = SettingsPath(options, config, host);
        ChangeReport report;
        try
        {
            report = Install.Change(path, s => Install.WithoutOurs(s).Settings, options.DryRun);
        }
        catch (Exception error) when (error is SettingsError or IOException or UnauthorizedAccessException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        if (options.DryRun)
        {
            Print(host.Out, report.Changed ? report.Diff : $"{path}: none of this tool's hooks are there; no change.");
            if (!options.NoAutostart)
            {
                Print(host.Out, $"\nWould remove the Task Scheduler task '{AutoStart.TaskName}', if it exists, and stop the watcher.");
                if (OldTaskExists(host))
                    Print(host.Out, $"Would remove the old task '{AutoStart.OldTaskName}' (the Python claude-keep-awake) too.");
            }
            return 0;
        }
        Print(host.Out, report.Changed
            ? $"Hooks removed from {path}." + (report.Backup is not null ? $" Backup: {report.Backup}" : "")
            : $"None of this tool's hooks were in {path}.");
        // The hooks are out by now, so a task that can't be removed is reported and the rest still done.
        var failed = false;
        if (!options.NoAutostart)
        {
            try
            {
                Print(host.Out, host.Tasks(AutoStart.TaskName).Disable()
                    ? "The watcher's task is removed (and the watcher stopped)."
                    : "There was no watcher task.");
            }
            catch (Exception error) when (IsTaskError(error))
            {
                Print(host.Err, $"The watcher's task '{AutoStart.TaskName}' couldn't be removed: {error.Message}");
                failed = true;
            }
            try
            {
                if (host.Tasks(AutoStart.OldTaskName).Disable())
                    Print(host.Out, $"The old task '{AutoStart.OldTaskName}' is removed too.");
            }
            catch (Exception error) when (IsTaskError(error))
            {
                Print(host.Err, $"The old task '{AutoStart.OldTaskName}' couldn't be removed: {error.Message}");
                failed = true;
            }
        }
        var gone = Leases.Clear(config.LeasesDir);
        if (gone > 0)
            Print(host.Out, $"{gone} lease file(s) cleared.");
        if (Programs.Names.Any(name => File.Exists(Path.Combine(config.InstallDir, name))))
            Print(host.Out, $"The programs are still in {config.InstallDir}; delete that folder to remove them.");
        return failed ? 1 : 0;
    }

    /// <summary>Whether the Python tool's task is there, for the dry runs. If Task Scheduler can't be asked, says
    /// so and answers no.</summary>
    private static bool OldTaskExists(CliHost host)
    {
        try
        {
            return host.Tasks(AutoStart.OldTaskName).Status() is not null;
        }
        catch (Exception error) when (IsTaskError(error))
        {
            Print(host.Err, $"Couldn't check for the old task '{AutoStart.OldTaskName}': {error.Message}");
            return false;
        }
    }

    private static int AutostartCommand(string action, Config config, CliHost host)
    {
        var task = host.Tasks(AutoStart.TaskName);
        if (action == "on")
        {
            var (exe, args) = AutoStart.Command(Programs.BackgroundFor(host.ProgramDir, config.InstallDir), config.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(config.Path)!);
            string command;
            try
            {
                command = task.Enable(exe, args, Path.GetDirectoryName(config.Path));
            }
            catch (Exception error) when (IsTaskError(error))
            {
                Print(host.Err, $"The watcher's task couldn't be registered: {error.Message}");
                return 1;
            }
            Print(host.Out, $"Autostart is on: '{AutoStart.TaskName}' runs at every logon, and has started now.\n  {command}");
        }
        else if (action == "off")
        {
            try
            {
                Print(host.Out, task.Disable() ? "Autostart is off." : "Autostart was already off.");
            }
            catch (Exception error) when (IsTaskError(error))
            {
                Print(host.Err, $"The watcher's task couldn't be removed: {error.Message}");
                return 1;
            }
        }
        else
        {
            IReadOnlyDictionary<string, string>? info;
            try
            {
                info = task.Status();
            }
            catch (Exception error) when (IsTaskError(error))
            {
                Print(host.Err, $"The watcher's task couldn't be read: {error.Message}");
                return 1;
            }
            if (info is null)
                Print(host.Out, "Autostart is off. `ai-keep-awake autostart on` turns it on.");
            else
            {
                Print(host.Out, $"Autostart is on ('{AutoStart.TaskName}').");
                foreach (var name in new[] { "state", "last_run", "last_result", "next_run", "command" })
                    Print(host.Out, $"  {name.Replace('_', ' '),-12} {info.GetValueOrDefault(name, "")}");
            }
        }
        return 0;
    }
}
