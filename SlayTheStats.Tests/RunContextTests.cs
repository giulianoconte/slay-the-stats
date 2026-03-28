using Xunit;
using SlayTheStats;

namespace SlayTheStats.Tests;

public class RunContextTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var ctx = new RunContext("CHARACTER.IRONCLAD", 5, 2);
        Assert.Equal(ctx, RunContext.Parse(ctx.ToKey()));
    }

    [Fact]
    public void ToKey_Format_IsCharacterPipeAscensionPipeAct()
    {
        var ctx = new RunContext("CHARACTER.IRONCLAD", 0, 1);
        Assert.Equal("CHARACTER.IRONCLAD|0|1", ctx.ToKey());
    }
}
