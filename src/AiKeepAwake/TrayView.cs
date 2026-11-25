using System.Text.Json.Nodes;

namespace AiKeepAwake;

/// <summary>The tray icon's six states.</summary>
public enum TrayState { Awake, NotNeeded, Blocked, Manual, Paused, Problem }

/// <summary>What the tray knows, read from the watcher's files and the settings: everything TrayView needs, so the
/// view can be worked out (and tested) without Windows.</summary>
public sealed record TrayFacts
{
    public double Now { get; init; }
    public bool WatcherRunning { get; init; }

    /// <summary>The watcher's state.json, or null if unreadable.</summary>
    public JsonObject? State { get; init; }

    /// <summary>The live leases: Claude Code sessions and the manual one.</summary>
    public IReadOnlyList<Lease> Live { get; init; } = [];

    /// <summary>The pause in force, if any.</summary>
    public Pause? Pause { get; init; }

    public int HookEvents { get; init; }

    /// <summary>Whether the watcher's task and the tray's Run entry are both there; null until known.</summary>
    public bool? AtLogon { get; init; }

    public bool TaskRegistered { get; init; }
    public string Version { get; init; } = "";
    public InstallRecord? Install { get; init; }
    public ReleaseInfo? Release { get; init; }
}

/// <summary>A session row in the window: the project folder's name, working or background, and until when.</summary>
public sealed record SessionRow(string Name, string What, string Until);

/// <summary>A problem shown in place of the headline, with the one thing to do about it.</summary>
public sealed record TrayProblem(string Text, string? Action, string? ActionId);

/// <summary>What the tray shows: the icon's state and tooltip, the menu's first line, and the window's lines.</summary>
public sealed record TrayView
{
    public const string StartWatcher = "start-watcher", PutHooksBack = "put-hooks-back";

    /// <summary>A state.json older than this, with the watcher running, is "status unknown" (it's written each minute).</summary>
    public const double StaleAfter = 180;

    public TrayState State { get; init; }
    public string Tooltip { get; init; } = "";
    public string MenuHeader { get; init; } = "";
    public string Headline { get; init; } = "";
    public IReadOnlyList<string> Detail { get; init; } = [];
    public IReadOnlyList<SessionRow> Sessions { get; init; } = [];
    public int MoreSessions { get; init; }
    public IReadOnlyList<string> LockLines { get; init; } = [];
    public TrayProblem? Problem { get; init; }
    public bool AwakeOn { get; init; }
    public string AwakeButton { get; init; } = "Keep awake now";
    public bool PausedOn { get; init; }
    public string PauseButton { get; init; } = "Pause";
    public string Footer { get; init; } = "";

    /// <summary>A line under the footer when another installer manages it: "Updated with right-hand".</summary>
    public string? FooterNote { get; init; }
    public string? ReleaseLine { get; init; }
    public string? ReleaseUrl { get; init; }

    /// <summary>Windows cuts a tray tooltip at 127 characters.</summary>
    public static string Tip(string text) => text.Length <= 127 ? text : text[..126] + "…";

