using System.Text.Json.Nodes;

namespace AiKeepAwake.Tests;

/// <summary>What the tray shows, worked out without Windows; and the release check, with a faked GitHub.</summary>
public class TrayViewTests
{
    private const double Now = Temp.Now;

    private static JsonObject State(bool held, string why, bool? mains = true, double? lockAfter = 300, double updated = Now - 10) => new()
    {
        ["held"] = held,
        ["why"] = why,
        ["since"] = Now - 600,
        ["updated"] = updated,
        ["mains"] = mains,
        ["lock_after"] = lockAfter,
        ["lock_why"] = "sleep after 5 min, sign-in required every time",
    };

    private static Lease Session(string name = "s1", string? cwd = @"C:\work\my-right-hand", bool bg = false, double until = Now + 900) =>
        new(name, until, bg, 1, cwd, "PreToolUse", "x");

    private static Lease Manual(double until = Now + 3600, bool untilOff = false) =>
        new("manual", until, false, untilOff ? 99 : null, null, null, "x", Lease.Manual, untilOff);

    private static TrayFacts Facts(JsonObject? state = null, IReadOnlyList<Lease>? live = null, Pause? pause = null, bool running = true,
        int hooks = 7, InstallRecord? install = null, ReleaseInfo? release = null) => new()
    {
        Now = Now,
        WatcherRunning = running,
        State = state ?? State(false, "no Claude Code session is working"),
        Live = live ?? [],
        Pause = pause,
        HookEvents = hooks,
        AtLogon = true,
        TaskRegistered = true,
        Version = "1.2.0",
        Install = install,
        Release = release,
    };

    [Fact]
    public void Keeping_awake_for_sessions()
    {
        var view = TrayView.From(Facts(State(true, "2 sessions working"), [Session(), Session("s2", @"C:\work\right-hand", bg: true)]));
        Assert.Equal(TrayState.Awake, view.State);
        Assert.Equal("ai-keep-awake: awake for 2 Claude Code sessions (on mains). Locks after 5 min idle.", view.Tooltip);
        Assert.Equal("ai-keep-awake: awake for 2 sessions", view.MenuHeader);
        Assert.Equal("Keeping the laptop awake", view.Headline);
        Assert.Equal(["2 Claude Code sessions are working", $"Since {Control.Clock(Now - 600)}, on mains"], view.Detail);
        Assert.Equal([new SessionRow("my-right-hand", "working", $"until {Control.Clock(Now + 900)}"),
            new SessionRow("right-hand", "background", $"until {Control.Clock(Now + 900)}")], view.Sessions);
        Assert.Equal(["Locks after 5 min idle, where Windows would", "(sleep after 5 min, sign-in required every time)"], view.LockLines);
        Assert.Equal("Keep awake now", view.AwakeButton);
        Assert.Equal("Pause", view.PauseButton);
        Assert.Equal("Watcher running. Hooks on 7 events. v1.2.0", view.Footer);
        Assert.Null(view.Problem);
    }

    [Fact]
    public void Not_needed()
    {
        var view = TrayView.From(Facts());
        Assert.Equal(TrayState.NotNeeded, view.State);
        Assert.Equal("ai-keep-awake: no Claude Code session is working; normal sleep.", view.Tooltip);
    }

    [Fact]
    public void Not_keeping_awake_though_claude_is_working()
    {
        var battery = TrayView.From(Facts(State(false, "on battery", mains: false), [Session()]));
        Assert.Equal(TrayState.Blocked, battery.State);
        Assert.Equal("ai-keep-awake: on battery, so not keeping awake", battery.Tooltip);
        var away = TrayView.From(Facts(State(false, "nobody at the laptop for 3.2 hours (the cap is 3)"), [Session()]));
        Assert.Equal(TrayState.Blocked, away.State);
        Assert.Equal("ai-keep-awake: nobody here for 3.2 hours", away.Tooltip);
        Assert.Equal("Nobody has used the laptop for 3.2 hours", away.Detail[0]);
    }

