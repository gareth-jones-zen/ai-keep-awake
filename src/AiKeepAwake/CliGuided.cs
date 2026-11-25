namespace AiKeepAwake;

public static partial class Cli
{
    /// <summary>The hidden command a copy of the program in %TEMP% runs after uninstall: wait for it to exit, then
    /// delete the programs (a running program can't delete itself).</summary>
    public const string RemoveProgramsCommand = "_remove-programs";

    /// <summary>ai-keep-awake.exe double-clicked in Explorer: say what an install changes, ask once, and do it. Or,
    /// if it's installed already, offer an upgrade, a repair or an uninstall. The console window stays open at the
    /// end until Enter, so the person can read what happened.</summary>
    public static int Guided(CliHost host)
    {
        Print(host.Out, $"ai-keep-awake {Version}");
        Print(host.Out, "Keeps this laptop awake while Claude Code is working, and lets it sleep and lock as normal otherwise.\n");
        Config config;
        try
        {
            config = AiKeepAwake.Config.Load(null, host.Env);
        }
        catch (Exception error) when (error is ConfigError or IOException or UnauthorizedAccessException)
        {
            Print(host.Err, error.Message);
            Pause(host);
            return 2;
        }
        var options = new Options { Command = "install" };
        var console = Path.Combine(config.InstallDir, Install.ConsoleExe);
        int code;
        if (File.Exists(console))
        {
            var here = Programs.RunningFrom(host.ProgramDir, config.InstallDir);
            var have = here ? Version : InstallRecord.Read(config) is { RemovedByPerson: false, Version: { } v } ? v : host.Desktop.VersionOf(console) ?? "?";
            if (here || have == Version)
            {
                Print(host.Out, $"ai-keep-awake {have} is installed in {config.InstallDir}.");
                var answer = Ask(host, "Repair, Uninstall or Close? [r/u/C]").ToLowerInvariant();
                if (answer.StartsWith('r'))
                    code = InstallCommand(options, config, host);
                else if (answer.StartsWith('u'))
                    return UninstallCommand(new Options { Command = "uninstall", Interactive = true }, config, host);
                else
                    return 0;
            }
            else if (have != "?" && ReleaseCheck.Newer(have, Version))
            {
                Print(host.Out, $"ai-keep-awake {have} is installed already, which is newer than this one ({Version}); nothing to do.");
                Pause(host);
                return 0;
            }
            else
            {
                if (!Yes(host, $"Upgrade {have} to {Version}? [Y/n]", defaultYes: true))
                    return 0;
                code = InstallCommand(options, config, host);
            }
        }
        else
        {
            Print(host.Out, "This will:");
            Print(host.Out, $"  - copy its programs to {config.InstallDir}");
            Print(host.Out, $"  - add its hooks to {config.ClaudeSettings} ({Install.Events.Length} events; the file is backed up first)");
            Print(host.Out, "  - start its watcher, and its tray icon, at each logon (Task Scheduler; the Run key)");
            Print(host.Out, "  - add it to the Start menu, and to Settings > Apps so it can be uninstalled there");
            Print(host.Out, "No admin rights needed. Nothing else on the laptop is changed.\n");
            if (!Yes(host, "Install? [Y/n]", defaultYes: true))
                return 0;
            code = InstallCommand(options, config, host);
        }
        Print(host.Out, code == 0 ? "\nDone." : "\nThat didn't finish: see above.");
        Pause(host);
        return code;
    }

    private static string Ask(CliHost host, string prompt)
    {
        host.Out.Write(prompt + " ");
        host.Out.Flush();
        return (host.In.ReadLine() ?? "").Trim();
    }

    private static bool Yes(CliHost host, string prompt, bool defaultYes)
    {
        var answer = Ask(host, prompt).ToLowerInvariant();
        return answer.Length == 0 ? defaultYes : answer.StartsWith('y');
    }

    private static void Pause(CliHost host)
    {
        host.Out.Write("Press Enter to close.");
        host.Out.Flush();
        host.In.ReadLine();
    }

    /// <summary>`_remove-programs --wait PID --dir FOLDER`: run from a copy in %TEMP% by uninstall.</summary>
    private static int RemoveProgramsLater(IReadOnlyList<string> argv)
    {
        string? folder = null;
        var pid = 0;
        for (var i = 1; i + 1 < argv.Count; i += 2)
        {
            if (argv[i] == "--dir")
                folder = argv[i + 1];
            else if (argv[i] == "--wait")
                int.TryParse(argv[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pid);
        }
        if (folder is null)
            return 2;
        if (pid > 0)
            Native.WaitForExit(pid, TimeSpan.FromSeconds(120));
        var done = RemovePrograms(folder);
        WindowsDesktop.DeleteSelfLater();
        return done ? 0 : 1;
    }
}
