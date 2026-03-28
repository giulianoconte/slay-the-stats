using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace SlayTheStats;

/// <summary>
/// Shows a card stats tooltip when a card is hovered on a reward screen (or any non-combat screen).
/// Only fires for NGridCardHolder (reward/shop/etc.) — NHandCardHolder (in-combat hand) is skipped.
/// Displays runs offered, runs picked, pick rate, and win rate per act,
/// filtered to the current character and standard (solo) mode only.
/// </summary>
[HarmonyPatch(typeof(NCardHolder), "CreateHoverTips")]
public static class CardHoverShowPatch
{
    private static bool _warnedOnce;
    private static bool _styleApplied;
    internal const string TooltipNodeName = "SlayTheStatsTooltip";

    /// <summary>
    /// Cached from the run-start patch so we have the character during reward screen hover,
    /// where card.Owner is null (card not yet assigned to the player).
    /// </summary>
    internal static string? CurrentCharacter;

    static void Postfix(NCardHolder __instance)
    {
        try
        {
            // Only show on non-combat card screens (reward, shop, etc.).
            // NHandCardHolder is the in-combat player hand — skip it.
            if (__instance is NHandCardHolder) return;

            EnsurePanelExists();

            var rawId = GetRawCardId(__instance);
            if (rawId == null) return;

            var upgradeLevel = GetUpgradeLevel(__instance);
            var suffix = upgradeLevel > 0 ? "+" : "";

            // Run files store ids with "CARD." prefix; game model returns them without.
            // Try prefixed form first, then raw.
            var lookupId = MainFile.Db.Cards.ContainsKey("CARD." + rawId + suffix) ? "CARD." + rawId + suffix
                         : MainFile.Db.Cards.ContainsKey("CARD." + rawId)          ? "CARD." + rawId
                         : MainFile.Db.Cards.ContainsKey(rawId + suffix)           ? rawId + suffix
                         : MainFile.Db.Cards.ContainsKey(rawId)                    ? rawId
                         : null;

            if (lookupId == null) return;

            var contextMap = MainFile.Db.Cards[lookupId];
            var character = CurrentCharacter ?? GetCharacterFromOwner(__instance);
            if (character != null && CurrentCharacter == null)
                CurrentCharacter = character; // cache for reward screens where Owner is null

            var actStats = StatsAggregator.AggregateByAct(contextMap, character, gameMode: "standard");
            if (actStats.Count == 0) return;

            var text = BuildStatsText(actStats, character);
            ShowTooltip(__instance, text);
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"SlayTheStats: tooltip unavailable — game update may have changed the card hover API: {e.Message}");
                _warnedOnce = true;
            }
        }
    }

    /// <summary>
    /// Creates the persistent tooltip panel if it doesn't already exist.
    /// Uses CallDeferred so the AddChild is safe to call from anywhere, including Harmony patches.
    /// The panel won't be available until the next frame after first call.
    /// </summary>
    internal static void EnsurePanelExists()
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root == null) return;
        if (root.GetNodeOrNull<Control>(TooltipNodeName) != null) return;

        var panel = new PanelContainer();
        panel.Name = TooltipNodeName;
        panel.Visible = false;

        var label = new Label();
        label.Name = "StatsLabel";
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        panel.AddChild(label);

        // Deferred so this is safe regardless of where it's called from.
        root.CallDeferred(Node.MethodName.AddChild, panel);
    }

    internal static void HideTooltip()
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        var node = root?.GetNodeOrNull<CanvasItem>(TooltipNodeName);
        if (node != null) node.Visible = false;
    }

    private static string? GetRawCardId(NCardHolder holder)
    {
        var cardNode = AccessTools.Property(typeof(NCardHolder), "CardNode")?.GetValue(holder);
        if (cardNode == null) return null;

        var model = AccessTools.Property(cardNode.GetType(), "Model")?.GetValue(cardNode);
        if (model == null) return null;

        var id = AccessTools.Property(model.GetType(), "Id")?.GetValue(model);
        if (id == null) return null;

        return AccessTools.Field(id.GetType(), "Entry")?.GetValue(id) as string
            ?? AccessTools.Property(id.GetType(), "Entry")?.GetValue(id) as string
            ?? id.ToString();
    }

    private static int GetUpgradeLevel(NCardHolder holder)
    {
        try
        {
            var cardNode = AccessTools.Property(typeof(NCardHolder), "CardNode")?.GetValue(holder);
            var model = cardNode != null ? AccessTools.Property(cardNode.GetType(), "Model")?.GetValue(cardNode) : null;
            if (model == null) return 0;

            return AccessTools.Property(model.GetType(), "CurrentUpgradeLevel")?.GetValue(model) as int?
                ?? AccessTools.Field(model.GetType(), "CurrentUpgradeLevel")?.GetValue(model) as int?
                ?? 0;
        }
        catch { return 0; }
    }

    private static string? GetCharacterFromOwner(NCardHolder holder)
    {
        try
        {
            var cardNode = AccessTools.Property(typeof(NCardHolder), "CardNode")?.GetValue(holder);
            var model = cardNode != null ? AccessTools.Property(cardNode.GetType(), "Model")?.GetValue(cardNode) : null;
            var owner = model != null ? AccessTools.Property(model.GetType(), "Owner")?.GetValue(model) : null;
            return owner != null ? AccessTools.Property(owner.GetType(), "CharacterId")?.GetValue(owner) as string : null;
        }
        catch { return null; }
    }

    private static string BuildStatsText(Dictionary<int, CardStat> actStats, string? character)
    {
        var characterLabel = character != null
            ? char.ToUpper(character.Split('.').Last()[0]) + character.Split('.').Last().Substring(1).ToLower()
            : "All Characters";

        var sb = new StringBuilder();
        sb.AppendLine($"SlayTheStats ({characterLabel}, Solo)");
        sb.AppendLine("Act | Offered | Picked |  PR%  |  WR%");

        foreach (var (act, stat) in actStats.OrderBy(kv => kv.Key))
        {
            var pr = stat.RunsOffered > 0 ? $"{100.0 * stat.RunsPicked / stat.RunsOffered:F1}%" : "—";
            var wr = stat.RunsPicked  > 0 ? $"{100.0 * stat.RunsWon    / stat.RunsPicked:F1}%"  : "—";
            sb.AppendLine($"  {act}   |    {stat.RunsOffered,4} |   {stat.RunsPicked,4} | {pr,5} | {wr,5}");
        }

        return sb.ToString().TrimEnd();
    }

    private static void ApplyStyle(Control panel, NCardHolder anchor)
    {
        // Try to steal the exact StyleBox from the game's own hover tip container.
        var gameStyle = TryStealGamePanelStyle(anchor);
        if (gameStyle != null)
        {
            panel.AddThemeStyleboxOverride("panel", gameStyle);
        }
        else
        {
            // Fallback: approximate the game's dark tooltip aesthetic.
            var style = new StyleBoxFlat();
            style.BgColor = new Color(0.11f, 0.07f, 0.06f, 0.94f);
            style.BorderWidthBottom = 1;
            style.BorderWidthTop = 1;
            style.BorderWidthLeft = 1;
            style.BorderWidthRight = 1;
            style.BorderColor = new Color(0f, 0f, 0f, 0.55f);
            style.ContentMarginLeft = 8f;
            style.ContentMarginRight = 8f;
            style.ContentMarginTop = 6f;
            style.ContentMarginBottom = 6f;
            style.CornerRadiusTopLeft = 3;
            style.CornerRadiusTopRight = 3;
            style.CornerRadiusBottomLeft = 3;
            style.CornerRadiusBottomRight = 3;
            panel.AddThemeStyleboxOverride("panel", style);
        }

        var label = panel.GetNodeOrNull<Label>("StatsLabel");
        if (label == null) return;

        // Cream text color matching the game's UI palette.
        label.AddThemeColorOverride("font_color", new Color(1f, 0.9647f, 0.8863f, 1f));

        // Monospace font for column alignment; fall back to game's Kreon if unavailable.
        var mono = ResourceLoader.Load<Font>("res://fonts/source_code_pro_medium.ttf");
        var fallbackFont = mono ?? ResourceLoader.Load<Font>("res://themes/kreon_regular_shared.tres");
        if (fallbackFont != null) label.AddThemeFontOverride("font", fallbackFont);

        label.AddThemeFontSizeOverride("font_size", 14);
    }

    /// <summary>
    /// Tries to get the StyleBox from the game's NHoverTipSet container so our panel
    /// matches the exact visual style of the existing card tooltips.
    /// </summary>
    private static StyleBox? TryStealGamePanelStyle(NCardHolder holder)
    {
        try
        {
            var container = AccessTools.Property(typeof(NCardHolder), "HoverTipsContainer")?.GetValue(holder);
            if (container is not Control ctrl) return null;

            if (ctrl is PanelContainer pc)
                return pc.GetThemeStylebox("panel");

            foreach (var child in ctrl.GetChildren())
                if (child is PanelContainer childPc)
                    return childPc.GetThemeStylebox("panel");
        }
        catch { }
        return null;
    }

    private static void ShowTooltip(NCardHolder anchor, string text)
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        var panel = root?.GetNodeOrNull<Control>(TooltipNodeName);
        if (panel == null) return;

        var label = panel.GetNodeOrNull<Label>("StatsLabel");
        if (label == null) return;

        if (!_styleApplied)
        {
            ApplyStyle(panel, anchor);
            _styleApplied = true;
        }

        label.Text = text;
        panel.GlobalPosition = anchor.GlobalPosition + new Vector2(anchor.Size.X + 10, 0);
        panel.Visible = true;
    }
}

[HarmonyPatch(typeof(NCardHolder), "ClearHoverTips")]
public static class CardHoverHidePatch
{
    static void Postfix(NCardHolder __instance)
    {
        try { CardHoverShowPatch.HideTooltip(); }
        catch { }
    }
}

/// <summary>
/// Safety net: hides the tooltip if the card node is returned to the pool
/// (e.g. when a card is picked and the reward screen closes) without going through ClearHoverTips.
/// </summary>
[HarmonyPatch(typeof(NCard), "OnFreedToPool")]
public static class CardFreedToPoolPatch
{
    static void Postfix()
    {
        try { CardHoverShowPatch.HideTooltip(); }
        catch { }
    }
}

/// <summary>
/// Caches the current character when a run starts so the tooltip can filter by it,
/// since reward screen cards don't have an Owner set yet.
/// Cleared when returning to the main menu.
/// </summary>
[HarmonyPatch(typeof(NGame), "ReturnToMainMenuAfterRun")]
public static class ClearCurrentCharacterPatch
{
    static void Prefix()
    {
        CardHoverShowPatch.CurrentCharacter = null;
    }
}
