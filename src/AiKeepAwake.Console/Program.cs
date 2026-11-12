// SPIKE (phase 2): does Tomlyn read the settings file under Native AOT? Replaced by the real CLI in phase 3.
if (args is ["toml-spike", var file])
{
    foreach (var (key, value) in AiKeepAwake.SpikeToml.Read(File.ReadAllText(file)))
        Console.WriteLine($"{key} = {value} ({value?.GetType().Name})");
    return 0;
}
return 2;
