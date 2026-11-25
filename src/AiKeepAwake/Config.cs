using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace AiKeepAwake;

/// <summary>A settings file that can't be used (Python's ValueError).</summary>
public sealed class ConfigError(string message) : Exception(message);

/// <summary>The settings file. Every setting has a default, so no file is needed.
///
///     reason = "An AI agent is working (Claude Code)"   # what `powercfg /requests` shows while the laptop is kept awake
///     lease_minutes = 15                      # how long one prompt or tool call keeps it awake
///     background_lease_minutes = 45           # after a turn ends with background work still running
///     unattended_hours = 3                    # stop keeping it awake after this long with no keyboard or mouse
///     mains_only = true                       # only on mains power (Windows ends the request on battery anyway)
///     lock = "windows"                        # "windows": lock where Windows would have locked; "never"; or minutes
///     check_seconds = 15                      # how often the watcher looks
///     check_pid = true                        # ignore a lease whose Claude Code process has gone
///     leases_dir = "%LOCALAPPDATA%/ai-keep-awake/leases"
///     log_file = "%LOCALAPPDATA%/ai-keep-awake/ai-keep-awake.log"
///     state_file = "%LOCALAPPDATA%/ai-keep-awake/state.json"
///     claude_settings = "~/.claude/settings.json"   # where `install` puts the hooks
///     install_dir = "%LOCALAPPDATA%/Programs/ai-keep-awake"   # where `install` copies the programs
///     start_menu = true                       # `install` adds it to the Start menu and Settings > Apps
///
/// Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file. After changing the lease
/// lengths, leases_dir or install_dir, run `ai-keep-awake install` again: the hooks carry them.</summary>
public sealed class Config
{
    public const string DefaultReason = "An AI agent is working (Claude Code)";
    public const string EnvConfig = "AI_KEEP_AWAKE_CONFIG";

    public const string Template = """
        # ai-keep-awake settings. Every line is optional; these are the defaults. See the README.
        # reason = "An AI agent is working (Claude Code)"
        # lease_minutes = 15
        # background_lease_minutes = 45
        # unattended_hours = 3
        # mains_only = true
        # lock = "windows"            # or "never", or a number of minutes
        # check_seconds = 15
        # check_pid = true
        # leases_dir = "%LOCALAPPDATA%/ai-keep-awake/leases"
        # log_file = "%LOCALAPPDATA%/ai-keep-awake/ai-keep-awake.log"
        # state_file = "%LOCALAPPDATA%/ai-keep-awake/state.json"
        # claude_settings = "~/.claude/settings.json"
        # install_dir = "%LOCALAPPDATA%/Programs/ai-keep-awake"
        # start_menu = true           # add it to the Start menu and Settings > Apps

        """;

    private static readonly string[] Known =
    [
        "reason", "lease_minutes", "background_lease_minutes", "unattended_hours", "mains_only", "lock", "check_seconds",
        "check_pid", "leases_dir", "log_file", "state_file", "claude_settings", "install_dir", "start_menu",
    ];

    /// <summary>The settings with every default, for a settings file at path (which needn't exist).</summary>
    public Config(string path, Func<string, string?>? env = null)
    {
        env ??= Environment;
        Env = env;
        Path = path;
        var local = LocalFolder(env);
        LeasesDir = PyPath.Pure(System.IO.Path.Combine(local, "leases"));
        LogFile = PyPath.Pure(System.IO.Path.Combine(local, "ai-keep-awake.log"));
        StateFile = PyPath.Pure(System.IO.Path.Combine(local, "state.json"));
        ClaudeSettings = PyPath.Pure(System.IO.Path.Combine(PyPath.Home(env), ".claude", "settings.json"));
        InstallDir = DefaultInstallDir(env);
    }

    public string Path { get; }

    /// <summary>The environment the defaults came from (the real one, or a test's).</summary>
    public Func<string, string?> Env { get; }
    public string Reason { get; set; } = DefaultReason;
    public double LeaseMinutes { get; set; } = 15;
    public double BackgroundLeaseMinutes { get; set; } = 45;
    public double UnattendedHours { get; set; } = 3;
    public bool MainsOnly { get; set; } = true;

    /// <summary>"windows", "never" (any case, spaces allowed), or a number of minutes (a long or a double, as the
    /// TOML file wrote it).</summary>
    public object Lock { get; set; } = "windows";

    public double CheckSeconds { get; set; } = 15;
    public bool CheckPid { get; set; } = true;

    /// <summary>Whether install adds it to the Start menu and Settings > Apps (false: a copy for testing, or portable).</summary>
    public bool StartMenu { get; set; } = true;
    public string LeasesDir { get; set; }
    public string LogFile { get; set; }
    public string StateFile { get; set; }
    public string ClaudeSettings { get; set; }
    public string InstallDir { get; set; }

