using System.Reflection;
using System.Text.Json.Nodes;
using WinBackground;

namespace AiKeepAwake.Tests;

/// <summary>A temporary folder per test, with a fake environment that points every default (APPDATA,
/// LOCALAPPDATA, the home folder) into it, so no test can reach the real settings, leases or programs.</summary>
public abstract class Temp : IDisposable
{
    public const double Now = 1_800_000_000.0;

    protected Temp()
    {
        Folder = Path.Combine(Path.GetTempPath(), "ai-keep-awake-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        LeasesDir = Path.Combine(Folder, "leases");
        EnvVars = new(StringComparer.OrdinalIgnoreCase)
        {
            ["APPDATA"] = Path.Combine(Folder, "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(Folder, "Local"),
            ["USERPROFILE"] = Path.Combine(Folder, "home"),
            ["USERNAME"] = "tester",
        };
    }

    protected string Folder { get; }
    protected string LeasesDir { get; }
    protected Dictionary<string, string> EnvVars { get; }
    protected string? Env(string name) => EnvVars.GetValueOrDefault(name);

    /// <summary>A lease file as parsed JSON, or null if there isn't one.</summary>
    protected JsonObject? LeaseFile(string session = "s1")
    {
        var path = Path.Combine(LeasesDir, $"{session}.json");
        return File.Exists(path) ? (JsonObject)JsonNode.Parse(File.ReadAllText(path))! : null;
    }

    protected Config NewConfig(string name = "config.toml") => new(Path.Combine(Folder, name), Env);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    public static string BuiltExe(string name) =>
        typeof(Temp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == name).Value!;
}

public sealed class FakeRequest : IPowerRequest
{
    public bool Held { get; private set; }
    public List<bool> History { get; } = [];

    public void Hold(bool on = true)
    {
        if (on != Held)
        {
            Held = on;
            History.Add(on);
        }
    }

    public void Close() => Hold(false);
}

public sealed class FakeSystem : ISystem
{
    public bool? Mains { get; set; } = true;
    public double Idle { get; set; }
    public Func<double>? IdleFunc { get; set; }
    public uint State { get; set; }
    public LockSettings Settings { get; set; } = Fake.Windows();
    public int Locks { get; private set; }
    public FakeRequest Req { get; } = new();

    public bool? OnMains() => Mains;
    public double IdleSeconds() => IdleFunc is null ? Idle : IdleFunc();
    public uint ExecutionState() => State | (Req.Held ? 1u : 0u);
    public LockSettings LockSettings() => Settings;

    public bool Lock()
    {
        Locks++;
        return true;
    }

    public IPowerRequest Request(string reason) => Req;
}

/// <summary>A Task Scheduler task that records what was asked of it.</summary>
public sealed class FakeTask(string name, FakeTasks all) : ITask
{
    public string Name { get; } = name;
    public bool Exists { get; set; }

    /// <summary>The calls ("enable", "disable", "stop", "start", "status") that throw, as a failing PowerShell would.</summary>
    public HashSet<string> FailOn { get; } = [];

    private void Call(string what)
    {
        all.Calls.Add($"{what} {Name}");
        if (FailOn.Contains(what))
            throw new InvalidOperationException($"Couldn't {what} the task: PowerShell said no");
    }

    public (string Exe, IReadOnlyList<string> Args, string? WorkingDir)? Enabled { get; private set; }

    /// <summary>The command Status reports (else the one Enable registered, if any).</summary>
    public string? Command { get; set; }

    public string Enable(string exe, IReadOnlyList<string> args, string? workingDir)
    {
        Call("enable");
        (Exists, Enabled) = (true, (exe, args, workingDir));
        return $"{exe} {Shell.ListToCommandLine(args)}";
    }

    public bool Disable()
    {
        Call("disable");
        var was = Exists;
        Exists = false;
        return was;
    }

    public bool Stop()
    {
        Call("stop");
        return Exists;
    }

    public bool Start()
    {
        Call("start");
        return Exists;
    }

    public IReadOnlyDictionary<string, string>? Status()
    {
        if (FailOn.Contains("status"))
            throw new InvalidOperationException("Couldn't read the task: PowerShell said no");
        if (!Exists)
            return null;
        var info = new Dictionary<string, string> { ["state"] = "Running", ["last_run"] = "today", ["last_result"] = "running" };
        var command = Command ?? (Enabled is { } e ? $"{e.Exe} {Shell.ListToCommandLine(e.Args)}" : null);
        if (command is not null)
            info["command"] = command;
        return info;
    }
}

public sealed class FakeTasks
{
    private readonly Dictionary<string, FakeTask> tasks = [];
    public List<string> Calls { get; } = [];

    public FakeTask this[string name] => tasks.TryGetValue(name, out var task) ? task : tasks[name] = new FakeTask(name, this);
}

public static class Fake
{
    public static Screensaver Saver(bool active = true, bool secure = true, long timeout = 1800, string? program = "scrnsave.scr",
        bool policy = false) => new(active, secure, timeout, program, policy);

    public static LockSettings Windows(long? sleep = 300, long? display = 300, long? signIn = 0, Screensaver? screensaver = null,
        long? inactivity = null) =>
        new(true, sleep, "test", display, signIn, "test", screensaver ?? Saver(), inactivity);
}

/// <summary>The Run key, Settings > Apps, the Start menu and the tray, as a test sees them: nothing real is touched.</summary>
public sealed class FakeDesktop : IDesktop
{
    public string? Run { get; set; }
    public AppsEntry? Apps { get; set; }
    public string? AppsLocation { get; set; }
    public (string Target, string Args)? Shortcut { get; set; }
    public HashSet<string> TraysRunning { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];
    public bool FromExplorer { get; set; }
    public string? Version { get; set; }

    public string? RunEntry() => Run;
    public void SetRunEntry(string command) { Calls.Add("run on"); Run = command; }
    public bool RemoveRunEntry() { Calls.Add("run off"); var was = Run is not null; Run = null; return was; }

    public string? AppsEntryLocation() => Apps?.InstallLocation ?? AppsLocation;
    public void SetAppsEntry(AppsEntry entry) { Calls.Add("apps on"); Apps = entry; }
    public bool RemoveAppsEntry() { Calls.Add("apps off"); var was = Apps is not null; Apps = null; return was; }

    public string ShortcutPath => @"C:\fake\Start Menu\Programs\ai-keep-awake.lnk";
    public void WriteShortcut(string target, string args, string workingDir, string description, string icon)
    {
        Calls.Add("shortcut on");
        Shortcut = (target, args);
    }
    public bool RemoveShortcut() { Calls.Add("shortcut off"); var was = Shortcut is not null; Shortcut = null; return was; }

    public bool TrayRunning(string installDir) => TraysRunning.Contains(PyPath.Pure(installDir));
    public bool StopTray(string installDir)
    {
        Calls.Add("tray stop");
        return TraysRunning.Remove(PyPath.Pure(installDir));
    }

    public int Start(string exe, IReadOnlyList<string> args, string? workingDir)
    {
        Calls.Add($"start {Path.GetFileName(exe)}");
        if (Path.GetFileName(exe) == Install.TrayExe)
            TraysRunning.Add(PyPath.Pure(Path.GetDirectoryName(exe)!));
        return 4242;
    }

    public void Warm(string exe) => Calls.Add($"warm {Path.GetFileName(exe)}");
    public string? VersionOf(string exe) => Version;
    public bool StartedFromExplorer() => FromExplorer;
    public void RemoveLater(string installDir) => Calls.Add("remove later");
}
