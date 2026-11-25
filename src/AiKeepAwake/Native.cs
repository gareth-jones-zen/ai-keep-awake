using System.Runtime.InteropServices;

namespace AiKeepAwake;

/// <summary>The few Windows calls the install and uninstall make beyond WinBackground's: starting a program outside
/// the caller's job, finding the tray's processes, and the shell link for the Start menu. Source-generated imports
/// and raw COM vtable calls, so Native AOT needs nothing at run time.</summary>
internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    internal const uint CreateNoWindow = 0x08000000, CreateBreakawayFromJob = 0x01000000, CreateUnicodeEnvironment = 0x00000400;
    internal const uint ProcessQueryLimitedInformation = 0x1000, Synchronize = 0x00100000;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? currentDirectory,
        ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetConsoleProcessList(uint* processes, uint count);

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    /// <summary>Start a program with no console window, outside the caller's job if Windows allows it (so a tray
    /// started from a terminal, or from Claude Code's shell, outlives it). Returns the process id.</summary>
    internal static int Start(string exe, IReadOnlyList<string> args, string? workingDir) =>
        StartRaw(exe, WinBackground.Shell.ListToCommandLine([exe, .. args]), workingDir);

    /// <summary>As Start, with the command line as it is (for cmd.exe, whose quoting isn't a program's).</summary>
    internal static int StartRaw(string exe, string line, string? workingDir)
    {
        foreach (var flags in new[] { CreateNoWindow | CreateBreakawayFromJob | CreateUnicodeEnvironment, CreateNoWindow | CreateUnicodeEnvironment })
        {
            var buffer = (line + "\0").ToCharArray();
            var startup = new STARTUPINFOW { cb = sizeof(STARTUPINFOW) };
            fixed (char* command = buffer)
            {
                if (CreateProcessW(exe, command, 0, 0, false, flags, 0, workingDir, ref startup, out var info))
                {
                    CloseHandle(info.hThread);
                    CloseHandle(info.hProcess);
                    return info.dwProcessId;
                }
            }
            if (Marshal.GetLastPInvokeError() != 5)   // ERROR_ACCESS_DENIED: the job doesn't allow breaking away
                break;
        }
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), $"Couldn't start {exe}");
    }

    /// <summary>The full path of a running process's program, or null if Windows won't say.</summary>
    internal static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
            return null;
        try
        {
            var buffer = stackalloc char[1024];
            uint size = 1024;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Wait for a process to exit; true if it has (or was never there).</summary>
    internal static bool WaitForExit(int pid, TimeSpan timeout)
    {
        var handle = OpenProcess(Synchronize, false, (uint)pid);
        if (handle == 0)
            return true;
        try
        {
            return WaitForSingleObject(handle, (uint)timeout.TotalMilliseconds) == 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>How many processes share this console: 1 when this program was started on its own, from Explorer.</summary>
    internal static int ConsoleProcesses()
    {
        var ids = stackalloc uint[8];
        return (int)GetConsoleProcessList(ids, 8);
    }

    // ---- The Start menu shortcut: IShellLinkW, IPersistFile and IPropertyStore, through their vtables ----------

    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidPersistFile = new("0000010b-0000-0000-C000-000000000046");
    private static readonly Guid IidPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt, r1, r2, r3;
        public nint p, p2;
    }

    private static nint Method(nint obj, int slot) => (*(nint**)obj)[slot];

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new IOException($"{what} failed (0x{hr:X8})");
    }

    private static void Release(nint obj)
    {
        if (obj != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Method(obj, 2))(obj);
    }

    private static nint Query(nint obj, Guid iid)
    {
        nint result;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(obj, 0))(obj, &iid, &result), "QueryInterface");
        return result;
    }

    private static void SetText(nint link, int slot, string value, string what)
    {
        fixed (char* text = value)
            Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Method(link, slot))(link, text), what);
    }

    /// <summary>Write a shortcut (.lnk): its target, arguments, folder, description, icon and AppUserModelID (so the
    /// tray's notifications come from "ai-keep-awake" with its icon).</summary>
    internal static void WriteShortcut(string path, string target, string args, string workingDir, string description,
        string icon, string appId)
    {
        CoInitializeEx(0, 2);   // apartment-threaded; already initialised (in either mode) is fine
        Check(CoCreateInstance(ClsidShellLink, 0, 1, IidShellLinkW, out var link), "CoCreateInstance(ShellLink)");
        nint store = 0, file = 0;
        try
        {
            SetText(link, 20, target, "SetPath");
            SetText(link, 11, args, "SetArguments");
            SetText(link, 9, workingDir, "SetWorkingDirectory");
            SetText(link, 7, description, "SetDescription");
            fixed (char* iconPath = icon)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Method(link, 17))(link, iconPath, 0), "SetIconLocation");

            store = Query(link, IidPropertyStore);
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };   // PKEY_AppUserModel_ID
            var id = Marshal.StringToCoTaskMemUni(appId);
            try
            {
                var value = new PROPVARIANT { vt = 31, p = id };   // VT_LPWSTR
                Check(((delegate* unmanaged[Stdcall]<nint, PROPERTYKEY*, PROPVARIANT*, int>)Method(store, 6))(store, &key, &value), "SetValue(AppUserModelID)");
                Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(store, 7))(store), "Commit");
            }
            finally
            {
                Marshal.FreeCoTaskMem(id);
            }

            file = Query(link, IidPersistFile);
            fixed (char* name = path)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Method(file, 6))(file, name, 1), "Save");
        }
        finally
        {
            Release(file);
            Release(store);
            Release(link);
        }
    }

    /// <summary>A shortcut's target, or null if it can't be read.</summary>
    internal static string? ShortcutTarget(string path)
    {
        if (!File.Exists(path))
            return null;
        CoInitializeEx(0, 2);
        if (CoCreateInstance(ClsidShellLink, 0, 1, IidShellLinkW, out var link) < 0)
            return null;
        nint file = 0;
        try
        {
            file = Query(link, IidPersistFile);
            fixed (char* name = path)
                if (((delegate* unmanaged[Stdcall]<nint, char*, uint, int>)Method(file, 5))(file, name, 0) < 0)
                    return null;
            var buffer = stackalloc char[1024];
            if (((delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)Method(link, 3))(link, buffer, 1024, 0, 0x4) < 0)   // SLGP_RAWPATH
                return null;
            return new string(buffer);
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            Release(file);
            Release(link);
        }
    }
}
