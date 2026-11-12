using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace AiKeepAwake.Tests;

public class HookTests : Temp
{
    private string RunHook(string action, JsonObject? data = null, double now = Now, Dictionary<string, string>? env = null,
        params string[] extra)
    {
        var payload = new JsonObject { ["session_id"] = "s1", ["hook_event_name"] = "X" };
        foreach (var (key, value) in data ?? [])
            payload[key] = value?.DeepClone();
        var argv = new List<string> { action, "--dir", LeasesDir };
        argv.AddRange(extra);
        return Hook.Handle(argv, payload, name => env?.GetValueOrDefault(name), now, ppid: 42);
    }

    private static JsonObject Tasks(params JsonObject[] tasks)
    {
        var list = new JsonArray();
        foreach (var task in tasks)
            list.Add((JsonNode)task);
        return new JsonObject { ["background_tasks"] = list };
    }

    private static JsonObject Running => new() { ["status"] = "running" };

    private double Until() => LeaseFile()!["until"]!.GetValue<double>();

    [Fact]
    public void A_lease_being_read_can_still_be_cleared()
    {
        RunHook("busy");
        var path = Path.Combine(LeasesDir, "s1.json");
        // The watcher reads leases sharing write and delete, so the hook's delete goes through while it reads.
        // (Replacing a file needs it closed whoever shares what, so a replace is retried: the tests below.)
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Assert.Equal("cleared", RunHook("stop", now: Now + 10));
        Assert.Null(LeaseFile());
        RunHook("busy");
        Assert.Single(Leases.Read(LeasesDir));   // and the watcher's read itself works
    }