    public int LeaseSeconds => (int)(LeaseMinutes * 60);
    public int BackgroundLeaseSeconds => (int)(BackgroundLeaseMinutes * 60);
    public double UnattendedSeconds => UnattendedHours * 3600;

    /// <summary>The real environment; an empty name is no variable.</summary>
    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    private static string LocalFolder(Func<string, string?> env)
    {
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "ai-keep-awake"));
    }

    public static string DefaultInstallDir(Func<string, string?> env)
    {
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "Programs", "ai-keep-awake"));
    }

    /// <summary>%APPDATA%\ai-keep-awake\config.toml.</summary>
    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "ai-keep-awake", "config.toml"));
    }

    /// <summary>The settings, from the file if there is one, else the defaults. Throws ConfigError.</summary>
    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        var config = new Config(full, env);
        if (!File.Exists(full))
            return config;
        TomlTable data;
        try
        {
            data = TomlSerializer.Deserialize(File.ReadAllText(full, new UTF8Encoding(false, true)), TomlContext.Default.TomlTable)
                   ?? new TomlTable();
        }
        catch (Exception error) when (error is not IOException and not UnauthorizedAccessException)
        {
            throw new ConfigError($"{full}: {error.Message}");
        }

        var unknown = data.Keys.Where(key => !Known.Contains(key)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new ConfigError($"{full}: unknown setting(s) {string.Join(", ", unknown)}.");
        var folder = System.IO.Path.GetDirectoryName(full)!;
        foreach (var (name, value) in data)
        {
            switch (name)
            {
                case "leases_dir": config.LeasesDir = PathSetting(full, folder, name, value, env); break;
                case "log_file": config.LogFile = PathSetting(full, folder, name, value, env); break;
                case "state_file": config.StateFile = PathSetting(full, folder, name, value, env); break;
                case "claude_settings": config.ClaudeSettings = PathSetting(full, folder, name, value, env); break;
                case "install_dir": config.InstallDir = PathSetting(full, folder, name, value, env); break;
                case "reason":
                    config.Reason = value as string ?? throw new ConfigError($"{full}: reason must be text.");
                    break;
                case "mains_only":
                    config.MainsOnly = value as bool? ?? throw new ConfigError($"{full}: mains_only must be true or false.");
                    break;
                case "check_pid":
                    config.CheckPid = value as bool? ?? throw new ConfigError($"{full}: check_pid must be true or false.");
                    break;
                case "start_menu":
                    config.StartMenu = value as bool? ?? throw new ConfigError($"{full}: start_menu must be true or false.");
                    break;
                case "lock":
                    config.Lock = value;
                    break;
            }
        }

        if (config.Lock is string text && text.Trim().ToLowerInvariant() is not ("windows" or "never"))
            throw new ConfigError($"{full}: lock must be \"windows\", \"never\" or a number of minutes, not {Py.ReprString(text)}.");
        if (config.Lock is not string && !(Number(config.Lock) is { } minutes && config.Lock is not bool && minutes > 0))
            throw new ConfigError($"{full}: lock must be \"windows\", \"never\" or a number of minutes above 0.");
        foreach (var name in new[] { "lease_minutes", "background_lease_minutes", "unattended_hours", "check_seconds" })
        {
            if (!data.TryGetValue(name, out var value))
                continue;
            if (Number(value) is not { } number || number <= 0)
                throw new ConfigError($"{full}: {name} must be a number above 0.");
            switch (name)
            {
                case "lease_minutes": config.LeaseMinutes = number; break;
                case "background_lease_minutes": config.BackgroundLeaseMinutes = number; break;
                case "unattended_hours": config.UnattendedHours = number; break;
                case "check_seconds": config.CheckSeconds = number; break;
            }
        }
        return config;
    }

    /// <summary>A TOML number as Python saw it (a bool counts, as True is 1 in Python); null if it isn't one.</summary>
    internal static double? Number(object? value) => value switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1 : 0,
        _ => null,
    };

    private static string PathSetting(string file, string folder, string name, object value, Func<string, string?> env)
    {
        if (value is not string text)
            throw new ConfigError($"{file}: {name} must be text (a path).");
        var expanded = PyPath.ExpandVars(PyPath.ExpandUser(text, env), env);
        return PyPath.IsAbsolute(expanded) ? PyPath.Pure(expanded) : PyPath.ResolveAgainst(folder, expanded);
    }

    /// <summary>The lock setting as Python's str() printed it ("10", "1.5", "windows").</summary>
    public string LockText => Lock switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Py.Repr(d),
        bool b => b ? "True" : "False",
        _ => Lock.ToString() ?? "",
    };

    /// <summary>Create the settings file at the default place if it's missing, and return where it is.</summary>
    public static string CreateDefault(Func<string, string?>? env = null)
    {
        var path = DefaultPath(env);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Template.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));   // as Python's text mode wrote it
        }
        return path;
    }
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;
