using WinBackground;

namespace AiKeepAwake;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes: where it writes,
/// the environment, Task Scheduler, Windows, notifications and where the running program is.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>Where the watcher's log lines are echoed (the console exe: stdout; the background exe: nowhere).</summary>
    public TextWriter? LogEcho { get; init; }

    public Func<string, string?> Env { get; init; } = Config.Environment;
    public Func<string, ITask> Tasks { get; init; } = AutoStart.Create;
    public Func<ISystem> System { get; init; } = () => new WindowsSystem();
    public Action<string, IReadOnlyList<string>> Notify { get; init; } = Toast.Notify;

    /// <summary>The folder the running program is in, which install copies from.</summary>
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;

    /// <summary>How long install waits between tries at replacing a program that's in use.</summary>
    public TimeSpan CopyPause { get; init; } = TimeSpan.FromMilliseconds(300);

    public Func<Stream>? Stdin { get; init; }
    public Func<double> Clock { get; init; } = Hook.Now;
}
