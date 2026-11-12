using System.Text;
using System.Text.Json.Nodes;

namespace AiKeepAwake.Tests;

public class CliTests : Temp
{
    private readonly FakeTasks tasks = new();
    private readonly string dist;
    private readonly string settings;
    private readonly string installDir;
    private readonly string configPath;
    private StringWriter output = new();
    private StringWriter error = new();

    public CliTests()
    {
        dist = Path.Combine(Folder, "download");
        Directory.CreateDirectory(dist);
        File.WriteAllText(Path.Combine(dist, Install.ConsoleExe), "console");
        File.WriteAllText(Path.Combine(dist, Install.BackgroundExe), "background");
        settings = Path.Combine(Folder, "settings.json");
        File.WriteAllText(settings, "{\"model\": \"opus\"}\n");
        installDir = Path.Combine(Folder, "Local", "Programs", "ai-keep-awake");   // the default, from the fake environment
        configPath = Path.Combine(Folder, "none.toml");
    }

    private int RunFrom(string? programDir, params string[] args)
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
            ProgramDir = programDir ?? dist,
            Clock = () => Now,
            CopyPause = TimeSpan.Zero,
            Stdin = () => new MemoryStream("{\"session_id\": \"s1\"}"u8.ToArray()),
        };
        return Cli.Run(["--config", configPath, .. args], host);
    }

    private int Run(params string[] args) => RunFrom(null, args);

    [Fact]
    public void Install_and_uninstall_from_the_command_line()
    {
        Assert.Equal(0, Run("install", "--settings", settings, "--dry-run"));
        Assert.Contains("+  \"hooks\": {", output.ToString());
        Assert.Contains("Would register", output.ToString());
        Assert.Equal("{\"model\": \"opus\"}\n", File.ReadAllText(settings));
        Assert.Equal(0, Run("install", "--settings", settings, "--no-autostart"));
        Assert.Equal(Install.Events.Length, Install.Installed(settings).Count);
        Assert.Equal(0, Run("uninstall", "--settings", settings, "--no-autostart"));
        Assert.True(PyJson.Equal(JsonNode.Parse("{\"model\": \"opus\"}"), JsonNode.Parse(File.ReadAllText(settings))));
        File.WriteAllText(settings, "{oops");
        Assert.Equal(2, Run("install", "--settings", settings, "--no-autostart"));
        Assert.Contains("isn't valid JSON", error.ToString());
    }

    [Fact]
    public void Install_copies_the_programs_and_points_the_hooks_and_the_task_there()
    {
        tasks[AutoStart.TaskName].Exists = true;
        tasks[AutoStart.OldTaskName].Exists = true;
        Assert.Equal(0, Run("install", "--settings", settings));
        Assert.Equal("background", File.ReadAllText(Path.Combine(installDir, Install.BackgroundExe)));
        Assert.Equal("console", File.ReadAllText(Path.Combine(installDir, Install.ConsoleExe)));
        Assert.Equal(["stop ai-keep-awake watch", "disable claude-keep-awake watch", "enable ai-keep-awake watch"], tasks.Calls);
        var exe = Path.Combine(installDir, Install.BackgroundExe);
        var hook = JsonNode.Parse(File.ReadAllText(settings))!["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!;
        Assert.Equal(exe, hook["command"]!.GetValue<string>());
        Assert.Equal(["hook", "busy"], hook["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        var (taskExe, taskArgs, workingDir) = tasks[AutoStart.TaskName].Enabled!.Value;
        Assert.Equal(exe, taskExe);
        Assert.Equal(["--config", configPath, "watch"], taskArgs);
        Assert.Equal(Folder, workingDir);
        Assert.Contains("The old task 'claude-keep-awake watch' is removed", output.ToString());
    }

    [Fact]
    public void Install_from_the_install_folder_copies_nothing()
    {
        Directory.CreateDirectory(installDir);
        File.Copy(Path.Combine(dist, Install.ConsoleExe), Path.Combine(installDir, Install.ConsoleExe));
        File.Copy(Path.Combine(dist, Install.BackgroundExe), Path.Combine(installDir, Install.BackgroundExe));
        Assert.Equal(0, RunFrom(installDir + @"\", "install", "--settings", settings));
        Assert.DoesNotContain("stop ai-keep-awake watch", tasks.Calls);
        Assert.DoesNotContain("Copied", output.ToString());
        Assert.Contains("Hooks added", output.ToString());
    }

    [Fact]
    public void An_install_dry_run_reports_the_copy_the_diff_and_both_tasks()
    {
        tasks[AutoStart.OldTaskName].Exists = true;
        Assert.Equal(0, Run("install", "--settings", settings, "--dry-run"));
        var text = output.ToString();
        Assert.Contains($"Would copy ai-keep-awake.exe and ai-keep-awake-background.exe to {installDir}, stopping the watcher first.", text);
        Assert.Contains("+            \"command\": \"" + installDir.Replace(@"\", @"\\") + @"\\ai-keep-awake-background.exe" + "\",", text);
        Assert.Contains($"Would register the Task Scheduler task 'ai-keep-awake watch'", text);
        Assert.Contains("Would remove the old task 'claude-keep-awake watch'", text);
        Assert.Empty(tasks.Calls);
        Assert.False(Directory.Exists(installDir));
    }

    private void OldInstall()
    {
        Directory.CreateDirectory(installDir);
        foreach (var name in Programs.Names)
            File.WriteAllText(Path.Combine(installDir, name), "old");
        tasks[AutoStart.TaskName].Exists = true;
    }

    [Fact]
    public void A_failed_copy_starts_the_stopped_watcher_again()
    {
        OldInstall();
        using (new FileStream(Path.Combine(installDir, Install.BackgroundExe), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(1, Run("install", "--settings", settings));
        Assert.Equal(["stop ai-keep-awake watch", "start ai-keep-awake watch"], tasks.Calls);
        Assert.Contains("The programs couldn't be copied", error.ToString());
        Assert.Contains("The watcher was started again", error.ToString());
        Assert.DoesNotContain("autostart off", error.ToString());   // the old, misleading advice
        Assert.Equal("{\"model\": \"opus\"}\n", File.ReadAllText(settings));
        foreach (var name in Programs.Names)
            Assert.Equal("old", File.ReadAllText(Path.Combine(installDir, name)));   // not half old, half new
    }

    [Fact]
    public void A_failed_settings_write_starts_the_stopped_watcher_again()
    {
        OldInstall();
        // Readable, so the first check passes, but it can't be replaced.
        using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(2, Run("install", "--settings", settings));
        Assert.Equal(["stop ai-keep-awake watch", "start ai-keep-awake watch"], tasks.Calls);
        Assert.Contains("The watcher was started again", error.ToString());
    }

    [Fact]
    public void A_failed_task_registration_starts_the_stopped_watcher_again()
    {
        OldInstall();
        tasks[AutoStart.TaskName].FailOn.Add("enable");
        Assert.Equal(1, Run("install", "--settings", settings));
        Assert.Equal(["stop ai-keep-awake watch", "disable claude-keep-awake watch", "enable ai-keep-awake watch", "start ai-keep-awake watch"],
            tasks.Calls);
        Assert.Contains("couldn't be registered", error.ToString());
    }

    [Fact]
    public void A_watcher_that_cannot_be_restarted_is_reported_not_thrown()
    {
        OldInstall();
        tasks[AutoStart.TaskName].FailOn.UnionWith(["enable", "start"]);
        Assert.Equal(1, Run("install", "--settings", settings));
        Assert.Contains("The watcher couldn't be started again", error.ToString());
        Assert.Contains("ai-keep-awake autostart on", error.ToString());
    }

    [Fact]
    public void A_watcher_that_cannot_be_stopped_changes_nothing()
    {
        OldInstall();
        tasks[AutoStart.TaskName].FailOn.Add("stop");
        Assert.Equal(1, Run("install", "--settings", settings));
        Assert.Equal(["stop ai-keep-awake watch", "start ai-keep-awake watch"], tasks.Calls);   // in case it stopped part way
        Assert.Contains("couldn't be stopped", error.ToString());
        Assert.Equal("old", File.ReadAllText(Path.Combine(installDir, Install.BackgroundExe)));
    }

    [Fact]
    public void Install_stops_if_a_program_is_missing()
    {
        File.Delete(Path.Combine(dist, Install.BackgroundExe));
        Assert.Equal(1, Run("install", "--settings", settings));
        Assert.Contains("ai-keep-awake-background.exe isn't beside this program", error.ToString());
        Assert.Equal("{\"model\": \"opus\"}\n", File.ReadAllText(settings));
        Assert.Empty(tasks.Calls);
    }

    [Fact]
    public void The_install_folder_can_be_given()
    {
        var elsewhere = Path.Combine(Folder, "elsewhere");
        Assert.Equal(0, Run("install", "--settings", settings, "--no-autostart", "--install-dir", elsewhere));
        Assert.True(File.Exists(Path.Combine(elsewhere, Install.BackgroundExe)));
        Assert.Contains(Path.Combine(elsewhere, Install.BackgroundExe).Replace(@"\", @"\\"), File.ReadAllText(settings));
        Assert.False(Directory.Exists(installDir));
    }

    [Fact]
    public void Task_errors_are_reported_not_thrown()
    {
        Run("install", "--settings", settings);
        var leases = Path.Combine(Folder, "Local", "ai-keep-awake", "leases");   // the default, from the fake environment
        Directory.CreateDirectory(leases);
        File.WriteAllText(Path.Combine(leases, "s1.json"), "{}");
        foreach (var name in new[] { AutoStart.TaskName, AutoStart.OldTaskName })
            tasks[name].FailOn.UnionWith(["disable", "status"]);

        // Uninstall: the hooks come out and the leases go, and the task failure makes it exit 1.
        Assert.Equal(1, Run("uninstall", "--settings", settings));
        Assert.Contains("'ai-keep-awake watch' couldn't be removed: Couldn't disable the task", error.ToString());
        Assert.Contains("'claude-keep-awake watch' couldn't be removed", error.ToString());
        Assert.Empty(Install.Installed(settings));
        Assert.Contains("1 lease file(s) cleared.", output.ToString());

        Assert.Equal(1, Run("autostart", "off"));
        Assert.Contains("couldn't be removed", error.ToString());
        Assert.Equal(1, Run("autostart", "status"));
        Assert.Contains("couldn't be read", error.ToString());

        Assert.Equal(0, Run("status"));
        Assert.Contains("task 'ai-keep-awake watch' couldn't be read (Couldn't read the task: PowerShell said no)", output.ToString());
        Assert.Contains("Lock point:", output.ToString());   // and the rest is still reported

        Assert.Equal(0, Run("install", "--settings", settings, "--dry-run"));
        Assert.Contains("Couldn't check for the old task 'claude-keep-awake watch'", error.ToString());
        Assert.Equal(0, Run("uninstall", "--settings", settings, "--dry-run"));
        Assert.Contains("Couldn't check for the old task", error.ToString());
    }

    [Fact]
    public void Uninstall_removes_both_tasks_and_leaves_the_programs()
    {
        Run("install", "--settings", settings);
        tasks[AutoStart.OldTaskName].Exists = true;
        tasks.Calls.Clear();
        Directory.CreateDirectory(LeasesDir);
        Assert.Equal(0, Run("uninstall", "--settings", settings));
        Assert.Equal(["disable ai-keep-awake watch", "disable claude-keep-awake watch"], tasks.Calls);
        Assert.Contains($"The programs are still in {installDir}", output.ToString());
        Assert.True(File.Exists(Path.Combine(installDir, Install.BackgroundExe)));
    }

    [Fact]
    public void The_hook_command_never_prints()
    {
        Assert.Equal(0, Run("hook", "busy", "--dir", LeasesDir));
        Assert.Equal("", output.ToString() + error.ToString());
        Assert.NotNull(LeaseFile());
    }

    [Fact]
    public void Status_reads_everything_without_changing_anything()
    {
        Assert.Equal(0, Run("status"));
        var text = output.ToString();
        Assert.Contains($"Settings:   {configPath} (not created; defaults apply)", text);
        Assert.Contains("Hooks:      not installed", text);
        Assert.Contains("Watcher:    not running; no logon task (ai-keep-awake install)", text);
        Assert.Contains("Lock point: 5 min idle: sleep after 5 min, sign-in required every time", text);
        Assert.Contains("Holds when: a lease is live, on mains, and idle under 3 h.", text);
        Assert.Empty(tasks.Calls);
    }

    [Fact]
    public void Status_sees_a_running_watcher_without_taking_its_lock()
    {
        var name = Watcher.LockName(Config.Load(configPath, Env));
        using (new LockHolder(name))
        {
            Assert.Equal(0, Run("status"));
            Assert.Contains("Watcher:    running;", output.ToString());
            Assert.True(WinBackground.Locks.Held(name));
        }
        Assert.Equal(0, Run("status"));
        Assert.Contains("Watcher:    not running;", output.ToString());
        var watcherLock = WinBackground.Locks.SingleInstance(name);   // status left nothing behind
        Assert.NotNull(watcherLock);
        watcherLock.Release();
    }

    [Fact]
    public void Config_creates_the_default_file_once()
    {
        (output, error) = (new StringWriter(), new StringWriter());
        var host = new CliHost { Out = output, Err = error, Env = Env, Tasks = name => tasks[name] };
        Assert.Equal(0, Cli.Run(["config"], host));
        var path = Path.Combine(Folder, "Roaming", "ai-keep-awake", "config.toml");
        Assert.Equal(path + Environment.NewLine, output.ToString());
        Assert.StartsWith("# ai-keep-awake settings.", File.ReadAllText(path));
        Assert.Contains("\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Bad_arguments_exit_2_and_version_exits_0()
    {
        Assert.Equal(2, Run("nonsense"));
        Assert.Contains("invalid choice", error.ToString());
        Assert.Equal(2, Run("autostart", "sideways"));
        Assert.Equal(0, Cli.Run(["--version"], new CliHost { Out = output = new StringWriter() }));
        Assert.Equal("1.0.0-dev", output.ToString().Trim());
    }
}

public class ConfigTests : Temp
{
    [Fact]
    public void Defaults_without_a_file()
    {
        var config = Config.Load(Path.Combine(Folder, "missing.toml"), Env);
        Assert.Equal((900, 2700, 3 * 3600.0), (config.LeaseSeconds, config.BackgroundLeaseSeconds, config.UnattendedSeconds));
        Assert.Equal("windows", config.Lock);
        Assert.True(config.MainsOnly);
        Assert.Equal("An AI agent is working (Claude Code)", config.Reason);
        Assert.Equal(Path.Combine(Folder, "Local", "ai-keep-awake", "leases"), config.LeasesDir);
        Assert.Equal(Path.Combine(Folder, "home", ".claude", "settings.json"), config.ClaudeSettings);
    }

    [Fact]
    public void Values_and_refusals()
    {
        var path = Path.Combine(Folder, "c.toml");
        File.WriteAllText(path, "lock = 7\nunattended_hours = 1.5\nleases_dir = \"leases\"\n");
        var config = Config.Load(path, Env);
        Assert.Equal(7L, config.Lock);
        Assert.Equal(5400, config.UnattendedSeconds);
        Assert.Equal(Path.Combine(Folder, "leases"), config.LeasesDir);
        foreach (var bad in new[] { "lock = \"sometimes\"", "lock = 0", "lock = true", "unattended_hours = 0", "nonsense = 1" })
        {
            File.WriteAllText(path, bad);
            Assert.Throws<ConfigError>(() => Config.Load(path, Env));
        }
    }

    [Fact]
    public void The_messages_match_the_python_version()
    {
        var path = Path.Combine(Folder, "c.toml");
        string Message(string text)
        {
            File.WriteAllText(path, text);
            return Assert.Throws<ConfigError>(() => Config.Load(path, Env)).Message;
        }
        Assert.Equal($"{path}: unknown setting(s) nonsense, zzz.", Message("zzz = 1\nnonsense = 2"));
        Assert.Equal($"{path}: lock must be \"windows\", \"never\" or a number of minutes, not 'sometimes'.", Message("lock = \"sometimes\""));
        Assert.Equal($"{path}: lock must be \"windows\", \"never\" or a number of minutes above 0.", Message("lock = -1"));
        Assert.Equal($"{path}: check_seconds must be a number above 0.", Message("check_seconds = \"x\""));
    }

    [Fact]
    public void Paths_expand_home_and_variables_as_python_did()
    {
        var path = Path.Combine(Folder, "c.toml");
        File.WriteAllText(path, "leases_dir = '%LOCALAPPDATA%/x//y/./z'\nlog_file = '~/logs/k.log'\nstate_file = '%NOPE%/s.json'\n");
        var config = Config.Load(path, Env);
        Assert.Equal(Path.Combine(Folder, "Local", "x", "y", "z"), config.LeasesDir);
        Assert.Equal(Path.Combine(Folder, "home", "logs", "k.log"), config.LogFile);
        Assert.Equal(Path.Combine(Folder, "%NOPE%", "s.json"), config.StateFile);
    }

    [Fact]
    public void The_settings_path_comes_from_the_environment_when_not_given()
    {
        EnvVars["AI_KEEP_AWAKE_CONFIG"] = Path.Combine(Folder, "from-env.toml");
        Assert.Equal(Path.Combine(Folder, "from-env.toml"), Config.Load(null, Env).Path);
        EnvVars.Remove("AI_KEEP_AWAKE_CONFIG");
        Assert.Equal(Path.Combine(Folder, "Roaming", "ai-keep-awake", "config.toml"), Config.Load(null, Env).Path);
    }
}
