// ai-keep-awake-background.exe: no window, ever. What the Claude Code hooks and the logon task run.
// `hook` goes straight to the hook (no settings file is read, so it starts in milliseconds); `watch` runs the
// watcher; anything else runs as the console program would, with nothing shown.
if (args.Length > 0 && args[0] == "hook")
    return AiKeepAwake.Hook.Main(args[1..]);
return AiKeepAwake.Cli.Run(args, new AiKeepAwake.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
