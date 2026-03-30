using BaseLib.Config;

namespace SlayTheStats;

internal class SlayTheStatsConfig : SimpleModConfig
{
    /// <summary>
    /// When true, stats are filtered to each character's highest won ascension.
    /// e.g. if Ironclad has won up to A9, only A0–A9 runs are included for Ironclad.
    /// </summary>
    public static bool OnlyHighestWonAscension { get; set; } = false;

    /// <summary>
    /// When true, base and upgraded card stats are merged in tooltips
    /// (e.g. Strike and Strike+ count as one entry).
    /// </summary>
    public static bool GroupCardUpgrades { get; set; } = true;

    /// <summary>
    /// When true, uses a color-blind-friendly palette for stat coloring.
    /// When false, uses red/green coloring.
    /// </summary>
    public static bool ColorBlindMode { get; set; } = true;
}
