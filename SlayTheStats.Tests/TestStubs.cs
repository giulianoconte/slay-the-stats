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
    public static bool ClassSpecificStats { get; set; } = false;
    public static string FilterProfile { get; set; } = "";

    public static int DefaultAscensionMin { get; set; } = 0;
    public static int DefaultAscensionMax { get; set; } = 10;
    public static string DefaultVersionMin { get; set; } = "";
    public static string DefaultVersionMax { get; set; } = "";
    public static bool DefaultClassSpecificStats { get; set; } = false;
    public static string DefaultFilterProfile { get; set; } = "";
    public static bool DefaultGroupCardUpgrades { get; set; } = true;

    internal static void SaveDefaults()
    {
        DefaultAscensionMin = AscensionMin; DefaultAscensionMax = AscensionMax;
        DefaultVersionMin = VersionMin; DefaultVersionMax = VersionMax;
        DefaultClassSpecificStats = ClassSpecificStats; DefaultFilterProfile = FilterProfile;
        DefaultGroupCardUpgrades = GroupCardUpgrades;
    }
    internal static void RestoreDefaults()
    {
        AscensionMin = DefaultAscensionMin; AscensionMax = DefaultAscensionMax;
        VersionMin = DefaultVersionMin; VersionMax = DefaultVersionMax;
        ClassSpecificStats = DefaultClassSpecificStats; FilterProfile = DefaultFilterProfile;
        GroupCardUpgrades = DefaultGroupCardUpgrades;
    }
    internal static void ClearAllFilters()
    {
        AscensionMin = 0; AscensionMax = 10; VersionMin = ""; VersionMax = "";
        ClassSpecificStats = false; FilterProfile = ""; GroupCardUpgrades = true;
    }
    internal static bool IsNonDefault(string field) => field switch
    {
        "AscensionMin" => AscensionMin != DefaultAscensionMin,
        "AscensionMax" => AscensionMax != DefaultAscensionMax,
        "VersionMin" => VersionMin != DefaultVersionMin,
        "VersionMax" => VersionMax != DefaultVersionMax,
        "ClassSpecific" => ClassSpecificStats != DefaultClassSpecificStats,
        "FilterProfile" => FilterProfile != DefaultFilterProfile,
        "GroupUpgrades" => GroupCardUpgrades != DefaultGroupCardUpgrades,
        _ => false,
    };
}
