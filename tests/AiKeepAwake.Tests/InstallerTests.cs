using System.Text.Json.Nodes;

namespace AiKeepAwake.Tests;

/// <summary>1.2.0's installer: the tray at logon, the Start menu and Settings > Apps, install.json, the full
/// uninstall and the guided install, all against a fake desktop and fake tasks.</summary>
public class InstallerTests : Temp
{
    private readonly FakeTasks tasks = new();
    private readonly FakeDesktop desktop = new();
    private readonly string dist;
    private readonly string settings;
    private readonly string installDir;
    private readonly string configPath;
    private StringWriter output = new();
    private StringWriter error = new();
    private string input = "";

    public InstallerTests()
    {
        dist = Path.Combine(Folder, "download");
        Directory.CreateDirectory(dist);
        foreach (var name in Programs.Names)
            File.WriteAllText(Path.Combine(dist, name), "new " + name);
        settings = Path.Combine(Folder, "home", ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{\"model\": \"opus\"}\n");
        installDir = Path.Combine(Folder, "Local", "Programs", "ai-keep-awake");
        configPath = Path.Combine(Folder, "Roaming", "ai-keep-awake", "config.toml");   // the default, so the guided install finds it
    }

    private CliHost Host(string? programDir = null)
    {
        (output, error) = (new StringWriter(), new StringWriter());
        return new CliHost
        {
            Out = output,
            Err = error,
            Env = Env,
            Tasks = name => tasks[name],
            System = () => new FakeSystem(),
            Notify = (_, _) => { },
            ProgramDir = programDir ?? dist,
            Clock = () => Now,
            Now = () => new DateTime(2026, 11, 25, 14, 5, 0),
            CopyPause = TimeSpan.Zero,
            Desktop = desktop,
            In = new StringReader(input),
        };
    }

    private int Run(params string[] args) => Cli.Run(args, Host());

    private int RunFrom(string programDir, params string[] args) => Cli.Run(args, Host(programDir));

    private Config Loaded() => Config.Load(configPath, Env);

    private string Text => output.ToString() + error.ToString();

    [Fact]
    public void Install_adds_the_tray_at_logon_the_start_menu_settings_apps_and_install_json()
    {
        Assert.Equal(0, Run("install"));
        var tray = Path.Combine(installDir, Install.TrayExe);
        Assert.Equal(Tray.RunCommand(Loaded()), desktop.Run);
        Assert.Contains(tray, desktop.Run);
        Assert.EndsWith($"--config {configPath}", desktop.Run);
        Assert.Equal((tray, "--show"), desktop.Shortcut);
        var apps = desktop.Apps!;
        Assert.Equal("ai-keep-awake", apps.Name);
        Assert.Equal(installDir, apps.InstallLocation);
        Assert.Equal($"\"{Path.Combine(installDir, Install.ConsoleExe)}\" uninstall --interactive", apps.UninstallString);
        Assert.Equal($"{tray},0", apps.Icon);
        Assert.Equal("20261125", apps.InstallDate);
        Assert.True(apps.SizeKb > 0);
        var record = InstallRecord.Read(Loaded())!;
        Assert.Equal(InstallRecord.Person, record.ManagedBy);
        Assert.Equal(Cli.Version, record.Version);
        Assert.Equal("2026-11-25T14:05:00", record.Installed);
        Assert.False(record.RemovedByPerson);
        Assert.Contains("warm ai-keep-awake.exe", desktop.Calls);
        Assert.Contains("warm ai-keep-awake-tray.exe", desktop.Calls);
        Assert.Contains("start ai-keep-awake-tray.exe", desktop.Calls);
        Assert.Contains("Other system tray icons", Text);                // the one line about the overflow
        Assert.Contains("The tray icon starts at every logon too", Text);
        Assert.Contains("Added to the Start menu and to Settings > Apps", Text);
    }

    [Fact]
    public void A_dry_run_lists_the_new_parts_and_changes_nothing()
    {
        Assert.Equal(0, Run("install", "--dry-run", "--managed-by", "right-hand"));
        Assert.Contains("Would start the tray icon at every logon", Text);
        Assert.Contains("Would add it to the Start menu", Text);
        Assert.Contains("(installed by right-hand)", Text);
        Assert.Empty(desktop.Calls);
        Assert.Null(InstallRecord.Read(Loaded()));
    }

    [Fact]
    public void Right_hands_install_records_it_and_starts_nothing_at_logon()
    {
        Assert.Equal(0, Run("install", "--no-autostart", "--managed-by", "right-hand"));
        Assert.True(InstallRecord.Read(Loaded())!.ManagedByRightHand);
        Assert.Null(desktop.Run);
        Assert.DoesNotContain("start ai-keep-awake-tray.exe", desktop.Calls);   // nothing at logon, nothing now
        Assert.NotNull(desktop.Apps);                                           // its own Settings > Apps entry still
        Assert.NotNull(desktop.Shortcut);
        Assert.Empty(tasks.Calls);
    }

    [Fact]
    public void An_upgrade_without_the_flag_keeps_who_installed_it()
    {
        Assert.Equal(0, Run("install", "--no-autostart", "--managed-by", "right-hand"));
        Assert.Equal(0, Run("install", "--no-autostart"));
        Assert.True(InstallRecord.Read(Loaded())!.ManagedByRightHand);
        Assert.Equal(2, Run("install", "--managed-by", "not a name"));
    }

    [Fact]
    public void An_upgrade_closes_a_running_tray_first_and_starts_the_new_one()
    {
        Assert.Equal(0, Run("install", "--no-autostart"));
        desktop.TraysRunning.Add(installDir);
        desktop.Calls.Clear();
        Assert.Equal(0, Run("install", "--no-autostart"));
        var stop = desktop.Calls.IndexOf("tray stop");
        var start = desktop.Calls.IndexOf("start ai-keep-awake-tray.exe");
        Assert.True(stop >= 0 && start > stop, string.Join(", ", desktop.Calls));
        Assert.DoesNotContain("Other system tray icons", Text);          // it was there before
    }

    [Fact]
    public void Another_copy_in_settings_apps_is_left_alone()
    {
        var other = Path.Combine(Folder, "real");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, Install.ConsoleExe), "real");
        desktop.AppsLocation = other;
        var testDir = Path.Combine(Folder, "test-programs");
        Assert.Equal(0, Run("install", "--no-autostart", "--install-dir", testDir));
        Assert.Null(desktop.Apps);
        Assert.Null(desktop.Shortcut);
        Assert.Contains($"Settings > Apps lists the copy in {other}", Text);
    }

    [Fact]
    public void Start_menu_false_leaves_out_the_start_menu_and_settings_apps()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "start_menu = false\n");
        Assert.Equal(0, Run("install", "--no-autostart", "--dry-run"));
        Assert.Contains("start_menu = false in the settings", Text);
        Assert.Equal(0, Run("install", "--no-autostart"));
        Assert.Null(desktop.Apps);
        Assert.Null(desktop.Shortcut);
        Assert.NotNull(InstallRecord.Read(Loaded()));
        Assert.Equal(0, Run("uninstall"));
        Assert.DoesNotContain("apps off", desktop.Calls);
        Assert.Equal(2, Run("--config", configPath.Also(p => File.WriteAllText(p, "start_menu = 1\n")), "status"));
    }

    [Fact]
    public void Inside_claude_code_the_tray_at_logon_is_left_for_the_person_too()
    {
        EnvVars[Cli.ClaudeCodeVariable] = "1";
        Assert.Equal(0, Run("install"));
        Assert.Null(desktop.Run);
        Assert.Contains("autostart on", Text);
        Assert.Contains("the watcher and the tray icon at every logon", Text);
        Assert.NotNull(desktop.Apps);
    }

    [Fact]
    public void Autostart_switches_the_task_and_the_tray_together()
    {
        Assert.Equal(0, Run("install", "--no-autostart"));
        desktop.Calls.Clear();
        Assert.Equal(0, RunFrom(installDir, "autostart", "on"));
        Assert.Contains("run on", desktop.Calls);
        Assert.Contains("start ai-keep-awake-tray.exe", desktop.Calls);
        Assert.Contains("enable ai-keep-awake watch", tasks.Calls);
        Assert.Equal(0, RunFrom(installDir, "autostart", "status"));
        Assert.Contains("tray         at logon:", Text);
        Assert.Equal(0, RunFrom(installDir, "autostart", "off"));
        Assert.Null(desktop.Run);
        Assert.Contains("Autostart is off.", Text);
        Assert.Contains("disable ai-keep-awake watch", tasks.Calls);
        Assert.Equal(0, RunFrom(installDir, "autostart", "off"));
        Assert.Contains("Autostart was already off.", Text);
    }

    [Fact]
    public void Uninstall_removes_everything_and_records_that_the_person_did()
    {
        Assert.Equal(0, Run("install"));
        Control.SetPause(Loaded(), null, Now);
        Control.SetManual(Loaded(), Now + 60, Now);
        Assert.Equal(0, Run("uninstall"));
        Assert.Null(desktop.Run);
        Assert.Null(desktop.Apps);
        Assert.Null(desktop.Shortcut);
        Assert.Contains("tray stop", desktop.Calls);
        Assert.Empty(Install.Installed(settings));
        Assert.False(Directory.Exists(installDir));
        Assert.Null(Control.ReadPause(Loaded()));
        Assert.Empty(Leases.Read(Loaded().LeasesDir));
        var record = InstallRecord.Read(Loaded())!;
        Assert.True(record.RemovedByPerson);
        Assert.Equal("2026-11-25T14:05:00", record.Removed);
        Assert.Contains("right-hand asks before putting it back", Text);
    }

    [Fact]
    public void Uninstall_by_right_hand_is_not_the_persons_choice()
    {
        Assert.Equal(0, Run("install", "--no-autostart", "--managed-by", "right-hand"));
        Assert.Equal(0, Run("uninstall", "--managed-by", "right-hand"));
        Assert.False(InstallRecord.Read(Loaded())!.RemovedByPerson);
    }

    [Fact]
    public void Uninstall_from_the_installed_copy_deletes_the_programs_after_it_exits()
    {
        Assert.Equal(0, Run("install", "--no-autostart"));
        Assert.Equal(0, RunFrom(installDir, "uninstall"));
        Assert.Contains("remove later", desktop.Calls);
        Assert.Contains("deleted as soon as this window closes", Text);
        Assert.True(File.Exists(Path.Combine(installDir, Install.ConsoleExe)));   // the helper's job
    }

    [Fact]
    public void Uninstall_purge_deletes_the_settings_and_the_log_but_keeps_install_json()
    {
        Assert.Equal(0, Run("install", "--no-autostart"));
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "");
        File.WriteAllText(Loaded().LogFile, "log");
        Assert.Equal(0, Run("uninstall", "--dry-run", "--purge"));
        Assert.Contains("and delete the settings file, the log and the state file", Text);
        Assert.True(File.Exists(configPath));
        Assert.Equal(0, Run("uninstall"));
        Assert.Contains("the settings file", Text);                          // kept, and said so
        Assert.Equal(0, Run("uninstall", "--purge"));
        Assert.False(File.Exists(configPath));
        Assert.False(File.Exists(Path.Combine(Folder, "Local", "ai-keep-awake", "ai-keep-awake.log")));
        Assert.NotNull(InstallRecord.Read(Config.Load(configPath, Env)));
    }

    [Fact]
    public void Uninstall_leaves_other_files_in_the_folder()
    {
        Assert.Equal(0, Run("install", "--no-autostart"));
        File.WriteAllText(Path.Combine(installDir, "notes.txt"), "mine");
        Assert.Equal(0, Run("uninstall"));
        Assert.Equal(["notes.txt"], Directory.GetFiles(installDir).Select(Path.GetFileName));
    }

    [Fact]
    public void An_interactive_uninstall_asks_first()
    {
        Assert.Equal(0, Run("install", "--no-autostart"));
        input = "n\n\n";
        Assert.Equal(0, Run("uninstall", "--interactive"));
        Assert.Contains("Uninstall ai-keep-awake? [y/N]", Text);
        Assert.Contains("Nothing was changed.", Text);
        Assert.NotEmpty(Install.Installed(settings));
        input = "y\n\n";
        Assert.Equal(0, Run("uninstall", "--interactive"));
        Assert.Contains("ai-keep-awake is uninstalled.", Text);
        Assert.Contains("Press Enter to close.", Text);
        Assert.Empty(Install.Installed(settings));
    }

    [Fact]
    public void The_remove_helper_deletes_only_the_programs()
    {
        Directory.CreateDirectory(installDir);
        foreach (var name in Programs.Names)
        {
            File.WriteAllText(Path.Combine(installDir, name), "x");
            File.WriteAllText(Path.Combine(installDir, name + ".old"), "x");
        }
        Assert.Equal(0, Cli.Run([Cli.RemoveProgramsCommand, "--wait", "0", "--dir", installDir]));
        Assert.False(Directory.Exists(installDir));
    }

    [Fact]
    public void Double_clicked_it_explains_asks_once_and_installs()
    {
        input = "\n\n";
        Assert.Equal(0, Cli.Guided(Host()));
        var text = Text;
        Assert.Contains($"ai-keep-awake {Cli.Version}", text);
        Assert.Contains("This will:", text);
        Assert.Contains($"  - copy its programs to {installDir}", text);
        Assert.Contains("(7 events; the file is backed up first)", text);
        Assert.Contains("No admin rights needed.", text);
        Assert.Contains("Install? [Y/n]", text);
        Assert.Contains("Done.", text);
        Assert.Contains("Press Enter to close.", text);
        Assert.Equal(7, Install.Installed(settings).Count);
        Assert.Contains("enable ai-keep-awake watch", tasks.Calls);
        Assert.NotNull(desktop.Run);
    }

    [Fact]
    public void Double_clicked_and_declined_changes_nothing()
    {
        input = "n\n";
        Assert.Equal(0, Cli.Guided(Host()));
        Assert.False(Directory.Exists(installDir));
        Assert.Empty(desktop.Calls);
    }

    [Fact]
    public void Double_clicked_over_an_older_install_offers_the_upgrade()
    {
        Assert.Equal(0, Run("install", "--no-autostart"));
        new InstallRecord("0.9.0", null, InstallRecord.Person, installDir).Write(Loaded());
        input = "y\n\n";
        Assert.Equal(0, Cli.Guided(Host()));
        Assert.Contains($"Upgrade 0.9.0 to {Cli.Version}? [Y/n]", Text);
        Assert.Equal(Cli.Version, InstallRecord.Read(Loaded())!.Version);
    }

    [Fact]
    public void Double_clicked_at_the_same_version_offers_repair_uninstall_or_close()
    {
        Assert.Equal(0, Run("install", "--no-autostart"));
        input = "\n";
        Assert.Equal(0, Cli.Guided(Host()));
        Assert.Contains("Repair, Uninstall or Close? [r/u/C]", Text);
        File.WriteAllText(settings, "{}");
        input = "r\n\n";
        Assert.Equal(0, Cli.Guided(Host()));
        Assert.Equal(7, Install.Installed(settings).Count);
        input = "u\ny\n\n";
        Assert.Equal(0, Cli.Guided(Host()));
        Assert.Empty(Install.Installed(settings));
    }

    [Fact]
    public void Double_clicked_over_a_newer_install_does_nothing()
    {
        Assert.Equal(0, Run("install", "--no-autostart"));
        new InstallRecord("99.0.0", null, InstallRecord.Person, installDir).Write(Loaded());
        input = "\n";
        Assert.Equal(0, Cli.Guided(Host()));
        Assert.Contains("newer than this one", Text);
    }

    [Fact]
    public void Status_shows_the_tray_and_install_json()
    {
        Assert.Equal(0, Run("install", "--no-autostart", "--managed-by", "right-hand"));
        Assert.Equal(0, Run("status"));
        Assert.Contains("Tray:       not running; not started at logon; installed by right-hand", Text);
        Assert.Equal(0, Run("status", "--json"));
        var status = JsonNode.Parse(output.ToString())!.AsObject();
        Assert.Equal("right-hand", status["install"]!["managed_by"]!.GetValue<string>());
        Assert.False(status["install"]!["removed_by_person"]!.GetValue<bool>());
        Assert.False(status["tray"]!["at_logon"]!.GetValue<bool>());
        Assert.Equal(0, Run("uninstall"));
        Assert.Equal(0, Run("status", "--json"));
        Assert.True(JsonNode.Parse(output.ToString())!["install"]!["removed_by_person"]!.GetValue<bool>());
    }
}
