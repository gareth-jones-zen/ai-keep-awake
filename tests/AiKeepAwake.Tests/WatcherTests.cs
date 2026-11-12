using System.Text.Json.Nodes;
using WinBackground;

namespace AiKeepAwake.Tests;

public class WatcherTests : Temp
{
    private readonly Config config;
    private readonly FakeSystem system = new();
    private readonly List<string> logged = [];
    private readonly List<string> notified = [];
    private readonly Watcher watcher;

    public WatcherTests()
    {
        config = NewConfig();
        config.LeasesDir = LeasesDir;
        config.StateFile = Path.Combine(Folder, "state.json");
        watcher = new Watcher(config, system, logged.Add, (title, _) => notified.Add(title), alive: _ => true);
    }

    private void Session(double until = Now + 900, string name = "s1")
    {
        Directory.CreateDirectory(LeasesDir);
        File.WriteAllText(Path.Combine(LeasesDir, $"{name}.json"), $"{{\"until\": {Py.Repr(until)}, \"bg\": false, \"pid\": 1}}");
    }

    private void Step(double now = Now, double? idle = null)
    {
        if (idle is { } i)
            system.Idle = i;
        watcher.Step(now);
    }

    [Fact]
    public void Holds_while_a_session_works_and_releases_after()
    {
        Step();
        Assert.False(system.Req.Held);
        Session();
        Step();
        Assert.True(system.Req.Held);
        Assert.Contains(logged, line => line.StartsWith("holding"));
        Step(now: Now + 901);
        Assert.False(system.Req.Held);
        Assert.Contains(logged, line => line.StartsWith("released"));
        Assert.False(JsonNode.Parse(File.ReadAllText(config.StateFile))!["held"]!.GetValue<bool>());
    }

    [Fact]
    public void Not_on_battery_unless_allowed()
    {
        Session();
        system.Mains = false;
        Step();
        Assert.False(system.Req.Held);
        config.MainsOnly = false;
        Step();
        Assert.True(system.Req.Held);
    }

    [Fact]
    public void Unknown_power_source_counts_as_mains()
    {
        Session();
        system.Mains = null;
        Step();
        Assert.True(system.Req.Held);
    }

    [Fact]
    public void The_unattended_cap_releases_and_notifies_once()
    {
        Session(until: Now + 99999);
        Step(idle: 60);
        Assert.True(system.Req.Held);
        Step(idle: 3 * 3600);
        Assert.False(system.Req.Held);
        Step(now: Now + 15, idle: 3 * 3600 + 15);
        Assert.Single(notified);
        Step(now: Now + 30, idle: 0);             // someone's back
        Assert.True(system.Req.Held);
        Step(now: Now + 45, idle: 3 * 3600);
        Assert.Equal(2, notified.Count);
    }

    [Fact]
    public void The_cap_does_not_notify_with_no_session_working()
    {
        Step(idle: 4 * 3600);
        Assert.Empty(notified);
    }

    [Fact]
    public void Locks_once_at_the_lock_point_while_holding()
    {
        Session();
        Step(idle: 299);
        Assert.Equal(0, system.Locks);
        Step(now: Now + 1, idle: 300);
        Assert.Equal(1, system.Locks);
        Step(now: Now + 15, idle: 315);
        Assert.Equal(1, system.Locks);       // once per idle stretch
        Step(now: Now + 30, idle: 5);        // back at the keyboard
        Step(now: Now + 330, idle: 305);
        Assert.Equal(2, system.Locks);
    }

    [Fact]
    public void Does_not_lock_when_not_holding()
    {
        Step(idle: 600);
        Assert.Equal(0, system.Locks);
    }

    [Fact]
    public void Does_not_lock_while_another_program_keeps_the_display_on()
    {
        Session();
        system.State = 0x2;
        Step(idle: 600);
        Assert.Equal(0, system.Locks);
        Assert.Contains(logged, line => line.Contains("display on"));
        system.State = 0;
        Step(now: Now + 15, idle: 615);
        Assert.Equal(1, system.Locks);
    }

    [Fact]
    public void No_lock_when_windows_would_not_lock()
    {
        Session();
        system.Settings = Fake.Windows(sleep: 0, screensaver: Fake.Saver(active: false));
        Step(idle: 3000);
        Assert.Equal(0, system.Locks);
    }

