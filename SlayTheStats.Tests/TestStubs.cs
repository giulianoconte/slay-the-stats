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
    public const int AscensionLowest = int.MinValue;
    public const int AscensionHighest = int.MaxValue;
    public static int AscensionMin { get; set; } = AscensionLowest;
    public static int AscensionMax { get; set; } = AscensionHighest;
    public static string VersionMin { get; set; } = "";
    public static string VersionMax { get; set; } = "";
    public static string ClassFilter { get; set; } = "";
    public const string ClassFilterClassSpecific = "__class__";
    public static bool ClassSpecificStats
    {
        get => ClassFilter == ClassFilterClassSpecific;
        set => ClassFilter = value ? ClassFilterClassSpecific : "";
    }
    public static string FilterProfile { get; set; } = "";

    public static int DefaultAscensionMin { get; set; } = AscensionLowest;
    public static int DefaultAscensionMax { get; set; } = AscensionHighest;
    public static string DefaultVersionMin { get; set; } = "";
    public static string DefaultVersionMax { get; set; } = "";
    public static string DefaultClassFilter { get; set; } = "";
    public static string DefaultFilterProfile { get; set; } = "";
    public static bool DefaultGroupCardUpgrades { get; set; } = true;

    internal static void SaveDefaults()
    {
        DefaultAscensionMin = AscensionMin; DefaultAscensionMax = AscensionMax;
        DefaultVersionMin = VersionMin; DefaultVersionMax = VersionMax;
        DefaultClassFilter = ClassFilter; DefaultFilterProfile = FilterProfile;
        DefaultGroupCardUpgrades = GroupCardUpgrades;
    }
    internal static void RestoreDefaults()
    {
        AscensionMin = DefaultAscensionMin; AscensionMax = DefaultAscensionMax;
        VersionMin = DefaultVersionMin; VersionMax = DefaultVersionMax;
        ClassFilter = DefaultClassFilter; FilterProfile = DefaultFilterProfile;
        GroupCardUpgrades = DefaultGroupCardUpgrades;
    }
    internal static void ClearAllFilters()
    {
        AscensionMin = AscensionLowest; AscensionMax = AscensionHighest;
        VersionMin = ""; VersionMax = "";
        ClassFilter = ""; FilterProfile = ""; GroupCardUpgrades = true;
    }
    internal static bool IsNonDefault(string field) => field switch
    {
        "AscensionMin" => AscensionMin != DefaultAscensionMin,
        "AscensionMax" => AscensionMax != DefaultAscensionMax,
        "VersionMin" => VersionMin != DefaultVersionMin,
        "VersionMax" => VersionMax != DefaultVersionMax,
        "ClassFilter" => ClassFilter != DefaultClassFilter,
        "FilterProfile" => FilterProfile != DefaultFilterProfile,
        "GroupUpgrades" => GroupCardUpgrades != DefaultGroupCardUpgrades,
        _ => false,
    };
}