    [Fact]
    public void Kept_awake_by_hand()
    {
        var view = TrayView.From(Facts(State(true, "kept awake by hand"), [Manual()]));
        Assert.Equal(TrayState.Manual, view.State);
        Assert.Equal($"ai-keep-awake: kept awake by hand until {Control.Clock(Now + 3600)}. Locks after 5 min idle.", view.Tooltip);
        Assert.True(view.AwakeOn);
        Assert.Equal($"Stop keeping awake (until {Control.Clock(Now + 3600)})", view.AwakeButton);
        Assert.Empty(view.Sessions);
        var off = TrayView.From(Facts(State(true, "kept awake by hand"), [Manual(untilOff: true)]));
        Assert.Equal("Stop keeping awake (until turned off)", off.AwakeButton);
    }

    [Fact]
    public void Paused()
    {
        var view = TrayView.From(Facts(State(false, "paused"), [Session()], new Pause(Now + 1800, Now)));
        Assert.Equal(TrayState.Paused, view.State);
        Assert.Equal($"ai-keep-awake: paused until {Control.Clock(Now + 1800)}; normal sleep.", view.Tooltip);
        Assert.True(view.PausedOn);
        Assert.Equal("Resume", view.PauseButton);
        var resume = TrayView.From(Facts(State(false, "paused"), [], new Pause(null, Now)));
        Assert.Equal("Paused until you resume", resume.Headline);
    }

    [Fact]
    public void Problems_come_first()
    {
        var stopped = TrayView.From(Facts(State(true, "1 session working"), [Session()], running: false));
        Assert.Equal(TrayState.Problem, stopped.State);
        Assert.Equal("ai-keep-awake: the watcher isn't running", stopped.Tooltip);
        Assert.Equal(TrayView.StartWatcher, stopped.Problem!.ActionId);
        Assert.Equal("Start it", stopped.Problem.Action);
        Assert.Equal("Watcher not running. Hooks on 7 events. v1.2.0", stopped.Footer);

        var hooks = TrayView.From(Facts(hooks: 0));
        Assert.Equal("ai-keep-awake: its hooks aren't in Claude Code's settings", hooks.Tooltip);
        Assert.Equal(TrayView.PutHooksBack, hooks.Problem!.ActionId);

        var stale = TrayView.From(Facts(State(true, "1 session working", updated: Now - 600)));
        Assert.Equal(TrayState.Problem, stale.State);
        Assert.Equal($"ai-keep-awake: status unknown since {Control.Clock(Now - 600)}", stale.Tooltip);
        Assert.Null(stale.Problem!.ActionId);
    }

    [Fact]
    public void Tooltips_fit_windows_limit()
    {
        var view = TrayView.From(Facts(State(false, new string('x', 200)), [Session()]));
        Assert.True(view.Tooltip.Length <= 127);
    }

    [Fact]
    public void A_newer_release_is_a_link_unless_right_hand_manages_it()
    {
        var release = new ReleaseInfo("1.3.0", "https://github.com/gareth-jones-zen/ai-keep-awake/releases/tag/v1.3.0", Now);
        var view = TrayView.From(Facts(release: release));
        Assert.Equal("1.3.0 is available", view.ReleaseLine);
        Assert.Equal(release.Url, view.ReleaseUrl);
        var managed = TrayView.From(Facts(release: release, install: new InstallRecord("1.2.0", null, InstallRecord.RightHand, null)));
        Assert.Null(managed.ReleaseLine);
        Assert.Equal("Updated with right-hand, which decides the version", managed.FooterNote);
        Assert.Equal("Watcher running. Hooks on 7 events. v1.2.0", managed.Footer);
        Assert.Null(view.FooterNote);
        Assert.Null(TrayView.From(Facts(release: release with { Version = "1.2.0" })).ReleaseLine);
    }

    [Fact]
    public void Many_sessions_are_summed_up()
    {
        var live = Enumerable.Range(0, 8).Select(i => Session($"s{i}", cwd: null)).ToList();
        var view = TrayView.From(Facts(State(true, "8 sessions working"), live));
        Assert.Equal(5, view.Sessions.Count);
        Assert.Equal(3, view.MoreSessions);
        Assert.Equal("s0", view.Sessions[0].Name);   // no folder: the start of its id
    }
}

