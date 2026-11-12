using System.Reflection;

namespace AiKeepAwake;

/// <summary>ai-keep-awake: keep a Windows laptop awake while an AI agent (Claude Code) is working, and let it
/// sleep, and lock, as normal otherwise.</summary>
public static partial class Cli
{
    public const string Prog = "ai-keep-awake";

    public const string Help = """
        usage: ai-keep-awake [-h] [--version] [--config CONFIG] {config,install,uninstall,status,watch,autostart,hook} ...

        Keep a Windows laptop awake while an AI agent (Claude Code) is working, and let it sleep otherwise.

            ai-keep-awake install            copy the programs to %LOCALAPPDATA%\Programs\ai-keep-awake, put the hooks
                                             in ~/.claude/settings.json and start the watcher at logon
                --dry-run                        show the copy, the change to settings.json and the task; change nothing
                --settings PATH                  another Claude Code settings file
                --install-dir PATH               another folder for the programs
                --no-autostart                   only the programs and the hooks
            ai-keep-awake uninstall          take out exactly this tool's hooks, and remove the watcher's task
                --dry-run                        show the change to settings.json and the task; change nothing
                --settings PATH                  another Claude Code settings file
                --no-autostart                   leave the watcher's task alone
            ai-keep-awake status             hooks, watcher, leases, the power request, and the lock point from Windows
            ai-keep-awake config             create the settings file (if missing) and say where it is
            ai-keep-awake watch              the watcher itself (what autostart runs)
            ai-keep-awake autostart on|off|status
            ai-keep-awake hook busy|stop|idle|end    what the hooks run (reads the hook's JSON on stdin)

        options:
          -h, --help       show this help message and exit
          --version        show program's version number and exit
          --config CONFIG  settings file (default %APPDATA%\ai-keep-awake\config.toml)
        """;

    private const string Usage =
        "usage: ai-keep-awake [-h] [--version] [--config CONFIG] {config,install,uninstall,status,watch,autostart,hook} ...";

    private static readonly string[] Commands = ["config", "install", "uninstall", "status", "watch", "autostart", "hook"];

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private sealed class Options
    {
        public string? Config;
        public string Command = "";
        public string? Settings;
        public string? InstallDir;
        public bool DryRun;
        public bool NoAutostart;
        public string Action = "";
    }

    private sealed class UsageError(string message) : Exception(message);

    /// <summary>Run a command. The exit code: 0 done, 1 failed, 2 bad arguments or settings, 3 a second watcher.</summary>
    public static int Run(IReadOnlyList<string> argv, CliHost? host = null)
    {
        host ??= new CliHost();
        Options options;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;   // --help or --version
            options = parsed;
        }
        catch (UsageError error)
        {
            Print(host.Err, Usage);
            Print(host.Err, $"{Prog}: error: {error.Message}");
            return 2;
        }

        if (options.Command == "hook")   // never fails, never prints, never reads the settings file
            return Hook.Main(argv.SkipWhile(arg => arg != "hook").Skip(1).ToList(), host.Stdin?.Invoke(), host.Env);
        if (options.Command == "config")
        {
            Print(host.Out, options.Config ?? AiKeepAwake.Config.CreateDefault(host.Env));
            return 0;
        }
        Config config;
        try
        {
            config = AiKeepAwake.Config.Load(options.Config, host.Env);
        }
        catch (Exception error) when (error is ConfigError or IOException or UnauthorizedAccessException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        if (options.InstallDir is not null)
            config.InstallDir = PyPath.Resolve(PyPath.ExpandUser(options.InstallDir, host.Env));

        return options.Command switch
        {
            "watch" => Watcher.Run(config, WinBackground.Logger.Create(config.LogFile, host.LogEcho), host.Notify, host.System()),
            "install" => InstallCommand(options, config, host),
            "uninstall" => UninstallCommand(options, config, host),
            "autostart" => AutostartCommand(options.Action, config, host),
            _ => Status(config, host),
        };
    }

    private static Options? Parse(IReadOnlyList<string> argv, CliHost host)
    {
        var options = new Options();
        var i = 0;
        for (; i < argv.Count && argv[i].StartsWith('-'); i++)
        {
            var arg = argv[i];
            if (arg is "-h" or "--help")
            {
                Print(host.Out, Help);
                return null;
            }
            if (arg == "--version")
            {
                Print(host.Out, Version);
                return null;
            }
            if (arg == "--config")
            {
                if (++i >= argv.Count)
                    throw new UsageError("argument --config: expected one argument");
                options.Config = argv[i];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                options.Config = arg["--config=".Length..];
            else
                throw new UsageError($"unrecognized arguments: {arg}");
        }
        if (i >= argv.Count)
            throw new UsageError("the following arguments are required: command");
        options.Command = argv[i++];
        if (!Commands.Contains(options.Command))
            throw new UsageError($"argument command: invalid choice: '{options.Command}' (choose from {string.Join(", ", Commands)})");
        if (options.Command == "hook")
            return options;

        var rest = argv.Skip(i).ToList();
        var extra = new List<string>();
        for (var j = 0; j < rest.Count; j++)
        {
            var arg = rest[j];
            if (arg is "-h" or "--help")
            {
                Print(host.Out, Help);
                return null;
            }
            if (options.Command is "install" or "uninstall")
            {
                switch (arg)
                {
                    case "--dry-run": options.DryRun = true; continue;
                    case "--no-autostart": options.NoAutostart = true; continue;
                    case "--settings" or "--install-dir":
                        if (++j >= rest.Count)
                            throw new UsageError($"argument {arg}: expected one argument");
                        if (arg == "--settings")
                            options.Settings = rest[j];
                        else
                            options.InstallDir = rest[j];
                        continue;
                }
                if (arg.StartsWith("--settings=", StringComparison.Ordinal))
                {
                    options.Settings = arg["--settings=".Length..];
                    continue;
                }
                if (arg.StartsWith("--install-dir=", StringComparison.Ordinal))
                {
                    options.InstallDir = arg["--install-dir=".Length..];
                    continue;
                }
            }
            if (options.Command == "autostart" && options.Action.Length == 0 && !arg.StartsWith('-'))
            {
                if (arg is not ("on" or "off" or "status"))
                    throw new UsageError($"argument action: invalid choice: '{arg}' (choose from on, off, status)");
                options.Action = arg;
                continue;
            }
            extra.Add(arg);
        }
        if (options.Command == "autostart" && options.Action.Length == 0)
            throw new UsageError("the following arguments are required: action");
        if (extra.Count > 0)
            throw new UsageError($"unrecognized arguments: {string.Join(' ', extra)}");
        return options;
    }

    /// <summary>A line as Python's print() wrote it on Windows.</summary>
    internal static void Print(TextWriter writer, string text) => writer.Write(Py.Lines(text + "\n"));

    private static string SettingsPath(Options options, Config config, CliHost host) =>
        options.Settings is not null ? PyPath.Resolve(PyPath.ExpandUser(options.Settings, host.Env)) : config.ClaudeSettings;
}
