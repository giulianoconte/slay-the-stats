namespace SlayTheStats;

/// <summary>
/// Hook for injecting fake data into the DB for UI scalability testing. Gated on
/// <see cref="SlayTheStatsConfig.DebugMode"/>. Currently a no-op — the fake biomes
/// and characters used during v0.3.0 development have been removed.
/// </summary>
internal static class DebugTestData
{
    internal static void InjectIfDebug(StatsDb db)
    {
        if (!SlayTheStatsConfig.DebugMode) return;
    }
}
