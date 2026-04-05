using Xunit;
using SlayTheStats;

namespace SlayTheStats.Tests;

public class RunContextTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var ctx = new RunContext("CHARACTER.IRONCLAD", 5, 2, "standard", "v0.98.0", "profile1");
        Assert.Equal(ctx, RunContext.Parse(ctx.ToKey()));
    }

    [Fact]
    public void ToKey_Format_IncludesProfile()
    {
        var ctx = new RunContext("CHARACTER.IRONCLAD", 0, 1, "standard", "v0.98.0", "profile1");
        Assert.Equal("CHARACTER.IRONCLAD|0|1|standard|v0.98.0|profile1", ctx.ToKey());
    }

    [Fact]
    public void Parse_LegacyFivePartKey_DefaultsProfile()
    {
        var ctx = RunContext.Parse("CHARACTER.IRONCLAD|0|1|standard|v0.98.0");
        Assert.Equal("default", ctx.Profile);
        Assert.Equal("CHARACTER.IRONCLAD", ctx.Character);
    }
}
