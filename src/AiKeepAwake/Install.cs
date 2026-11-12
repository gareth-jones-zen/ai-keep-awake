using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiKeepAwake;

/// <summary>Claude Code's settings file can't be changed safely.</summary>
public sealed class SettingsError(string message) : Exception(message);

/// <summary>What a change to the settings file did, or would do.</summary>
public sealed record ChangeReport(bool Changed, string Diff, string? Backup);

/// <summary>Putting the hooks into Claude Code's user settings, and taking them out again.
///
/// Only this tool's own hook entries are ever added or removed: each is a command hook that runs one of its
/// programs with "hook" as the first argument (or the Python claude-keep-awake's hook.py, which it replaces), so
/// they're found by that shape, and nothing else in the file is touched. Each gets a matcher group of its own rather than joining someone
/// else's. The file is backed up before every change, written whole and replaced in one step, and left alone
/// if it isn't valid JSON.
///
/// The hooks run the installed ai-keep-awake-background.exe directly (exec form, `args`, no shell): it has
/// no window, and it never reads the settings file, so it starts in a few milliseconds.</summary>
public static class Install
{
    public const string ConsoleExe = "ai-keep-awake.exe";
    public const string BackgroundExe = "ai-keep-awake-background.exe";
    /// <summary>Seconds Claude Code gives a hook before killing it. A hook call takes about 40 ms, but the first
    /// run of a newly installed exe waits for Defender's scan (about 6 seconds was measured in the Phase 4 parity
    /// run), and a killed hook shows an error and writes no lease. So well over that.</summary>
    public const int Timeout = 15;

    /// <summary>The script the Python claude-keep-awake's hooks ran (pythonw.exe -I -S ...\claude_keep_awake\hook.py
    /// action), which install replaces and uninstall removes.</summary>
    public const string OldScript = @"claude_keep_awake\hook.py";

    /// <summary>(event, matcher, action).</summary>
    public static readonly (string Event, string? Matcher, string Action)[] Events =
    [
        ("UserPromptSubmit", null, "busy"),
        ("PreToolUse", null, "busy"),
        ("SubagentStart", null, "busy"),
        ("Stop", null, "stop"),
        ("StopFailure", null, "idle"),
        ("Notification", "idle_prompt", "idle"),
        ("SessionEnd", null, "end"),
    ];

    public static string HookExe(string installDir) => Path.Combine(installDir, BackgroundExe);

