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

    // Reflection handles for the one-way sync from our static fields → ModConfig-STS2's
    // internal value store + live-binding refresh. See SyncFromConfig().
    private static FieldInfo? _managerValuesField;        // ModConfigManager._values (Dict<string, Dict<string, object>>)
    private static MethodInfo? _injectorNotifyMethod;     // SettingsTabInjector.NotifyValueChanged(modId, key, value)

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

            var managerType  = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ModConfigManager");
            var injectorType = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.SettingsTabInjector");
            _managerValuesField    = managerType?.GetField("_values", BindingFlags.NonPublic | BindingFlags.Static);
            _injectorNotifyMethod  = injectorType?.GetMethod("NotifyValueChanged", BindingFlags.NonPublic | BindingFlags.Static);
        }
        catch { _available = false; }
    }

    private static void Register()
    {
        if (_registered) return;
        _registered = true;

        // One-way sync: BaseLib's auto-generated settings page mutates our static
        // fields directly, but ModConfig-STS2 keeps its own internal value cache
        // that only updates from its own UI. Without this poll, toggling something
        // in BaseLib's page leaves the ModConfig page showing stale values until
        // the user clicks the row twice (once to "catch up", once to actually
        // change). Cheap: 6 dict reads + Equals checks per frame.
        if (_managerValuesField != null)
        {
            try
            {
                var tree = (SceneTree)Engine.GetMainLoop();
                tree.ProcessFrame += SyncFromConfig;
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"SlayTheStats: ModConfig sync hookup failed — {e.Message}");
            }
        }

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
            Set(cfg, "Key",          "open_filter_settings");
            Set(cfg, "Label",        "Filter Settings");
            Set(cfg, "Type",         EnumVal("Button"));
            Set(cfg, "ButtonText",   "Open");
            Set(cfg, "Description",  "Open the SlayTheStats filter pane (same controls as the Card Library / Relic Collection filter button). Changes take effect immediately; click 'Save Defaults' inside the pane to persist them across restarts.");
            Set(cfg, "OnChanged",    new Action<object>(_ => CompendiumFilterPatch.OpenStandalonePane()));
        }));

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
            Set(cfg, "Key",          "tutorial_seen");
            Set(cfg, "Label",        "Tutorial Seen");
            Set(cfg, "Type",         EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)SlayTheStatsConfig.TutorialSeen);
            Set(cfg, "Description",  "Whether the first-run welcome tutorial has been dismissed. Toggle off to see it again next time you open the Card Library or Relic Collection compendium page.");
            Set(cfg, "OnChanged",    new Action<object>(v =>
            {
                SlayTheStatsConfig.TutorialSeen = Convert.ToBoolean(v);
                ModConfig.SaveDebounced<SlayTheStatsConfig>();
            }));
        }));

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key",          "bestiary_tutorial_seen");
            Set(cfg, "Label",        "Bestiary Tutorial Seen");
            Set(cfg, "Type",         EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)SlayTheStatsConfig.BestiaryTutorialSeen);
            Set(cfg, "Description",  "Whether the bestiary tutorial overlay has been dismissed. Toggle off to see it again on next bestiary open.");
            Set(cfg, "OnChanged",    new Action<object>(v =>
            {
                SlayTheStatsConfig.BestiaryTutorialSeen = Convert.ToBoolean(v);
                ModConfig.SaveDebounced<SlayTheStatsConfig>();
            }));
        }));

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key",          "encounter_stats_mode");
            Set(cfg, "Label",        "Encounter Stats (requires restart)");
            Set(cfg, "Type",         EnumVal("Dropdown"));
            Set(cfg, "DefaultValue", (object)SlayTheStatsConfig.EncounterStatsRestartRequired.ToString());
            Set(cfg, "Options",      new[] { "BestiaryAndTooltips", "Tooltips", "Disabled" });
            Set(cfg, "Description",  "Controls which encounter-stats surfaces are enabled. BestiaryAndTooltips: Stats Bestiary button in compendium + in-combat hover tooltip. Tooltips: tooltip only, bestiary button hidden. Disabled: both off. Requires a game restart to take effect.");
            Set(cfg, "OnChanged",    new Action<object>(v =>
            {
                if (Enum.TryParse<SlayTheStatsConfig.EncounterStatsMode>(Convert.ToString(v), out var m))
                    SlayTheStatsConfig.EncounterStatsRestartRequired = m;
                ModConfig.SaveDebounced<SlayTheStatsConfig>();
            }));
        }));

        // BestiaryPreviewMode is exposed in the in-bestiary settings pane
        // (not the mod-settings page) so it sits next to the surface it affects.

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

        var result = Array.CreateInstance(_entryType!, list.Count);
        for (int i = 0; i < list.Count; i++)
            result.SetValue(list[i], i);
        return result;
    }

    // ── One-way sync: SlayTheStatsConfig static fields → ModConfig-STS2 _values ──
    //
    // Pushes the live static-field values into ModConfig-STS2's internal value
    // dictionary on every frame so its settings page never shows stale values
    // when the user changes a setting via BaseLib's settings page (or via the
    // filter pane, in the case of GroupCardUpgrades). Reflection-bypasses
    // OnChanged so we don't loop back into our own callback or schedule
    // redundant BaseLib saves; calls SettingsTabInjector.NotifyValueChanged
    // directly so any open ModConfig settings UI live-binding refreshes too.
    private static void SyncFromConfig()
    {
        try
        {
            var allValues = _managerValuesField?.GetValue(null) as System.Collections.IDictionary;
            if (allValues == null || !allValues.Contains(MainFile.ModId)) return;
            var modValues = allValues[MainFile.ModId] as Dictionary<string, object>;
            if (modValues == null) return;

            SyncOne(modValues, "color_blind_mode",          SlayTheStatsConfig.ColorBlindMode);
            SyncOne(modValues, "show_in_run_stats",         SlayTheStatsConfig.ShowInRunStats);
            SyncOne(modValues, "disable_tooltips_entirely", SlayTheStatsConfig.DisableTooltipsEntirely);
            SyncOne(modValues, "tutorial_seen",             SlayTheStatsConfig.TutorialSeen);
            SyncOne(modValues, "data_directory",            SlayTheStatsConfig.DataDirectory);
            SyncOne(modValues, "debug_mode",                SlayTheStatsConfig.DebugMode);
            SyncOne(modValues, "encounter_stats_mode",      SlayTheStatsConfig.EncounterStatsRestartRequired.ToString());
        }
        catch (Exception e)
        {
            // Don't spam — log once and unsubscribe to avoid hammering the log on every frame.
            MainFile.Logger.Warn($"SlayTheStats: ModConfig sync error, disabling — {e.Message}");
            try { ((SceneTree)Engine.GetMainLoop()).ProcessFrame -= SyncFromConfig; } catch { }
        }
    }

    private static void SyncOne(Dictionary<string, object> modValues, string key, object current)
    {
        if (modValues.TryGetValue(key, out var stored) && Equals(stored, current)) return;
        modValues[key] = current;
        try { _injectorNotifyMethod?.Invoke(null, new object[] { MainFile.ModId, key, current }); }
        catch { /* live-binding refresh is best-effort; underlying _values is the source of truth */ }
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
