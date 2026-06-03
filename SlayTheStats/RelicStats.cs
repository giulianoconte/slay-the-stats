using System.Text.Json.Serialization;

namespace SlayTheStats;

/// <summary>
/// Stats for a single relic in a single context. Like <see cref="CardStat"/>,
/// the run-level counts derive from a run-index → flag set (#6) so unions across
/// acts (the Total row) count each run once. Relics have no upgrade variants and
/// aren't offered/picked at card-reward screens, so only Present/Won/ShopSeen/
/// ShopBought flags are used.
/// </summary>
public class RelicStat
{
    /// <summary>Contributing run index → OR'd <see cref="RunFlag"/> bits.</summary>
    [JsonPropertyName("runs")] public Dictionary<int, byte> RunFlags { get; set; } = new();

    [JsonIgnore] public int RunsPresent    => CountFlag(RunFlag.Present);
    [JsonIgnore] public int RunsWon        => CountFlag(RunFlag.Won);
    [JsonIgnore] public int RunsShopSeen   => CountFlag(RunFlag.ShopSeen);
    [JsonIgnore] public int RunsShopBought => CountFlag(RunFlag.ShopBought);

    private int CountFlag(RunFlag flag)
    {
        byte bit = (byte)flag;
        int n = 0;
        foreach (var v in RunFlags.Values)
            if ((v & bit) != 0) n++;
        return n;
    }

    /// <summary>Records that run <paramref name="runIndex"/> had these flags for this relic+context.</summary>
    public void SetRun(int runIndex, RunFlag flags)
    {
        RunFlags.TryGetValue(runIndex, out var cur);
        RunFlags[runIndex] = (byte)(cur | (byte)flags);
    }

    /// <summary>Unions another stat's run flags into this one (aggregating across acts).</summary>
    public void MergeFrom(RelicStat other)
    {
        foreach (var (runIndex, flags) in other.RunFlags)
            SetRun(runIndex, (RunFlag)flags);
    }
}
