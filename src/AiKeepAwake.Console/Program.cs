// ai-keep-awake.exe: every command, with output. `hook` goes straight to the hook, which never prints.
if (args.Length > 0 && args[0] == "hook")
    return AiKeepAwake.Hook.Main(args[1..]);
return AiKeepAwake.Cli.Run(args, new AiKeepAwake.CliHost { LogEcho = Console.Out });
