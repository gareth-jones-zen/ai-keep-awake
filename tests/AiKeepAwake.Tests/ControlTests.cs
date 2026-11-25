using System.Text.Json.Nodes;
using WinBackground;

namespace AiKeepAwake.Tests;

/// <summary>Keep awake now and Pause: the files, the watcher's rules for them, and the commands.</summary>
public class ControlTests : Temp
{
    private readonly Config config;
    private readonly FakeSystem system = new();
    private readonly List<string> logged = [];
    private readonly Watcher watcher;

    public ControlTests()
    {
        config = NewConfig();
        config.LeasesDir = LeasesDir;
        config.StateFile = Path.Combine(Folder, "state.json");
        watcher = new Watcher(config, system, logged.Add, (_, _) => { }, alive: _ => true);
    }

    private void Session(double until = Now + 900, string name = "s1")
    {
        Directory.CreateDirectory(LeasesDir);
        File.WriteAllText(Path.Combine(LeasesDir, $"{name}.json"), $"{{\"until\": {Py.Repr(until)}, \"bg\": false, \"pid\": 1}}");
    }

    private JsonObject State() => JsonNode.Parse(File.ReadAllText(config.StateFile))!.AsObject();

    [Theory]
    [InlineData("1h", 3600.0)]
    [InlineData("2h", 7200.0)]
    [InlineData("30m", 1800.0)]
    [InlineData("90min", 5400.0)]
    [InlineData("1.5h", 5400.0)]
    [InlineData(" 2 hours ", 7200.0)]
    [InlineData("24h", 86400.0)]
    public void Durations(string text, double seconds) => Assert.Equal(seconds, Control.Duration(text));

    [Theory]
    [InlineData("")]
    [InlineData("0h")]
    [InlineData("25h")]
    [InlineData("1d")]
    [InlineData("-1h")]
    [InlineData("an hour")]
    public void Not_durations(string text) => Assert.Null(Control.Duration(text));

    [Fact]
    public void A_manual_lease_is_a_lease_of_its_own_kind()
    {
        Control.SetManual(config, Now + 3600, Now);
        var lease = Assert.Single(Leases.Read(LeasesDir));
        Assert.Equal(Lease.Manual, lease.Kind);
        Assert.True(lease.IsManual);
        Assert.False(lease.UntilOff);
        Assert.Null(lease.Pid);
        Assert.Equal(Now + 3600, lease.Until);
        Assert.Equal(lease, Control.ReadManual(config));
        Session();
        Assert.Equal(Lease.Claude, Leases.Read(LeasesDir).Single(l => l.Session == "s1").Kind);   // a hook's lease has no kind
    }

    [Fact]
    public void Each_control_ends_the_other()
    {
        Control.SetManual(config, Now + 3600, Now);
        Control.SetPause(config, Now + 600, Now);
        Assert.Null(Control.ReadManual(config));
        Assert.Equal(new Pause(Now + 600, Now), Control.ReadPause(config));
        Control.SetManual(config, Now + 3600, Now);
        Assert.Null(Control.ReadPause(config));
        Assert.True(Control.ClearManual(config));
        Assert.False(Control.ClearManual(config));
        Assert.False(Control.ClearPause(config));
    }

    [Fact]
    public void A_pause_until_resumed_has_no_end()
    {
        Control.SetPause(config, null, Now);
        var pause = Control.ReadPause(config);
        Assert.Null(pause!.Until);
        Assert.True(Control.Paused(pause, Now + 1e6));
        Assert.False(Control.Paused(new Pause(Now, Now - 10), Now));
        Assert.False(Control.Paused(null, Now));
    }

    [Fact]
    public void Paused_holds_nothing_even_while_a_session_works()
    {
        Session();
        watcher.Step(Now);
        Assert.True(system.Req.Held);
        Control.SetPause(config, Now + 3600, Now);
        watcher.Step(Now + 1);
        Assert.False(system.Req.Held);
        Assert.StartsWith("paused until ", watcher.Why);
        Assert.Contains(logged, line => line.StartsWith("paused until "));
        Assert.True(State()["paused"]!.GetValue<bool>());
        Assert.Equal(Now + 3600, State()["paused_until"]!.GetValue<double>());
    }

    [Fact]
    public void A_pause_ends_at_its_time_and_its_file_goes()
    {
        Session(until: Now + 7200);
        Control.SetPause(config, Now + 600, Now);
        watcher.Step(Now);
        Assert.False(system.Req.Held);
        watcher.Step(Now + 601);
        Assert.True(system.Req.Held);
        Assert.Contains("pause ended", logged);
        Assert.False(File.Exists(Control.PauseFile(config)));
    }

