using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events;

namespace SlayTheStats;

// ─────────────────────────────────────────────────────────────────────
// In-run event option tooltip (own panel, not the shared TooltipHelper).
//
// Why own panel: the shared TooltipHelper panel is designed for surfaces
// that emit an NHoverTipSet (cards, relics, merchant items). Event option
// buttons go through rapid Focus/Unfocus cycles that thrash
// HasActiveHover, which stops SlayTheStatsPositionFollower from ticking;
// the shared RichTextLabel also has FitContent=true with no SizeFlagsExpandFill,
// so BBCode [table=N] content collapses to 1px wide. We fought both for
// many rounds without a clean fix. EncounterStatsHover took the same
// decision earlier — roll its own panel — and that's the proven pattern
// in this codebase.
//
// Render: event name header + [table=2] (Picks | Win%) for the hovered
// option. Baseline line + filter footer.
// ─────────────────────────────────────────────────────────────────────

internal static class EventHoverHelper
{
    private static PanelContainer? _panel;
    private static RichTextLabel?  _panelLabel;       // table body
    private static RichTextLabel?  _panelNameLabel;   // event name header
    private static Label?          _panelBrandLabel;  // SlayTheStats brand
    private static NinePatchRect?  _shadow;
    private static NEventOptionButton? _activeBtn;

    private const float PanelWidth = TooltipHelper.TooltipWidth;

    /// <summary>Horizontal gap between the option button and our panel.</summary>
    private const float HorizontalGapPx = 12f;

    /// <summary>Anchor point captured on Show — position is snapshotted so the
    /// panel doesn't drift as the hovered button scales during its hover-animation
    /// tween. Set once, held until the next Show/Hide cycle.</summary>
    private static Vector2 _anchorGlobal;
    private static Vector2 _anchorSize;

    /// <summary>Generation counter for debouncing Hide. NEventOptionButton's
    /// OnFocus/OnUnfocus cycle fires rapidly during the button's hover-scale
    /// tween; without a debounce the panel flickers on/off every frame and
    /// never stays visible long enough to read. Show increments this counter
    /// (cancelling any pending Hide); Hide schedules a 50ms timer that only
    /// runs if the counter hasn't moved — i.e. no new Show has cancelled it.
    /// Mirrors <see cref="TooltipHelper.HideWithDelay"/>.</summary>
    private static int _hideGen;

