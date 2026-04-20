using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;

namespace SlayTheStats;

/// <summary>
/// Mod-owned localization. All user-facing strings flow through <see cref="T"/>;
/// the current locale tracks the game's <see cref="LocManager"/> language, English
/// is the fallback, and a missing key renders as the key itself so gaps are
/// visible in-game rather than silently blank.
///
/// JSON files live on disk at <c>{modPath}/localization/{lang}/*.json</c>. Keys
/// are flat strings (e.g. <c>"tooltip.event.header"</c>); every JSON file inside a
/// language dir is merged into one dict for that language at load time, so
/// translators can split strings across as many files as they want.
///
/// We read from disk with <see cref="File.ReadAllText"/> rather than Godot's
/// <c>ResourceLoader</c> because the mod doesn't ship a <c>.pck</c> (dropped in
/// v0.3.1), so <c>res://</c> paths can't resolve mod-owned JSON. Trade-off: we
/// don't hook into the game's Weblate / <see cref="LocTable"/> pipeline. If
/// community translators emerge and want Weblate integration we revisit.
/// </summary>
public static class L
{
    private const string DefaultLang = "eng";

    private static readonly Dictionary<string, Dictionary<string, string>> _tables = new();
    private static Dictionary<string, string> _currentTable = new();
    private static Dictionary<string, string> _fallbackTable = new();
    private static string _currentLang = DefaultLang;

    /// <summary>Missing keys are logged once per session. Without this, a key
    /// referenced from a hover-tooltip code path would spam the log.</summary>
    private static readonly HashSet<string> _warnedMissing = new();

    /// <summary>Called once from <see cref="MainFile.Initialize"/>. Needs
    /// <see cref="ModManager.Mods"/> populated (true by the time a mod initializer
    /// runs) and <see cref="LocManager.Instance"/> alive (always true before
    /// mod load). Safe to call multiple times — reloads tables and re-binds.</summary>
    public static void Init()
    {
        try
        {
            _tables.Clear();
            _warnedMissing.Clear();

            var modPath = FindModPath();
            if (modPath == null)
            {
                MainFile.Logger.Warn("[L] Could not find SlayTheStats mod path — localization disabled, all keys render as keys");
                return;
            }

            var localeRoot = Path.Combine(modPath, "localization");
            if (!Directory.Exists(localeRoot))
            {
                MainFile.Logger.Warn($"[L] Localization dir not found at {localeRoot} — all keys render as keys");
                return;
            }

            foreach (var langDir in Directory.GetDirectories(localeRoot))
            {
                var lang = Path.GetFileName(langDir);
                var merged = new Dictionary<string, string>();
                foreach (var jsonFile in Directory.GetFiles(langDir, "*.json"))
                {
                    try
                    {
                        var text = File.ReadAllText(jsonFile);
                        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
                        if (dict == null) continue;
                        foreach (var kv in dict) merged[kv.Key] = kv.Value;
                    }
                    catch (Exception e)
                    {
                        MainFile.Logger.Warn($"[L] Failed to parse {jsonFile}: {e.Message}");
                    }
                }
                _tables[lang] = merged;
                MainFile.Logger.Info($"[L] Loaded locale '{lang}' ({merged.Count} keys)");
            }

            _fallbackTable = _tables.TryGetValue(DefaultLang, out var f) ? f : new Dictionary<string, string>();
            if (_fallbackTable.Count == 0)
                MainFile.Logger.Warn($"[L] No '{DefaultLang}' locale loaded — missing keys will render as keys");

            RefreshFromGameLocale();
            LocString.SubscribeToLocaleChange(RefreshFromGameLocale);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[L] Init failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static string? FindModPath()
    {
        foreach (var mod in ModManager.Mods)
        {
            if (mod.manifest?.id == MainFile.ModId)
                return mod.path;
        }
        return null;
    }

    /// <summary>Re-points <see cref="_currentTable"/> at the game's active locale.
    /// Called on boot and on every locale change (via <see cref="LocString.SubscribeToLocaleChange"/>).
    /// UI surfaces built once and cached (bestiary chrome, tutorial popups) need
    /// to rebuild on locale change too — that's a separate subscription concern;
    /// tooltip surfaces that rebuild on every show pick up the new locale for free.</summary>
    private static void RefreshFromGameLocale()
    {
        _currentLang = LocManager.Instance?.Language ?? DefaultLang;
        _currentTable = _tables.TryGetValue(_currentLang, out var t) ? t : _fallbackTable;
        MainFile.Logger.Info($"[L] Active locale: '{_currentLang}' ({_currentTable.Count} keys)");
        _warnedMissing.Clear();
    }

    /// <summary>Look up a translation. Current locale → English fallback → key
    /// itself. Missing-key warnings log once per key per session.</summary>
    public static string T(string key)
    {
        if (_currentTable.TryGetValue(key, out var val)) return val;
        if (_fallbackTable.TryGetValue(key, out var fallback)) return fallback;
        if (_warnedMissing.Add(key))
            MainFile.Logger.Warn($"[L] Missing key '{key}' in '{_currentLang}' and fallback '{DefaultLang}'");
        return key;
    }

    /// <summary>Look up a translation with named-placeholder substitution:
    /// template like <c>"(baseline) Pick% {pick}"</c>, call as
    /// <c>L.T("tooltip.baseline.card", ("pick", "35%"))</c>. Placeholder names
    /// are substituted via simple string replace — no SmartFormat pluralization
    /// for v1.0.0; revisit if translators need it.</summary>
    public static string T(string key, params (string name, object value)[] variables)
    {
        var template = T(key);
        if (variables.Length == 0) return template;
        foreach (var (name, value) in variables)
            template = template.Replace("{" + name + "}", value?.ToString() ?? "");
        return template;
    }
}
