// Stub for SlayTheStatsConfig — provides the static properties that RunParser and
// StatsAggregator reference, without pulling in BaseLib or Godot dependencies.

namespace SlayTheStats;

internal class SlayTheStatsConfig
{
    public static string DataDirectory { get; set; } = "";
    public static bool OnlyHighestWonAscension { get; set; } = false;
    public static bool GroupCardUpgrades { get; set; } = true;
    public static bool ColorBlindMode { get; set; } = false;
    public static bool ShowInRunStats { get; set; } = true;
    public static bool DisableTooltipsEntirely { get; set; } = false;
    public static bool DebugMode { get; set; } = false;
    public static int AscensionMin { get; set; } = 0;
    public static int AscensionMax { get; set; } = 10;
    public static string VersionMin { get; set; } = "";
    public static string VersionMax { get; set; } = "";
    public static string FilterCharacters { get; set; } = "";
    public static string FilterProfile { get; set; } = "";
}