    public static TrayView From(TrayFacts facts)
    {
        var now = facts.Now;
        var state = facts.State ?? [];
        var held = Py.Truthy(state["held"]);
        var why = state.TryGetPropertyValue("why", out var whyNode) && whyNode is not null ? Py.Str(whyNode) : "";
        double? since = Py.Truthy(state["since"]) ? Py.Float(state["since"]) : null;
        double? updated = state.ContainsKey("updated") && state["updated"] is not null ? Py.Float(state["updated"]) : null;
        bool? mains = state["mains"] is JsonValue m && m.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            ? m.GetValue<bool>() : null;
        double? lockAfter = state["lock_after"] is JsonValue la && la.GetValueKind() == System.Text.Json.JsonValueKind.Number ? Py.Float(la) : null;
        var lockWhy = state.TryGetPropertyValue("lock_why", out var lw) && lw is not null ? Py.Str(lw) : "";

        var sessions = facts.Live.Where(lease => !lease.IsManual).OrderBy(lease => lease.Until).ToList();
        var manual = facts.Live.FirstOrDefault(lease => lease.IsManual);
        var paused = facts.Pause is not null && Control.Paused(facts.Pause, now);
        var power = mains switch { true => "on mains", false => "on battery", null => "power source unknown" };
        var sinceText = since is { } s ? $"since {Control.Clock(s)}, {power}" : power;
        var lockShort = lockAfter is { } after ? $" Locks after {LockPoint.Minutes(after)} idle." : "";
        var manualUntil = manual is null ? "" : manual.UntilOff ? "until turned off" : $"until {Control.Clock(manual.Until)}";
        var count = sessions.Count == 1 ? "1 Claude Code session" : $"{sessions.Count} Claude Code sessions";

        TrayState kind;
        string tooltip, header, headline;
        var detail = new List<string>();
        TrayProblem? problem = null;
        if (!facts.WatcherRunning)
        {
            (kind, tooltip, header) = (TrayState.Problem, "ai-keep-awake: the watcher isn't running", "ai-keep-awake: the watcher isn't running");
            headline = "Not keeping the laptop awake";
            problem = new TrayProblem("The watcher isn't running.", "Start it", StartWatcher);
        }
        else if (facts.HookEvents == 0)
        {
            (kind, tooltip, header) = (TrayState.Problem, "ai-keep-awake: its hooks aren't in Claude Code's settings", "ai-keep-awake: no hooks in Claude Code's settings");
            headline = "Not keeping the laptop awake";
            problem = new TrayProblem("Claude Code's settings don't have its hooks.", "Put them back", PutHooksBack);
        }
        else if (updated is null || now - updated > StaleAfter)
        {
            var at = updated is { } u ? $" since {Control.Clock(u)}" : "";
            (kind, tooltip, header) = (TrayState.Problem, $"ai-keep-awake: status unknown{at}", $"ai-keep-awake: status unknown{at}");
            headline = $"Status unknown{at}";
            problem = new TrayProblem($"The watcher hasn't said what it's doing{at}.", null, null);
        }
        else if (paused)
        {
            var until = facts.Pause!.Until is { } pu ? $"until {Control.Clock(pu)}" : "until you resume";
            (kind, header) = (TrayState.Paused, $"ai-keep-awake: paused {until}");
            tooltip = $"ai-keep-awake: paused {until}; normal sleep.";
            headline = $"Paused {until}";
            detail.Add(sessions.Count > 0 ? $"Sleeping as normal, though {count} {(sessions.Count == 1 ? "is" : "are")} working" : "Sleeping as normal");
        }
        else if (manual is not null && held)
        {
            (kind, header) = (TrayState.Manual, $"ai-keep-awake: awake by hand {manualUntil}");
            tooltip = $"ai-keep-awake: kept awake by hand {manualUntil}.{lockShort}";
            headline = "Keeping the laptop awake by hand";
            detail.Add(sessions.Count > 0 ? $"{Capital(manualUntil)}, and for {count}" : Capital(manualUntil));
            detail.Add(Capital(sinceText));
        }
        else if (sessions.Count > 0 && held)
        {
            (kind, header) = (TrayState.Awake, $"ai-keep-awake: awake for {(sessions.Count == 1 ? "1 session" : $"{sessions.Count} sessions")}");
            tooltip = $"ai-keep-awake: awake for {count} ({power}).{lockShort}";
            headline = "Keeping the laptop awake";
            detail.Add($"{count} {(sessions.Count == 1 ? "is" : "are")} working");
            detail.Add(Capital(sinceText));
        }
        else if (sessions.Count > 0 || manual is not null)
        {
            var reason = why == "on battery" ? "on battery" : why.StartsWith("nobody at the laptop", StringComparison.Ordinal) ? "nobody here for a while" : why;
            (kind, header) = (TrayState.Blocked, $"ai-keep-awake: not keeping awake ({reason})");
            tooltip = why == "on battery" ? "ai-keep-awake: on battery, so not keeping awake"
                : why.StartsWith("nobody at the laptop", StringComparison.Ordinal) ? $"ai-keep-awake: nobody here for {UnattendedText(why)}"
                : $"ai-keep-awake: not keeping awake ({why})";
            headline = "Not keeping the laptop awake";
            detail.Add(why == "on battery" ? "On battery: it keeps awake on mains only"
                : why.StartsWith("nobody at the laptop", StringComparison.Ordinal) ? $"Nobody has used the laptop for {UnattendedText(why)}"
                : Capital(why));
            detail.Add(sessions.Count > 0 ? $"{count} {(sessions.Count == 1 ? "is" : "are")} working" : $"Kept awake by hand {manualUntil}");
        }
        else
        {
            (kind, header) = (TrayState.NotNeeded, "ai-keep-awake: not needed now");
            tooltip = "ai-keep-awake: no Claude Code session is working; normal sleep.";
            headline = "Not needed now";
            detail.Add("No Claude Code session is working; normal sleep");
        }

        var rows = sessions.Take(5).Select(lease => new SessionRow(Project(lease), lease.Bg ? "background" : "working", $"until {Control.Clock(lease.Until)}")).ToList();
        var lockLines = new List<string>();
        if (problem is null)
        {
            if (lockAfter is { } la2)
            {
                lockLines.Add($"Locks after {LockPoint.Minutes(la2)} idle, where Windows would");
                if (lockWhy.Length > 0)
                    lockLines.Add($"({lockWhy})");
            }
            else if (lockWhy.Length > 0)
                lockLines.Add($"Doesn't lock: {Uncapital(lockWhy)}");
        }

        var footer = $"Watcher {(facts.WatcherRunning ? "running" : "not running")}. "
                     + (facts.HookEvents > 0 ? $"Hooks on {facts.HookEvents} events." : "No hooks.")
                     + $" v{facts.Version}";
        string? releaseLine = null, releaseUrl = null, footerNote = null;
        if (facts.Install?.ManagedByRightHand == true)
            footerNote = "Updated with right-hand, which decides the version";
        else if (facts.Release is { } release && ReleaseCheck.Newer(release.Version, facts.Version))
            (releaseLine, releaseUrl) = ($"{release.Version} is available", release.Url);

        var manualOn = manual is not null;
        return new TrayView
        {
            State = kind,
            Tooltip = Tip(tooltip),
            MenuHeader = header,
            Headline = headline,
            Detail = detail,
            Sessions = rows,
            MoreSessions = Math.Max(0, sessions.Count - rows.Count),
            LockLines = lockLines,
            Problem = problem,
            AwakeOn = manualOn,
            AwakeButton = manualOn ? $"Stop keeping awake ({manualUntil})" : "Keep awake now",
            PausedOn = paused,
            PauseButton = paused ? "Resume" : "Pause",
            Footer = footer,
            FooterNote = footerNote,
            ReleaseLine = releaseLine,
            ReleaseUrl = releaseUrl,
        };
    }

    /// <summary>The last part of a session's folder (my-right-hand), else the start of its id.</summary>
    public static string Project(Lease lease)
    {
        var cwd = lease.Cwd?.TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(cwd))
        {
            var name = Path.GetFileName(cwd);
            if (name.Length > 0)
                return name;
        }
        return lease.Session.Length > 8 ? lease.Session[..8] : lease.Session;
    }

    private static string UnattendedText(string why)
    {
        // "nobody at the laptop for 3.2 hours (the cap is 3)"
        var start = "nobody at the laptop for ".Length;
        var end = why.IndexOf(" (", StringComparison.Ordinal);
        return end > start ? why[start..end] : "a while";
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Uncapital(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) && !text.StartsWith("Windows", StringComparison.Ordinal)
            ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}
