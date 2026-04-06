// =============================================================================
// ModConfigBridge.cs — Optional ModConfig-STS2 integration
// =============================================================================
// Registers our two settings with ModConfig-STS2's Mod Settings UI if it is
// installed. Zero DLL reference — everything via reflection.
// If ModConfig is not installed this file does nothing.
//
// OnChanged callbacks update SlayTheStatsConfig's static properties and trigger
// a BaseLib debounced save, keeping both persistence files in sync.
// =============================================================================

using System.Reflection;
using BaseLib.Config;
using Godot;

namespace SlayTheStats;

internal static class ModConfigBridge
{
    private static bool _available;
    private static bool _registered;
    private static Type? _apiType;
    private static Type? _entryType;
    private static Type? _configTypeEnum;

    internal static bool IsAvailable => _available;

    // Call from Initialize(). ModConfig may load after us (alphabetical), so defer one frame.
    internal static void DeferredRegister()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame += OnNextFrame;
    }

    private static void OnNextFrame()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame -= OnNextFrame;
        Detect();
        if (_available) Register();
    }

    private static void Detect()
    {
        try
        {
            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .ToArray();

            _apiType        = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ModConfigApi");
            _entryType      = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ConfigEntry");
            _configTypeEnum = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ConfigType");
            _available      = _apiType != null && _entryType != null && _configTypeEnum != null;
        }
        catch { _available = false; }
    }

    private static void Register()
    {
        if (_registered) return;
        _registered = true;

        try
        {
            var entries = BuildEntries();

            var registerMethod = _apiType!.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Register")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

            if (registerMethod.GetParameters().Length == 4)
            {
                registerMethod.Invoke(null, new object[]
                {
                    MainFile.ModId,
                    "SlayTheStats",
                    new Dictionary<string, string> { ["en"] = "SlayTheStats" },
                    entries
                });
            }
            else
            {
                registerMethod.Invoke(null, new object[] { MainFile.ModId, "SlayTheStats", entries });
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"SlayTheStats: ModConfig registration failed — {e.Message}");
        }
    }

    private static Array BuildEntries()
    {
        var list = new List<object>();

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key",          "color_blind_mode");
            Set(cfg, "Label",        "Color Blind Mode");
            Set(cfg, "Type",         EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)SlayTheStatsConfig.ColorBlindMode);
            Set(cfg, "Description",  "Use a color-blind-friendly palette instead of red/green for stat coloring.");
            Set(cfg, "OnChanged",    new Action<object>(v =>
            {
                SlayTheStatsConfig.ColorBlindMode = Convert.ToBoolean(v);
                ModConfig.SaveDebounced<SlayTheStatsConfig>();
            }));
        }));

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key",          "show_in_run_stats");
            Set(cfg, "Label",        "Show In-Run Stats");
            Set(cfg, "Type",         EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)SlayTheStatsConfig.ShowInRunStats);
            Set(cfg, "Description",  "Show stat tooltips during a run (card rewards, shop, relic hovers). When off, stats only appear in the compendium.");
            Set(cfg, "OnChanged",    new Action<object>(v =>
            {
                SlayTheStatsConfig.ShowInRunStats = Convert.ToBoolean(v);
                ModConfig.SaveDebounced<SlayTheStatsConfig>();
            }));
        }));

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key",          "disable_tooltips_entirely");
            Set(cfg, "Label",        "Disable All Stat Tooltips");
            Set(cfg, "Type",         EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)SlayTheStatsConfig.DisableTooltipsEntirely);
            Set(cfg, "Description",  "Master switch — turns off all stat tooltips everywhere in the game.");
            Set(cfg, "OnChanged",    new Action<object>(v =>
            {
                SlayTheStatsConfig.DisableTooltipsEntirely = Convert.ToBoolean(v);
                ModConfig.SaveDebounced<SlayTheStatsConfig>();
            }));
        }));

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key",          "debug_mode");
            Set(cfg, "Label",        "Debug Mode");
            Set(cfg, "Type",         EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)SlayTheStatsConfig.DebugMode);
            Set(cfg, "Description",  "Surface internal state in tooltips (context key, raw counters, build version) to help diagnose issues without reading logs.");
            Set(cfg, "OnChanged",    new Action<object>(v =>
            {
                SlayTheStatsConfig.DebugMode = Convert.ToBoolean(v);
                ModConfig.SaveDebounced<SlayTheStatsConfig>();
            }));
        }));

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key",          "data_directory");
            Set(cfg, "Label",        "Data Directory");
            Set(cfg, "Type",         EnumVal("TextInput"));
            Set(cfg, "DefaultValue", (object)SlayTheStatsConfig.DataDirectory);
            Set(cfg, "Placeholder",  "(default — leave empty)");
            Set(cfg, "MaxLength",    256);
            Set(cfg, "Description",  "Override the root SlayTheSpire2 data directory (the folder containing the 'steam' subfolder). Leave empty to use the platform default. Example: /home/deck/.local/share/SlayTheSpire2");
            Set(cfg, "OnChanged",    new Action<object>(v =>
            {
                SlayTheStatsConfig.DataDirectory = Convert.ToString(v) ?? "";
                ModConfig.SaveDebounced<SlayTheStatsConfig>();
            }));
        }));

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key",          "open_filters");
            Set(cfg, "Label",        "Filter Defaults");
            Set(cfg, "Type",         EnumVal("Button"));
            Set(cfg, "ButtonText",   "Open Filters");
            Set(cfg, "Description",  "Open the SlayTheStats filter pane to edit the saved aggregation defaults (ascension, version, profile, class filter). Use 'Save Defaults' inside the pane to persist your changes.");
            Set(cfg, "OnChanged",    new Action<object>(_ =>
            {
                CompendiumFilterPatch.OpenStandalonePane();
            }));
        }));

        var result = Array.CreateInstance(_entryType!, list.Count);
        for (int i = 0; i < list.Count; i++)
            result.SetValue(list[i], i);
        return result;
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    private static object Entry(Action<object> configure)
    {
        var inst = Activator.CreateInstance(_entryType!)!;
        configure(inst);
        return inst;
    }

    private static void Set(object obj, string name, object value)
        => obj.GetType().GetProperty(name)?.SetValue(obj, value);

    private static object EnumVal(string name)
        => Enum.Parse(_configTypeEnum!, name);
}
