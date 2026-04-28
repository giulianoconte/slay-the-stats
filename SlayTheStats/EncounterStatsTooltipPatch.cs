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

    /// <summary>
    /// NCreature.OnFocus is wired to BOTH MouseEntered AND FocusEntered signals on the
    /// hitbox. After the player plays a card on a monster, the click sets keyboard focus
    /// on the hitbox → FocusEntered fires → OnFocus is called → the base method
    /// early-returns because IsFocused is already true → but our Postfix would still
    /// run and kick off the hover timer, popping a tooltip the player didn't ask for.
    ///
    /// Capture IsFocused BEFORE the base call so the Postfix can tell whether this
    /// invocation actually transitioned focus (true → run our logic) or was a no-op
    /// re-entry (false → skip to mirror the base method's early-return).
    /// </summary>
    static void Prefix(NCreature __instance, out bool __state)
    {
        __state = __instance.IsFocused;
    }

    static void Postfix(NCreature __instance, bool __state)
    {
        // Was already focused before this call → base method no-op'd → mirror that.
        if (__state) return;

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
            // Only show encounter stats for actual enemy monsters — NCreature.OnFocus
            // fires for the player and for pets (Osty et al) too, since they all derive
            // from NCreature. Walk Entity to Creature and gate on IsMonster so hovering
            // the player / pet never pops the stats tooltip.
            if (!IsEnemyMonster(__instance)) return;

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
    /// True iff the hovered NCreature's underlying Creature entity is a
    /// monster (as opposed to the player or a friendly pet like Osty).
    /// NCreature.OnFocus fires for any creature in combat — without this
    /// gate, the stats tooltip would pop over the player's head and over
    /// pet hitboxes. Reads <c>Creature.IsMonster</c> via reflection to stay
    /// consistent with the rest of the patch's reflection-based entity
    /// walking. Defensive: returns false on any reflection failure so we
    /// only show the tooltip when we're confident it's a monster.
    /// </summary>
    private static bool IsEnemyMonster(NCreature creature)
    {
        try
        {
            var entity = creature.GetType()
                .GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(creature);
            if (entity == null) return false;

            var isMonster = entity.GetType()
                .GetProperty("IsMonster", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(entity);
            return isMonster is bool b && b;
        }
        catch
        {
            return false;
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
    /// <summary>How long the player must hover an enemy before the panel appears.
    /// 0 = show immediately. The original 0.75s delay was kept while the tooltip was
    /// debounced for rapid movement; reverted to instant since the column trim made the
    /// panel small enough that pop-in during quick hovers is no longer disruptive.</summary>
    private const double HoverDelaySeconds = 0.0;

    /// <summary>Vertical gap between our panel and the top of the existing tooltip stack.</summary>
    private const float StackGapPx = 8f;

    // Generation token: incremented on every focus and every unfocus. Pending timers compare
    // against the current value before showing — any mismatch means the hover became stale.
    private static int _gen;
    private static NCreature? _focusedCreature;
    private static string?    _focusedEncounterId;

    private static PanelContainer? _panel;
    private static RichTextLabel?  _panelLabel;       // table body (mono font)
    private static RichTextLabel?  _panelNameLabel;   // encounter name (Kreon bold, gold)
    private static Label?          _panelBrandLabel;  // "SlayTheStats" suffix (grey, right-aligned)
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
        // User opted out via the mod config — never build/show the in-combat tooltip.
        if (!SlayTheStatsConfig.InCombatTooltipEnabled) return;

        // Look up the hitbox via reflection rather than the public Hitbox getter, in case the
        // property name varies across game builds.
        var hitbox = creature.GetType()
            .GetProperty("Hitbox", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(creature) as Control;
        if (hitbox == null) return;

        EnsurePanel();
        if (_panel == null || _panelLabel == null || _panelNameLabel == null) return;

        var handle = new TooltipPanelHandle
        {
            Panel      = _panel,
            NameLabel  = _panelNameLabel,
            BrandLabel = _panelBrandLabel!,
            TableLabel = _panelLabel,
            Shadow     = _shadow!,
        };
        if (!PopulatePanelData(handle, encounterId)) return;

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

        // Hide for one frame to let the WordSmart-wrapped label finalise its
        // height. The label's GetMinimumSize returns the unwrapped height on
        // the first query after SetText, then the correct wrapped height on
        // the next frame. If we position+show this frame, panelSize.Y is
        // stale and UpdatePosition's y<0 overflow branch drops the tooltip
        // below the hover stack for a frame. Deferring avoids that flicker.
        _panel.Visible = false;
        if (_shadow != null && GodotObject.IsInstanceValid(_shadow)) _shadow.Visible = false;
        Callable.From(DeferredShowAndPosition).CallDeferred();
    }

    /// <summary>
    /// Called via <see cref="Callable.CallDeferred"/> from
    /// <see cref="ShowForCreature"/> once the current frame's layout has
    /// propagated the new label text into the panel's Size. Reads Size via
    /// UpdatePosition and only then flips the panel to visible.
    /// </summary>
    private static void DeferredShowAndPosition()
    {
        if (_panel == null || CurrentOwner == null || !GodotObject.IsInstanceValid(CurrentOwner))
            return;
        _panel.Visible = true;
        if (_shadow != null && GodotObject.IsInstanceValid(_shadow)) _shadow.Visible = true;
        UpdatePosition();
    }

    /// <summary>
    /// Loads encounter data via the StatsDb, builds the table BBCode + name BBCode, and
    /// writes them into the supplied panel handle's labels. Returns false if the encounter
    /// id has no data in the db (caller should skip the show in that case).
    /// </summary>
    private static bool PopulatePanelData(TooltipPanelHandle handle, string encounterId)
    {
        if (!MainFile.Db.Encounters.TryGetValue(encounterId, out var contextMap)) return false;

        var effectiveChar = CardHoverShowPatch.RunCharacter;
        var filter = BuildFilter(effectiveChar);
        var actStats = StatsAggregator.AggregateEncountersByAct(contextMap, filter);

        string? category = null;
        string? biome = null;
        if (MainFile.Db.EncounterMeta.TryGetValue(encounterId, out var meta))
        {
            category = meta.Category;
            biome = meta.Biome;
        }

        var categoryLabel = category != null ? EncounterCategory.FormatCategory(category) : "";
        var characterLabel = effectiveChar != null ? FormatCharacterName(effectiveChar) : null;

        var poolBaseline = StatsAggregator.AggregateEncounterPoolWeighted(MainFile.Db, filter, category, biome);
        double dmgBaseline = StatsAggregator.GetEncounterDmgBaseline(MainFile.Db, filter, category, biome);

        var combined = new EncounterEvent();
        foreach (var stat in actStats.Values)
            EncounterTooltipHelper.Accumulate(combined, stat);

        string statsText = combined.Fought == 0
            ? EncounterTooltipHelper.NoDataText(characterLabel, filter.AscensionMin, filter.AscensionMax)
            : EncounterTooltipHelper.BuildEncounterStatsTextSingleRow(
                combined, poolBaseline, dmgBaseline,
                effectiveChar, categoryLabel, filter);

        var encounterName = TruncateEncounterNameIfTooLong(EncounterCategory.FormatName(encounterId));

        // Reapply the locale-aware fonts + size every show. The cached
        // label otherwise holds whatever it got at panel-build time, so a
        // mid-session locale switch (e.g. eng → rus) leaves the Latin
        // Kreon FontVariation in place even though TooltipHelper's
        // GetKreonFont() has rebuilt against the locale substitute, and
        // similarly the size stays at 20pt instead of dropping to 17pt.
        // Mirrors TooltipHelper.ApplyTableStyle / ApplyHeaderLabelStyles
        // running per-show on the shared card/relic panel.
        var tableFont = TooltipHelper.GetKreonFont();
        var tableBold = TooltipHelper.GetKreonBoldFont();
        if (tableFont != null) handle.TableLabel.AddThemeFontOverride("normal_font", tableFont);
        if (tableBold != null) handle.TableLabel.AddThemeFontOverride("bold_font", tableBold);
        int tableFontSize = TooltipHelper.TableFontSize;
        handle.TableLabel.AddThemeFontSizeOverride("normal_font_size", tableFontSize);
        handle.TableLabel.AddThemeFontSizeOverride("bold_font_size", tableFontSize);
        if (tableBold != null) handle.NameLabel.AddThemeFontOverride("bold_font", tableBold);
        if (tableFont != null) handle.NameLabel.AddThemeFontOverride("normal_font", tableFont);
        if (tableFont != null) handle.BrandLabel.AddThemeFontOverride("font", tableFont);

        handle.NameLabel.Text = $"[b]{encounterName}[/b]";
        handle.TableLabel.Text = statsText;
        handle.NameLabel.ResetSize();
        handle.TableLabel.ResetSize();
        handle.Panel.ResetSize();
        return true;
    }

    /// <summary>
    /// Encounter names that exceed this many characters are truncated with an ellipsis
    /// so they don't push the SlayTheStats brand off the right edge of the in-combat
    /// tooltip. Vanilla names should all fit (the longest currently known are
    /// "Lagavulin Matriarch" and "Overgrowth Crawlers" at 19 chars); the cap exists for
    /// modded encounters with absurd labels.
    /// </summary>
    private const int MaxEncounterNameChars = 22;

    private static string TruncateEncounterNameIfTooLong(string name)
    {
        if (name.Length <= MaxEncounterNameChars) return name;
        return name.Substring(0, MaxEncounterNameChars - 1) + "…";
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
        // the screen. The stone-texture stylebox has asymmetric content margins (left=22,
        // right=0) AND asymmetric texture margins (left=55, right=91) — the two pull in
        // opposite directions, so the net visual content center is offset from the bbox
        // center by less than the full content-margin asymmetry. Empirically -6 px lines
        // up with the underlying hover-tip stack; -11 (full content-margin asymmetry) was
        // too much and shifted the panel slightly left.
        const float ContentMarginShiftX = 6f;
        var panelSize    = _panel.Size;
        var viewportSize = _panel.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        float x = anchor.X + anchorSize.X * 0.5f - panelSize.X * 0.5f - ContentMarginShiftX;
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

    /// <summary>Bag of references for one encounter tooltip instance. Used by both the
    /// main hover-driven tooltip and the debug test panels (which are always-visible
    /// fixed-position copies showing canned encounter IDs to verify layout).</summary>
    internal sealed class TooltipPanelHandle
    {
        public required PanelContainer Panel;
        public required RichTextLabel  NameLabel;
        public required Label          BrandLabel;
        public required RichTextLabel  TableLabel;
        public required NinePatchRect  Shadow;
    }

    private static void EnsurePanel()
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
            return;

        var handle = BuildTooltipPanelHandle("SlayTheStatsEncounterTooltip");
        _panel           = handle.Panel;
        _panelLabel      = handle.TableLabel;
        _panelNameLabel  = handle.NameLabel;
        _panelBrandLabel = handle.BrandLabel;
        _shadow          = handle.Shadow;
    }

    /// <summary>
    /// Builds a fresh, unparented tooltip panel + shadow with all the labels themed
    /// the same way the main hover tooltip uses. Caller is responsible for adding the
    /// panel and shadow to the scene tree, populating the labels via PopulatePanelData,
    /// and positioning them.
    /// </summary>
    private static TooltipPanelHandle BuildTooltipPanelHandle(string panelName)
    {
        // Make sure the shared TooltipHelper assets (panel stylebox, fonts) are initialized.
        TooltipHelper.TrySceneTheftOnce();

        var panel = new PanelContainer();
        panel.Name      = panelName;
        panel.Visible   = false;
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        // After v0.3.0 trim (4 cols: Dmg|Var|Turns|Deaths), the in-combat tooltip fits
        // within the standard hover-tip width — no longer needs the wider EncounterTooltipWidth.
        panel.CustomMinimumSize = new Vector2(TooltipHelper.TooltipWidth, 0);
        panel.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f);
        panel.ZIndex = 200;

        // Inner VBox: header HBox (encounter name | SlayTheStats brand) + table label.
        // Splitting into two control levels lets the brand sit on the same line as the
        // encounter name without literal-spaces alignment hacks.
        var vbox = new VBoxContainer();
        vbox.Name = "EncounterStatsVBox";
        vbox.AddThemeConstantOverride("separation", 4);
        vbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        // Force the vbox to fill the panel's content area horizontally so the header
        // HBox's spacer can push the brand to the right edge.
        vbox.CustomMinimumSize = new Vector2(TooltipHelper.TooltipWidth - 22f, 0);
        panel.AddChild(vbox);

        // Header is a plain Control (not an HBox) so we can anchor the encounter name
        // to the top-left and the brand label to the top-right independently. HBox +
        // ExpandFill didn't reliably push the brand flush right because RichTextLabel
        // with FitContent reports its content size as its minimum, and the leftover
        // space ended up distributed unpredictably.
        const int HeaderHeightPx = 28;
        // Independent of TooltipHelper's 12px pad for card/relic: the encounter
        // panel's stone chrome has less effective right inset, so 12 visually
        // crowds the brand against the frame. Bumped to match the event/post-fight
        // tooltips which use the same value.
        const int BrandRightPadPx = 24;
        var headerRow = new Control();
        headerRow.Name = "HeaderRow";
        headerRow.CustomMinimumSize = new Vector2(0, HeaderHeightPx);
        headerRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.MouseFilter = Control.MouseFilterEnum.Ignore;
        vbox.AddChild(headerRow);

        var kreonBold = TooltipHelper.GetKreonBoldFont();
        var kreonRegular = TooltipHelper.GetKreonFont();

        var nameLabel = new RichTextLabel();
        nameLabel.Name                = "EncounterNameLabel";
        nameLabel.BbcodeEnabled       = true;
        nameLabel.FitContent          = true;
        nameLabel.AutowrapMode        = TextServer.AutowrapMode.Off;
        nameLabel.ClipContents        = false;
        nameLabel.ScrollActive        = false;
        nameLabel.SelectionEnabled    = false;
        nameLabel.ShortcutKeysEnabled = false;
        nameLabel.MouseFilter         = Control.MouseFilterEnum.Ignore;
        // Anchor top-left; FitContent grows the rect to fit text.
        nameLabel.AnchorLeft   = 0f;
        nameLabel.AnchorTop    = 0f;
        nameLabel.AnchorRight  = 0f;
        nameLabel.AnchorBottom = 1f;
        nameLabel.OffsetLeft   = 0;
        nameLabel.OffsetTop    = 0;
        nameLabel.OffsetBottom = 0;
        // Kreon font (bold) + classic header gold so the encounter name matches the
        // bestiary stats title and the compendium tooltip headers. Using BBCode tags
        // alone is not enough — RichTextLabel needs the bold font themed in or [b]
        // tags fall back to a synthetic bold of the default font.
        if (kreonBold != null) nameLabel.AddThemeFontOverride("bold_font", kreonBold);
        if (kreonRegular != null) nameLabel.AddThemeFontOverride("normal_font", kreonRegular);
        nameLabel.AddThemeFontSizeOverride("normal_font_size", 20);
        nameLabel.AddThemeFontSizeOverride("bold_font_size", 20);
        nameLabel.AddThemeColorOverride("default_color", ThemeStyle.GoldColor); // #efc851
        TooltipHelper.ApplyTooltipShadow(nameLabel);
        headerRow.AddChild(nameLabel);

        var brandLabel = new Label();
        brandLabel.Name = "SlayTheStatsBrand";
        brandLabel.Text = "SlayTheStats";
        brandLabel.AddThemeColorOverride("font_color", ThemeStyle.FooterGreyColor); // #686868
        brandLabel.AddThemeFontSizeOverride("font_size", ThemeStyle.BrandSize);
        brandLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        if (kreonRegular != null) brandLabel.AddThemeFontOverride("font", kreonRegular);
        TooltipHelper.ApplyTooltipShadow(brandLabel);
        // Anchor to top-right so the brand's right edge always sits BrandRightPadPx
        // inside the header's right edge, regardless of the encounter name length.
        // VerticalAlignment.Top (not Center) nudges the brand up so its top margin
        // from the panel roughly matches the right margin — visual symmetry with the
        // stone frame corner.
        brandLabel.AnchorLeft   = 1f;
        brandLabel.AnchorRight  = 1f;
        brandLabel.AnchorTop    = 0f;
        brandLabel.AnchorBottom = 0f;
        brandLabel.GrowHorizontal = Control.GrowDirection.Begin;
        brandLabel.OffsetRight  = -BrandRightPadPx;
        brandLabel.OffsetTop    = 0;
        brandLabel.VerticalAlignment = VerticalAlignment.Top;
        headerRow.AddChild(brandLabel);

        var label = new RichTextLabel();
        label.Name                = "EncounterStatsLabel";
        label.BbcodeEnabled       = true;
        label.FitContent          = true;
        // WordSmart wrap so the filter-context line (which can get long
        // with all four segments) wraps inside the panel instead of
        // overflowing the right border. The [table=N] stat rows above are
        // sized by the panel width and never wrap. The CustomMinimumSize
        // below pins the wrap width so Godot's FitContent+WordSmart doesn't
        // need to guess on the first frame (which used to cause a one-frame
        // height glitch that dropped the tooltip below the hover-tip stack).
        label.AutowrapMode        = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize   = new Vector2(TooltipHelper.TooltipWidth - 22f, 0);
        label.ScrollActive        = false;
        label.SelectionEnabled    = false;
        label.ShortcutKeysEnabled = false;
        label.MouseFilter         = Control.MouseFilterEnum.Ignore;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var tableFont = TooltipHelper.GetKreonFont();
        if (tableFont != null)
        {
            label.AddThemeFontOverride("normal_font", tableFont);
            var tableBold = TooltipHelper.GetKreonBoldFont();
            if (tableBold != null) label.AddThemeFontOverride("bold_font", tableBold);
        }
        // Default size at panel-build; PopulatePanelData reapplies per-show
        // so the cached label tracks locale changes mid-session.
        int tableFontSize = TooltipHelper.TableFontSize;
        label.AddThemeFontSizeOverride("normal_font_size", tableFontSize);
        label.AddThemeFontSizeOverride("bold_font_size", tableFontSize);
        label.AddThemeColorOverride("default_color", ThemeStyle.CreamColor);
        label.AddThemeConstantOverride("line_separation", 0);
        TooltipHelper.ApplyTooltipShadow(label);
        vbox.AddChild(label);

        // Steal the same stone-tile stylebox the shared TooltipHelper builds for cards/relics.
        var style = TooltipHelper.BuildPanelStyle();
        if (style != null)
            panel.AddThemeStyleboxOverride("panel", style);

        var shadow = new NinePatchRect();
        shadow.Name              = panelName + "Shadow";
        shadow.Texture           = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png");
        shadow.PatchMarginLeft   = 55;
        shadow.PatchMarginRight  = 91;
        shadow.PatchMarginTop    = 43;
        shadow.PatchMarginBottom = 32;
        shadow.Modulate          = new Color(0f, 0f, 0f, 0.25098f);
        shadow.ZIndex            = 199;
        shadow.Visible           = false;
        shadow.MouseFilter       = Control.MouseFilterEnum.Ignore;

        return new TooltipPanelHandle
        {
            Panel      = panel,
            NameLabel  = nameLabel,
            BrandLabel = brandLabel,
            TableLabel = label,
            Shadow     = shadow,
        };
    }

    private static AggregationFilter BuildFilter(string? character)
    {
        // In-combat tooltip uses the user's persisted DEFAULT filters, not
        // the live bestiary pane state — matches how the in-run card / relic
        // tooltips work (CardHoverShowPatch.BuildInRunFilter). Tweaking the
        // bestiary filter pane mid-run shouldn't shift what the combat
        // tooltip shows for the enemy you're hovering.
        var filter = SlayTheStatsConfig.BuildSafeFilterFromDefaults();
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
