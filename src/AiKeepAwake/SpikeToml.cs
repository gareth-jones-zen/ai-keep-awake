// SPIKE (phase 2): Tomlyn's untyped model, which needs no reflection. Replaced by Config in phase 3.
using Tomlyn;
using Tomlyn.Model;

namespace AiKeepAwake;

public static class SpikeToml
{
    public static IEnumerable<(string, object?)> Read(string text)
    {
        var table = TomlSerializer.Deserialize(text, SpikeTomlContext.Default.TomlTable)!;
        foreach (var pair in table)
            yield return (pair.Key, pair.Value);
    }
}

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class SpikeTomlContext : Tomlyn.Serialization.TomlSerializerContext;
