namespace AiKeepAwake;

public static partial class Cli
{
    private const string Description = "Keeps the laptop awake while Claude Code is working";

    private const string PinTip = "The tray icon may be hidden under ^ by the clock: to keep it in view, turn it on in "
                                  + "Settings > Personalisation > Taskbar > Other system tray icons.";

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
        if (options.HooksOnly)
            return HooksOnly(options, path, report, config, hookExe, host);
        var copy = !Programs.RunningFrom(host.ProgramDir, config.InstallDir);
        var missing = copy ? Programs.Missing(host.ProgramDir) : [];
        if (missing.Count > 0)
        {
            Print(host.Err, $"{Programs.Listed(missing)} {(missing.Count == 1 ? "isn't" : "aren't")} beside this program (in {PyPath.Pure(host.ProgramDir)}), so it can't be installed.");
            return 1;
        }
        var desktop = host.Desktop;
        var (exe, taskArgs) = AutoStart.Command(Install.HookExe(config.InstallDir), config.Path);
        // Inside Claude Code the logon entries are left for the person (its permission check blocks registering them).
        var insideClaude = !options.NoAutostart && InsideClaudeCode(host);
        var register = !options.NoAutostart && !insideClaude;
        // Not registering, a watcher already running this install's program is still stopped before the copy and
        // started again after, so it runs the new version rather than the old one until the next logon.
        var restart = copy && !register && TaskRunsHere(host, config);
        var record = InstallRecord.Read(config);
        var managedBy = options.ManagedBy ?? (record is { RemovedByPerson: false } ? record.ManagedBy : InstallRecord.Person);
        var other = config.StartMenu ? OtherCopy(desktop, config) : null;

        if (options.DryRun)
        {
            Print(host.Out, copy
                ? $"Would copy {Programs.Listed(Programs.Names)} to {config.InstallDir}"
                  + (register ? ", stopping the watcher (and the tray) first." : restart ? ", stopping the watcher first and starting it again after." : ".")
                : $"Running from {config.InstallDir} already; nothing to copy.");
            Print(host.Out, report.Changed ? report.Diff : $"{path}: the hooks are already in place; no change.");
            if (insideClaude)
                Print(host.Out, "\nInside Claude Code, so it wouldn't register the logon task or the tray at logon; it would print the command for you to run.");
            if (register)
            {
                Print(host.Out, $"\nWould register the Task Scheduler task '{AutoStart.TaskName}' (at every logon, kept running, and "
                                + $"started now):\n  {exe} {WinBackground.Shell.ListToCommandLine(taskArgs)}");
                Print(host.Out, $"Would start the tray icon at every logon (HKCU\\{WindowsDesktop.RunKey}, '{WindowsDesktop.RunValue}'), and now:\n  {Tray.RunCommand(config)}");
                if (OldTaskExists(host))
                    Print(host.Out, $"Would remove the old task '{AutoStart.OldTaskName}' (the Python claude-keep-awake), and stop its watcher.");
            }
            Print(host.Out, !config.StartMenu
                ? "start_menu = false in the settings, so it wouldn't be added to the Start menu or Settings > Apps."
                : other is not null
                    ? $"Settings > Apps lists the copy in {other}, so the Start menu and Settings > Apps would be left as they are."
                    : $"Would add it to the Start menu ({desktop.ShortcutPath}) and to Settings > Apps, so it can be uninstalled there.");
            Print(host.Out, $"Would record the install in {InstallRecord.PathFor(config)} (installed by {managedBy}).");
            return 0;
        }

