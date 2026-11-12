using System.Text.Json.Nodes;

namespace AiKeepAwake.Tests;

public class InstallTests : Temp
{
    private readonly string settings;
    private readonly Config config;
    private readonly JsonObject original;

    public InstallTests()
    {
        settings = Path.Combine(Folder, "settings.json");
        config = NewConfig();
        original = (JsonObject)JsonNode.Parse("""
            {"env": {"X": "1"}, "model": "opus",
             "hooks": {"PreToolUse": [{"matcher": "Bash", "hooks": [{"type": "command", "command": "lint.sh"}]}],
                       "Stop": [{"hooks": [{"type": "command", "command": "notify.sh"}]}]},
             "theme": "dark"}
            """)!;
        File.WriteAllText(settings, Install.Render(original));
    }

    private ChangeReport DoInstall(bool dryRun = false) => Install.Change(settings, s => Install.WithOurs(s, config), dryRun);
    private ChangeReport DoUninstall() => Install.Change(settings, s => Install.WithoutOurs(s).Settings);
    private JsonObject ReadBack() => (JsonObject)JsonNode.Parse(File.ReadAllText(settings))!;
    private string[] Backups() => Directory.GetFiles(Folder, "settings.json.ai-keep-awake-*.bak");

    [Fact]
    public void Install_adds_every_event_and_keeps_everything_else()
    {
        Assert.True(DoInstall().Changed);
        var data = ReadBack();
        Assert.Equal(Install.Events.Select(e => e.Event).ToHashSet(), Install.Installed(settings).ToHashSet());
        foreach (var key in new[] { "env", "model", "theme" })
            Assert.True(PyJson.Equal(original[key], data[key]));
        Assert.True(PyJson.Equal(original["hooks"]!["PreToolUse"]![0], data["hooks"]!["PreToolUse"]![0]));
        Assert.True(PyJson.Equal(original["hooks"]!["Stop"]![0], data["hooks"]!["Stop"]![0]));
        Assert.Equal("idle_prompt", data["hooks"]!["Notification"]![0]!["matcher"]!.GetValue<string>());
        var ours = data["hooks"]!["Stop"]![1]!["hooks"]![0]!;
        Assert.EndsWith(@"\ai-keep-awake-background.exe", ours["command"]!.GetValue<string>());
        Assert.Equal(["hook", "stop"], ours["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(5, ours["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void Install_backs_up_first()
    {
        var before = File.ReadAllBytes(settings);
        var report = DoInstall();
        Assert.Equal(before, File.ReadAllBytes(report.Backup!));
        Assert.Single(Backups());
    }

    [Fact]
    public void Install_twice_changes_nothing()
    {
        DoInstall();
        var afterFirst = File.ReadAllText(settings);
        Assert.False(DoInstall().Changed);
        Assert.Equal(afterFirst, File.ReadAllText(settings));
        Assert.Single(Backups());
    }

    [Fact]
    public void Reinstalling_with_new_settings_replaces_ours_without_duplicating()
    {
        DoInstall();
        config.LeaseMinutes = 20;
        DoInstall();
        var stops = ReadBack()["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stops.Count);
        Assert.Contains("1200", stops[1]!["hooks"]![0]!["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void Uninstall_removes_exactly_ours()
    {
        DoInstall();
        DoUninstall();
        Assert.True(PyJson.Equal(original, ReadBack()));
        Assert.Empty(Install.Installed(settings));
    }

    [Fact]
    public void Uninstall_keeps_a_group_shared_with_another_hook()
    {
        var data = (JsonObject)original.DeepClone();
        var other = new JsonObject { ["type"] = "command", ["command"] = "other.sh" };
        data["hooks"] = new JsonObject
        {
            ["PreToolUse"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(other, Install.Handler(config, "busy")) }),
        };
        File.WriteAllText(settings, Install.Render(data));
        DoUninstall();
        var expected = JsonNode.Parse("""{"PreToolUse": [{"hooks": [{"type": "command", "command": "other.sh"}]}]}""");
        Assert.True(PyJson.Equal(expected, ReadBack()["hooks"]));
    }

    [Fact]
    public void Uninstall_with_nothing_of_ours_writes_nothing()
    {
        Assert.False(DoUninstall().Changed);
        Assert.Empty(Backups());
    }

    [Fact]
    public void A_dry_run_shows_the_diff_and_writes_nothing()
    {
        var before = File.ReadAllText(settings);
        var report = DoInstall(dryRun: true);
        Assert.Equal(before, File.ReadAllText(settings));
        Assert.Empty(Backups());
        var lines = report.Diff.Split('\n');
        var added = lines.Where(line => line.StartsWith('+') && !line.StartsWith("+++")).ToList();
        var removed = lines.Where(line => line.StartsWith('-') && !line.StartsWith("---")).ToList();
        Assert.Contains(added, line => line.Contains("ai-keep-awake-background.exe"));
        Assert.Empty(removed);    // only additions
    }

    [Fact]
    public void Invalid_json_is_refused_and_left_alone()
    {
        File.WriteAllText(settings, "{\"model\": \"opus\",}");
        Assert.Throws<SettingsError>(() => DoInstall());
        Assert.Equal("{\"model\": \"opus\",}", File.ReadAllText(settings));
        Assert.Empty(Backups());
    }

    [Fact]
    public void A_byte_order_mark_is_tolerated()
    {
        File.WriteAllBytes(settings, [0xEF, 0xBB, 0xBF, .. "{\"model\": \"opus\"}"u8]);
        Assert.True(DoInstall().Changed);
        Assert.Equal((byte)'{', File.ReadAllBytes(settings)[0]);
        Assert.Equal("opus", ReadBack()["model"]!.GetValue<string>());
    }

    [Fact]
    public void A_non_object_is_refused()
    {
        File.WriteAllText(settings, "[]");
        Assert.Throws<SettingsError>(() => DoInstall());
    }

    [Fact]
    public void A_missing_file_is_created()
    {
        File.Delete(settings);
        Assert.Null(DoInstall().Backup);
        Assert.Equal(["hooks"], ReadBack().Select(pair => pair.Key));
    }

    [Fact]
    public void Custom_leases_dir_and_lengths_reach_the_hook()
    {
        config.LeasesDir = Path.Combine(Folder, "elsewhere");
        config.BackgroundLeaseMinutes = 60;
        var args = Install.Handler(config, "stop")["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal(Path.Combine(Folder, "elsewhere"), args[args.IndexOf("--dir") + 1]);
        Assert.Equal("3600", args[args.IndexOf("--bg-lease") + 1]);
        Assert.DoesNotContain("--lease", args);
    }

    // ---- New in the port ----------------------------------------------------------------------------------

    private static JsonObject OldHook(string action) => new()
    {
        ["type"] = "command",
        ["command"] = @"C:\Python\pythonw.exe",
        ["args"] = new JsonArray("-I", "-S", @"C:\repos\claude-keep-awake\src\claude_keep_awake\hook.py", action),
        ["timeout"] = 5,
    };

    [Fact]
    public void Install_replaces_the_old_python_tools_hooks()
    {
        var data = (JsonObject)original.DeepClone();
        data["hooks"]!["Stop"]!.AsArray().Add((JsonNode)new JsonObject { ["hooks"] = new JsonArray(OldHook("stop")) });
        data["hooks"]!["SessionEnd"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(OldHook("end")) });
        File.WriteAllText(settings, Install.Render(data));
        Assert.Equal(["Stop", "SessionEnd"], Install.Installed(settings, old: true));
        DoInstall();
        var text = File.ReadAllText(settings);
        Assert.DoesNotContain("claude_keep_awake", text);
        Assert.Empty(Install.Installed(settings, old: true));
        Assert.Equal(7, Install.Installed(settings).Count);
        var stops = ReadBack()["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stops.Count);    // the other tool's, then ours: the old one is gone, not kept beside it
        Assert.True(PyJson.Equal(original["hooks"]!["Stop"]![0], stops[0]));
    }

    [Fact]
    public void Uninstall_removes_the_old_tools_hooks_too()
    {
        var data = (JsonObject)original.DeepClone();
        data["hooks"]!["SessionEnd"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(OldHook("end")) });
        File.WriteAllText(settings, Install.Render(data));
        DoUninstall();
        Assert.True(PyJson.Equal(original, ReadBack()));
    }

    [Fact]
    public void The_file_is_written_as_python_wrote_it()
    {
        File.WriteAllText(settings, "{\"a\": 1.50, \"b\": \"caf" + (char)0xE9 + "\", \"c\": [], \"d\": {\"e\": true}}");
        DoInstall();
        var bytes = File.ReadAllText(settings);
        Assert.StartsWith("{\r\n  \"a\": 1.5,\r\n  \"b\": \"caf" + (char)0xE9 + "\",\r\n  \"c\": [],\r\n  \"d\": {\r\n    \"e\": true\r\n  },\r\n  \"hooks\": {\r\n", bytes);
        Assert.EndsWith("}\r\n", bytes);
    }
}
