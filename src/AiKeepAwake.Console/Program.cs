// ai-keep-awake.exe: every command, with output. `hook` goes straight to the hook, which never prints.
// Double-clicked in Explorer (no arguments, and the console is its own), it's the guided install.
if (args.Length > 0 && args[0] == "hook")
    return AiKeepAwake.Hook.Main(args[1..]);
var host = new AiKeepAwake.CliHost { LogEcho = Console.Out };
if (args.Length == 0 && host.Desktop.StartedFromExplorer())
    return AiKeepAwake.Cli.Guided(host);
return AiKeepAwake.Cli.Run(args, host);
