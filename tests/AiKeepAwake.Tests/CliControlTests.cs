using System.Text.Json.Nodes;

namespace AiKeepAwake.Tests;

/// <summary>The commands added in 1.1.0: awake, pause, status --json, install --hooks-only, and install inside Claude Code.</summary>
public class CliControlTests : Temp
{
    private readonly FakeTasks tasks = new();
    private readonly string dist;
    private readonly string settings;
    private readonly string installDir;
    private readonly string configPath;
    private StringWriter output = new();
    private StringWriter error = new();

    public CliControlTests()
    {
        dist = Path.Combine(Folder, "download");
        Directory.CreateDirectory(dist);
        foreach (var name in Programs.Names)
            File.WriteAllText(Path.Combine(dist, name), "new");
        settings = Path.Combine(Folder, "settings.json");
        File.WriteAllText(settings, "{\"model\": \"opus\"}\n");
        installDir = Path.Combine(Folder, "Local", "Programs", "ai-keep-awake");
        configPath = Path.Combine(Folder, "none.toml");
    }

    private int Run(params string[] args)
    {
        (output, error) = (new StringWriter(), new StringWriter());
        var host = new CliHost
        {
            Out = output,
            Err = error,
            Env = Env,
            Tasks = name => tasks[name],
            System = () => new FakeSystem(),
            Notify = (_, _) => { },
            ProgramDir = dist,
            Clock = () => Now,
            CopyPause = TimeSpan.Zero,
        };
        return Cli.Run(["--config", configPath, .. args], host);
    }

    private Config Loaded() => Config.Load(configPath, Env);

    [Fact]
    public void Awake_keeps_it_awake_by_hand_for_a_time()
    {
        Assert.Equal(0, Run("awake", "2h"));
        var manual = Control.ReadManual(Loaded());
        Assert.NotNull(manual);
        Assert.Equal(Now + 7200, manual.Until);
        Assert.Null(manual.Pid);                                         // timed only: nothing to tie it to
        Assert.Contains($"Kept awake by hand until {Control.Clock(Now + 7200)}", output.ToString());
        Assert.Contains("on mains only", output.ToString());
        Assert.Contains("The watcher isn't running", output.ToString());
        Assert.Equal(0, Run("awake", "off"));
        Assert.Contains("No longer kept awake by hand.", output.ToString());
        Assert.Null(Control.ReadManual(Loaded()));
        Assert.Equal(0, Run("awake", "off"));
        Assert.Contains("It wasn't being kept awake by hand.", output.ToString());
    }

    [Fact]
    public void Pause_for_a_time_until_resumed_and_off()
    {
        Assert.Equal(0, Run("awake", "1h"));
        Assert.Equal(0, Run("pause", "1h"));
        Assert.Contains($"Paused until {Control.Clock(Now + 3600)}", output.ToString());
        Assert.Contains("no longer kept awake by hand", output.ToString());
        Assert.Null(Control.ReadManual(Loaded()));
        Assert.Equal(0, Run("pause", "on"));
        Assert.Contains("Paused until resumed (or the next logoff)", output.ToString());
        Assert.Null(Control.ReadPause(Loaded())!.Until);
        Assert.Equal(0, Run("status"));
        Assert.Contains("Paused:     until resumed (ai-keep-awake pause off)", output.ToString());
        Assert.Equal(0, Run("pause", "off"));
        Assert.Contains("Resumed", output.ToString());
        Assert.Null(Control.ReadPause(Loaded()));
    }

    [Theory]
    [InlineData("awake")]
    [InlineData("awake", "forever")]
    [InlineData("awake", "on")]
    [InlineData("pause", "25h")]
    [InlineData("awake", "1h", "2h")]
    public void Bad_control_arguments_exit_2(params string[] args)
    {
        Assert.Equal(2, Run(args));
        Assert.Contains("ai-keep-awake: error:", error.ToString());
    }

    [Fact]
    public void Status_shows_keeping_awake_by_hand_apart_from_the_sessions()
    {
        Assert.Equal(0, Run("awake", "1h"));
        Assert.Equal(0, Run("status"));
        var text = output.ToString();
        Assert.Contains($"By hand:    kept awake until {DateTimeOffset.FromUnixTimeSeconds((long)Now + 3600).ToLocalTime():HH:mm:ss}", text);
        Assert.Contains("Leases:     none", text);
    }