    [Fact]
    public void Resuming_by_hand_is_logged()
    {
        Session();
        Control.SetPause(config, null, Now);
        watcher.Step(Now);
        Assert.Equal("paused until resumed", watcher.Why);
        Control.ClearPause(config);
        watcher.Step(Now + 1);
        Assert.Contains("resumed", logged);
        Assert.True(system.Req.Held);
    }

    [Fact]
    public void Keep_awake_now_holds_under_the_same_rules_as_a_session()
    {
        Control.SetManual(config, Now + 3600, Now);
        watcher.Step(Now);
        Assert.True(system.Req.Held);
        Assert.Equal($"kept awake by hand until {Control.Clock(Now + 3600)}", watcher.Why);
        Assert.Empty(State()["sessions"]!.AsArray());
        Assert.Equal(Now + 3600, State()["manual_until"]!.GetValue<double>());

        system.Mains = false;                                    // mains only
        watcher.Step(Now + 1);
        Assert.False(system.Req.Held);
        Assert.Equal("on battery", watcher.Why);
        system.Mains = true;
        watcher.Step(Now + 2);
        Assert.True(system.Req.Held);
        system.Idle = config.UnattendedSeconds;                  // the cap
        watcher.Step(Now + 3);
        Assert.False(system.Req.Held);
        system.Idle = 0;
        watcher.Step(Now + 3601);                                // and it ends at its time
        Assert.False(system.Req.Held);
        Assert.Equal("no Claude Code session is working", watcher.Why);
    }

    [Fact]
    public void Keep_awake_now_locks_where_windows_would()
    {
        Control.SetManual(config, Now + 3600, Now);
        watcher.Step(Now);
        system.Idle = 301;
        watcher.Step(Now + 301);
        Assert.Equal(1, system.Locks);
    }

    [Fact]
    public void Until_turned_off_ends_with_the_tray()
    {
        var trayAlive = true;
        var w = new Watcher(config, system, logged.Add, (_, _) => { }, alive: _ => trayAlive);
        Control.SetManual(config, Now + Control.UntilOffLease, Now, pid: 4242, untilOff: true);
        w.Step(Now);
        Assert.True(system.Req.Held);
        Assert.Equal("kept awake by hand until turned off", w.Why);
        trayAlive = false;
        w.Step(Now + 1);
        Assert.False(system.Req.Held);
    }

    [Fact]
    public void Sessions_and_by_hand_together()
    {
        Session();
        Session(name: "s2");
        Control.SetManual(config, Now + 3600, Now);
        watcher.Step(Now);
        Assert.Equal($"2 sessions working, and kept awake by hand until {Control.Clock(Now + 3600)}", watcher.Why);
        Assert.Equal(["s1", "s2"], State()["sessions"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public void The_state_file_is_written_when_the_sessions_change_not_only_when_held_flips()
    {
        Session();
        watcher.Step(Now);
        Assert.Single(State()["sessions"]!.AsArray());
        Session(name: "s2");
        watcher.Step(Now + 15);                                  // still held, a minute not yet up
        Assert.Equal(2, State()["sessions"]!.AsArray().Count);
        Assert.Equal(Now + 15, State()["updated"]!.GetValue<double>());
    }

    [Fact]
    public void The_watcher_clears_a_pause_when_it_starts()
    {
        Control.SetPause(config, null, Now);
        Watcher.Run(config, logged.Add, (_, _) => { }, system, sleep: _ => { }, steps: 1);
        Assert.Null(Control.ReadPause(config));
        Assert.Contains("pause ended: the watcher started", logged);
    }

    [Fact]
    public async Task The_wake_event_makes_the_watcher_look_at_once()
    {
        config.CheckSeconds = 60;
        var steps = 0;
        var clock = () => { Interlocked.Increment(ref steps); return Now; };
        var run = Task.Run(() => Watcher.Run(config, logged.Add, (_, _) => { }, new FakeSystem(), steps: 3, clock: clock),
            TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            Control.Wake(config);
            Thread.Sleep(50);
        }
        Assert.True(run.IsCompleted, "the watcher waited out its 60-second check instead of waking");
        Assert.Equal(0, await run);
    }

    [Fact]
    public void Waking_with_no_watcher_does_nothing()
    {
        Control.Wake(config);
        Assert.False(EventWaitHandle.TryOpenExisting(Control.WakeName(config), out _));
    }
}