    [Fact]
    public void A_lease_held_for_a_moment_is_cleared_on_the_retry()
    {
        RunHook("busy");
        var path = Path.Combine(LeasesDir, "s1.json");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);   // as File.ReadAllBytes holds it
        var release = new Thread(() =>
        {
            Thread.Sleep(10);
            held.Dispose();
        });
        release.Start();
        Assert.Equal("cleared", RunHook("stop", now: Now + 10));
        release.Join();
        Assert.Null(LeaseFile());
    }

    [Fact]
    public void A_lease_held_for_a_moment_is_replaced_on_the_retry()
    {
        RunHook("busy");
        var path = Path.Combine(LeasesDir, "s1.json");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = new Thread(() =>
        {
            Thread.Sleep(10);
            held.Dispose();
        });
        release.Start();
        Assert.Equal("wrote", RunHook("busy", now: Now + 120));
        release.Join();
        Assert.Equal(Now + 120 + Hook.Lease, Until());
        Assert.Empty(Directory.GetFiles(LeasesDir, "*.tmp"));
    }

    [Fact]
    public void Busy_writes_a_fifteen_minute_lease()
    {
        Assert.Equal("wrote", RunHook("busy"));
        var lease = LeaseFile()!;
        Assert.Equal(Now + 900, lease["until"]!.GetValue<double>());
        Assert.False(lease["bg"]!.GetValue<bool>());
        Assert.Equal(42, lease["pid"]!.GetValue<long>());
    }

    [Fact]
    public void Busy_is_skipped_if_renewed_under_a_minute_ago()
    {
        RunHook("busy");
        Assert.Equal("skipped", RunHook("busy", now: Now + 30));
        Assert.Equal(Now + 900, Until());
        Assert.Equal("wrote", RunHook("busy", now: Now + 61));
        Assert.Equal(Now + 61 + 900, Until());
    }

    [Fact]
    public void Stop_with_nothing_in_flight_clears()
    {
        RunHook("busy");
        Assert.Equal("cleared", RunHook("stop", Tasks()));
        Assert.Null(LeaseFile());
    }

    [Fact]
    public void Stop_with_only_finished_tasks_clears()
    {
        RunHook("busy");
        RunHook("stop", Tasks(new JsonObject { ["id"] = "1", ["status"] = "completed" }));
        Assert.Null(LeaseFile());
    }

    [Fact]
    public void Stop_with_background_work_sets_a_background_lease()
    {
        RunHook("busy");
        RunHook("stop", Tasks(new JsonObject { ["id"] = "t", ["type"] = "shell", ["status"] = "running" }), now: Now + 5);
        var lease = LeaseFile()!;
        Assert.True(lease["bg"]!.GetValue<bool>());
        Assert.Equal(Now + 5 + 2700, lease["until"]!.GetValue<double>());
    }

    [Fact]
    public void Idle_prompt_does_not_clear_a_background_lease()
    {
        RunHook("stop", Tasks(Running));
        Assert.Equal("kept", RunHook("idle", now: Now + 60));
        Assert.True(LeaseFile()!["bg"]!.GetValue<bool>());
    }

    [Fact]
    public void Idle_clears_a_foreground_lease()
    {
        RunHook("busy");
        Assert.Equal("cleared", RunHook("idle"));
        Assert.Null(LeaseFile());
    }

    [Fact]
    public void End_clears_even_a_background_lease()
    {
        RunHook("stop", Tasks(Running));
        Assert.Equal("cleared", RunHook("end"));
        Assert.Null(LeaseFile());
    }

    [Fact]
    public void Activity_during_background_work_keeps_it_a_background_lease()
    {
        RunHook("stop", Tasks(Running));
        Assert.Equal("skipped", RunHook("busy", now: Now + 60));     // 45 minutes is longer anyway
        Assert.Equal("wrote", RunHook("busy", now: Now + 2000));      // under 15 minutes left
        var lease = LeaseFile()!;
        Assert.True(lease["bg"]!.GetValue<bool>());
        Assert.Equal(Now + 2000 + 900, lease["until"]!.GetValue<double>());
    }

    [Fact]
    public void An_expired_background_lease_is_not_carried_forward()
    {
        RunHook("stop", Tasks(Running));
        RunHook("busy", now: Now + 3000);
        Assert.False(LeaseFile()!["bg"]!.GetValue<bool>());
    }

    [Fact]
    public void Opt_out()
    {
        Assert.Equal("off", RunHook("busy", env: new() { ["AI_KEEP_AWAKE"] = "0" }));
        Assert.Null(LeaseFile());
    }

    [Fact]
    public void Lease_lengths_from_arguments()
    {
        RunHook("busy", extra: ["--lease", "60"]);
        Assert.Equal(Now + 60, Until());
        RunHook("stop", Tasks(new JsonObject()), extra: ["--bg-lease", "120"]);
        Assert.Equal(Now + 120, Until());
    }

    [Fact]
    public void Session_ids_cannot_escape_the_folder()
    {
        Hook.Handle(["busy", "--dir", LeasesDir], new JsonObject { ["session_id"] = @"..\..\evil" }, _ => null, Now, ppid: 1);
        Assert.Equal(["evil.json"], Directory.GetFiles(LeasesDir).Select(Path.GetFileName));
    }

    [Fact]
    public void The_lease_is_written_as_python_wrote_it_with_the_agent_added()
    {
        RunHook("busy", new JsonObject { ["cwd"] = @"C:\work" });
        var text = File.ReadAllText(Path.Combine(LeasesDir, "s1.json"));
        Assert.Equal("{\"until\": 1800000900.0, \"bg\": false, \"pid\": 42, \"cwd\": \"C:\\\\work\", \"event\": \"X\", "
                     + "\"at\": 1800000000.0, \"agent\": \"claude-code\"}", text);
    }

    // ---- The real program, run as Claude Code runs it ----------------------------------------------------

    private (int Code, string Out, string Err, TimeSpan Took) RunExe(byte[] stdin, Dictionary<string, string>? env = null, params string[] args)
    {
        var info = new ProcessStartInfo(BuiltExe("BackgroundExe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        info.Environment["LOCALAPPDATA"] = Path.Combine(Folder, "Local");   // never the real lease folder
        info.Environment["APPDATA"] = Path.Combine(Folder, "Roaming");
        info.Environment.Remove("AI_KEEP_AWAKE");
        foreach (var (key, value) in env ?? [])
            info.Environment[key] = value;
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(info)!;
        process.StandardInput.BaseStream.Write(stdin);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000));
        return (process.ExitCode, output.Result, error.Result, clock.Elapsed);
    }

    [Fact]
    public void The_program_always_exits_0_and_prints_nothing()
    {
        foreach (var stdin in new[] { "not json", "", "[1, 2]", "{\"session_id\": \"x\"}" })
        {
            var (code, output, error, _) = RunExe(Encoding.UTF8.GetBytes(stdin), null, "hook", "busy", "--dir", LeasesDir);
            Assert.Equal((0, "", ""), (code, output, error));
        }
        var bare = RunExe("{}"u8.ToArray(), null, "hook");
        Assert.Equal((0, ""), (bare.Code, bare.Out));
        Assert.True(File.Exists(Path.Combine(LeasesDir, "x.json")));
    }

    /// <summary>Replaces the Python "imports nothing heavy" test: the hook path must never read the settings
    /// file, so a broken one can't slow it or stop it.</summary>
    [Fact]
    public void The_hook_never_reads_the_settings_file()
    {
        var broken = Path.Combine(Folder, "broken.toml");
        File.WriteAllText(broken, "this is = = not toml");
        var run = RunExe("{\"session_id\": \"s1\"}"u8.ToArray(), new() { ["AI_KEEP_AWAKE_CONFIG"] = broken }, "hook", "busy", "--dir", LeasesDir);
        Assert.Equal(0, run.Code);
        Assert.NotNull(LeaseFile());
    }

    [Fact]
    public void The_off_switch_reaches_the_program()
    {
        RunExe("{\"session_id\": \"s1\"}"u8.ToArray(), new() { ["AI_KEEP_AWAKE"] = "off" }, "hook", "busy", "--dir", LeasesDir);
        Assert.Null(LeaseFile());
    }

    /// <summary>The startup check: a published (Native AOT) hook call stays under 100 ms. It needs the program
    /// published to out\ first (dotnet publish src/AiKeepAwake.Background -c Release -r win-x64 -o out), so it's
    /// skipped when that hasn't been done, as in a plain `dotnet test`.</summary>
    [Fact]
    public void A_published_hook_call_takes_under_100_ms()
    {
        var exe = Path.GetFullPath(BuiltExe("PublishedBackgroundExe"));
        if (!File.Exists(exe))
            Assert.Skip($"Not published to {exe}; see the comment.");
        var times = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true };
            foreach (var arg in new[] { "hook", "busy", "--dir", LeasesDir })
                info.ArgumentList.Add(arg);
            var clock = Stopwatch.StartNew();
            using var process = Process.Start(info)!;
            process.StandardInput.Write($"{{\"session_id\": \"t{i}\"}}");
            process.StandardInput.Close();
            process.WaitForExit();
            times.Add(clock.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Assert.True(times[times.Count / 2] < 100, $"median {times[times.Count / 2]:0} ms");
    }
}