    [Fact]
    public void Status_json_has_the_same_facts()
    {
        Directory.CreateDirectory(LeasesDir);
        var leases = Path.Combine(Folder, "Local", "ai-keep-awake", "leases");
        Directory.CreateDirectory(leases);
        File.WriteAllText(Path.Combine(leases, "abc.json"),
            $"{{\"until\": {Py.Repr(Now + 900)}, \"bg\": true, \"pid\": null, \"cwd\": \"C:\\\\work\\\\proj\", \"event\": \"Stop\"}}");
        Assert.Equal(0, Run("awake", "1h"));
        Assert.Equal(0, Run("status", "--json"));
        var status = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal(1, status["format"]!.GetValue<int>());
        Assert.Equal(Cli.Version, status["version"]!.GetValue<string>());
        Assert.False(status["hooks"]!["installed"]!.GetValue<bool>());
        Assert.Empty(status["hooks"]!["events"]!.AsArray());
        Assert.False(status["watcher"]!["running"]!.GetValue<bool>());
        Assert.Null(status["watcher"]!["task"]);
        Assert.Null(status["request"]);
        Assert.Null(status["paused"]);
        Assert.Equal(Now + 3600, status["manual"]!["until"]!.GetValue<double>());
        var lease = Assert.Single(status["leases"]!.AsArray())!;
        Assert.Equal("abc", lease["session"]!.GetValue<string>());
        Assert.True(lease["live"]!.GetValue<bool>());
        Assert.True(lease["background"]!.GetValue<bool>());
        Assert.Equal(@"C:\work\proj", lease["cwd"]!.GetValue<string>());
        Assert.Equal(300, status["lock"]!["after"]!.GetValue<double>());
        Assert.True(status["power"]!["mains"]!.GetValue<bool>());
        Assert.True(status["rules"]!["mains_only"]!.GetValue<bool>());
        Assert.Empty(tasks.Calls);

        Run("install", "--settings", settings, "--no-autostart");
        File.Copy(settings, Path.Combine(Folder, "home", ".claude", "settings.json").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)));
        Assert.Equal(0, Run("status", "--json"));
        status = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.True(status["hooks"]!["installed"]!.GetValue<bool>());
        Assert.Equal(7, status["hooks"]!["events"]!.AsArray().Count);
        Assert.True(status["programs"]!["installed"]!.GetValue<bool>());
    }

    [Fact]
    public void The_status_text_lines_the_kit_reads_are_unchanged()
    {
        Assert.Equal(0, Run("status"));
        Assert.Matches("(?m)^Hooks:      not installed \\(", output.ToString());
        Assert.Matches("(?m)^Watcher:    not running; no logon task \\(ai-keep-awake install\\)", output.ToString());
    }

    [Fact]
    public void Install_hooks_only_puts_the_hooks_back_and_nothing_else()
    {
        Assert.Equal(0, Run("install", "--hooks-only", "--settings", settings, "--dry-run"));
        Assert.Contains("+  \"hooks\": {", output.ToString());
        Assert.Equal(0, Run("install", "--hooks-only", "--settings", settings));
        Assert.Contains("Hooks added", output.ToString());
        Assert.Contains("isn't there, so the hooks can't run yet", output.ToString());
        Assert.Equal(Install.Events.Length, Install.Installed(settings).Count);
        Assert.False(Directory.Exists(installDir));
        Assert.Empty(tasks.Calls);
        Assert.Equal(2, Run("uninstall", "--hooks-only"));              // install's only
    }

    [Fact]
    public void Inside_claude_code_install_leaves_the_logon_task_for_the_person()
    {
        EnvVars[Cli.ClaudeCodeVariable] = "1";
        Assert.Equal(0, Run("install", "--settings", settings, "--dry-run"));
        Assert.Contains("Inside Claude Code, so it wouldn't register the logon task", output.ToString());
        Assert.DoesNotContain("Would register", output.ToString());
        Assert.Equal(0, Run("install", "--settings", settings));
        Assert.DoesNotContain(tasks.Calls, call => call.StartsWith("enable"));
        Assert.Contains("run this yourself at a console:", output.ToString());
        Assert.Contains($"& \"{Path.Combine(installDir, Install.ConsoleExe)}\" autostart on", output.ToString());
        Assert.Equal(Install.Events.Length, Install.Installed(settings).Count);
        Assert.True(File.Exists(Path.Combine(installDir, Install.BackgroundExe)));
    }

    [Fact]
    public void An_upgrade_that_does_not_register_restarts_a_watcher_running_this_install()
    {
        Directory.CreateDirectory(installDir);
        foreach (var name in Programs.Names)
            File.WriteAllText(Path.Combine(installDir, name), "old");
        tasks[AutoStart.TaskName].Exists = true;
        tasks[AutoStart.TaskName].Command = $"{Path.Combine(installDir, Install.BackgroundExe)} --config {configPath} watch";
        Assert.Equal(0, Run("install", "--settings", settings, "--no-autostart", "--dry-run"));
        Assert.Contains("stopping the watcher first and starting it again after", output.ToString());
        Assert.Equal(0, Run("install", "--settings", settings, "--no-autostart"));
        Assert.Equal(["stop ai-keep-awake watch", "start ai-keep-awake watch"], tasks.Calls);
        Assert.Contains("The watcher was started again, on the new programs.", output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal("new", File.ReadAllText(Path.Combine(installDir, Install.BackgroundExe)));
    }

    [Fact]
    public void A_watcher_task_running_another_copy_is_left_alone()
    {
        tasks[AutoStart.TaskName].Exists = true;
        tasks[AutoStart.TaskName].Command = @"C:\elsewhere\ai-keep-awake-background.exe --config x watch";
        Assert.Equal(0, Run("install", "--settings", settings, "--no-autostart"));
        Assert.Empty(tasks.Calls);
    }

    [Fact]
    public void The_help_lists_the_new_commands()
    {
        Assert.Equal(0, Run("--help"));
        Assert.Contains("ai-keep-awake awake 1h|2h|30m|off", output.ToString());
        Assert.Contains("ai-keep-awake pause 1h|on|off", output.ToString());
        Assert.Contains("--json", output.ToString());
        Assert.Contains("--hooks-only", output.ToString());
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
