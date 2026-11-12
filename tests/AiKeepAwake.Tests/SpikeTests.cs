// SPIKE (phase 2): proves the test pipeline. Replaced by the ported tests in phase 3.
namespace AiKeepAwake.Tests;

public class SpikeTests
{
    [Fact]
    public void Toml_reads_lock_as_text_or_a_number()
    {
        Assert.Equal("windows", AiKeepAwake.SpikeToml.Read("lock = \"windows\"").Single().Item2);
        Assert.Equal(10L, AiKeepAwake.SpikeToml.Read("lock = 10").Single().Item2);
    }
}
