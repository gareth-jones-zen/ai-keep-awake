using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace AiKeepAwake;

/// <summary>The latest release, as GitHub said it when last asked.</summary>
public sealed record ReleaseInfo(string Version, string Url, double Checked);

/// <summary>The tray's daily look at GitHub for a newer release, for a standalone install only: when right-hand
/// manages it, the kit's pins decide the version, so nothing is asked. It only ever says a release exists, with a
/// link; it never downloads or installs anything. A public repo, so no account or token.</summary>
public static class ReleaseCheck
{
    public const string Api = "https://api.github.com/repos/gareth-jones-zen/ai-keep-awake/releases/latest";
    public const double Every = 24 * 3600;

    public static string PathFor(Config config) => Path.Combine(InstallRecord.StateDir(config), "release-check.json");

    /// <summary>Whether to look at all: not when right-hand manages the install.</summary>
    public static bool Applies(InstallRecord? record) => record is null || !record.ManagedByRightHand;

    public static ReleaseInfo? Cached(Config config)
    {
        try
        {
            if (PyJson.Parse(PyJson.ReadStrict(PathFor(config))) is not JsonObject data)
                return null;
            return new ReleaseInfo(Py.Str(data["version"]), Py.Str(data["url"]), Py.Float(data["checked"]));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether a day has passed since the last look (or the clock went back).</summary>
    public static bool Due(ReleaseInfo? cached, double now) => cached is null || now - cached.Checked >= Every || cached.Checked > now + 3600;

    /// <summary>GitHub's latest release, from its JSON (tag_name "v1.3.0", html_url); null if it isn't one.</summary>
    public static ReleaseInfo? Parse(string json, double now)
    {
        try
        {
            if (PyJson.Parse(json) is not JsonObject data || Py.Truthy(data["draft"]) || Py.Truthy(data["prerelease"]))
                return null;
            var tag = data["tag_name"] is JsonValue t && t.GetValueKind() == System.Text.Json.JsonValueKind.String ? t.GetValue<string>() : null;
            var url = data["html_url"] is JsonValue u && u.GetValueKind() == System.Text.Json.JsonValueKind.String ? u.GetValue<string>() : null;
            if (tag is null || url is null || !url.StartsWith("https://github.com/", StringComparison.Ordinal))
                return null;
            var version = tag.TrimStart('v');
            return Numbers(version) is null ? null : new ReleaseInfo(version, url, now);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Look if it's due: ask GitHub (fetch), keep the answer, and return what's known. A failure keeps
    /// the last answer and tries again in a day, quietly.</summary>
    public static async Task<ReleaseInfo?> Run(Config config, Func<CancellationToken, Task<string>> fetch, double now,
        CancellationToken cancel = default)
    {
        var cached = Cached(config);
        if (!Due(cached, now))
            return cached;
        ReleaseInfo? found = null;
        try
        {
            found = Parse(await fetch(cancel).ConfigureAwait(false), now);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
        }
        var keep = found ?? (cached is null ? new ReleaseInfo("", "", now) : cached with { Checked = now });
        try
        {
            var path = PathFor(config);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, PyJson.Dump(new JsonObject
            {
                ["version"] = keep.Version,
                ["url"] = keep.Url,
                ["checked"] = PyJson.Float(keep.Checked),
            }), new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return keep.Version.Length > 0 ? keep : null;
    }

    /// <summary>Whether latest is newer than current. A current pre-release or dev build (1.2.0-dev) is older than
    /// the release of the same number.</summary>
    public static bool Newer(string latest, string current)
    {
        if (Numbers(latest) is not { } a || Numbers(current) is not { } b)
            return false;
        for (var i = 0; i < 3; i++)
            if (a[i] != b[i])
                return a[i] > b[i];
        return current.Contains('-') && !latest.Contains('-');
    }

    private static int[]? Numbers(string version)
    {
        var parts = version.Split('-', 2)[0].Split('.');
        if (parts.Length != 3)
            return null;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        return numbers;
    }
}