        // Install stops the watcher before replacing its program. Whatever happens after that, it must not be
        // left stopped: if install doesn't get as far as registering (and so starting) the task, it's started
        // again as it was. Otherwise a failed copy or settings write left no watcher until the next logon.
        var task = host.Tasks(AutoStart.TaskName);
        var stopped = false;
        var enabled = false;
        var trayWasRunning = false;
        try
        {
            if (copy)
            {
                if (register || restart)
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
                trayWasRunning = StopTray(desktop, config, host);
                try
                {
                    Programs.Copy(host.ProgramDir, config.InstallDir, pause: host.CopyPause);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Print(host.Err, $"The programs couldn't be copied to {config.InstallDir}: {error.Message}");
                    Print(host.Err, "The programs there were left as they were, and nothing else was changed. "
                                    + "Something may be scanning or holding them; try again in a minute.");
                    if (trayWasRunning)
                        StartTray(desktop, config, host);
                    return 1;
                }
                Print(host.Out, $"Copied {Programs.Listed(Programs.Names)} to {config.InstallDir}.");
            }

            try
            {
                report = Install.Change(path, s => Install.WithOurs(s, config, hookExe));
            }
            catch (Exception error) when (error is SettingsError or IOException or UnauthorizedAccessException)
            {
                Print(host.Err, error.Message);
                if (trayWasRunning)
                    StartTray(desktop, config, host);
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

            if (restart && stopped)
            {
                stopped = false;
                StartAgain(task, host, planned: true);
            }
            var failed = false;
            if (register)
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
                    failed = true;
                }
                if (!failed && TrayAtLogon(desktop, config, host))
                    Print(host.Out, "The tray icon starts at every logon too (Task Manager > Startup apps lists it).");
            }

            if (config.StartMenu)
                DesktopEntries(desktop, config, other, host);
            try
            {
                new InstallRecord(Version, InstallRecord.Stamp(host.Now()), managedBy, config.InstallDir).Write(config);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Print(host.Err, $"{InstallRecord.PathFor(config)} couldn't be written: {error.Message}");
            }
            if (copy)
                foreach (var name in Programs.Names)
                    desktop.Warm(Path.Combine(config.InstallDir, name));

