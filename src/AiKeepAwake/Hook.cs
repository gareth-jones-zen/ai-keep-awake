using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinBackground;

namespace AiKeepAwake;

/// <summary>The Claude Code hook: writes or clears this session's lease. Claude Code runs it on every prompt and
/// tool call, as `ai-keep-awake-background.exe hook busy|stop|idle|end [--dir FOLDER] [--lease SECONDS]
/// [--bg-lease SECONDS]`.
///
/// It reads the hook's JSON from stdin and keeps one small file per session, {"until": epoch, "bg": bool, ...},
/// in FOLDER (default %LOCALAPPDATA%\ai-keep-awake\leases). The watcher keeps the laptop awake while any file's
/// "until" is in the future.
///
///     busy   UserPromptSubmit, PreToolUse, SubagentStart: a lease of --lease seconds (15 minutes). Skipped if
///            renewed under a minute ago, so a burst of tool calls costs one write.
///     stop   Stop: if background_tasks has anything still running, a background lease of --bg-lease seconds
///            (45 minutes); otherwise the turn is over, so the lease goes.
///     idle   Notification idle_prompt and StopFailure: the lease goes, unless it's a background one (idle_prompt
///            fires while a background shell or subagent is still working).
///     end    SessionEnd: the lease goes.
///
/// Rules for a hook, all kept here: it always exits 0, prints nothing (Claude Code reads a hook's stdout as
/// context or a decision), never waits on anything, and never reads the settings file (so it starts fast).
/// AI_KEEP_AWAKE=0 turns it off.</summary>
public static class Hook
{
    public const int Lease = 15 * 60, BgLease = 45 * 60, RenewAfter = 60;
    public const string Agent = "claude-code";
    public const string OffSwitch = "AI_KEEP_AWAKE";

    private static readonly HashSet<string> Done =
    [
        "completed", "complete", "done", "finished", "failed", "error", "killed", "stopped", "cancelled", "canceled",
        "exited", "succeeded",
    ];

    public static string DefaultDir(Func<string, string?> env)
    {
        var local = env("LOCALAPPDATA");
        var root = !string.IsNullOrEmpty(local)
            ? local
            : Path.Combine(PyPath.ExpandUser("~", env), ".local", "state");
        return Path.Combine(root, "ai-keep-awake", "leases");
    }

    /// <summary>(action, folder, lease, bgLease) from the arguments, with the defaults for anything missing.</summary>
    public static (string Action, string? Folder, long Lease, long BgLease) Parse(IReadOnlyList<string> argv)
    {
        var action = argv.Count > 0 ? argv[0] : "";
        string? folder = null;
        long lease = Lease, bgLease = BgLease;
        for (var i = 1; i + 1 < argv.Count; i += 2)
        {
            var (name, value) = (argv[i], argv[i + 1]);
            if (name == "--dir")
                folder = value;
            else if (name == "--lease")
                lease = Py.Int(JsonValue.Create(value));
            else if (name == "--bg-lease")
                bgLease = Py.Int(JsonValue.Create(value));
        }
        return (action, folder, lease, bgLease);
    }

    /// <summary>Whether Stop's background_tasks still has anything working. The docs show only "running", so
    /// anything not plainly finished counts.</summary>
    public static bool InFlight(JsonNode? tasks)
    {
        if (tasks is not JsonArray list)
            return false;
        return list.Any(task => task is not JsonObject item
                                || !Done.Contains(item.TryGetPropertyValue("status", out var status)
                                    ? Py.Str(status).ToLowerInvariant()
                                    : "running"));
    }

    public static string SafeName(string session)
    {
        var name = new StringBuilder();
        var count = 0;
        foreach (var rune in session.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune) || rune.Value is '-' or '_')
            {
                if (count++ == 100)
                    break;
                name.Append(rune.ToString());
            }
        }
        return name.Length > 0 ? name.ToString() : "unknown";
    }

    public static double Now() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>Apply one hook event. Returns what it did ("wrote", "kept", "cleared", "skipped", "off"), for tests.</summary>
    public static string Handle(IReadOnlyList<string> argv, JsonObject data, Func<string, string?>? env = null,
        double? now = null, long? ppid = null)
    {
        env ??= Config.Environment;
        if (env(OffSwitch)?.Trim().ToLowerInvariant() is "0" or "off" or "false" or "no")
            return "off";
        var (action, folder, lease, bgLease) = Parse(argv);
        var time = now ?? Now();
        folder ??= DefaultDir(env);
        var session = data.TryGetPropertyValue("session_id", out var id) ? Py.Str(id) : "unknown";
        var path = Path.Combine(folder, SafeName(session) + ".json");
        var old = ReadOld(path);
        old.TryGetPropertyValue("until", out var untilNode);
        var oldUntil = Py.Truthy(untilNode) ? Py.Float(untilNode) : 0;
        var oldBg = old.TryGetPropertyValue("bg", out var bgNode) && Py.Truthy(bgNode) && oldUntil > time;

        double until;
        bool bg;
        if (action == "busy")
        {
            // Activity. A background lease stays a background lease (a background subagent's own tool calls land
            // here too), and never gets shorter.
            if (oldUntil - time > lease - RenewAfter)
                return "skipped";   // renewed under a minute ago, or a longer background lease is running
            (until, bg) = (time + lease, oldBg);
        }
        else if (action == "stop" && InFlight(data["background_tasks"]))
            (until, bg) = (Math.Max(time + bgLease, oldBg ? oldUntil : 0), true);
        else if (action == "idle" && oldBg)
            return "kept";
        else if (action is "stop" or "idle" or "end")
        {
            if (!File.Exists(path))
                return "skipped";
            try
            {
                File.Delete(path);
                return "cleared";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return "skipped";
            }
        }
        else
            return "skipped";

        data.TryGetPropertyValue("cwd", out var cwd);
        data.TryGetPropertyValue("hook_event_name", out var hookEvent);
        var record = new JsonObject
        {
            ["until"] = PyJson.Float(until),
            ["bg"] = bg,
            ["pid"] = PyJson.Int(ppid ?? Processes.ParentId()),
            ["cwd"] = cwd?.DeepClone(),
            ["event"] = hookEvent?.DeepClone(),
            ["at"] = PyJson.Float(time),
            ["agent"] = Agent,
        };
        Directory.CreateDirectory(folder);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, PyJson.Dump(record), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return "wrote";
    }

    private static JsonObject ReadOld(string path)
    {
        try
        {
            var text = PyJson.ReadStrict(path);
            return PyJson.Parse(text) as JsonObject ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                          or DecoderFallbackException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>What both programs run for `hook ...`: never fails, never prints, always 0.</summary>
    public static int Main(IReadOnlyList<string> argv, Stream? stdin = null, Func<string, string?>? env = null)
    {
        try
        {
            using var input = stdin ?? Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var data = PyJson.Parse(text.Length > 0 ? text : "{}") as JsonObject ?? [];
            Handle(argv, data, env);
        }
        catch (Exception)
        {
            // a hook must never fail the session it's watching
        }
        return 0;
    }
}
