using System.Text.Json.Nodes;
using WinBackground;

namespace AiKeepAwake;

/// <summary>One session's lease, as the hook wrote it.</summary>
public sealed record Lease(string Session, double Until, bool Bg, long? Pid, string? Cwd, string? Event, string Path);

/// <summary>Reading the leases the hook writes: one JSON file per Claude Code session.</summary>
public static class Leases
{
    public const double StaleAfter = 24 * 3600;   // an expired lease file is deleted after a day; it's kept that long for `status`

    private static readonly EnumerationOptions Glob = new()
    {
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
    };

    private static IEnumerable<string> Files(string folder, string pattern)
    {
        try
        {
            return Directory.GetFiles(folder, pattern, Glob).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception error) when (error is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static List<Lease> Read(string folder)
    {
        var found = new List<Lease>();
        foreach (var path in Files(folder, "*.json"))
        {
            try
            {
                if (PyJson.Parse(PyJson.ReadStrict(path)) is not JsonObject data)
                    continue;
                data.TryGetPropertyValue("until", out var until);
                var hasUntil = data.ContainsKey("until");
                data.TryGetPropertyValue("bg", out var bg);
                data.TryGetPropertyValue("pid", out var pid);
                data.TryGetPropertyValue("cwd", out var cwd);
                data.TryGetPropertyValue("event", out var hookEvent);
                found.Add(new Lease(
                    System.IO.Path.GetFileNameWithoutExtension(path),
                    hasUntil ? Py.Float(until) : 0,
                    Py.Truthy(bg),
                    Py.Truthy(pid) ? Py.Int(pid) : null,
                    cwd is null ? null : Py.Str(cwd),
                    hookEvent is null ? null : Py.Str(hookEvent),
                    path));
            }
            catch (Exception)
            {
                // unreadable, or not a lease: not one
            }
        }
        return found;
    }

    /// <summary>Leases that haven't expired, and (with checkPid) whose Claude Code process is still running.</summary>
    public static List<Lease> Live(string folder, double? now = null, bool checkPid = true, Func<long, bool>? alive = null)
    {
        var time = now ?? Hook.Now();
        alive ??= PidAlive;
        return Read(folder).Where(lease => lease.Until > time && (!checkPid || lease.Pid is null || alive(lease.Pid.Value))).ToList();
    }

    /// <summary>Delete lease files that expired over a day ago, and leftover temporary files. Returns how many.</summary>
    public static int Tidy(string folder, double? now = null)
    {
        var time = now ?? Hook.Now();
        var gone = 0;
        foreach (var lease in Read(folder))
        {
            if (lease.Until < time - StaleAfter)
            {
                File.Delete(lease.Path);
                gone++;
            }
        }
        foreach (var temp in Files(folder, "*.tmp"))
        {
            try
            {
                var modified = (File.GetLastWriteTimeUtc(temp) - DateTime.UnixEpoch).TotalSeconds;
                if (modified < time - 3600)
                {
                    File.Delete(temp);
                    gone++;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return gone;
    }

    public static int Clear(string folder)
    {
        var gone = 0;
        foreach (var path in Files(folder, "*.json"))
        {
            File.Delete(path);
            gone++;
        }
        return gone;
    }

    /// <summary>Whether a process is running; when in doubt, true.</summary>
    public static bool PidAlive(long pid) => Processes.Alive(pid);
}
