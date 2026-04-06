using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using System.Reflection;

namespace SlayTheStats;

// ─────────────────────────────────────────────────────────────────────
// Combat-monster encounter stats tooltip.
//
// Design (revised): the previous version dumped a tooltip the moment the player's mouse touched
// a monster. That was disruptive because targeting cards involves a lot of fast hovering over
// enemies. The new flow:
//
//   1. On focus, kick off a 1-second hover timer (a single generation token cancels stale
//      timers if the player moves off the monster).
//   2. When the timer fires, look up the active NHoverTipSet for the monster's hitbox and
//      build a free-floating stats panel. Position it directly above the tipset's text
//      hover container (the stack of debuff/intent rows the game shows). Because we sit
//      *above* the existing stack, the original tooltips stay where they were — they
//      don't slide down to make room for us.
//   3. A small follower process keeps the panel pinned above the moving tipset each frame.
//   4. On unfocus, the panel is hidden and the generation token bumps so any in-flight
//      timer becomes a no-op.
// ─────────────────────────────────────────────────────────────────────

[HarmonyPatch(typeof(NCreature), "OnFocus")]
public static class CreatureFocusPatch
{
    private static bool _warnedOnce;

    static void Postfix(NCreature __instance)
    {
        if (SlayTheStatsConfig.DisableTooltipsEntirely) return;
        if (!CardHoverShowPatch.IsInRun) return;

        // Don't pop the stats tooltip while the player is mid-targeting (e.g. clicked an
        // attack and is now hovering enemies to pick a target). It's distracting and the
        // mouse passes over enemies very quickly during this state.
        try
        {
            if (NTargetManager.Instance != null && NTargetManager.Instance.IsInSelection)
                return;
        }
        catch { /* defensive: NTargetManager may not exist outside combat */ }

        try
        {
            var encounterId = GetEncounterId(__instance);
            if (encounterId == null) return;
            if (!MainFile.Db.Encounters.ContainsKey(encounterId)) return;

            EncounterStatsHover.OnFocus(__instance, encounterId);
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"[SlayTheStats] Encounter focus failed: {e.Message}");
                _warnedOnce = true;
            }
        }
    }

    /// <summary>
    /// Extracts the encounter model ID from an NCreature via reflection:
    /// NCreature.Entity.CombatState.Encounter.Id -> "ENCOUNTER.X"
    /// </summary>
    private static string? GetEncounterId(NCreature creature)
    {
        var entity = creature.GetType()
            .GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(creature);
        if (entity == null) return null;

        var combatState = entity.GetType()
            .GetProperty("CombatState", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(entity);
        if (combatState == null) return null;

        var encounter = combatState.GetType()
            .GetProperty("Encounter", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(combatState);
        if (encounter == null) return null;

        var encId = encounter.GetType()
            .GetProperty("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(encounter);
        if (encId == null) return null;

        var category = encId.GetType()
            .GetProperty("Category", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(encId) as string;
        var entry = encId.GetType()
            .GetProperty("Entry", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(encId) as string;

        return category != null && entry != null ? $"{category}.{entry}".ToUpper() : null;
    }
}

[HarmonyPatch(typeof(NCreature), "OnUnfocus")]
public static class CreatureUnfocusPatch
{
    static void Postfix(NCreature __instance)
    {
        EncounterStatsHover.OnUnfocus(__instance);
    }
}

// Extend the hover area to include the health bar / state display. NCreature wires its own
// MouseEntered/Exited on the Hitbox; we add a parallel pair of handlers on _stateDisplay so
// that hovering anywhere over the health bar shows the same tooltips as hovering the body.
[HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
public static class CreatureReadyPatch
{
    private static bool _warnedOnce;

    static void Postfix(NCreature __instance)
    {
        try
        {
            var stateDisplayField = AccessTools.Field(typeof(NCreature), "_stateDisplay");
            if (stateDisplayField?.GetValue(__instance) is not Control stateDisplay) return;

            // The state display is normally non-interactive. Bumping its mouse filter to Pass
            // lets it receive MouseEntered/Exited without intercepting clicks.
            if (stateDisplay.MouseFilter == Control.MouseFilterEnum.Ignore)
                stateDisplay.MouseFilter = Control.MouseFilterEnum.Pass;

            var onFocus   = AccessTools.Method(typeof(NCreature), "OnFocus");
            var onUnfocus = AccessTools.Method(typeof(NCreature), "OnUnfocus");
            if (onFocus == null || onUnfocus == null) return;

            stateDisplay.MouseEntered += () =>
            {
                try { onFocus.Invoke(__instance, null); } catch { }
            };
            stateDisplay.MouseExited += () =>
            {
                try { onUnfocus.Invoke(__instance, null); } catch { }
            };
        }
        catch (Exception e)
        {
            if (!_warnedOnce)
            {
                MainFile.Logger.Warn($"[SlayTheStats] Health bar hover wire-up failed: {e.Message}");
                _warnedOnce = true;
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────
// Self-contained hover state machine + panel for combat encounter stats.
// ─────────────────────────────────────────────────────────────────────

internal static class EncounterStatsHover
{
    /// <summary>How long the player must hover an enemy before the panel appears.</summary>
    private const double HoverDelaySeconds = 0.75;

    /// <summary>Vertical gap between our panel and the top of the existing tooltip stack.</summary>
    private const float StackGapPx = 8f;

    // Generation token: incremented on every focus and every unfocus. Pending timers compare
    // against the current value before showing — any mismatch means the hover became stale.
    private static int _gen;
    private static NCreature? _focusedCreature;
    private static string?    _focusedEncounterId;

    private static PanelContainer? _panel;
    private static RichTextLabel?  _panelLabel;
    private static NinePatchRect?  _shadow;
    private static bool _followerInjected;

    /// <summary>The hitbox owner the currently-shown panel is anchored to.</summary>
    internal static Control? CurrentOwner;

    private static FieldInfo? _activeHoverTipsField;
    private static FieldInfo? GetActiveHoverTipsField() =>
        _activeHoverTipsField ??= AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips");

    public static void OnFocus(NCreature creature, string encounterId)
    {
        _gen++;
        var token = _gen;
        _focusedCreature    = creature;
        _focusedEncounterId = encounterId;

        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null) return;

        var timer = tree.CreateTimer(HoverDelaySeconds);
        timer.Timeout += () =>
        {
            // Stale-timer guard: if the player moved off (or onto another monster) the gen
            // bumped and we silently drop this fire.
            if (token != _gen) return;
            if (_focusedCreature == null || _focusedEncounterId == null) return;
            // Card targeting may have started during the delay window — bail out so we don't
            // pop a panel over the targeting reticle.
            try
            {
                if (NTargetManager.Instance != null && NTargetManager.Instance.IsInSelection)
                    return;
            }
            catch { }
            try
            {
                ShowFor(_focusedCreature, _focusedEncounterId);
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"[SlayTheStats] Encounter show failed: {e.Message}");
            }
        };
    }

    public static void OnUnfocus(NCreature creature)
    {
        if (_focusedCreature != null && _focusedCreature != creature) return;
        _gen++;
        _focusedCreature    = null;
        _focusedEncounterId = null;
        Hide();
    }

    private static void ShowFor(NCreature creature, string encounterId)
    {
        // Look up the hitbox via reflection rather than the public Hitbox getter, in case the
        // property name varies across game builds.
        var hitbox = creature.GetType()
            .GetProperty("Hitbox", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(creature) as Control;
        if (hitbox == null) return;

        if (!MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap)) return;

        var effectiveChar = CardHoverShowPatch.RunCharacter;
        var filter = BuildFilter(effectiveChar);
        var actStats = StatsAggregator.AggregateEncountersByAct(contextMap, filter);

        string? category = null;
        if (MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta))
            category = meta.Category;

        var categoryLabel = category != null ? EncounterCategory.FormatCategory(category) : "";
        var characterLabel = effectiveChar != null ? FormatCharacterName(effectiveChar) : "All";

        double deathRateBaseline = StatsAggregator.GetEncounterDeathRateBaseline(MainFile.Db, filter, category);
        double dmgPctBaseline    = StatsAggregator.GetEncounterDmgPctBaseline(MainFile.Db, filter, category);

        var combined = new EncounterEvent();
        foreach (var stat in actStats.Values)
        {
            combined.Fought           += stat.Fought;
            combined.Died             += stat.Died;
            combined.WonRun           += stat.WonRun;
            combined.TurnsTakenSum    += stat.TurnsTakenSum;
            combined.DamageTakenSum   += stat.DamageTakenSum;
            combined.DamageTakenSqSum += stat.DamageTakenSqSum;
            combined.HpEnteringSum    += stat.HpEnteringSum;
            combined.MaxHpSum         += stat.MaxHpSum;
            combined.PotionsUsedSum   += stat.PotionsUsedSum;
            combined.DmgPctSum        += stat.DmgPctSum;
            combined.DmgPctSqSum      += stat.DmgPctSqSum;
        }

        string statsText = combined.Fought == 0
            ? EncounterTooltipHelper.NoDataText(characterLabel, filter.AscensionMin, filter.AscensionMax)
            : EncounterTooltipHelper.BuildEncounterStatsTextSingleRow(
                combined, deathRateBaseline, dmgPctBaseline,
                characterLabel, filter.AscensionMin, filter.AscensionMax, categoryLabel);

        var encounterName = EncounterCategory.FormatName(encounterId);
        var bbcode = $"[b]{encounterName}[/b]                    [color=#606060]SlayTheStats[/color]\n{statsText}";

        EnsurePanel();
        if (_panel == null || _panelLabel == null) return;

        _panelLabel.Text = bbcode;
        _panelLabel.ResetSize();
        _panel.ResetSize();

        // Reparent to the root so we render above everything else.
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root != null && _panel.GetParent() != root)
        {
            _panel.GetParent()?.RemoveChild(_panel);
            root.AddChild(_panel);
        }
        if (root != null && _shadow != null && GodotObject.IsInstanceValid(_shadow) && _shadow.GetParent() != root)
        {
            _shadow.GetParent()?.RemoveChild(_shadow);
            root.AddChild(_shadow);
        }

        if (!_followerInjected)
        {
            _followerInjected = true;
            var follower = new EncounterStatsHoverFollower { Name = "SlayTheStatsEncounterFollower" };
            root?.AddChild(follower);
        }

        CurrentOwner = hitbox;
        _panel.Visible = true;
        if (_shadow != null && GodotObject.IsInstanceValid(_shadow)) _shadow.Visible = true;

        // Position immediately so there's no one-frame flicker before the follower runs.
        UpdatePosition();
    }

    public static void Hide()
    {
        CurrentOwner = null;
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
            _panel.Visible = false;
        if (_shadow != null && GodotObject.IsInstanceValid(_shadow))
            _shadow.Visible = false;
    }

    /// <summary>
    /// Repositions the panel directly above the active NHoverTipSet for the current owner.
    /// Called every frame from <see cref="EncounterStatsHoverFollower"/>.
    /// </summary>
    public static void UpdatePosition()
    {
        if (_panel == null || !_panel.Visible || CurrentOwner == null) return;

        var tipSet = LookupActiveTipSet(CurrentOwner);
        Vector2 anchor;
        Vector2 anchorSize;

        if (tipSet != null)
        {
            // Prefer the textHoverTipContainer rect — that's the visible block of debuff/intent
            // rows, and our panel should sit directly above it.
            var textContainer = ((Node)tipSet).GetNodeOrNull<Control>("textHoverTipContainer");
            if (textContainer != null && textContainer.Size.Y > 0f)
            {
                anchor     = textContainer.GlobalPosition;
                anchorSize = textContainer.Size;
            }
            else
            {
                anchor     = ((Control)tipSet).GlobalPosition;
                anchorSize = ((Control)tipSet).Size;
            }
        }
        else
        {
            // No tipset — fall back to the hitbox itself so the panel still floats above the
            // monster sprite even if the game's hover stack hasn't materialized.
            anchor     = CurrentOwner.GlobalPosition;
            anchorSize = CurrentOwner.Size;
        }

        // Center horizontally over the anchor; clamp to viewport so the panel never escapes
        // the screen.
        var panelSize    = _panel.Size;
        var viewportSize = _panel.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        float x = anchor.X + anchorSize.X * 0.5f - panelSize.X * 0.5f;
        x = Math.Clamp(x, 0f, Math.Max(0f, viewportSize.X - panelSize.X));
        float y = anchor.Y - panelSize.Y - StackGapPx;
        if (y < 0f) y = anchor.Y + anchorSize.Y + StackGapPx; // overflow → drop below

        _panel.GlobalPosition = new Vector2(x, y);

        if (_shadow != null && GodotObject.IsInstanceValid(_shadow))
        {
            _shadow.GlobalPosition = _panel.GlobalPosition + new Vector2(TooltipHelper.ShadowOffset, TooltipHelper.ShadowOffset);
            _shadow.Size           = _panel.Size;
        }
    }

    private static NHoverTipSet? LookupActiveTipSet(Control owner)
    {
        var field = GetActiveHoverTipsField();
        if (field == null) return null;
        if (field.GetValue(null) is not System.Collections.IDictionary dict) return null;
        return dict[owner] as NHoverTipSet;
    }

    private static void EnsurePanel()
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
            return;

        // Make sure the shared TooltipHelper assets (panel stylebox, fonts) are initialized.
        TooltipHelper.TrySceneTheftOnce();

        var panel = new PanelContainer();
        panel.Name      = "SlayTheStatsEncounterTooltip";
        panel.Visible   = false;
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        panel.CustomMinimumSize = new Vector2(TooltipHelper.EncounterTooltipWidth, 0);
        panel.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f);
        panel.ZIndex = 200;

        var label = new RichTextLabel();
        label.Name                = "EncounterStatsLabel";
        label.BbcodeEnabled       = true;
        label.FitContent          = true;
        label.AutowrapMode        = TextServer.AutowrapMode.Off;
        label.ScrollActive        = false;
        label.SelectionEnabled    = false;
        label.ShortcutKeysEnabled = false;
        label.MouseFilter         = Control.MouseFilterEnum.Ignore;

        var monoFont = TooltipHelper.GetMonoFont();
        if (monoFont != null)
        {
            label.AddThemeFontOverride("normal_font", monoFont);
            var monoBold = TooltipHelper.GetMonoBoldFont();
            if (monoBold != null) label.AddThemeFontOverride("bold_font", monoBold);
        }
        label.AddThemeFontSizeOverride("normal_font_size", 18);
        label.AddThemeFontSizeOverride("bold_font_size", 18);
        label.AddThemeColorOverride("default_color", new Color(1f, 0.9647f, 0.8863f, 1f));
        label.AddThemeConstantOverride("line_separation", 0);

        panel.AddChild(label);

        // Steal the same stone-tile stylebox the shared TooltipHelper builds for cards/relics.
        var style = TooltipHelper.BuildPanelStyle();
        if (style != null)
            panel.AddThemeStyleboxOverride("panel", style);

        var shadow = new NinePatchRect();
        shadow.Name              = "SlayTheStatsEncounterShadow";
        shadow.Texture           = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png");
        shadow.PatchMarginLeft   = 55;
        shadow.PatchMarginRight  = 91;
        shadow.PatchMarginTop    = 43;
        shadow.PatchMarginBottom = 32;
        shadow.Modulate          = new Color(0f, 0f, 0f, 0.25098f);
        shadow.ZIndex            = 199;
        shadow.Visible           = false;
        shadow.MouseFilter       = Control.MouseFilterEnum.Ignore;

        _panel      = panel;
        _panelLabel = label;
        _shadow     = shadow;
    }

    private static AggregationFilter BuildFilter(string? character)
    {
        // Use the central sanitised filter so corrupt cfg values can never block stats here.
        var filter = SlayTheStatsConfig.BuildSafeFilter();
        filter.Character = character;
        return filter;
    }

    private static string FormatCharacterName(string characterId)
    {
        var dotIdx = characterId.IndexOf('.');
        var name = dotIdx >= 0 ? characterId[(dotIdx + 1)..] : characterId;
        if (name.Length == 0) return characterId;
        return char.ToUpper(name[0]) + name[1..].ToLower().Replace('_', ' ');
    }
}

/// <summary>
/// Tiny scene-tree node that drives the encounter stats panel's per-frame follow logic.
/// Lives for the entire session — it's idle when no panel is shown.
/// </summary>
public partial class EncounterStatsHoverFollower : Node
{
    public override void _Process(double delta)
    {
        EncounterStatsHover.UpdatePosition();
    }
}