    [Fact]
    public void Lock_never_and_fixed_minutes()
    {
        Session();
        config.Lock = "never";
        Step(idle: 3000);
        Assert.Equal(0, system.Locks);
        config.Lock = 1L;
        watcher.Point = null;
        Step(now: Now + 1, idle: 61);
        Assert.Equal(1, system.Locks);
    }

    [Fact]
    public void A_change_in_windows_settings_applies_within_a_minute()
    {
        Session(until: Now + 9999);
        Step(idle: 0);
        system.Settings = Fake.Windows(sleep: 120);
        Step(now: Now + 30, idle: 130);
        Assert.Equal(0, system.Locks);       // still the old 5 minutes
        Step(now: Now + 61, idle: 130);
        Assert.Equal(1, system.Locks);
        Assert.Contains(logged, line => line.StartsWith("lock point now 2 min"));
    }

    [Fact]
    public void Logs_alive_hourly()
    {
        Step();
        Step(now: Now + 1800);
        Step(now: Now + 3600);
        Assert.Equal(2, logged.Count(line => line.StartsWith("alive")));
    }

    [Fact]
    public void Closing_releases()
    {
        Session();
        Step();
        watcher.Close();
        Assert.False(system.Req.Held);
    }

    [Fact]
    public void The_state_file_is_written_as_python_wrote_it()
    {
        Session();
        Step(idle: 60);
        var state = JsonNode.Parse(File.ReadAllText(config.StateFile))!.AsObject();
        Assert.Equal(["held", "why", "since", "updated", "pid", "sessions", "mains", "idle", "lock_after", "lock_why"],
            state.Select(pair => pair.Key));
        var text = File.ReadAllText(config.StateFile);
        Assert.StartsWith("{\"held\": true, \"why\": \"1 session working\", \"since\": 1800000000.0, \"updated\": 1800000000.0, ", text);
        Assert.EndsWith("\"sessions\": [\"s1\"], \"mains\": true, \"idle\": 60.0, \"lock_after\": 300, "
                        + "\"lock_why\": \"sleep after 5 min, sign-in required every time\"}", text);
    }

    [Fact]
    public void The_log_lines_read_as_python_wrote_them()
    {
        Session();
        Step(idle: 90);
        Assert.Contains("holding: 1 session working; idle 2 min, mains", logged);   // 1.5 rounds to even, as Python does
        system.Mains = false;
        Step(now: Now + 1, idle: 150);
        Assert.Contains("released: on battery; idle 2 min, battery", logged);       // 2.5 rounds to even too
    }
}

public class LoopTests : Temp
{
    private Config LoopConfig(string name, double checkSeconds = 15)
    {
        var config = NewConfig(name);
        config.LeasesDir = LeasesDir;
        config.StateFile = Path.Combine(Folder, "state.json");
        config.CheckSeconds = checkSeconds;
        return config;
    }

    [Fact]
    public void Run_steps_then_releases_and_refuses_a_second_watcher()
    {
        var config = LoopConfig("c.toml", 0.01);
        var system = new FakeSystem();
        var logged = new List<string>();
        Directory.CreateDirectory(LeasesDir);
        File.WriteAllText(Path.Combine(LeasesDir, "s.json"), "{\"until\": 9000000000000.0, \"pid\": null}");
        Assert.Equal(0, Watcher.Run(config, logged.Add, (_, _) => { }, system, sleep: _ => { }, steps: 2));
        Assert.False(system.Req.Held);
        Assert.Equal([true, false], system.Req.History);
        Assert.StartsWith("started", logged[0]);

        // A mutex is re-entrant in one thread, so hold it from another one.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = new Thread(() =>
        {
            using var instance = Locks.SingleInstance(Watcher.LockName(config));
            held.Set();
            release.Wait();
        });
        other.Start();
        try
        {
            Assert.True(held.Wait(10_000, TestContext.Current.CancellationToken));
            Assert.Equal(3, Watcher.Run(config, logged.Add, (_, _) => { }, new FakeSystem(), steps: 1));
        }
        finally
        {
            release.Set();
            other.Join();
        }
    }

    [Fact]
    public void Errors_are_logged_and_notified_on_the_third()
    {
        var config = LoopConfig("c.toml");
        var system = new FakeSystem { IdleFunc = () => throw new DivideByZeroException() };
        var logged = new List<string>();
        var notified = new List<string>();
        Watcher.Run(config, logged.Add, (title, _) => notified.Add(title), system, sleep: _ => { }, steps: 4);
        Assert.Equal(4, logged.Count(line => line.StartsWith("error")));
        Assert.Single(notified);
    }
}
