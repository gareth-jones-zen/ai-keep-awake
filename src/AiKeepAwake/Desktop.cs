using System.Diagnostics;
using Microsoft.Win32;
using WinBackground;

namespace AiKeepAwake;

/// <summary>What Settings > Apps shows for ai-keep-awake, and how it uninstalls it.</summary>
public sealed record AppsEntry(string Name, string Version, string Publisher, string InstallLocation, string Icon,
    string UninstallString, long SizeKb, string InstallDate, string Comments);

/// <summary>Everything the install touches on the desktop beyond its files and the task: the tray at logon (the Run
/// key), the Settings > Apps entry, the Start menu shortcut, the tray's process, and starting programs. Tests pass
/// a fake.</summary>
public interface IDesktop
{
    /// <summary>The tray's command at logon (HKCU Run), or null.</summary>
    string? RunEntry();
    void SetRunEntry(string command);
    bool RemoveRunEntry();

    /// <summary>The Settings > Apps entry's install location, or null if there's no entry.</summary>
    string? AppsEntryLocation();
    void SetAppsEntry(AppsEntry entry);
    bool RemoveAppsEntry();

    string ShortcutPath { get; }
    void WriteShortcut(string target, string args, string workingDir, string description, string icon);
    bool RemoveShortcut();

    /// <summary>Whether a tray from this folder is running.</summary>
    bool TrayRunning(string installDir);

    /// <summary>Ask a tray from this folder to close, and wait (then end it if it won't). True if one was running.</summary>
    bool StopTray(string installDir);

    /// <summary>Start a program in the background, outside the caller's job; its process id.</summary>
    int Start(string exe, IReadOnlyList<string> args, string? workingDir);

    /// <summary>Run a newly copied program once (--version), so Defender's first-run scan happens now rather than on
    /// the first hook call.</summary>
    void Warm(string exe);

    /// <summary>The version an installed console program reports, or null.</summary>
    string? VersionOf(string exe);

    /// <summary>Whether this console is this program's alone: started from Explorer, not a terminal.</summary>
    bool StartedFromExplorer();

    /// <summary>Delete the programs in installDir once this process has exited (it may be one of them): a copy of
    /// it in %TEMP% waits, deletes them, and then itself.</summary>
    void RemoveLater(string installDir);
}

/// <summary>The real desktop: the registry (HKCU only), the Start menu, processes.</summary>
public sealed class WindowsDesktop : IDesktop
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ai-keep-awake";
    public const string RunValue = "ai-keep-awake";
    public const string AppId = "AiKeepAwake.Tray";

    public string? RunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) as string;
    }

    public void SetRunEntry(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(RunValue, command, RegistryValueKind.String);
    }

    public bool RemoveRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(RunValue) is null)
            return false;
        key.DeleteValue(RunValue, throwOnMissingValue: false);
        return true;
    }

    public string? AppsEntryLocation()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key is null ? null : key.GetValue("InstallLocation") as string ?? "";
    }

    public void SetAppsEntry(AppsEntry entry)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", entry.Name);
        key.SetValue("DisplayVersion", entry.Version);
        key.SetValue("Publisher", entry.Publisher);
        key.SetValue("InstallLocation", entry.InstallLocation);
        key.SetValue("DisplayIcon", entry.Icon);
        key.SetValue("UninstallString", entry.UninstallString);
        key.SetValue("InstallDate", entry.InstallDate);
        key.SetValue("Comments", entry.Comments);
        key.SetValue("URLInfoAbout", Install.RepoUrl);
        key.SetValue("HelpLink", Install.RepoUrl);
        key.SetValue("EstimatedSize", (int)Math.Min(entry.SizeKb, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public bool RemoveAppsEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (key is null)
            return false;
        key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        return true;
    }

    public string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ai-keep-awake.lnk");

    public void WriteShortcut(string target, string args, string workingDir, string description, string icon)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        Native.WriteShortcut(ShortcutPath, target, args, workingDir, description, icon, AppId);
    }

    public bool RemoveShortcut()
    {
        if (!File.Exists(ShortcutPath))
            return false;
        File.Delete(ShortcutPath);
        return true;
    }

    /// <summary>The tray processes whose program is in installDir (after an upgrade, perhaps renamed aside).</summary>
    private static List<Process> Trays(string installDir)
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Install.TrayExe)))
        {
            var image = Native.ImagePath(process.Id);
            if (image is not null && PyPath.Same(Path.GetDirectoryName(image)!, installDir))
                found.Add(process);
            else
                process.Dispose();
        }
        return found;
    }

    public bool TrayRunning(string installDir)
    {
        var trays = Trays(installDir);
        foreach (var tray in trays)
            tray.Dispose();
        return trays.Count > 0;
    }

    public bool StopTray(string installDir)
    {
        var trays = Trays(installDir);
        if (trays.Count == 0)
            return false;
        try
        {
            if (EventWaitHandle.TryOpenExisting(Tray.QuitName(installDir), out var quit))
            {
                using (quit)
                    quit.Set();
            }
            foreach (var tray in trays)
            {
                if (!Native.WaitForExit(tray.Id, TimeSpan.FromSeconds(5)))
                {
                    try
                    {
                        tray.Kill();
                        Native.WaitForExit(tray.Id, TimeSpan.FromSeconds(5));
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
        }
        finally
        {
            foreach (var tray in trays)
                tray.Dispose();
        }
        return true;
    }

    public int Start(string exe, IReadOnlyList<string> args, string? workingDir) => Native.Start(exe, args, workingDir);

    public void Warm(string exe)
    {
        try
        {
            var pid = Native.Start(exe, ["--version"], Path.GetDirectoryName(exe));
            Native.WaitForExit(pid, TimeSpan.FromSeconds(30));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // best effort: the first real run waits for the scan instead
        }
    }

    public string? VersionOf(string exe)
    {
        if (!File.Exists(exe))
            return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return null;
            var text = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000))
                return null;
            var line = text.Trim();
            return line.Length > 0 ? line : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public bool StartedFromExplorer() => Native.ConsoleProcesses() == 1;

    public void RemoveLater(string installDir)
    {
        var self = Environment.ProcessPath ?? Path.Combine(installDir, Install.ConsoleExe);
        var copy = Path.Combine(Path.GetTempPath(), $"ai-keep-awake-uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(self, copy);
        Native.Start(copy, [Cli.RemoveProgramsCommand, "--wait", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--dir", installDir], Path.GetTempPath());
    }

    /// <summary>The copy in %TEMP% that RemoveLater started: delete it once it has exited (cmd waits two seconds).</summary>
    public static void DeleteSelfLater()
    {
        try
        {
            if (Environment.ProcessPath is not { } self || !Path.GetFileName(self).StartsWith("ai-keep-awake-uninstall-", StringComparison.OrdinalIgnoreCase))
                return;
            var cmd = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comspec ? comspec : "cmd.exe";
            Native.StartRaw(cmd, $"\"{cmd}\" /d /c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\"", Path.GetTempPath());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // left in %TEMP%, for Windows' own clean-up
        }
    }
}