    internal static void Show(NEventOptionButton? holder)
    {
        if (!SlayTheStatsConfig.ShowInRunStats)        { MainFile.Logger.Info("[SlayTheStats] EventHover.Show bail: ShowInRunStats=false");        return; }
        if (SlayTheStatsConfig.DisableTooltipsEntirely) { MainFile.Logger.Info("[SlayTheStats] EventHover.Show bail: DisableTooltipsEntirely=true"); return; }
        if (holder == null)                             { MainFile.Logger.Info("[SlayTheStats] EventHover.Show bail: holder=null");                 return; }

        try
        {
            var eventModel = holder.Event;
            if (eventModel == null)
            {
                MainFile.Logger.Info("[SlayTheStats] EventHover.Show bail: holder.Event=null");
                return;
            }
            var eventModelType = eventModel.GetType().Name;
            if (eventModel is AncientEventModel)
            {
                MainFile.Logger.Info($"[SlayTheStats] EventHover.Show bail: AncientEventModel ({eventModelType})");
                return;
            }
            if (holder.Option?.IsProceed == true)
            {
                MainFile.Logger.Info($"[SlayTheStats] EventHover.Show bail: Option.IsProceed=true ({eventModelType})");
                return;
            }

            var eventId = eventModel.Id.ToString();
            MainFile.Logger.Info($"[SlayTheStats] EventHover.Show: eventId='{eventId}' type={eventModelType} hasVisits={MainFile.Db.EventVisits.ContainsKey(eventId)}");
            if (AncientEvents.IsAncient(eventId))
            {
                MainFile.Logger.Info($"[SlayTheStats] EventHover.Show bail: AncientEvents.IsAncient('{eventId}')");
                return;
            }

            // Build filter upfront so the "no data" path can still render a
            // tooltip with the current filter context in the footer.
            // AggregateEvent is safe to call when the event id isn't in the
            // db — it returns an empty aggregate (TotalVisits=0).
            var filter = CardHoverShowPatch.BuildInRunFilter(CardHoverShowPatch.RunCharacter);
            var agg = StatsAggregator.AggregateEvent(MainFile.Db, eventId, filter);
            var hasData = agg.TotalVisits > 0;

            var hoveredTerminal = TerminalOptionOf(holder.Option);
            // Shape2 buckets are keyed by terminal + BucketVariable value.
            // Read the value off the option's Title LocString (same API the
            // game uses to populate it, same representation that flows into
            // the run file's event_choices[].variables), so our hover
            // lookup matches the parsed bucket keys.
            var hoveredVariables = ReadOptionVariables(holder.Option);
            var hoveredOptionKey = agg.BuildBucketKey(hoveredTerminal, hoveredVariables);

            var effectiveChar  = CardHoverShowPatch.GetEffectiveCharacter(filter);
            var characterLabel = CardHoverShowPatch.GetCharacterLabel(filter);
            var wrBaseline = effectiveChar != null
                ? StatsAggregator.GetCharacterWR(MainFile.Db, effectiveChar, filter: filter)
                : StatsAggregator.GetGlobalWR(MainFile.Db, filter: filter);

            EnsurePanel();
            if (_panel == null || _panelLabel == null || _panelNameLabel == null) return;

            // Cancel any pending Hide timer — Show reasserts that the tooltip
            // should be visible. Without this the OnFocus/OnUnfocus ping-pong
            // during the button's hover-scale tween would race with the 50ms
            // Hide timer and flicker the panel.
            _hideGen++;

            _panelNameLabel.Text = $"[b]Event Stats[/b]";
            _panelLabel.Text     = hasData
                ? BuildStatsText(eventId, agg, hoveredOptionKey, characterLabel, wrBaseline, filter)
                : CardHoverShowPatch.NoDataText(filter);
            _panelNameLabel.ResetSize();
            _panelLabel.ResetSize();
            _panel.ResetSize();

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

            _activeBtn = holder;

            // Snapshot anchor position / size BEFORE the button's hover tween
            // starts modifying scale. Subsequent frames don't re-read these, so
            // the panel stays put even while the button scales up/down.
            _anchorGlobal = holder.GlobalPosition;
            _anchorSize   = holder.Size;

            // Hide for one frame to let the WordSmart-wrapped label finalise
            // its size (same pattern EncounterStatsHover uses).
            _panel.Visible = false;
            if (_shadow != null && GodotObject.IsInstanceValid(_shadow)) _shadow.Visible = false;
            Callable.From(DeferredShowAndPosition).CallDeferred();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] EventHover.Show: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void DeferredShowAndPosition()
    {
        // Diag: trace why the event tooltip never appears even though Show
        // succeeds (hasVisits=True logged). Strip once root cause is found.
        if (_panel == null)        { MainFile.Logger.Info("[SlayTheStats] EventHover.DeferredShow bail: _panel=null"); return; }
        if (_activeBtn == null)    { MainFile.Logger.Info("[SlayTheStats] EventHover.DeferredShow bail: _activeBtn=null (Hide fired between Show and deferred)"); return; }
        if (!GodotObject.IsInstanceValid(_activeBtn))
                                   { MainFile.Logger.Info("[SlayTheStats] EventHover.DeferredShow bail: _activeBtn invalid"); return; }
        _panel.Visible = true;
        if (_shadow != null && GodotObject.IsInstanceValid(_shadow)) _shadow.Visible = true;
        UpdatePosition();
        MainFile.Logger.Info($"[SlayTheStats] EventHover.DeferredShow OK: panelPos={_panel.GlobalPosition} panelSize={_panel.Size} visible={_panel.Visible}");
    }

    internal static void Hide(NEventOptionButton? holder)
    {
        if (holder == null || _activeBtn != holder)
        {
            MainFile.Logger.Info($"[SlayTheStats] EventHover.Hide noop: holderNull={holder == null} matched={_activeBtn == holder}");
            return;
        }
        // Debounce: NEventOptionButton.OnFocus/OnUnfocus cycles rapidly during
        // the hover-scale tween (see log pattern Show→Hide→Show→Hide every
        // frame). Schedule the real hide on a 50ms timer; if Show fires
        // again before the timer expires, it increments _hideGen and the
        // timer callback becomes a no-op.
        var genAtHide = ++_hideGen;
        MainFile.Logger.Info($"[SlayTheStats] EventHover.Hide: scheduled (gen={genAtHide})");
        var timer = (Engine.GetMainLoop() as SceneTree)?.CreateTimer(0.05f);
        if (timer == null) { DoHide(genAtHide, "no-timer"); return; }
        timer.Timeout += () => DoHide(genAtHide, "timer");
    }

    private static void DoHide(int genAtHide, string source)
    {
        if (_hideGen != genAtHide)
        {
            MainFile.Logger.Info($"[SlayTheStats] EventHover.DoHide skipped ({source}): newer gen (expected={genAtHide}, current={_hideGen})");
            return;
        }
        MainFile.Logger.Info($"[SlayTheStats] EventHover.DoHide firing ({source}, gen={genAtHide})");
        _activeBtn = null;
        if (_panel != null && GodotObject.IsInstanceValid(_panel)) _panel.Visible = false;
        if (_shadow != null && GodotObject.IsInstanceValid(_shadow)) _shadow.Visible = false;
    }

    /// <summary>Positions the panel vertically above the hovered option
    /// button so it doesn't overlap the card-reference hover tip the game
    /// renders at the same y as the button. Called once from
    /// <see cref="DeferredShowAndPosition"/> using the snapshotted anchor —
    /// no per-frame updates so the hover-animation scale tween on the
    /// button doesn't cause the tooltip to drift.</summary>
    private static void UpdatePosition()
    {
        if (_panel == null || !_panel.Visible) return;

        var viewportSize = _panel.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        var panelSize    = _panel.Size;

        // Anchor rect: use the snapshot so button scaling during hover doesn't move us.
        // If the snapshot size is zero (unexpected), use a small fallback so x/y math
        // still produces a visible position.
        float btnW = _anchorSize.X > 1f ? _anchorSize.X : 280f;
        float btnH = _anchorSize.Y > 1f ? _anchorSize.Y : 80f;

        // Horizontal: center on the button. Clamp to viewport.
        float x = _anchorGlobal.X + btnW * 0.5f - panelSize.X * 0.5f;
        x = Math.Clamp(x, 0f, Math.Max(0f, viewportSize.X - panelSize.X));

        // Vertical: above the button. Flip below if there's not enough room up top.
        float y = _anchorGlobal.Y - panelSize.Y - HorizontalGapPx;
        if (y < 0f) y = _anchorGlobal.Y + btnH + HorizontalGapPx;
        y = Math.Clamp(y, 0f, Math.Max(0f, viewportSize.Y - panelSize.Y));

        _panel.GlobalPosition = new Vector2(x, y);

        if (_shadow != null && GodotObject.IsInstanceValid(_shadow))
        {
            _shadow.GlobalPosition = _panel.GlobalPosition + new Vector2(TooltipHelper.ShadowOffset, TooltipHelper.ShadowOffset);
            _shadow.Size           = _panel.Size;
        }
    }

    private static void EnsurePanel()
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel)) return;
        BuildPanel();
    }

    /// <summary>
    /// Builds the event tooltip panel. Structure mirrors
    /// <see cref="EncounterStatsTooltipPatch"/>'s <c>BuildTooltipPanelHandle</c>:
    /// PanelContainer with stolen stylebox → VBox → HeaderRow (event name +
    /// brand) + StatsLabel. The key detail that the shared TooltipHelper panel
    /// lacks is <c>SizeFlagsHorizontal = ExpandFill</c> on the table label —
    /// without it, BBCode [table=N] content collapses to 1px wide.
    /// </summary>
    private static void BuildPanel()
    {
        TooltipHelper.TrySceneTheftOnce();

        var panel = new PanelContainer
        {
            Name        = "SlayTheStatsEventTooltip",
            Visible     = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(PanelWidth, 0),
            SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f),
            ZIndex = 200,
        };

        var vbox = new VBoxContainer { Name = "EventStatsVBox", MouseFilter = Control.MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 4);
        vbox.CustomMinimumSize = new Vector2(PanelWidth - 22f, 0);
        panel.AddChild(vbox);

        // Header row: event name on the left, SlayTheStats brand on the right.
        const int HeaderHeightPx = 28;
        const int BrandRightPadPx = 24;
        var headerRow = new Control
        {
            Name                = "HeaderRow",
            CustomMinimumSize   = new Vector2(0, HeaderHeightPx),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter         = Control.MouseFilterEnum.Ignore,
        };
        vbox.AddChild(headerRow);

        var kreonBold    = TooltipHelper.GetKreonBoldFont();
        var kreonRegular = TooltipHelper.GetKreonFont();

        var nameLabel = new RichTextLabel
        {
            Name                = "EventNameLabel",
            BbcodeEnabled       = true,
            FitContent          = true,
            AutowrapMode        = TextServer.AutowrapMode.Off,
            ClipContents        = false,
            ScrollActive        = false,
            SelectionEnabled    = false,
            ShortcutKeysEnabled = false,
            MouseFilter         = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0f, AnchorTop = 0f, AnchorRight = 0f, AnchorBottom = 1f,
            OffsetLeft = 0, OffsetTop = 0, OffsetBottom = 0,
        };
        if (kreonBold    != null) nameLabel.AddThemeFontOverride("bold_font",   kreonBold);
        if (kreonRegular != null) nameLabel.AddThemeFontOverride("normal_font", kreonRegular);
        nameLabel.AddThemeFontSizeOverride("normal_font_size", 20);
        nameLabel.AddThemeFontSizeOverride("bold_font_size", 20);
        nameLabel.AddThemeColorOverride("default_color", ThemeStyle.GoldColor); // gold
        TooltipHelper.ApplyTooltipShadow(nameLabel);
        headerRow.AddChild(nameLabel);

        var brandLabel = new Label
        {
            Name                = "SlayTheStatsBrand",
            Text                = "SlayTheStats",
            MouseFilter         = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 0f,
            GrowHorizontal      = Control.GrowDirection.Begin,
            OffsetRight         = -BrandRightPadPx,
            OffsetTop           = 0,
            VerticalAlignment   = VerticalAlignment.Top,
        };
        brandLabel.AddThemeColorOverride("font_color", ThemeStyle.FooterGreyColor);
        brandLabel.AddThemeFontSizeOverride("font_size", ThemeStyle.BrandSize);
        if (kreonRegular != null) brandLabel.AddThemeFontOverride("font", kreonRegular);
        TooltipHelper.ApplyTooltipShadow(brandLabel);
        headerRow.AddChild(brandLabel);

        var label = new RichTextLabel
        {
            Name                = "EventStatsLabel",
            BbcodeEnabled       = true,
            FitContent          = true,
            AutowrapMode        = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize   = new Vector2(PanelWidth - 22f, 0),
            ScrollActive        = false,
            SelectionEnabled    = false,
            ShortcutKeysEnabled = false,
            MouseFilter         = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        if (kreonRegular != null) label.AddThemeFontOverride("normal_font", kreonRegular);
        if (kreonBold    != null) label.AddThemeFontOverride("bold_font",   kreonBold);
        label.AddThemeFontSizeOverride("normal_font_size", 18);
        label.AddThemeFontSizeOverride("bold_font_size", 18);
        label.AddThemeColorOverride("default_color", ThemeStyle.CreamColor);
        label.AddThemeConstantOverride("line_separation", 0);
        TooltipHelper.ApplyTooltipShadow(label);
        vbox.AddChild(label);

        var style = TooltipHelper.BuildPanelStyle();
        if (style != null)
            panel.AddThemeStyleboxOverride("panel", style);

        var shadow = new NinePatchRect
        {
            Name              = "SlayTheStatsEventTooltipShadow",
            Texture           = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png"),
            PatchMarginLeft   = 55,
            PatchMarginRight  = 91,
            PatchMarginTop    = 43,
            PatchMarginBottom = 32,
            Modulate          = new Color(0f, 0f, 0f, 0.25098f),
            ZIndex            = 199,
            Visible           = false,
            MouseFilter       = Control.MouseFilterEnum.Ignore,
        };

        _panel           = panel;
        _panelLabel      = label;
        _panelNameLabel  = nameLabel;
        _panelBrandLabel = brandLabel;
        _shadow          = shadow;
    }

    // ── Table rendering ──────────────────────────────────────────────

    private static string TerminalOptionOf(EventOption? option)
    {
        var title = option?.Title;
        if (title == null) return "";
        var key = title.LocEntryKey;
        if (string.IsNullOrEmpty(key)) return "";
        var terminal = EventIdHelpers.TerminalOption(new List<string> { key });
        // Locked variants (e.g. BARGAIN_BIN_LOCKED) are the same logical
        // choice as their unlocked counterpart; strip the suffix so the
        // hover lookup hits the unlocked option's stored bucket. Locked
        // options have a null action so they're never picked — the
        // database only ever contains the unlocked key, and without this
        // mapping every locked-option hover reads 0/N.
        const string LockedSuffix = "_LOCKED";
        if (terminal.EndsWith(LockedSuffix, StringComparison.Ordinal))
            terminal = terminal[..^LockedSuffix.Length];
        return terminal;
    }

    /// <summary>
    /// Reads the LocString variables attached to a hovered option's Title
    /// into a plain string dict. Matches the shape the parser flattens
    /// visit.Variables into — so agg.BuildBucketKey produces identical
    /// bucket keys at hover vs. parse time.
    /// </summary>
    private static Dictionary<string, string> ReadOptionVariables(EventOption? option)
    {
        var dict = new Dictionary<string, string>();
        var title = option?.Title;
        if (title == null) return dict;
        try
        {
            foreach (var kv in title.Variables)
            {
                if (kv.Value == null) continue;
                var s = kv.Value.ToString();
                if (!string.IsNullOrEmpty(s)) dict[kv.Key] = s;
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] ReadOptionVariables failed: {e.Message}");
        }
        return dict;
    }

    /// <summary>
    /// Renders the tooltip body. One row for the hovered option. Columns
    /// and the Picks format depend on <see cref="EventAggregate.Shape"/>:
    /// <list type="bullet">
    ///   <item>Shape1 / Unknown: Picks shown as "N/M" (N=picks for this
    ///         option, M=TotalVisits — same model as shop pick%).</item>
    ///   <item>Shape2: Picks shown as raw "N" with no denominator, and a
    ///         footer note explains why. The game records what was picked
    ///         per visit, never what was offered, so any denominator would
    ///         conflate "was offered" with "chose given offered" — see
    ///         slay-the-stats.md §v1.0.0 event shape classification.</item>
    /// </list>
    /// </summary>
    private static string BuildStatsText(
        string eventId,
        EventAggregate agg,
        string hoveredOptionKey,
        string characterLabel,
        double wrBaseline,
        AggregationFilter filter)
    {
        var sb = new StringBuilder();

        sb.Append("[table=2]");
        sb.Append(TooltipHelper.HdrCell("Picks",  TooltipHelper.ColPadOuter));
        sb.Append(TooltipHelper.HdrCell("Win%",   TooltipHelper.ColPadLast));

        agg.Options.TryGetValue(hoveredOptionKey, out var opt);

        int picks = opt?.Picks ?? 0;
        int total = agg.TotalVisits;
        bool isShape2 = agg.Shape == EventShape.Shape2_VariableKey;

        string picksCell;
        if (isShape2)
        {
            // No denominator: offered counts are unknowable for Shape2 events.
            picksCell = picks > 0
                ? $"{picks}"
                : $"[color={ThemeStyle.NeutralShade}]0[/color]";
        }
        else
        {
            picksCell = picks > 0
                ? $"{picks}/{total}"
                : $"[color={ThemeStyle.NeutralShade}]0/{total}[/color]";
        }

        string winCell = picks > 0
            ? $"{Math.Round(100.0 * opt!.Wins / picks):F0}%"
            : $"[color={ThemeStyle.NeutralShade}]-[/color]";

        sb.Append(TooltipHelper.DataCell(picksCell, TooltipHelper.ColPadOuter));
        sb.Append(TooltipHelper.DataCell(winCell,   TooltipHelper.ColPadLast));

        sb.Append("[/table]");

        if (isShape2)
        {
            // Non-comparative note, rendered in the same muted style as the
            // baseline/footer strip so it reads as metadata rather than data.
            sb.Append(TooltipHelper.FormatBaselineLine("Offered data not recorded for this event"));
        }
        else
        {
            var wrStr = double.IsNaN(wrBaseline) ? "—" : $"{Math.Round(wrBaseline):F0}%";
            sb.Append(TooltipHelper.FormatBaselineLine($"(baseline) Win% {wrStr}"));
        }

        var filterCtx = CardHoverShowPatch.BuildFilterContext(characterLabel, filter);
        sb.Append(TooltipHelper.FormatFooter(filterCtx));

        return sb.ToString();
    }

    /// <summary>
    /// Converts a SCREAMING_SNAKE segment to Title Case words.
    /// Numeric suffixes stay intact, so <c>HOLD_ON_1</c> → <c>Hold On 1</c>
    /// — that trailing number carries the depth distinction for recursive
    /// events and we want it visible.
    /// </summary>
    private static string TitleCase(string seg)
    {
        if (string.IsNullOrEmpty(seg)) return seg;
        var words = seg.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0) continue;
            // Digit-only words ("1", "2") stay as-is.
            if (char.IsDigit(w[0])) continue;
            words[i] = char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        }
        return string.Join(' ', words);
    }
}

[HarmonyPatch(typeof(NEventOptionButton), "OnFocus")]
public static class EventOptionFocusPatch
{
    static void Postfix(NEventOptionButton __instance) => EventHoverHelper.Show(__instance);
}

[HarmonyPatch(typeof(NEventOptionButton), "OnUnfocus")]
public static class EventOptionUnfocusPatch
{
    static void Postfix(NEventOptionButton __instance) => EventHoverHelper.Hide(__instance);
}

