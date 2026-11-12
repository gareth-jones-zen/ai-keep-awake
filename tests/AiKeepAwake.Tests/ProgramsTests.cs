using System.Diagnostics;

namespace AiKeepAwake.Tests;

/// <summary>Copying the programs into the install folder while one may be running (a hook) or being scanned.</summary>
public class ProgramsTests : Temp
{
    private readonly string dist;
    private readonly string installDir;

    public ProgramsTests()
    {
        dist = Path.Combine(Folder, "download");
        installDir = Path.Combine(Folder, "install");
        Directory.CreateDirectory(dist);
        Directory.CreateDirectory(installDir);
        foreach (var name in Programs.Names)
        {
            File.WriteAllText(Path.Combine(dist, name), "new " + name);
            File.WriteAllText(Path.Combine(installDir, name), "old " + name);
        }
    }

    private string Installed(string name) => Path.Combine(installDir, name);
    private string Read(string name) => File.ReadAllText(Installed(name));
    private string[] Leftovers() => Directory.GetFiles(installDir).Select(Path.GetFileName).Where(n => n!.EndsWith(".old") || n.EndsWith(".new")).ToArray()!;
    private void Copy(int attempts = 3, int pauseMs = 0) => Programs.Copy(dist, installDir, attempts, TimeSpan.FromMilliseconds(pauseMs));

    /// <summary>Open as Windows holds a running exe: others may read it and rename it, but not overwrite it.</summary>
    private FileStream HoldLikeARunningExe(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    /// <summary>Open so it can't be renamed or deleted either (a scanner, or an older install still running).</summary>
    private static FileStream HoldFast(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public void A_fresh_install_leaves_only_the_programs()
    {
        foreach (var name in Programs.Names)
            File.Delete(Installed(name));
        Copy();
        foreach (var name in Programs.Names)
            Assert.Equal("new " + name, Read(name));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void A_program_in_use_is_renamed_aside_and_replaced()
    {
        using (HoldLikeARunningExe(Installed(Install.BackgroundExe)))
        {
            Assert.ThrowsAny<IOException>(() => File.Copy(Path.Combine(dist, Install.BackgroundExe), Installed(Install.BackgroundExe), overwrite: true));
            Copy();
            foreach (var name in Programs.Names)
                Assert.Equal("new " + name, Read(name));
            // (A handle that shares delete lets the old copy be deleted at once; a running exe can't be, which
            // the next test shows.)
        }
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void A_really_running_exe_is_replaced()
    {
        // The built background program, run from the install folder as a hook would be, held open on stdin.
        var built = Path.GetDirectoryName(BuiltExe("BackgroundExe"))!;
        foreach (var file in Directory.GetFiles(built))
            File.Copy(file, Path.Combine(installDir, Path.GetFileName(file)), overwrite: true);
        var info = new ProcessStartInfo(Installed(Install.BackgroundExe))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            ArgumentList = { "hook", "busy", "--dir", LeasesDir },
        };
        info.Environment["AI_KEEP_AWAKE"] = "0";
        using var process = Process.Start(info)!;
        try
        {
            Thread.Sleep(300);
            Assert.False(process.HasExited);
            Copy(attempts: 10, pauseMs: 100);
            Assert.Equal("new " + Install.BackgroundExe, Read(Install.BackgroundExe));
            Assert.Equal(["ai-keep-awake-background.exe.old"], Leftovers());   // still running, so not deleted yet
        }
        finally
        {
            process.StandardInput.Close();
            process.WaitForExit(10_000);
        }
        Assert.Equal(0, process.ExitCode);
        Copy();   // the next install tidies it away
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void A_failure_puts_back_what_was_already_replaced()
    {
        // The console exe is replaced first; the background one can't be, so the console one goes back too.
        using (HoldFast(Installed(Install.BackgroundExe)))
            Assert.ThrowsAny<IOException>(() => Copy());
        foreach (var name in Programs.Names)
            Assert.Equal("old " + name, Read(name));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void A_program_held_for_a_moment_is_retried()
    {
        var held = HoldFast(Installed(Install.BackgroundExe));
        var release = new Thread(() =>
        {
            Thread.Sleep(150);
            held.Dispose();
        });
        release.Start();
        Copy(attempts: 40, pauseMs: 50);
        release.Join();
        foreach (var name in Programs.Names)
            Assert.Equal("new " + name, Read(name));
    }

    [Fact]
    public void An_old_copy_still_running_gets_a_new_aside_name()
    {
        File.WriteAllText(Installed(Install.BackgroundExe + ".old"), "older still");
        using (HoldFast(Installed(Install.BackgroundExe + ".old")))
        {
            Copy();
            Assert.Equal("new " + Install.BackgroundExe, Read(Install.BackgroundExe));
            Assert.Equal(["ai-keep-awake-background.exe.old"], Leftovers());
        }
        Copy();
        Assert.Empty(Leftovers());
    }
}
