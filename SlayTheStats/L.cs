using System.Collections.Generic;
using MegaCrit.Sts2.Core.Localization;

namespace SlayTheStats;

/// <summary>
/// Mod-owned localization. Thin wrapper over the game's <see cref="LocManager"/>
/// that queries our entries in the merged <c>settings_ui</c> loc table.
///
/// <para><b>How strings get into the game.</b> Our <c>.pck</c> ships
/// <c>res://SlayTheStats/localization/{lang}/settings_ui.json</c>. At boot,
/// <c>LocManager.LoadTablesFromPath</c> iterates base-game loc files and
/// merges each mod's contribution via <c>ModManager.GetModdedLocTables</c> +
/// <c>LocTable.MergeWith</c>. Our entries live in the base <c>settings_ui</c>
/// table alongside the game's own keys and BaseLib's auto-UI keys
/// (BASELIB-*, SLAYTHESTATS-*). No separate mod-owned file reading.</para>
///
/// <para><b>English fallback.</b> Non-English <c>LocTable</c> instances are
/// constructed with the English table as their fallback, so <see
/// cref="LocTable.GetRawText"/> automatically recurses when a key is missing
/// in the current locale's table. If the key is absent everywhere it throws
/// <see cref="LocException"/>, which we catch and surface the key itself so
/// gaps stay visible in-game rather than silently blank.</para>
///
/// <para><b>Live locale switching.</b> <see cref="LocManager"/> rebinds its
/// active table on language change, so every <see cref="T"/> call hits the
/// current locale automatically — no manual subscription or cache-invalidate
/// on our side. <see cref="TooltipHelper.InitLocaleSubscription"/> handles
/// the orthogonal concern of font cache invalidation for CJK fallback.</para>
///
/// <para><b>Replacements.</b> <see cref="T(string, (string, object)[])"/>
/// does a simple <c>Replace("{name}", value)</c> pass over the template —
/// no SmartFormat pluralization for v1.0.0.</para>
/// </summary>
public static class L
{
    private const string Table = "settings_ui";

    /// <summary>Missing-key warnings log once per key per session so a
    /// hover-heavy UI doesn't flood the log.</summary>
    private static readonly HashSet<string> _warnedMissing = new();

    /// <summary>Look up a translation. Missing keys log once + return the
    /// key itself so gaps stay visible in-game.</summary>
    public static string T(string key)
    {
        try
        {
            var mgr = LocManager.Instance;
            if (mgr == null) return key;
            var table = mgr.GetTable(Table);
            return table.GetRawText(key);
        }
        catch (LocException)
        {
            // Key absent in current locale AND English fallback — table's
            // internal fallback chain already walked.
            if (_warnedMissing.Add(key))
                MainFile.Logger.Warn($"[L] Missing key '{key}' in '{Table}' table (checked current locale + English fallback)");
        }
        catch (Exception e)
        {
            if (_warnedMissing.Add(key))
                MainFile.Logger.Warn($"[L] T('{key}') failed: {e.GetType().Name}: {e.Message}");
        }
        return key;
    }

    /// <summary>Template lookup with named-placeholder substitution:
    /// e.g. <c>L.T("tooltip.baseline.buys", ("buys", "22%"), ("win", "48%"))</c>
    /// replaces <c>{buys}</c> and <c>{win}</c> in the template. Simple string
    /// replace — no format specifiers, no escaping.</summary>
    public static string T(string key, params (string name, object value)[] variables)
    {
        var template = T(key);
        if (variables.Length == 0) return template;
        foreach (var (name, value) in variables)
            template = template.Replace("{" + name + "}", value?.ToString() ?? "");
        return template;
    }

    /// <summary>Resolves a character entry (<c>"IRONCLAD"</c> or the fully
    /// qualified <c>"CHARACTER.IRONCLAD"</c>) to its localized short display
    /// name via the game's <c>"characters"</c> loc table. Returns
    /// <paramref name="fallback"/> if the key is missing (modded characters
    /// without a titleObject, or pre-LocManager call).</summary>
    public static string CharacterName(string characterIdOrEntry, string fallback)
    {
        try
        {
            var entry = characterIdOrEntry.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
                ? characterIdOrEntry.Substring("CHARACTER.".Length)
                : characterIdOrEntry;
            if (entry.Length == 0) return fallback;
            var table = LocManager.Instance?.GetTable("characters");
            if (table != null && table.HasEntry(entry + ".titleObject"))
                return table.GetRawText(entry + ".titleObject");
        }
        catch (Exception e)
        {
            if (_warnedMissing.Add("character:" + characterIdOrEntry))
                MainFile.Logger.Warn($"[L] CharacterName lookup failed for '{characterIdOrEntry}': {e.Message}");
        }
        return fallback;
    }
}
