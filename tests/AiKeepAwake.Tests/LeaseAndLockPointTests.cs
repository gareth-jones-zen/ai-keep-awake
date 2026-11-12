using WinBackground;

namespace AiKeepAwake.Tests;

public class LeaseTests : Temp
{
    private void Write(string session, double until, long? pid = null, bool bg = false)
    {
        Directory.CreateDirectory(LeasesDir);
        var pidText = pid is { } p ? p.ToString() : "null";
        File.WriteAllText(Path.Combine(LeasesDir, $"{session}.json"),
            $"{{\"until\": {Py.Repr(until)}, \"bg\": {(bg ? "true" : "false")}, \"pid\": {pidText}}}");
    }

    [Fact]
    public void Only_unexpired_leases_are_live()
    {
        Write("a", Now + 10);
        Write("b", Now - 10);
        Assert.Equal(["a"], Leases.Live(LeasesDir, Now).Select(lease => lease.Session));
    }

    [Fact]
    public void A_lease_whose_process_has_gone_is_not_live()
    {
        Write("a", Now + 10, pid: 1);
        Write("b", Now + 10, pid: 2);
        var found = Leases.Live(LeasesDir, Now, checkPid: true, alive: pid => pid == 2);
        Assert.Equal(["b"], found.Select(lease => lease.Session));
        Assert.Equal(2, Leases.Live(LeasesDir, Now, checkPid: false, alive: _ => false).Count);
    }

    [Fact]
    public void Tidy_removes_day_old_leases_only()
    {
        Write("old", Now - 2 * 86400);
        Write("recent", Now - 60);
        Assert.Equal(1, Leases.Tidy(LeasesDir, Now));
        Assert.Equal(["recent"], Leases.Read(LeasesDir).Select(lease => lease.Session));
    }

    [Fact]
    public void A_missing_folder_or_bad_file_is_no_lease()
    {
        Assert.Empty(Leases.Live(Path.Combine(Folder, "nowhere"), Now));
        Directory.CreateDirectory(LeasesDir);
        File.WriteAllText(Path.Combine(LeasesDir, "bad.json"), "{");
        Assert.Empty(Leases.Read(LeasesDir));
    }

    [Fact]
    public void Pid_alive_is_true_for_this_process_and_false_for_none()
    {
        Assert.True(Leases.PidAlive(Environment.ProcessId));
        Assert.False(Leases.PidAlive(0x7FFFFFF0));
    }
}

public class LockPointTests
{
    private static void Check(LockSettings settings, double? seconds, string whyHas)
    {
        var point = LockPoint.Windows(settings);
        Assert.Equal(seconds, point.Seconds);
        Assert.Contains(whyHas, point.Why);
    }

    [Fact]
    public void Sleep_with_sign_in_every_time_locks_at_the_sleep_timeout() =>
        Check(Fake.Windows(sleep: 300, signIn: 0), 300, "sleep after 5 min");

    [Fact]
    public void A_sign_in_grace_is_added_to_the_sleep_timeout() =>
        Check(Fake.Windows(sleep: 300, signIn: 60), 360, "after 1 min asleep");

    [Fact]
    public void Sleep_never_leaves_the_screensaver() => Check(Fake.Windows(sleep: 0), 1800, "screensaver");

    [Fact]
    public void Sign_in_on_wake_off_leaves_the_screensaver() => Check(Fake.Windows(sleep: 300, signIn: null), 1800, "screensaver");

    [Fact]
    public void An_earlier_secure_screensaver_wins() =>
        Check(Fake.Windows(sleep: 1800, screensaver: Fake.Saver(timeout: 600)), 600, "screensaver after 10 min");

    [Fact]
    public void A_screensaver_that_does_not_lock_is_ignored()
    {
        foreach (var kind in new[] { Fake.Saver(secure: false), Fake.Saver(active: false), Fake.Saver(program: null), Fake.Saver(timeout: 0) })
            Check(Fake.Windows(sleep: 0, screensaver: kind), null, "wouldn't lock");
    }

    [Fact]
    public void The_inactivity_limit_counts()
    {
        Check(Fake.Windows(sleep: 0, screensaver: Fake.Saver(active: false), inactivity: 900), 900, "inactivity limit");
        Check(Fake.Windows(sleep: 600, inactivity: 120), 120, "inactivity limit");
    }

    [Fact]
    public void Nothing_locks_means_no_lock() => Check(Fake.Windows(sleep: 0, screensaver: Fake.Saver(active: false)), null, "wouldn't lock");

    [Fact]
    public void An_unreadable_sleep_timeout_falls_back_to_the_display_then_five_minutes()
    {
        Check(Fake.Windows(sleep: null, display: 180), 180, "assumed");
        Check(Fake.Windows(sleep: null, display: null), 300, "assumed");
    }

    [Fact]
    public void The_setting_can_override_windows()
    {
        LockSettings Fail() => throw new InvalidOperationException("not read");
        Assert.Null(LockPoint.Effective("never", Fail).Seconds);
        Assert.Equal(600, LockPoint.Effective(10L, Fail).Seconds);
        Assert.Equal(300, LockPoint.Effective("windows", () => Fake.Windows()).Seconds);
    }

    [Fact]
    public void Minutes_reads_as_python_printed_it()
    {
        Assert.Equal("never", LockPoint.Minutes(null));
        Assert.Equal("5 min", LockPoint.Minutes(300));
        Assert.Equal("90 s", LockPoint.Minutes(90));
        Assert.Equal("0 min", LockPoint.Minutes(0));
        Assert.Equal("lock = 1.5 minutes in the settings", LockPoint.Effective(1.5, () => Fake.Windows()).Why);
    }
}

/// <summary>The real Windows calls, read-only apart from a power request that's created and cleared at once.</summary>
public class WindowsTests
{
    [Fact]
    public void A_power_request_can_be_held_and_released()
    {
        using var request = new SystemRequired("ai-keep-awake test (cleared at once)");
        request.Hold(true);
        Assert.True(request.Held);
        Assert.NotEqual(0u, Power.ExecutionState() & Power.EsSystemRequired);
        request.Hold(false);
        Assert.False(request.Held);
    }

    [Fact]
    public void The_lock_settings_and_idle_time_can_be_read()
    {
        var settings = Power.LockSettings();
        Assert.False(string.IsNullOrEmpty(settings.SleepSource));
        Assert.True(Power.IdleSeconds() >= 0);
        Assert.True(Power.LockSettings(ac: true).OnMains);
    }
}
