using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WinBackground;

/// <summary>A named lock between processes: a mutex in the session's Local namespace. Windows releases a mutex
/// when the process holding it dies, so a crash can never leave a lock stuck, which a lock file can.
///
/// A mutex belongs to the thread that acquired it, and only that thread can release it. So acquire and release
/// on the same thread: never hold one across an await. Release on another thread throws rather than failing
/// quietly and leaving the lock held until the process exits.</summary>
public sealed class NamedLock : IDisposable
{
    private const uint WaitObject0 = 0, WaitAbandoned = 0x80;
    private const int NotOwner = 288;   // ERROR_NOT_OWNER
    private nint handle;

    public NamedLock(string name) => Name = name;

    public string Name { get; }

    /// <summary>Wait up to timeout seconds; 0 means try once.</summary>
    public bool Acquire(double timeoutSeconds = 0)
    {
        handle = Native.CreateMutexW(0, false, $@"Local\{Name}");
        if (handle == 0)
            return false;
        var result = Native.WaitForSingleObject(handle, (uint)(timeoutSeconds * 1000));
        if (result is WaitObject0 or WaitAbandoned)   // abandoned: the last holder died, so it's ours
            return true;
        Native.CloseHandle(handle);
        handle = 0;
        return false;
    }

    /// <summary>Release the lock. Throws InvalidOperationException if Windows refuses (most likely: called on a
    /// thread other than the one that acquired it), leaving it held so the right thread can still release it.</summary>
    public void Release()
    {
        if (handle == 0)
            return;
        if (!Native.ReleaseMutex(handle))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"Couldn't release the lock '{Name}' (Windows error {error}"
                + (error == NotOwner ? ": not the thread that acquired it; a mutex must be released on that thread)" : ")"));
        }
        Native.CloseHandle(handle);
        handle = 0;
    }

    public void Dispose() => Release();
}

/// <summary>One watcher at a time. Two names that are equal hold the same lock across every tool, so name
/// locks after the thing they protect: Key() turns a path into a short stable name.</summary>
public static class Locks
{
    /// <summary>16 hex characters from a path or other text, ignoring case (Windows paths).</summary>
    public static string Key(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..16];

    /// <summary>The lock for a long-running process, such as a watcher: held until it exits or releases it.
    /// Null if another process (or thread) already holds it.</summary>
    public static NamedLock? SingleInstance(string name)
    {
        var lockObject = new NamedLock(name);
        return lockObject.Acquire(0) ? lockObject : null;
    }

    /// <summary>Whether some process has the lock open (in practice: holds it), without ever acquiring it. A
    /// probe that acquired it, even for a moment, could make a watcher starting at that moment think another was
    /// running and exit. The mutex exists only while a process has it open, and a holder keeps it open.</summary>
    public static bool Held(string name)
    {
        const uint synchronize = 0x00100000;
        const int accessDenied = 5;
        var handle = Native.OpenMutexW(synchronize, false, $@"Local\{name}");
        if (handle != 0)
        {
            Native.CloseHandle(handle);
            return true;
        }
        return Marshal.GetLastPInvokeError() == accessDenied;   // there, but someone else's to open
    }
}