    public static JsonObject Handler(Config config, string action, string? exe = null)
    {
        var args = new List<string> { "hook", action };
        if (!PyPath.Same(config.LeasesDir, Hook.DefaultDir(config.Env)))
            args.AddRange(["--dir", config.LeasesDir]);
        if (config.LeaseSeconds != Hook.Lease)
            args.AddRange(["--lease", config.LeaseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (config.BackgroundLeaseSeconds != Hook.BgLease)
            args.AddRange(["--bg-lease", config.BackgroundLeaseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var array = new JsonArray();
        foreach (var arg in args)
            array.Add((JsonNode)arg);
        return new JsonObject
        {
            ["type"] = "command",
            ["command"] = exe ?? HookExe(config.InstallDir),
            ["args"] = array,
            ["timeout"] = PyJson.Int(Timeout),
        };
    }

    /// <summary>Whether a hook handler is one install put there: this tool's, or the Python tool's it replaces.</summary>
    public static bool IsOurs(JsonNode? item) => IsNew(item) || IsOld(item);

    /// <summary>This tool's: the command one of its two programs (by file name, in any folder) and the first
    /// argument "hook". Matched by that shape rather than by the name appearing somewhere, so someone else's hook
    /// that merely mentions ai-keep-awake (a script in a repo of that name, a notifier's title) is left alone.</summary>
    public static bool IsNew(JsonNode? item)
    {
        if (item is not JsonObject handler || Text(handler["command"]) is not { } command)
            return false;
        var program = Path.GetFileName(command.Trim().Trim('"'));
        return (program.Equals(ConsoleExe, StringComparison.OrdinalIgnoreCase) || program.Equals(BackgroundExe, StringComparison.OrdinalIgnoreCase))
               && handler["args"] is JsonArray { Count: > 0 } args && Text(args[0]) == "hook";
    }

    /// <summary>The Python claude-keep-awake's: an argument that is its hook.py (either slash, any case).</summary>
    public static bool IsOld(JsonNode? item) =>
        item is JsonObject handler && handler["args"] is JsonArray args
        && args.Any(arg => Text(arg) is { } text && text.Replace('/', '\\').EndsWith(OldScript, StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>A copy with this tool's hook entries removed, and how many there were. Empty groups and events
    /// that only held ours go too; anything else stays exactly as it was.</summary>
    public static (JsonObject Settings, int Removed) WithoutOurs(JsonObject settings)
    {
        var result = (JsonObject)settings.DeepClone();
        if (!result.TryGetPropertyValue("hooks", out var hooksNode) || hooksNode is not JsonObject hooks)
            return (result, 0);
        var removed = 0;
        foreach (var hookEvent in hooks.Select(pair => pair.Key).ToList())
        {
            if (hooks[hookEvent] is not JsonArray groups)
                continue;
            var keptGroups = new List<JsonNode?>();
            foreach (var groupNode in groups)
            {
                var group = groupNode;
                if (group is JsonObject obj && obj["hooks"] is JsonArray handlers)
                {
                    var kept = handlers.Where(h => !IsOurs(h)).ToList();
                    var taken = handlers.Count - kept.Count;
                    removed += taken;
                    if (taken > 0 && kept.Count == 0)
                        continue;   // the group was only ours
                    if (taken > 0)
                    {
                        var copy = (JsonObject)obj.DeepClone();
                        var keptArray = new JsonArray();
                        foreach (var h in kept)
                            keptArray.Add(h?.DeepClone());
                        copy["hooks"] = keptArray;
                        group = copy;
                    }
                }
                keptGroups.Add(group);
            }
            if (keptGroups.Count > 0)
            {
                var array = new JsonArray();
                foreach (var g in keptGroups)
                    array.Add(g?.DeepClone());
                hooks[hookEvent] = array;
            }
            else if (groups.Count > 0)
                hooks.Remove(hookEvent);
        }
        if (hooks.Count == 0 && removed > 0)
            result.Remove("hooks");
        return (result, removed);
    }

    /// <summary>A copy with this tool's hooks in place (any earlier copy of them, or of the Python tool's, replaced).</summary>
    public static JsonObject WithOurs(JsonObject settings, Config config, string? exe = null)
    {
        var (result, _) = WithoutOurs(settings);
        if (!result.TryGetPropertyValue("hooks", out var hooksNode))
            result["hooks"] = hooksNode = new JsonObject();
        if (hooksNode is not JsonObject hooks)
            throw new SettingsError("\"hooks\" in the settings file isn't an object; not changing it.");
        foreach (var (hookEvent, matcher, action) in Events)
        {
            var group = new JsonObject();
            if (!string.IsNullOrEmpty(matcher))
                group["matcher"] = matcher;
            group["hooks"] = new JsonArray(Handler(config, action, exe));
            if (!hooks.TryGetPropertyValue(hookEvent, out var existing))
                hooks[hookEvent] = existing = new JsonArray();
            if (existing is not JsonArray list)
                throw new SettingsError($"\"hooks.{hookEvent}\" in the settings file isn't a list; not changing it.");
            list.Add((JsonNode)group);
        }
        return result;
    }

    /// <summary>(settings, original text). A missing file is an empty one; a file that isn't a JSON object is
    /// refused. The text is read as Python read it: a byte order mark dropped, line endings made "\n".</summary>
    public static (JsonObject Settings, string Text) Read(string path)
    {
        if (!File.Exists(path))
            return ([], "");
        string text;
        try
        {
            text = PyJson.ReadStrict(path);
        }
        catch (DecoderFallbackException error)
        {
            throw new SettingsError($"{path} isn't valid UTF-8 ({error.Message}). Fix it first; nothing was changed.");
        }
        if (text.StartsWith('﻿'))
            text = text[1..];
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(text))
            return ([], text);
        JsonNode? data;
        try
        {
            data = PyJson.Parse(text);
        }
        catch (JsonException error)
        {
            throw new SettingsError($"{path} isn't valid JSON ({error.Message}). Fix it first; nothing was changed.");
        }
        if (data is not JsonObject settings)
            throw new SettingsError($"{path} isn't a JSON object. Nothing was changed.");
        return (settings, text);
    }

    /// <summary>As Claude Code writes it: 2-space indent, non-ASCII kept, a final newline.</summary>
    public static string Render(JsonObject settings) => PyJson.Dump(settings, 2, ensureAscii: false) + "\n";

    public static string Diff(string path, string before, string after) =>
        UnifiedDiff.Of(Py.SplitLinesKeepEnds(before), Py.SplitLinesKeepEnds(after), $"{path} (now)", $"{path} (after)");

    /// <summary>Apply transform(settings) to the file. A backup is made first, named
    /// settings.json.ai-keep-awake-yyyyMMdd-HHmmss.bak.</summary>
    public static ChangeReport Change(string path, Func<JsonObject, JsonObject> transform, bool dryRun = false, Func<DateTime>? clock = null)
    {
        var (settings, before) = Read(path);
        var afterSettings = transform(settings);
        if (PyJson.Equal(afterSettings, settings) && before.Length > 0)
            return new ChangeReport(false, "", null);
        var after = Render(afterSettings);
        var diff = Diff(path, before, after);
        if (dryRun)
            return new ChangeReport(true, diff, null);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(path);
        string? backup = null;
        if (File.Exists(path))
        {
            backup = Path.Combine(folder, $"{name}.ai-keep-awake-{(clock ?? (() => DateTime.Now))():yyyyMMdd-HHmmss}.bak");
            File.WriteAllBytes(backup, File.ReadAllBytes(path));
        }
        var temp = Path.Combine(folder, $"{name}.ai-keep-awake.tmp");
        File.WriteAllText(temp, after.Replace("\n", "\r\n"), new UTF8Encoding(false));   // as Python's text mode wrote it on Windows
        File.Move(temp, path, overwrite: true);
        return new ChangeReport(true, diff, backup);
    }

    /// <summary>The events that have this tool's hook (old: the Python tool's), from the settings file.</summary>
    public static List<string> Installed(string path, bool old = false)
    {
        Func<JsonNode?, bool> match = old ? IsOld : IsNew;
        JsonObject settings;
        try
        {
            (settings, _) = Read(path);
        }
        catch (Exception error) when (error is SettingsError or IOException or UnauthorizedAccessException)
        {
            return [];
        }
        if (settings["hooks"] is not JsonObject hooks)
            return [];
        return hooks.Where(pair => pair.Value is JsonArray groups && groups.Any(g =>
                g is JsonObject group && group["hooks"] is JsonArray handlers && handlers.Any(match)))
            .Select(pair => pair.Key).ToList();
    }
}
