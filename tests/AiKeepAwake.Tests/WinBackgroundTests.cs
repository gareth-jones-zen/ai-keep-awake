using WinBackground;

namespace AiKeepAwake.Tests;

/// <summary>The shared Windows plumbing (src/WinBackground). The PowerShell these run only echoes or reads.</summary>
public class ShellTests
{
    private static string CodePoints(string text) => string.Join(",", text.Select(c => ((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void System_programs_are_started_by_their_full_path()
    {
        var system = Environment.SystemDirectory;
        Assert.Equal(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), Shell.PowerShellExe);
        Assert.Equal(Path.Combine(system, "powercfg.exe"), Power.PowercfgExe);
        Assert.True(Path.IsPathFullyQualified(Shell.PowerShellExe));
        Assert.True(File.Exists(Shell.PowerShellExe));
        Assert.True(File.Exists(Power.PowercfgExe));
    }

    [Fact]
    public void Quote_doubles_every_quote_powershell_ends_a_string_at()
    {
        Assert.Equal("'plain'", Shell.Quote("plain"));
        Assert.Equal("'it''s'", Shell.Quote("it's"));
        Assert.Equal("'C:\\Users\\O\u2019\u2019Brien'", Shell.Quote("C:\\Users\\O\u2019Brien"));
        Assert.Equal("'\u2018\u2018a\u2019\u2019 \u201A\u201Ab\u201B\u201B'", Shell.Quote("\u2018a\u2019 \u201Ab\u201B"));
        Assert.Equal("'\u201C\u201D'", Shell.Quote("\u201C\u201D"));   // double curly quotes don't end a single-quoted string
    }

    [Fact]
    public void Quoted_text_reaches_powershell_unchanged()
    {
        // Every kind of single quote, and what would run if the quoting failed. The script only echoes the
        // string's code points (so the console's code page can't garble the comparison).
        var text = "C:\\Users\\O\u2019Brien \u2018a\u2019 \u201Ab\u201B 'c' $(Write-Output injected) `n \"d\" \u201Ce\u201D";
        var done = Shell.PowerShell($"[string]::Join(',', [int[]][char[]]{Shell.Quote(text)})", 60);
        Assert.Equal(0, done.ExitCode);
        Assert.Equal(CodePoints(text), done.StandardOutput.Trim());
    }
}

public class ScheduledTaskTests
{
    [Fact]
    public void Stop_and_wait_never_waits_for_its_own_powershell()
    {
        // The script that registers the task contains the task's arguments, so the powershell.exe running it
        // matched its own command line and the wait always ran the full 30 seconds.
        var task = new ScheduledTask("ai-keep-awake watch");
        Assert.Contains(ScheduledTask.AliveFilter, task.StopAndWait());
        Assert.Contains("$_.ProcessId -ne $PID", ScheduledTask.AliveFilter);
        Assert.Contains("$_.ProcessId -ne $PID", task.EnableScript(@"C:\x\ai-keep-awake-background.exe", ["--config", @"C:\x\c.toml", "watch"], @"C:\x"));
    }

    [Fact]
    public void The_alive_filter_does_not_find_the_script_running_it()
    {
        // Read-only: lists processes whose command line holds a unique marker. Only this script's own
        // powershell.exe does, and the filter must leave it out.
        var marker = $"--config C:\\ai-keep-awake-test-{Guid.NewGuid():N}\\c.toml watch";
        var done = Shell.PowerShell($$"""
            $taskArgs = {{Shell.Quote(marker)}}
            $alive = {{ScheduledTask.AliveFilter}}
            $self = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($taskArgs) }
            "alive=$(@($alive).Count) self=$(@($self).Count)"
            """, 60);
        Assert.Equal(0, done.ExitCode);
        Assert.Equal("alive=0 self=1", done.StandardOutput.Trim());   // self=1 shows the marker really was there to find
    }
}

public class ScheduledTaskStartTests
{
    [Fact]
    public void Start_runs_and_reports_no_task()
    {
        // A name no task can have, so Get-ScheduledTask finds nothing and nothing is started: this only checks
        // that the script runs and says there was no task.
        Assert.False(new ScheduledTask($"ai-keep-awake test {Guid.NewGuid():N}").Start());
    }
}

/// <summary>Holds a named lock on a thread of its own, as a watcher in another process would.</summary>
public sealed class LockHolder : IDisposable
{
    private readonly ManualResetEventSlim held = new(), done = new();
    private readonly Thread thread;

    public LockHolder(string name)
    {
        thread = new Thread(() =>
        {
            var lockObject = Locks.SingleInstance(name)!;
            held.Set();
            done.Wait();
            lockObject.Release();
        });
        thread.Start();
        held.Wait();
    }

    public void Dispose()
    {
        done.Set();
        thread.Join();
        held.Dispose();
        done.Dispose();
    }
}

public class LockTests
{
    private static string Unique() => $"ai-keep-awake-test-{Guid.NewGuid():N}";

    /// <summary>Whether another thread could take the lock now (releasing it again if so).</summary>
    private static bool FreeElsewhere(string name)
    {
        var free = false;
        var thread = new Thread(() =>
        {
            var lockObject = Locks.SingleInstance(name);
            free = lockObject is not null;
            lockObject?.Release();
        });
        thread.Start();
        thread.Join();
        return free;
    }

    [Fact]
    public void Held_looks_without_taking()
    {
        var name = Unique();
        Assert.False(Locks.Held(name));
        Assert.False(Locks.Held(name));   // looking doesn't create it
        using (new LockHolder(name))
        {
            Assert.True(Locks.Held(name));
            Assert.False(FreeElsewhere(name));   // still held by its holder, not disturbed by the look
        }
        Assert.False(Locks.Held(name));
        Assert.True(FreeElsewhere(name));
    }

    [Fact]
    public void Release_on_another_thread_throws_and_leaves_it_releasable()
    {
        var name = Unique();
        var lockObject = Locks.SingleInstance(name)!;
        Exception? error = null;
        var other = new Thread(() =>
        {
            try
            {
                lockObject.Release();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        other.Start();
        other.Join();
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("not the thread that acquired it", error!.Message);
        Assert.False(FreeElsewhere(name));   // still held, not silently stuck as released
        lockObject.Release();                // the right thread can still release it
        Assert.True(FreeElsewhere(name));
        lockObject.Release();                // and a second release is a no-op
    }
}
