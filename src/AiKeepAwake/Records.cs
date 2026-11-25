using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using WinBackground;

namespace AiKeepAwake;

/// <summary>The tray's names, shared by the tray and by install (which stops and starts it).</summary>
public static class Tray
{
    /// <summary>One tray per leases folder, as one watcher.</summary>
    public static string LockName(Config config) => $"ai-keep-awake-tray-{Locks.Key(config.LeasesDir)}";

    /// <summary>Set by install and uninstall to ask the tray running from installDir to close.</summary>
    public static string QuitName(string installDir) => $@"Local\ai-keep-awake-tray-quit-{Locks.Key(PyPath.Pure(installDir))}";

    /// <summary>The window class the tray registers, so a second start can find the first and show its window.</summary>
    public const string WindowClass = "AiKeepAwakeTray";

    public static string Exe(Config config) => Path.Combine(config.InstallDir, Install.TrayExe);

    /// <summary>The tray's arguments: the settings file, so it reads what the watcher reads.</summary>
    public static List<string> Args(Config config) => ["--config", PyPath.Resolve(config.Path)];

    /// <summary>The Run key's command for the tray at logon.</summary>
    public static string RunCommand(Config config) => Shell.ListToCommandLine([Exe(config), .. Args(config)]);

    public static bool Running(Config config) => Locks.Held(LockName(config));
}

/// <summary>install.json: which version was installed, when, by whom ("person", or "right-hand" when the kit
/// installs it with --managed-by right-hand), and whether the person then uninstalled it. Kept by uninstall, so
/// right-hand asks rather than quietly putting back something the person removed.</summary>
public sealed record InstallRecord(string? Version, string? Installed, string ManagedBy, string? InstallDir,
    bool RemovedByPerson = false, string? Removed = null)
{
    public const string Person = "person", RightHand = "right-hand";

    public bool ManagedByRightHand => ManagedBy == RightHand;

    public static string PathFor(Config config) => Path.Combine(StateDir(config), "install.json");

    public static string StateDir(Config config) => Path.GetDirectoryName(Path.GetFullPath(config.StateFile))!;

    public static InstallRecord? Read(Config config)
    {
        try
        {
            if (PyJson.Parse(PyJson.ReadStrict(PathFor(config))) is not JsonObject data)
                return null;
            string? Text(string name) => data[name] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;
            return new InstallRecord(Text("version"), Text("installed"), Text("managed_by") ?? Person, Text("install_dir"),
                Py.Truthy(data["removed_by_person"]), Text("removed"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Write(Config config)
    {
        var path = PathFor(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new JsonObject
        {
            ["version"] = Version,
            ["installed"] = Installed,
            ["managed_by"] = ManagedBy,
            ["install_dir"] = InstallDir,
            ["removed_by_person"] = RemovedByPerson,
            ["removed"] = Removed,
        };
        var temp = path + ".tmp";
        File.WriteAllText(temp, PyJson.Dump(data, 2) + "\n", new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public JsonObject ToJson() => new()
    {
        ["version"] = Version,
        ["installed"] = Installed,
        ["managed_by"] = ManagedBy,
        ["install_dir"] = InstallDir,
        ["removed_by_person"] = RemovedByPerson,
        ["removed"] = Removed,
    };

    public static string Stamp(DateTime when) => when.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>A notification for the tray to show (the watcher's two: the cap, and failing checks). When the tray is
/// running the watcher leaves it in notice.json, and the tray shows it as its own; otherwise the watcher's toast.</summary>
public sealed record Notice(string Title, IReadOnlyList<string> Lines, double At)
{
    public static string PathFor(Config config) => Path.Combine(InstallRecord.StateDir(config), "notice.json");

    public static Notice? Read(Config config)
    {
        try
        {
            if (PyJson.Parse(PyJson.ReadStrict(PathFor(config))) is not JsonObject data)
                return null;
            var lines = data["lines"] is JsonArray array ? array.Select(Py.Str).ToList() : [];
            return new Notice(Py.Str(data["title"]), lines, Py.Float(data["at"]));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Write(Config config)
    {
        var path = PathFor(config);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new JsonArray();
        foreach (var line in Lines)
            lines.Add((JsonNode)line);
        var temp = path + ".tmp";
        File.WriteAllText(temp, PyJson.Dump(new JsonObject { ["title"] = Title, ["lines"] = lines, ["at"] = PyJson.Float(At) }), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The watcher's notify: to the tray if it's running, else the toast. Never throws.</summary>
    public static Action<string, IReadOnlyList<string>> Route(Config config, Action<string, IReadOnlyList<string>> toast,
        Func<bool>? trayRunning = null, Func<double>? clock = null) =>
        (title, lines) =>
        {
            try
            {
                if ((trayRunning ?? (() => Tray.Running(config)))())
                {
                    new Notice(title, lines, (clock ?? Hook.Now)()).Write(config);
                    return;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            toast(title, lines);
        };
}
