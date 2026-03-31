using Xunit;
using SlayTheStats;

namespace SlayTheStats.Tests;

public class RunContextTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var ctx = new RunContext("CHARACTER.IRONCLAD", 5, 2, "standard", "v0.98.0");
        Assert.Equal(ctx, RunContext.Parse(ctx.ToKey()));
    }

    [Fact]
    public void ToKey_Format_IsCharacterPipeAscensionPipeActPipeBuildVersion()
    {
        var ctx = new RunContext("CHARACTER.IRONCLAD", 0, 1, "standard", "v0.98.0");
        Assert.Equal("CHARACTER.IRONCLAD|0|1|standard|v0.98.0", ctx.ToKey());
    }
}