public class ReleaseCheckTests : Temp
{
    private const string Latest = """
        {"tag_name": "v1.3.0", "html_url": "https://github.com/gareth-jones-zen/ai-keep-awake/releases/tag/v1.3.0",
         "draft": false, "prerelease": false, "assets": []}
        """;

    private Config Settings(string folder = "state")
    {
        var config = NewConfig();
        config.StateFile = Path.Combine(Folder, folder, "state.json");
        return config;
    }

    [Theory]
    [InlineData("1.3.0", "1.2.0", true)]
    [InlineData("1.2.1", "1.2.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.1.0", "1.2.0", false)]
    [InlineData("1.2.0", "1.2.0-dev", true)]
    [InlineData("1.2.0", "nonsense", false)]
    [InlineData("v1", "1.2.0", false)]
    public void Newer(string latest, string current, bool newer) => Assert.Equal(newer, ReleaseCheck.Newer(latest, current));

    [Fact]
    public async Task GitHubs_answer_is_read_and_kept_for_a_day()
    {
        var config = Settings();
        var calls = 0;
        Task<string> Fetch(CancellationToken _) { calls++; return Task.FromResult(Latest); }
        var found = await ReleaseCheck.Run(config, Fetch, Now, TestContext.Current.CancellationToken);
        Assert.Equal("1.3.0", found!.Version);
        Assert.Equal("https://github.com/gareth-jones-zen/ai-keep-awake/releases/tag/v1.3.0", found.Url);
        Assert.Equal(found, await ReleaseCheck.Run(config, Fetch, Now + 3600, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);                                              // once a day
        await ReleaseCheck.Run(config, Fetch, Now + ReleaseCheck.Every, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_failure_keeps_the_last_answer_and_waits_a_day()
    {
        var config = Settings();
        await ReleaseCheck.Run(config, _ => Task.FromResult(Latest), Now, TestContext.Current.CancellationToken);
        var failed = await ReleaseCheck.Run(config, _ => throw new HttpRequestException("offline"), Now + ReleaseCheck.Every, TestContext.Current.CancellationToken);
        Assert.Equal("1.3.0", failed!.Version);
        Assert.False(ReleaseCheck.Due(ReleaseCheck.Cached(config), Now + ReleaseCheck.Every + 60));
        var none = await ReleaseCheck.Run(Settings("other"), _ => throw new HttpRequestException("offline"), Now, TestContext.Current.CancellationToken);
        Assert.Null(none);
    }

    [Theory]
    [InlineData("""{"tag_name": "v1.3.0", "html_url": "https://evil.example/x", "draft": false, "prerelease": false}""")]
    [InlineData("""{"tag_name": "v1.3.0-rc.1", "html_url": "https://github.com/x", "draft": false, "prerelease": true}""")]
    [InlineData("""{"tag_name": "latest", "html_url": "https://github.com/x"}""")]
    [InlineData("""not json""")]
    [InlineData("""[]""")]
    public void Only_a_real_release_counts(string json) => Assert.Null(ReleaseCheck.Parse(json, Now));

    [Fact]
    public void Not_when_right_hand_manages_it()
    {
        Assert.True(ReleaseCheck.Applies(null));
        Assert.True(ReleaseCheck.Applies(new InstallRecord("1.2.0", null, InstallRecord.Person, null)));
        Assert.False(ReleaseCheck.Applies(new InstallRecord("1.2.0", null, InstallRecord.RightHand, null)));
    }
}

public class RecordTests : Temp
{
    [Fact]
    public void Install_json_round_trips()
    {
        var config = NewConfig();
        config.StateFile = Path.Combine(Folder, "state", "state.json");
        Assert.Null(InstallRecord.Read(config));
        var record = new InstallRecord("1.2.0", "2026-11-25T14:05:00", InstallRecord.RightHand, @"C:\p", RemovedByPerson: true, Removed: "2026-11-26T09:00:00");
        record.Write(config);
        Assert.Equal(record, InstallRecord.Read(config));
        var json = JsonNode.Parse(File.ReadAllText(InstallRecord.PathFor(config)))!.AsObject();
        Assert.Equal(["version", "installed", "managed_by", "install_dir", "removed_by_person", "removed"], json.Select(p => p.Key));
        Assert.Equal(Path.Combine(Folder, "state", "install.json"), InstallRecord.PathFor(config));
    }

    [Fact]
    public void The_watchers_notifications_go_to_the_tray_when_it_runs()
    {
        var config = NewConfig();
        config.StateFile = Path.Combine(Folder, "state.json");
        var toasts = new List<string>();
        var trayRunning = true;
        var notify = Notice.Route(config, (title, _) => toasts.Add(title), () => trayRunning, () => Now);
        notify("Claude Code may now let the laptop sleep", ["1 session(s) still working"]);
        Assert.Empty(toasts);
        var notice = Notice.Read(config)!;
        Assert.Equal("Claude Code may now let the laptop sleep", notice.Title);
        Assert.Equal(["1 session(s) still working"], notice.Lines);
        Assert.Equal(Now, notice.At);
        trayRunning = false;
        notify("ai-keep-awake is failing", ["x"]);
        Assert.Equal(["ai-keep-awake is failing"], toasts);
    }
}

public class TrayColorTests
{
    private const uint LightWindow = 0xFFF9F9F9, DarkWindow = 0xFF2C2C2C, LightText = 0xFF1A1A1A, DarkText = 0xFFFFFFFF;

    /// <summary>Windows' default blue palette, as AccentPalette holds it: R, G, B, A, lightest first.</summary>
    private static readonly byte[] Blue =
    [
        0x99, 0xEB, 0xFF, 0, 0x60, 0xCD, 0xFF, 0, 0x00, 0x91, 0xF8, 0, 0x00, 0x78, 0xD4, 0,
        0x00, 0x67, 0xC0, 0, 0x00, 0x3E, 0x92, 0, 0x00, 0x1A, 0x68, 0, 0x88, 0x17, 0x98, 0,
    ];

    [Fact]
    public void Contrast_is_the_usual_ratio()
    {
        Assert.Equal(21, TrayColors.Contrast(TrayColors.Black, TrayColors.White), 3);
        Assert.Equal(1, TrayColors.Contrast(0xFF808080, 0xFF808080), 3);
        Assert.Equal(TrayColors.Contrast(LightWindow, LightText), TrayColors.Contrast(LightText, LightWindow), 6);
        Assert.Equal(4.54, TrayColors.Contrast(0xFF767676, TrayColors.White), 2);   // the well-known lightest grey that passes on white
    }

    [Fact]
    public void The_accent_is_a_shade_darker_on_light_and_lighter_on_dark()
    {
        Assert.Equal((0xFF0067C0u, 0xFF003E92u), TrayColors.Accent(Blue, dark: false));
        Assert.Equal((0xFF60CDFFu, 0xFF99EBFFu), TrayColors.Accent(Blue, dark: true));
        Assert.Null(TrayColors.Accent(null, dark: false));
        Assert.Null(TrayColors.Accent([1, 2, 3], dark: false));
    }

    [Fact]
    public void Text_on_a_fill_is_whichever_of_black_and_white_reads()
    {
        Assert.Equal(TrayColors.White, TrayColors.On(0xFF0067C0));
        Assert.Equal(TrayColors.Black, TrayColors.On(0xFF60CDFF));
        Assert.Equal(TrayColors.Black, TrayColors.On(0xFFFFD400));   // a yellow accent
    }

    [Fact]
    public void A_colour_that_reads_is_left_alone_and_one_that_doesnt_is_moved_only_as_far_as_it_takes()
    {
        Assert.Equal(0xFF003E92u, TrayColors.Readable(0xFF003E92, LightWindow, LightText));
        Assert.Equal(0xFF99EBFFu, TrayColors.Readable(0xFF99EBFF, DarkWindow, DarkText));
        // Every accent, however pale or deep, ends up readable on both windows.
        foreach (var accent in new uint[] { 0xFFFFD400, 0xFFFFF4B0, 0xFF101060, 0xFF00CC6A, 0xFFE81123, 0xFF7A7574, 0xFFFFFFFF, 0xFF000000 })
        {
            foreach (var (window, text) in new[] { (LightWindow, LightText), (DarkWindow, DarkText) })
            {
                var link = TrayColors.Readable(accent, window, text);
                Assert.True(TrayColors.Contrast(link, window) >= TrayColors.TextContrast, $"{accent:X8} as text on {window:X8}");
                var fill = TrayColors.Readable(accent, window, text, TrayColors.ShapeContrast);
                Assert.True(TrayColors.Contrast(fill, window) >= TrayColors.ShapeContrast, $"{accent:X8} as a fill on {window:X8}");
                Assert.True(TrayColors.Contrast(TrayColors.On(fill), fill) >= TrayColors.TextContrast, $"text on {fill:X8}");
            }
        }
        var pale = TrayColors.Readable(0xFFFFF4B0, LightWindow, LightText);
        Assert.NotEqual(LightText, pale);   // still some of the accent in it
    }
}

public class IconTests
{
    [Fact]
    public void Every_state_has_its_own_icon_at_every_size()
    {
        foreach (var size in new[] { 16, 20, 24, 32, 48 })
        {
            var drawn = Enum.GetValues<TrayState>().Select(state => IconArt.Render(state, size)).ToList();
            Assert.All(drawn, pixels => Assert.Equal(size * size, pixels.Length));
            Assert.All(drawn, pixels => Assert.Contains(pixels, p => p >> 24 == 0xFF));   // something solid
            Assert.All(drawn, pixels => Assert.Equal(0u, pixels[0] >> 24));              // the corners clear
            for (var a = 0; a < drawn.Count; a++)
                for (var b = a + 1; b < drawn.Count; b++)
                    Assert.False(drawn[a].SequenceEqual(drawn[b]), $"{(TrayState)a} and {(TrayState)b} look the same at {size} px");
        }
        Assert.False(IconArt.Render(TrayState.NotNeeded, 16, dark: true).SequenceEqual(IconArt.Render(TrayState.NotNeeded, 16)));
    }

    [Fact]
    public void A_png_is_a_png()
    {
        var png = IconArt.Png(IconArt.Render(TrayState.Awake, 16), 16);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    /// <summary>src/app.ico is what IconArt draws. AI_KEEP_AWAKE_WRITE_ICON=1 writes it afresh after a change.</summary>
    [Fact]
    public void The_program_icon_is_what_the_code_draws()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "app.ico"));
        var drawn = IconArt.Ico();
        if (Environment.GetEnvironmentVariable("AI_KEEP_AWAKE_WRITE_ICON") == "1")
            File.WriteAllBytes(path, drawn);
        Assert.True(File.Exists(path), path);
        Assert.Equal(drawn, File.ReadAllBytes(path));
    }

    /// <summary>With AI_KEEP_AWAKE_WRITE_ICON=1, the contact sheets in docs/design/icon: every state (a row each, in
    /// the enum's order) at 16, 20, 24, 32 and 48 px as the taskbar shows them, then the 16 px one six times over,
    /// on a light taskbar and on a dark one; and the program icon at each of its sizes on both.</summary>
    [Fact]
    public void The_contact_sheets_are_drawn()
    {
        const uint light = 0xFFF3F3F3, dark = 0xFF202020;
        int[] sizes = [16, 20, 24, 32, 48];
        const int gap = 16, row = 96 + gap, zoom = 6;
        var half = gap + sizes.Sum(size => size + gap) + 16 * zoom + gap;
        var states = Enum.GetValues<TrayState>();
        var sheet = new Canvas(half * 2, gap + states.Length * row);
        sheet.Fill(0, half, light);
        sheet.Fill(half, half, dark);
        foreach (var onDark in new[] { false, true })
        {
            for (var s = 0; s < states.Length; s++)
            {
                int x = (onDark ? half : 0) + gap, y = gap + s * row;
                foreach (var size in sizes)
                {
                    sheet.Blend(IconArt.Render(states[s], size, onDark), size, x, y + (96 - size) / 2);
                    x += size + gap;
                }
                sheet.Blend(IconArt.Render(states[s], 16, onDark), 16, x, y, zoom);
            }
        }
        Assert.Contains(sheet.Pixels, p => p != light && p != dark);

        // The program icon: the images in the .ico itself, read back.
        int[] appSizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var appHalf = gap + appSizes.Sum(size => size + gap);
        var app = new Canvas(appHalf, 2 * (256 + 2 * gap));
        app.Fill(0, appHalf, light, 0, 256 + 2 * gap);
        app.Fill(0, appHalf, dark, 256 + 2 * gap, 256 + 2 * gap);
        var ico = IconArt.Ico(appSizes);
        for (var band = 0; band < 2; band++)
        {
            var x = gap;
            for (var i = 0; i < appSizes.Length; i++)
            {
                var size = appSizes[i];
                app.Blend(IcoPixels(ico, i, size), size, x, band * (256 + 2 * gap) + gap + (256 - size) / 2);
                x += size + gap;
            }
        }

        if (Environment.GetEnvironmentVariable("AI_KEEP_AWAKE_WRITE_ICON") != "1")
            return;
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "design", "icon"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "tray-states.png"), IconArt.Png(sheet.Pixels, sheet.Width, sheet.Height));
        File.WriteAllBytes(Path.Combine(folder, "program-icon.png"), IconArt.Png(app.Pixels, app.Width, app.Height));
    }

    /// <summary>One image out of an .ico written by IconArt.Ico: a PNG with one unfiltered, zlib-packed block.</summary>
    private static uint[] IcoPixels(byte[] ico, int index, int size)
    {
        var entry = 6 + 16 * index;
        var png = ico.AsSpan(BitConverter.ToInt32(ico, entry + 12), BitConverter.ToInt32(ico, entry + 8));
        var packedLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[33..]);   // after the signature and IHDR
        Assert.Equal("IDAT", System.Text.Encoding.ASCII.GetString(png.Slice(37, 4)));
        using var packed = new MemoryStream(png.Slice(41, packedLength).ToArray());
        using var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var bytes = raw.ToArray();
        Assert.Equal(size * (size * 4 + 1), bytes.Length);
        var pixels = new uint[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var at = y * (size * 4 + 1) + 1 + x * 4;
                pixels[y * size + x] = (uint)(bytes[at + 3] << 24 | bytes[at] << 16 | bytes[at + 1] << 8 | bytes[at + 2]);
            }
        }
        return pixels;
    }

    private sealed class Canvas(int width, int height)
    {
        public int Width => width;
        public int Height => height;
        public uint[] Pixels { get; } = new uint[width * height];

        public void Fill(int x0, int w, uint color, int y0 = 0, int h = -1)
        {
            for (var y = y0; y < (h < 0 ? height : y0 + h); y++)
                Array.Fill(Pixels, color, y * width + x0, w);
        }

        /// <summary>Draw ARGB pixels over what's there, each one scale x scale.</summary>
        public void Blend(uint[] source, int size, int x0, int y0, int scale = 1)
        {
            for (var y = 0; y < size * scale; y++)
            {
                for (var x = 0; x < size * scale; x++)
                {
                    var p = source[y / scale * size + x / scale];
                    var a = (p >> 24) / 255.0;
                    var under = Pixels[(y0 + y) * width + x0 + x];
                    uint Mix(int shift) => (uint)Math.Round(((p >> shift) & 0xFF) * a + ((under >> shift) & 0xFF) * (1 - a));
                    Pixels[(y0 + y) * width + x0 + x] = 0xFF000000 | Mix(16) << 16 | Mix(8) << 8 | Mix(0);
                }
            }
        }
    }
}

public class ShortcutTests : Temp
{
    /// <summary>The real Start menu code, writing a shortcut into the test's folder (not the Start menu).</summary>
    [Fact]
    public void A_shortcut_is_written_with_its_target()
    {
        var target = Path.Combine(Folder, "ai-keep-awake-tray.exe");
        File.WriteAllBytes(target, []);
        var link = Path.Combine(Folder, "ai-keep-awake.lnk");
        Native.WriteShortcut(link, target, "--show", Folder, "Keeps the laptop awake", target, WindowsDesktop.AppId);
        Assert.True(File.Exists(link));
        Assert.Equal(target, Native.ShortcutTarget(link));
        // UTF-16 in the property store, at whatever byte offset it lands: search the bytes, not a decoded string.
        Assert.True(File.ReadAllBytes(link).AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(WindowsDesktop.AppId)) >= 0);
    }
}