            var trayStarted = false;
            if ((trayWasRunning || (register && !failed)) && !desktop.TrayRunning(config.InstallDir))
                trayStarted = StartTray(desktop, config, host);
            if (insideClaude)
            {
                var registered = restart || TaskRunsHere(host, config);
                var trayAtLogon = desktop.RunEntry() is { } run && run.Contains(Tray.Exe(config), StringComparison.OrdinalIgnoreCase);
                Print(host.Out, registered && trayAtLogon
                    ? $"Inside Claude Code, so the logon entries were left as they are; they're registered already ('{AutoStart.TaskName}', and the tray)."
                    : "Inside Claude Code, so the logon entries are left for you (Claude Code's permission check blocks registering them). "
                      + $"To start the watcher and the tray icon at every logon, run this yourself at a console:\n  & \"{Path.Combine(config.InstallDir, Install.ConsoleExe)}\" autostart on");
            }
            if (trayStarted && !trayWasRunning)
                Print(host.Out, PinTip);
            return failed ? 1 : 0;
        }
        finally
        {
            if (stopped && !enabled)
                StartAgain(task, host);
        }
    }

    /// <summary>Where another copy is installed, if Settings > Apps already lists one that's still there (so this
    /// install, in another folder, leaves its entries alone). Null when the entries are this install's to write.</summary>
    private static string? OtherCopy(IDesktop desktop, Config config)
    {
        string? location;
        try
        {
            location = desktop.AppsEntryLocation();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(location) || PyPath.Same(location, config.InstallDir))
            return null;
        return File.Exists(Path.Combine(location, Install.ConsoleExe)) ? location : null;
    }

    /// <summary>The Start menu shortcut and the Settings > Apps entry (unless another copy has them). A failure is
    /// said and the install carries on: neither is what keeps the laptop awake.</summary>
    private static void DesktopEntries(IDesktop desktop, Config config, string? other, CliHost host)
    {
        if (other is not null)
        {
            Print(host.Out, $"Settings > Apps lists the copy in {other}, so the Start menu and Settings > Apps were left as they are.");
            return;
        }
        var tray = Tray.Exe(config);
        var console = Path.Combine(config.InstallDir, Install.ConsoleExe);
        var done = new List<string>();
        try
        {
            desktop.WriteShortcut(tray, "--show", config.InstallDir, Description, tray);
            done.Add("the Start menu");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Print(host.Err, $"The Start menu shortcut couldn't be made: {error.Message}");
        }
        try
        {
            var size = Programs.Names.Select(name => Path.Combine(config.InstallDir, name)).Where(File.Exists).Sum(file => new FileInfo(file).Length);
            desktop.SetAppsEntry(new AppsEntry("ai-keep-awake", Version.Split('-')[0], "Gareth Jones", config.InstallDir, $"{tray},0",
                $"\"{console}\" uninstall --interactive", (size + 1023) / 1024, DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
                Description + "."));
            done.Add("Settings > Apps (uninstall it there)");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Print(host.Err, $"The Settings > Apps entry couldn't be made: {error.Message}");
        }
        if (done.Count > 0)
            Print(host.Out, $"Added to {string.Join(" and to ", done)}.");
    }

    /// <summary>Set the tray's Run entry, if this install has a tray. True if set.</summary>
    private static bool TrayAtLogon(IDesktop desktop, Config config, CliHost host)
    {
        if (!File.Exists(Tray.Exe(config)))
            return false;
        try
        {
            desktop.SetRunEntry(Tray.RunCommand(config));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Print(host.Err, $"The tray icon couldn't be set to start at logon: {error.Message}");
            return false;
        }
    }

    /// <summary>Close a tray running from this install, before its program is replaced or removed. True if one was.</summary>
    private static bool StopTray(IDesktop desktop, Config config, CliHost host)
    {
        try
        {
            return desktop.StopTray(config.InstallDir);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Print(host.Err, $"The tray icon couldn't be closed: {error.Message}");
            return false;
        }
    }

    /// <summary>Start the installed tray, if there is one. True if it started.</summary>
    private static bool StartTray(IDesktop desktop, Config config, CliHost host)
    {
        var tray = Tray.Exe(config);
        if (!File.Exists(tray))
            return false;
        try
        {
            desktop.Start(tray, Tray.Args(config), config.InstallDir);
            Print(host.Out, "The tray icon has started.");
            return true;
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            Print(host.Err, $"The tray icon couldn't be started: {error.Message}");
            return false;
        }
    }

    /// <summary>What a Task Scheduler call can throw: PowerShell failing (InvalidOperationException), timing out,
    /// or not starting at all.</summary>
    private static bool IsTaskError(Exception error) =>
        error is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception;

    /// <summary>Whether the watcher's task is registered and runs this install's background program (so an install
    /// that doesn't register it can still restart it on the new version). If Task Scheduler can't be asked, no.</summary>
    private static bool TaskRunsHere(CliHost host, Config config)
    {
        try
        {
            var info = host.Tasks(AutoStart.TaskName).Status();
            return info is not null && info.TryGetValue("command", out var command)
                   && command.Contains(Install.HookExe(config.InstallDir), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (IsTaskError(error))
        {
            return false;
        }
    }

    /// <summary>`install --hooks-only`: put the hooks back in settings.json, and nothing else.</summary>
    private static int HooksOnly(Options options, string path, ChangeReport preview, Config config, string hookExe, CliHost host)
    {
        if (options.DryRun)
        {
            Print(host.Out, preview.Changed ? preview.Diff : $"{path}: the hooks are already in place; no change.");
            return 0;
        }
        ChangeReport report;
        try
        {
            report = Install.Change(path, s => Install.WithOurs(s, config, hookExe));
        }
        catch (Exception error) when (error is SettingsError or IOException or UnauthorizedAccessException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        Print(host.Out, report.Changed
            ? $"Hooks added to {path} ({Install.Events.Length} events)." + (report.Backup is not null ? $" Backup: {report.Backup}" : "")
            : $"The hooks were already in {path}; no change.");
        if (!File.Exists(hookExe))
            Print(host.Out, $"{hookExe} isn't there, so the hooks can't run yet: run the full install.");
        return 0;
    }

    /// <summary>Start the watcher's task again after install stopped it: planned (an upgrade that doesn't register
    /// the task), or because what came after failed. Never throws.</summary>
    private static void StartAgain(WinBackground.ITask task, CliHost host, bool planned = false)
    {
        try
        {
            if (task.Start())
            {
                if (planned)
                    Print(host.Out, "The watcher was started again, on the new programs.");
                else
                    Print(host.Err, "The watcher was started again, as it was.");
            }
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
        var desktop = host.Desktop;
        var keepPrograms = options.NoAutostart;   // the task, left alone, still runs them
        var ours = config.StartMenu && OtherCopy(desktop, config) is null;
        if (options.Interactive && !options.DryRun)
        {
            Print(host.Out, $"ai-keep-awake {Version}\n\nThis will take out its hooks from {path}, its logon task and tray icon, its Start menu "
                            + $"and Settings > Apps entries, and {(keepPrograms ? "leave" : "delete")} its programs in {config.InstallDir}.");
            if (!Yes(host, "Uninstall ai-keep-awake? [y/N]", defaultYes: false))
            {
                Print(host.Out, "Nothing was changed.");
                Pause(host);
                return 0;
            }
        }
        ChangeReport report;
        try
        {
            report = Install.Change(path, s => Install.WithoutOurs(s).Settings, options.DryRun);
        }
        catch (Exception error) when (error is SettingsError or IOException or UnauthorizedAccessException)
        {
            Print(host.Err, error.Message);
            if (options.Interactive)
                Pause(host);
            return 2;
        }
        if (options.DryRun)
        {
            Print(host.Out, report.Changed ? report.Diff : $"{path}: none of this tool's hooks are there; no change.");
            if (!options.NoAutostart)
            {
                Print(host.Out, $"\nWould remove the Task Scheduler task '{AutoStart.TaskName}', if it exists, and stop the watcher.");
                Print(host.Out, "Would stop starting the tray icon at logon, and close it.");
                if (OldTaskExists(host))
                    Print(host.Out, $"Would remove the old task '{AutoStart.OldTaskName}' (the Python claude-keep-awake) too.");
            }
            if (ours)
                Print(host.Out, "Would remove it from the Start menu and from Settings > Apps.");
            Print(host.Out, keepPrograms
                ? $"Would leave the programs in {config.InstallDir} (--no-autostart leaves the task that runs them)."
                : $"Would delete the programs in {config.InstallDir}.");
            Print(host.Out, $"Would keep {InstallRecord.PathFor(config)}, noting the uninstall"
                            + (options.Purge ? $"; and delete the settings file, the log and the state file." : "."));
            return 0;
        }
        Print(host.Out, report.Changed
            ? $"Hooks removed from {path}." + (report.Backup is not null ? $" Backup: {report.Backup}" : "")
            : $"None of this tool's hooks were in {path}.");
        // The hooks are out by now, so anything that can't be removed is reported and the rest still done.
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
            failed |= !Quietly(host, "The tray's logon entry", () =>
            {
                if (desktop.RunEntry() is { } run && run.Contains(Tray.Exe(config), StringComparison.OrdinalIgnoreCase) && desktop.RemoveRunEntry())
                    Print(host.Out, "The tray icon no longer starts at logon.");
            });
            if (StopTray(desktop, config, host))
                Print(host.Out, "The tray icon is closed.");
        }
        if (ours)
        {
            failed |= !Quietly(host, "The Start menu shortcut", () =>
            {
                if (desktop.RemoveShortcut())
                    Print(host.Out, "Removed from the Start menu.");
            });
            failed |= !Quietly(host, "The Settings > Apps entry", () =>
            {
                if (desktop.RemoveAppsEntry())
                    Print(host.Out, "Removed from Settings > Apps.");
            });
        }
        var gone = Leases.Clear(config.LeasesDir);
        if (gone > 0)
            Print(host.Out, $"{gone} lease file(s) cleared.");
        Control.ClearPause(config, wake: false);
        TryDelete(Notice.PathFor(config));

        var record = InstallRecord.Read(config);
        var byPerson = (options.ManagedBy ?? InstallRecord.Person) != InstallRecord.RightHand;
        try
        {
            new InstallRecord(record?.Version ?? Version, record?.Installed, record?.ManagedBy ?? InstallRecord.Person, config.InstallDir,
                RemovedByPerson: byPerson, Removed: InstallRecord.Stamp(host.Now())).Write(config);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Print(host.Err, $"{InstallRecord.PathFor(config)} couldn't be written: {error.Message}");
        }

        if (keepPrograms)
        {
            if (Programs.Names.Any(name => File.Exists(Path.Combine(config.InstallDir, name))))
                Print(host.Out, $"The programs are still in {config.InstallDir} (--no-autostart leaves the task that runs them).");
        }
        else if (Programs.Names.Any(name => File.Exists(Path.Combine(config.InstallDir, name))))
        {
            if (!Programs.RunningFrom(host.ProgramDir, config.InstallDir) && RemovePrograms(config.InstallDir, attempts: 3))
                Print(host.Out, $"The programs are deleted ({config.InstallDir}).");
            else
            {
                try
                {
                    desktop.RemoveLater(config.InstallDir);
                    Print(host.Out, $"The programs in {config.InstallDir} are deleted as soon as this window closes.");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    Print(host.Err, $"The programs in {config.InstallDir} couldn't be deleted ({error.Message}); delete that folder by hand.");
                    failed = true;
                }
            }
        }

        var left = new List<string>();
        if (options.Purge)
        {
            foreach (var file in new[] { config.Path, config.LogFile, config.StateFile, ReleaseCheck.PathFor(config) })
                TryDelete(file);
            Print(host.Out, "The settings file, the log and the state file are deleted (--purge).");
        }
        else
        {
            if (File.Exists(config.Path))
                left.Add($"the settings file ({config.Path})");
            if (File.Exists(config.LogFile))
                left.Add($"the log ({config.LogFile})");
        }
        Print(host.Out, $"Kept{(left.Count > 0 ? $": {string.Join(", ", left)}, and" : ":")} {InstallRecord.PathFor(config)}, "
                        + (byPerson ? "which says you removed it, so right-hand asks before putting it back." : "a record of the install.")
                        + (left.Count > 0 ? " `uninstall --purge` deletes the first." : ""));
        if (options.Interactive)
        {
            Print(host.Out, failed ? "\nUninstalled, with the problems above." : "\nai-keep-awake is uninstalled.");
            Pause(host);
        }
        return failed ? 1 : 0;
    }

    /// <summary>Delete this install's programs (and what earlier installs left beside them), then the folder if
    /// that leaves it empty; nothing else in it is touched. True if the programs are gone.</summary>
    internal static bool RemovePrograms(string installDir, int attempts = 60, TimeSpan? pause = null)
    {
        var options = new EnumerationOptions { MatchType = MatchType.Simple, MatchCasing = MatchCasing.CaseInsensitive };
        for (var attempt = 1; ; attempt++)
        {
            var left = 0;
            if (Directory.Exists(installDir))
            {
                foreach (var name in Programs.Names)
                    foreach (var pattern in new[] { name, name + ".old", name + ".*.old", name + ".new" })
                        foreach (var file in Directory.GetFiles(installDir, pattern, options))
                            if (!TryDelete(file))
                                left++;
            }
            if (left == 0)
            {
                try
                {
                    if (Directory.Exists(installDir) && !Directory.EnumerateFileSystemEntries(installDir).Any())
                        Directory.Delete(installDir);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
                return true;
            }
            if (attempt >= attempts)
                return false;
            Thread.Sleep(pause ?? TimeSpan.FromMilliseconds(500));
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Do it; if the registry or a file refuses, say what couldn't be done. False if it failed.</summary>
    private static bool Quietly(CliHost host, string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Print(host.Err, $"{what} couldn't be removed: {error.Message}");
            return false;
        }
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

    /// <summary>`autostart on|off|status`: the watcher's task and the tray's Run entry, together.</summary>
    private static int AutostartCommand(string action, Config config, CliHost host)
    {
        var task = host.Tasks(AutoStart.TaskName);
        var desktop = host.Desktop;
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
            if (TrayAtLogon(desktop, config, host))
            {
                var started = !desktop.TrayRunning(config.InstallDir) && StartTray(desktop, config, host);
                Print(host.Out, "The tray icon starts at every logon too (Task Manager > Startup apps lists it).");
                if (started)
                    Print(host.Out, PinTip);
            }
        }
        else if (action == "off")
        {
            bool removed;
            try
            {
                removed = task.Disable();
            }
            catch (Exception error) when (IsTaskError(error))
            {
                Print(host.Err, $"The watcher's task couldn't be removed: {error.Message}");
                return 1;
            }
            var trayRemoved = false;
            if (!Quietly(host, "The tray's logon entry", () => trayRemoved = desktop.RemoveRunEntry()))
                return 1;
            Print(host.Out, removed || trayRemoved ? "Autostart is off." : "Autostart was already off.");
            if (trayRemoved)
                Print(host.Out, "The tray icon no longer starts at logon (it stays open until you close it or log off).");
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
            Print(host.Out, $"  {"tray",-12} {(desktop.RunEntry() is { } run ? $"at logon: {run}" : "not at logon")}");
        }
        return 0;
    }
}
