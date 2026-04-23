using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;

namespace SlayTheStats;

/// <summary>
/// Injects a "SlayTheStats" button into the Card Library sidebar and the Relic Collection
/// screen, each opening a shared floating filter/aggregation pane.
/// </summary>
[HarmonyPatch(typeof(NCardLibrary), "_Ready")]
public static partial class CompendiumFilterPatch
{
    // ── Shared colours (game palette from StsColors) ──────────────────────
    private static readonly Color Cream       = new("FEF6E2");        // primary text
    private static readonly Color Gold        = new("EFC851");        // titles, highlights
    private static readonly Color MutedButton = new(0.85f, 0.75f, 0.55f, 1f);
    private static readonly Color HoverButton = new(1f, 0.9f, 0.7f, 1f);
    private static readonly Color ActiveFilterColor = new(0.4f, 0.85f, 0.5f, 1f);
    // Deeper steel-blue used to tint the SlayTheStats sidebar button so it
    // visually matches the filter pane background. Darker than the pane's
    // SelfModulate (0.60, 0.68, 0.88) because the button has no stone texture
    // underneath to brighten it.
    private static readonly Color SlayTheStatsButtonTint = new(0.32f, 0.42f, 0.78f, 1f);

    // Track active pane instances so they can be hidden from external patches.
    private static readonly List<PanelContainer> _activePanes = new();
    // Active sidebar/floating sort buttons we cloned, so action-button clicks
    // (Clear / Reset / Save Defaults) can refresh their "(not default)" suffix
    // immediately rather than waiting for pane VisibilityChanged.
    private static readonly List<NCardViewSortButton> _activeSortButtons = new();
    // Callbacks to sync pane controls with current config values.
    private static readonly List<Action> _syncCallbacks = new();
    // Cached sort-button template so the relic page can reuse it.
    private static NCardViewSortButton? _sortButtonTemplate;
    // Cached tickbox template (cloned from the card library's stat tickboxes
    // — e.g. "Multiplayer Cards") so we can use the game's native tickbox style
    // for our Group-Card-Upgrades checkbox.
    private static NLibraryStatTickbox? _tickboxTemplate;

    /// <summary>
    /// Hides all filter panes. Called when a card/relic is hovered to avoid
    /// the pane blocking inspect mode.
    /// </summary>
    internal static void HideAllPanes()
    {
        foreach (var pane in _activePanes)
        {
            if (GodotObject.IsInstanceValid(pane))
                pane.Visible = false;
        }
    }

    // ── Card Library patch ──────────────────────────────────────────────────

    static void Postfix(NCardLibrary __instance)
    {
        try { InjectCardLibraryButton(__instance); }
        catch (System.Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] CompendiumFilterPatch failed: {e.Message}");
        }
    }

    private static void InjectCardLibraryButton(NCardLibrary library)
    {
        var multiplayerCards = library.FindChild("MultiplayerCards", true, false);
        if (multiplayerCards == null)
        {
            MainFile.Logger.Warn("[SlayTheStats] Could not find MultiplayerCards node in card library");
            return;
        }
        var sidebar = multiplayerCards.GetParent();
        if (sidebar == null) return;

        // Clone an existing NCardViewSortButton to get the exact game style
        // (textures, fonts, HSV shader, tween animations, SFX).
        var template = library.FindChild("CardTypeSorter", true, false) as NCardViewSortButton;
        if (template != null)
            _sortButtonTemplate = template; // cache for the relic page

        // Cache one of the card library's stat tickboxes as a template for our
        // group-upgrades checkbox so it gets the game's native styling.
        if (multiplayerCards is NLibraryStatTickbox tickbox)
            _tickboxTemplate = tickbox;

        var pane = BuildFilterPane();
        library.AddChild(pane);
        RegisterPane(pane);

        if (template != null)
        {
            var button = CloneSortButton(template);
            sidebar.AddChild(button);
            WireSortButtonToPane(button, pane);
        }
        else
        {
            MainFile.Logger.Warn("[SlayTheStats] Could not find CardTypeSorter; using fallback button");
            var button = CreateFallbackSidebarButton();
            sidebar.AddChild(button);
            WireButtonToPane(button, pane);
        }

        if (SlayTheStatsConfig.DebugMode)
            MainFile.Logger.Info("[SlayTheStats] CompendiumFilterPatch: button injected into card library sidebar");
    }

    // ── Card Library OnSubmenuOpened — hide pane each time the page is shown ─
    [HarmonyPatch(typeof(NCardLibrary), "OnSubmenuOpened")]
    public static class CardLibraryOpenedPatch
    {
        static void Postfix(NCardLibrary __instance)
        {
            HideAllPanes();
            MaybeShowTutorialFor(__instance);
        }
    }

    // ── Relic Collection patch ──────────────────────────────────────────────

    [HarmonyPatch(typeof(NRelicCollection), "_Ready")]
    public static class RelicCollectionFilterPatch
    {
        static void Postfix(NRelicCollection __instance)
        {
            try { InjectRelicCollectionButton(__instance); }
            catch (System.Exception e)
            {
                MainFile.Logger.Warn($"[SlayTheStats] RelicCollectionFilterPatch failed: {e.Message}");
            }
        }
    }

    // ── Relic Collection OnSubmenuOpened — hide pane each time the page is shown ─
    [HarmonyPatch(typeof(NRelicCollection), "OnSubmenuOpened")]
    public static class RelicCollectionOpenedPatch
    {
        static void Postfix(NRelicCollection __instance)
        {
            HideAllPanes();
            // If the relic page was opened first (before the card library) at
            // session start, the button got injected as the unstyled fallback
            // because no NCardViewSortButton template was reachable yet. Now
            // that the user is interacting with the compendium, the card
            // library may have been instantiated — try once more to find a
            // template and upgrade the fallback to the styled clone.
            try { TryUpgradeRelicButton(__instance); }
            catch (Exception e) { MainFile.Logger.Warn($"[SlayTheStats] TryUpgradeRelicButton failed: {e.Message}"); }
            MaybeShowTutorialFor(__instance);
        }
    }

    // ── Relic inspect — hide pane when a relic is inspected ─
    [HarmonyPatch(typeof(NInspectRelicScreen), "Open")]
    public static class InspectRelicOpenPatch
    {
        static void Postfix() => HideAllPanes();
    }

    private static void InjectRelicCollectionButton(NRelicCollection collection)
    {
        // Warm BOTH the sort-button and tickbox template caches BEFORE building
        // the pane — otherwise BuildFilterPane sees a cold _tickboxTemplate and
        // falls back to the unstyled CheckBox even though we'd populate the
        // template moments later. ResolveSortButtonTemplate's strategy 2
        // (WarmTemplatesFromColdCardLibrary) populates both caches in one
        // Create() pass, so this single call warms everything we need.
        var template = ResolveSortButtonTemplate(collection);

        var pane = BuildFilterPane();
        collection.AddChild(pane);
        RegisterPane(pane);

        if (template != null)
        {
            CreateAndAttachStyledFilterButton(collection, pane, template);
            // Unconditional (not gated on DebugMode) so we can verify the relic
            // patch fired even when the user enables debug mode mid-session.
            MainFile.Logger.Info("[SlayTheStats] RelicCollectionFilterPatch: styled button injected on _Ready");
        }
        else
        {
            var button = CreateFloatingButton();
            collection.AddChild(button);
            WireButtonToPane(button, pane);
            MainFile.Logger.Info("[SlayTheStats] RelicCollectionFilterPatch: fallback button injected on _Ready (no template available yet — will retry on OnSubmenuOpened)");
        }
    }

    /// <summary>
    /// Looks up an NCardViewSortButton template — first via the cached field
    /// (warmed by the card library injector), then via a scene-tree walk in
    /// case the card library was instantiated but its _Ready hasn't run yet.
    /// Returns null when the card library hasn't been instantiated at all
    /// (typical when the user opens the Relic Collection page first).
    /// </summary>
    internal static NCardViewSortButton? ResolveSortButtonTemplate(Node fromNode)
    {
        if (_sortButtonTemplate != null && GodotObject.IsInstanceValid(_sortButtonTemplate))
            return _sortButtonTemplate;

        var found = FindSortButtonTemplateInTree(fromNode);
        if (found != null) _sortButtonTemplate = found;
        return found;
    }

    /// <summary>
    /// Builds a styled NCardViewSortButton clone, parents it to
    /// <paramref name="parent"/> at the bottom-left anchor, and wires it to
    /// <paramref name="pane"/>. Used by both the relic collection and bestiary
    /// pages so the filter button looks identical across all the compendium
    /// surfaces. Caller has already verified <paramref name="template"/> is
    /// valid via <see cref="ResolveSortButtonTemplate"/>.
    /// </summary>
    internal static NCardViewSortButton CreateAndAttachStyledFilterButton(Node parent, PanelContainer pane, NCardViewSortButton template)
    {
        var button = CloneSortButton(template);

        // Match the card button's size: read from the template, falling
        // through to defaults if the template's _Ready hasn't fired yet.
        var templateSize = template.Size;
        if (templateSize.X < 10) templateSize.X = template.CustomMinimumSize.X;
        if (templateSize.Y < 10) templateSize.Y = template.CustomMinimumSize.Y;
        if (templateSize.X < 10) templateSize.X = 180;
        if (templateSize.Y < 10) templateSize.Y = 50;
        button.CustomMinimumSize = templateSize;

        // Anchor to bottom-left, sized to match the card button.
        const float padX = 20f;
        const float padY = 20f;
        button.AnchorLeft = 0f;
        button.AnchorRight = 0f;
        button.AnchorTop = 1f;
        button.AnchorBottom = 1f;
        button.OffsetLeft = padX;
        button.OffsetRight = padX + templateSize.X;
        button.OffsetTop = -templateSize.Y - padY;
        button.OffsetBottom = -padY;

        parent.AddChild(button);
        WireSortButtonToPane(button, pane);
        return button;
    }

    /// <summary>
    /// Called from the relic collection's OnSubmenuOpened postfix. If the
    /// existing filter button is the unstyled fallback (a plain Godot Button
    /// rather than a NCardViewSortButton clone), and a template is now
    /// available, free the fallback and replace it with the styled clone
    /// wired to the same existing pane. No-op when the button is already
    /// styled or no template can be found.
    /// </summary>
    private static void TryUpgradeRelicButton(NRelicCollection collection)
    {
        var existing = collection.FindChild("SlayTheStatsFilterButton", false, false);
        if (existing == null) return;
        if (existing is NCardViewSortButton) return; // already styled, nothing to do

        var template = ResolveSortButtonTemplate(collection);
        if (template == null) return; // still no template — leave fallback in place

        // Find the existing filter pane on the same collection so we can
        // re-bind it to the new button.
        FilterPanelContainer? pane = null;
        foreach (var child in collection.GetChildren())
        {
            if (child is FilterPanelContainer fpc)
            {
                pane = fpc;
                break;
            }
        }
        if (pane == null)
        {
            MainFile.Logger.Warn("[SlayTheStats] TryUpgradeRelicButton: no existing pane found, skipping upgrade");
            return;
        }

        // Make sure the pane is hidden during the swap so a stale toggle
        // handler can't briefly show it at the wrong position.
        pane.Visible = false;

        // Free the fallback button.
        if (existing is Node existingNode)
        {
            existingNode.QueueFree();
        }

        CreateAndAttachStyledFilterButton(collection, pane, template);
        MainFile.Logger.Info("[SlayTheStats] TryUpgradeRelicButton: replaced fallback button with styled clone");
    }

    /// <summary>
    /// Locates an NCardViewSortButton template via three escalating strategies:
    ///
    ///   1. Walk the live scene tree from the SceneTree root for any existing
    ///      NCardViewSortButton (typically CardTypeSorter inside NCardLibrary).
    ///      Cheap and works once the user has opened the card library.
    ///
    ///   2. If the live tree has none (because NCardLibrary was never
    ///      instantiated this session), call NCardLibrary.Create() to
    ///      force-instantiate a temporary, cold NCardLibrary scene. The
    ///      temporary is never added to the live tree, so its _Ready never
    ///      fires (and our Harmony postfix doesn't double-inject). We extract
    ///      both the sort-button and tickbox templates in a single Create()
    ///      pass and cache them, then QueueFree the temporary.
    ///
    ///   3. Return null on total failure — caller falls back to the unstyled
    ///      Button.
    /// </summary>
    private static NCardViewSortButton? FindSortButtonTemplateInTree(Node from)
    {
        try
        {
            // Strategy 1: live scene tree walk.
            var root = from.GetTree()?.Root;
            if (root != null)
            {
                if (root.FindChild("CardTypeSorter", true, false) is NCardViewSortButton named)
                {
                    MainFile.Logger.Info("[SlayTheStats] FindSortButtonTemplateInTree: found CardTypeSorter in live tree");
                    return named;
                }

                var walked = FindFirstDescendantOfType<NCardViewSortButton>(root);
                if (walked != null)
                {
                    MainFile.Logger.Info("[SlayTheStats] FindSortButtonTemplateInTree: found NCardViewSortButton via type walk");
                    return walked;
                }
            }

            // Strategy 2: warm both templates from a cold NCardLibrary instance.
            WarmTemplatesFromColdCardLibrary();
            return _sortButtonTemplate;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] FindSortButtonTemplateInTree failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Calls NCardLibrary.Create() once to get a fresh, cold NCardLibrary scene
    /// instance (never added to the live tree, so _Ready never fires on it and
    /// our Harmony postfix on NCardLibrary._Ready stays silent). Extracts both
    /// templates we need from it in a single pass and caches them as parentless
    /// duplicates, then QueueFrees the temporary instance. Idempotent — early-
    /// returns if both caches are already warm. The duplicates are independent
    /// of the source instance (Godot's Duplicate creates a deep copy of the
    /// node graph; Resources are shared by reference but that's fine for a
    /// clone-template).
    /// </summary>
    private static void WarmTemplatesFromColdCardLibrary()
    {
        bool needSortButton = _sortButtonTemplate == null || !GodotObject.IsInstanceValid(_sortButtonTemplate);
        bool needTickbox    = _tickboxTemplate    == null || !GodotObject.IsInstanceValid(_tickboxTemplate);
        if (!needSortButton && !needTickbox) return;

        NCardLibrary? temp = null;
        try
        {
            temp = NCardLibrary.Create();
            if (temp == null)
            {
                MainFile.Logger.Warn("[SlayTheStats] WarmTemplatesFromColdCardLibrary: NCardLibrary.Create() returned null");
                return;
            }

            if (needSortButton)
            {
                if (temp.FindChild("CardTypeSorter", true, false) is NCardViewSortButton sortButton)
                {
                    _sortButtonTemplate = (NCardViewSortButton)sortButton.Duplicate(
                        (int)(Node.DuplicateFlags.Groups
                            | Node.DuplicateFlags.Scripts
                            | Node.DuplicateFlags.UseInstantiation));
                    MainFile.Logger.Info("[SlayTheStats] WarmTemplatesFromColdCardLibrary: extracted sort-button template");
                }
                else
                {
                    MainFile.Logger.Warn("[SlayTheStats] WarmTemplatesFromColdCardLibrary: CardTypeSorter not found in cold instance");
                }
            }

            if (needTickbox)
            {
                if (temp.FindChild("MultiplayerCards", true, false) is NLibraryStatTickbox tickbox)
                {
                    _tickboxTemplate = (NLibraryStatTickbox)tickbox.Duplicate(
                        (int)(Node.DuplicateFlags.Groups
                            | Node.DuplicateFlags.Scripts
                            | Node.DuplicateFlags.UseInstantiation));
                    MainFile.Logger.Info("[SlayTheStats] WarmTemplatesFromColdCardLibrary: extracted tickbox template");
                }
                else
                {
                    MainFile.Logger.Warn("[SlayTheStats] WarmTemplatesFromColdCardLibrary: MultiplayerCards not found in cold instance");
                }
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] WarmTemplatesFromColdCardLibrary failed: {e.Message}");
        }
        finally
        {
            if (temp != null && GodotObject.IsInstanceValid(temp))
                temp.QueueFree();
        }
    }

    private static T? FindFirstDescendantOfType<T>(Node node) where T : class
    {
        if (node is T match) return match;
        foreach (var child in node.GetChildren())
        {
            if (child is Node childNode)
            {
                var found = FindFirstDescendantOfType<T>(childNode);
                if (found != null) return found;
            }
        }
        return null;
    }

    internal static void RegisterPane(PanelContainer pane)
    {
        // Clean up any stale references.
        _activePanes.RemoveAll(p => !GodotObject.IsInstanceValid(p));
        _activePanes.Add(pane);
    }

    // ── Shared UI builders ──────────────────────────────────────────────────

    /// <summary>
    /// Duplicates an existing NCardViewSortButton to get the exact game style:
    /// textures, game fonts (Kreon), HSV shader material, tween animations, and SFX.
    /// </summary>
    private static NCardViewSortButton CloneSortButton(NCardViewSortButton template)
    {
        // Duplicate nodes + scripts but NOT signal connections — ConnectSignals()
        // in _Ready will wire them fresh on the clone.  Include UseInstantiation
        // so the duplicate is re-created from the original PackedScene.
        var clone = (NCardViewSortButton)template.Duplicate(
            (int)(Node.DuplicateFlags.Groups
                | Node.DuplicateFlags.Scripts
                | Node.DuplicateFlags.UseInstantiation));
        clone.Name = "SlayTheStatsFilterButton";

        // Godot copies the "already readied" flag from the template.
        // RequestReady() clears it so _Ready fires when the clone enters the tree,
        // which initialises _label, _button, _hsv, and all signal connections.
        clone.RequestReady();

        // Set label, make shader unique, and hide sort icon after _Ready has run.
        clone.Ready += () =>
        {
            clone.SetLabel("SlayTheStats");

            // The HSV ShaderMaterial on %ButtonImage is shared by reference with
            // the template. Duplicate it so our hover animations don't bleed into
            // the original CardTypeSorter button.
            var buttonImage = clone.FindChild("ButtonImage", true, false);
            ShaderMaterial? newMaterial = null;
            if (buttonImage is CanvasItem ci)
            {
                if (ci.Material is ShaderMaterial sm)
                {
                    newMaterial = (ShaderMaterial)sm.Duplicate();
                    ci.Material = newMaterial;
                }
                // Tint the button blue so it visibly differs from vanilla sort buttons.
                ci.SelfModulate = SlayTheStatsButtonTint;
            }

            // CRITICAL: NCardViewSortButton caches the material in a private `_hsv`
            // field during its own _Ready (before this callback runs). That cache
            // still points to the template's shared material, so the clone's own
            // hover/press tweens would mutate the TEMPLATE's material — causing
            // the wrong button (CardTypeSorter) to flash when the clone is
            // hovered. Repoint `_hsv` to the clone's unique material via
            // reflection so the clone animates itself instead.
            if (newMaterial != null)
            {
                try
                {
                    var hsvField = HarmonyLib.AccessTools.Field(typeof(NCardViewSortButton), "_hsv");
                    hsvField?.SetValue(clone, newMaterial);
                }
                catch (Exception e)
                {
                    MainFile.Logger.Warn($"[SlayTheStats] Failed to repoint _hsv: {e.Message}");
                }
            }

            // Hide the sort arrow — our button opens a pane, not a sorter.
            var sortIcon = clone.FindChild("Image", true, false);
            if (sortIcon is CanvasItem icon)
                icon.Visible = false;

            // Attach a "(not default)" suffix label — a separate sibling Label
            // overlaid at the bottom of the button. Uses a sibling (not a text
            // concat on the main label) so: (a) "SlayTheStats" stays yellow and
            // doesn't re-center when the suffix appears; (b) the suffix can have
            // its own smaller font + green color.
            AttachNotDefaultSuffix(clone);
        };

        return clone;
    }

    /// <summary>
    /// Duplicates a cached NLibraryStatTickbox to get the game-native checkbox
    /// style (image-based ticked/unticked sprites, hover tween, MegaLabel font).
    /// Same _hsv reflection fix as CloneSortButton — the cloned tickbox's
    /// private _hsv field still points at the template's shared material after
    /// _Ready, which we have to repoint to a unique duplicate so hover tweens
    /// don't bleed onto the template tickbox.
    /// </summary>
    private static NLibraryStatTickbox CloneTickbox(NLibraryStatTickbox template, string label, bool initialTicked)
    {
        var clone = (NLibraryStatTickbox)template.Duplicate(
            (int)(Node.DuplicateFlags.Groups
                | Node.DuplicateFlags.Scripts
                | Node.DuplicateFlags.UseInstantiation));
        clone.Name = "SlayTheStatsTickbox";
        clone.RequestReady();

        clone.Ready += () =>
        {
            clone.SetLabel(label);
            clone.IsTicked = initialTicked;

            // Duplicate the HSV ShaderMaterial so hover tweens don't bleed back
            // into the template tickbox in the card library sidebar.
            var buttonImage = clone.FindChild("ButtonImage", true, false);
            ShaderMaterial? newMaterial = null;
            if (buttonImage is CanvasItem ci && ci.Material is ShaderMaterial sm)
            {
                newMaterial = (ShaderMaterial)sm.Duplicate();
                ci.Material = newMaterial;
            }
            if (newMaterial != null)
            {
                try
                {
                    var hsvField = HarmonyLib.AccessTools.Field(typeof(NTickbox), "_hsv");
                    hsvField?.SetValue(clone, newMaterial);
                }
                catch (Exception e)
                {
                    MainFile.Logger.Warn($"[SlayTheStats] Failed to repoint tickbox _hsv: {e.Message}");
                }
            }


            // Default label color is white (matches how the filter pane's
            // highlightGroupUpgrades resets to Colors.White when at default).
            // Caller can override via a post-clone highlight callback.
            var lblForColor = clone.GetNodeOrNull<Control>("Label") ?? clone.FindChild("Label", true, false) as Control;
            if (lblForColor != null)
            {
                lblForColor.AddThemeColorOverride("font_color", Colors.White);
                lblForColor.Modulate = Colors.White;
            }
        };

        return clone;
    }

    /// <summary>
    /// Adds a small "(not default)" label as a direct child of the cloned sort
    /// button, dynamically positioned just to the right of the main "SlayTheStats"
    /// MegaLabel using the label's actual rect. Hidden by default; shown by
    /// UpdateSortButtonActiveState when HasActiveFilters() is true.
    ///
    /// Implementing this as a sibling positioned via the label's rect (instead of
    /// a child of the label, or appending text to the label) means the main label
    /// keeps its original yellow color and centered position — the suffix simply
    /// extends past the right side of the button when active.
    /// </summary>
    private static void AttachNotDefaultSuffix(NCardViewSortButton clone)
    {
        var suffix = new Label
        {
            Name = "SlayTheStatsNotDefaultSuffix",
            Text = "(non-default filters)",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipText = false,
        };
        suffix.AddThemeColorOverride("font_color", ActiveFilterColor);
        ApplyGameFont(suffix, 11);
        // Top-left anchors so we can drive the position with Position directly.
        suffix.AnchorLeft = 0f; suffix.AnchorTop = 0f;
        suffix.AnchorRight = 0f; suffix.AnchorBottom = 0f;
        clone.AddChild(suffix);

        // Recompute the suffix position from the main label's actual rect each
        // time it (re)layouts. The Resized signal fires when the MegaLabel
        // auto-sizes to fit "SlayTheStats" at the chosen font size.
        var mainLabel = clone.FindChild("Label", true, false) as Control;
        if (mainLabel == null) return;

        Action reposition = () =>
        {
            if (!GodotObject.IsInstanceValid(mainLabel) || !GodotObject.IsInstanceValid(suffix)) return;
            var labelPosInClone = mainLabel.GlobalPosition - clone.GlobalPosition;
            var suffMin = suffix.GetCombinedMinimumSize();
            // Vertically center on the main label, horizontally place 4px after
            // the label's right edge.
            suffix.Position = new Vector2(
                labelPosInClone.X + mainLabel.Size.X + 4,
                labelPosInClone.Y + (mainLabel.Size.Y - suffMin.Y) / 2);
        };

        mainLabel.Resized += () => reposition();
        clone.Resized += () => reposition();

        // Initial reposition after the layout has settled (the cloned button's
        // rect/size isn't valid until the SceneTree has run a frame).
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame += DeferredReposition;
        void DeferredReposition()
        {
            tree.ProcessFrame -= DeferredReposition;
            reposition();
        }
    }

    /// <summary>
    /// Wires a cloned NCardViewSortButton to toggle a filter pane.
    /// Uses the game's Released signal (NClickableControl) which fires after
    /// the built-in press/release animations and SFX.
    /// </summary>
    private static void WireSortButtonToPane(NCardViewSortButton button, PanelContainer pane)
    {
        _activeSortButtons.RemoveAll(b => !GodotObject.IsInstanceValid(b));
        _activeSortButtons.Add(button);

        if (pane is FilterPanelContainer fpc)
            fpc.AssociatedButton = button;

        ((NClickableControl)button).Released += (_) =>
        {
            if (GodotObject.IsInstanceValid(pane))
            {
                pane.Visible = !pane.Visible;
                if (pane.Visible)
                {
                    RepositionPaneNextToButton(button, pane);
                    SyncAllControls();
                }
            }
        };

        pane.VisibilityChanged += () =>
        {
            if (!GodotObject.IsInstanceValid(button)) return;
            UpdateSortButtonActiveState(button);
        };

        // Initial sync — the label text/color should reflect non-default state
        // even before the user opens the pane for the first time. Deferred so
        // SetLabel runs after the cloned button's _Ready (which is when its
        // inner MegaLabel reference is initialised).
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame += DeferredInitialSync;
        void DeferredInitialSync()
        {
            tree.ProcessFrame -= DeferredInitialSync;
            if (GodotObject.IsInstanceValid(button))
                UpdateSortButtonActiveState(button);
        }
    }

    /// <summary>
    /// Positions the filter pane just to the right of the sidebar button at toggle time
    /// using the button's actual global rect (not hard-coded offsets).  The pane's bottom
    /// is aligned with the button's bottom so it grows upward and stays on-screen.
    /// </summary>
    internal static void RepositionPaneNextToButton(Control button, PanelContainer pane)
    {
        if (!GodotObject.IsInstanceValid(button) || !GodotObject.IsInstanceValid(pane))
            return;

        const float padding = 24f;

        // Use top-left anchors so GlobalPosition controls the placement directly
        // and the pane is sized by its content (PanelContainer wraps its child).
        pane.AnchorLeft = 0f;
        pane.AnchorTop = 0f;
        pane.AnchorRight = 0f;
        pane.AnchorBottom = 0f;

        // Compute the pane's natural height from its content so we can align its
        // bottom with the button's bottom (the pane grows upward, not downward).
        var paneHeight = pane.GetCombinedMinimumSize().Y;
        var btnRight = button.GlobalPosition.X + button.Size.X;
        var btnBottom = button.GlobalPosition.Y + button.Size.Y;

        pane.GlobalPosition = new Vector2(btnRight + padding, btnBottom - paneHeight);
    }

    /// <summary>
    /// Shows/hides the "(not default)" suffix label attached to the cloned sort
    /// button. The main "SlayTheStats" label stays unchanged (same text, same
    /// yellow color, same centered position) so the button's visual identity
    /// doesn't shift when filters change.
    /// </summary>
    private static void UpdateSortButtonActiveState(NCardViewSortButton button)
    {
        bool active = HasActiveFilters();
        var suffix = button.FindChild("SlayTheStatsNotDefaultSuffix", false, false);
        if (suffix is Control suffixControl)
            suffixControl.Visible = active;
    }

    private static Button CreateFallbackSidebarButton()
    {
        var button = new Button();
        button.Text = "SlayTheStats";
        button.Name = "SlayTheStatsFilterButton";
        button.CustomMinimumSize = new Vector2(0, 32);
        ApplyPlainButtonStyle(button);
        return button;
    }

    private static Button CreateFloatingButton()
    {
        var button = new Button();
        button.Text = "SlayTheStats";
        button.Name = "SlayTheStatsFilterButton";
        button.CustomMinimumSize = new Vector2(160, 36);
        ApplyPlainButtonStyle(button);
        // Bottom-left corner positioning.
        button.AnchorLeft = 0f;
        button.AnchorTop = 1f;
        button.AnchorRight = 0f;
        button.AnchorBottom = 1f;
        button.OffsetLeft = 20;
        button.OffsetTop = -56;
        button.OffsetRight = 180;
        button.OffsetBottom = -20;
        return button;
    }

    private static void ApplyPlainButtonStyle(Button button)
    {
        button.AddThemeColorOverride("font_color", MutedButton);
        button.AddThemeColorOverride("font_hover_color", HoverButton);
        button.AddThemeColorOverride("font_pressed_color", new Color(1f, 0.85f, 0.5f, 1f));

        var normal = new StyleBoxFlat();
        normal.BgColor = new Color(0.15f, 0.16f, 0.22f, 0.85f);
        normal.BorderColor = new Color(0.45f, 0.50f, 0.60f, 1f);
        normal.SetBorderWidthAll(1);
        normal.SetCornerRadiusAll(4);
        normal.ContentMarginLeft = 8;
        normal.ContentMarginRight = 8;
        normal.ContentMarginTop = 4;
        normal.ContentMarginBottom = 4;
        button.AddThemeStyleboxOverride("normal", normal);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BorderColor = new Color(0.6f, 0.65f, 0.75f, 1f);
        hover.BgColor = new Color(0.18f, 0.19f, 0.26f, 0.9f);
        button.AddThemeStyleboxOverride("hover", hover);

        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(0.12f, 0.13f, 0.18f, 0.95f);
        button.AddThemeStyleboxOverride("pressed", pressed);
    }

    /// <summary>Wires a plain Godot Button to toggle a filter pane (fallback / relic collection).</summary>
    private static void WireButtonToPane(Button button, PanelContainer pane)
    {
        if (pane is FilterPanelContainer fpc)
            fpc.AssociatedButton = button;

        button.Pressed += () =>
        {
            if (GodotObject.IsInstanceValid(pane))
            {
                pane.Visible = !pane.Visible;
                if (pane.Visible)
                {
                    // Same positioning as the styled-clone path so the fallback
                    // button doesn't leave the pane stranded at top-left.
                    RepositionPaneNextToButton(button, pane);
                    SyncAllControls();
                }
            }
        };

        // Update button color when filters are active.
        pane.VisibilityChanged += () =>
        {
            if (!GodotObject.IsInstanceValid(button)) return;
            UpdateButtonActiveState(button);
        };
    }

    internal static void SyncAllControls()
    {
        foreach (var cb in _syncCallbacks) cb();
    }

    private static void RefreshAllSortButtonStates()
    {
        _activeSortButtons.RemoveAll(b => !GodotObject.IsInstanceValid(b));
        foreach (var b in _activeSortButtons)
            UpdateSortButtonActiveState(b);
    }

    // ── First-run tutorial ───────────────────────────────────────────────────

    private static PanelContainer? _tutorialPanel;

    /// <summary>
    /// Shows the first-run tutorial box pointing at the SlayTheStats button
    /// inside the given submenu, if the user hasn't seen it yet. Called from
    /// the Card Library / Relic Collection OnSubmenuOpened patches with the
    /// opening submenu as <paramref name="openingSubmenu"/>. A click anywhere
    /// dismisses the tutorial and persists TutorialSeen = true.
    /// </summary>
    internal static void MaybeShowTutorialFor(Node openingSubmenu)
    {
        if (SlayTheStatsConfig.TutorialSeen) return;
        if (_tutorialPanel != null && GodotObject.IsInstanceValid(_tutorialPanel)) return;

        // Find the cloned SlayTheStats button that lives inside the submenu
        // currently being opened. Matching by ancestor (rather than visibility)
        // is reliable because OnSubmenuOpened fires before visibility has
        // propagated up the tree, so IsVisibleInTree() can be false here.
        _activeSortButtons.RemoveAll(b => !GodotObject.IsInstanceValid(b));
        NCardViewSortButton? anchorButton = null;
        foreach (var b in _activeSortButtons)
        {
            if (IsAncestorOf(openingSubmenu, b))
            {
                anchorButton = b;
                break;
            }
        }
        if (anchorButton == null)
        {
            if (SlayTheStatsConfig.DebugMode)
                MainFile.Logger.Warn($"[SlayTheStats] MaybeShowTutorialFor({openingSubmenu?.GetType().Name}): no matching button");
            return;
        }

        // Defer by one frame so the page has laid out (button's global rect is
        // populated and visibility has propagated, so the dimmer/arrow render
        // in the right place).
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame += DeferredShow;
        void DeferredShow()
        {
            tree.ProcessFrame -= DeferredShow;
            if (!GodotObject.IsInstanceValid(anchorButton)) return;
            BuildTutorialPanel(anchorButton);
        }
    }

    private static bool IsAncestorOf(Node ancestor, Node descendant)
    {
        Node? current = descendant;
        while (current != null)
        {
            if (current == ancestor) return true;
            current = current.GetParent();
        }
        return false;
    }

    private static void BuildTutorialPanel(NCardViewSortButton anchorButton)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        if (tree?.Root == null) return;

        // Use a CanvasLayer so the tutorial draws above the compendium's own UI.
        var layer = new CanvasLayer
        {
            Name = "SlayTheStatsTutorialLayer",
            Layer = 100,
        };

        // Full-screen click-catcher so any click dismisses the tutorial.
        var dimmer = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.55f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        dimmer.AnchorRight = 1f;
        dimmer.AnchorBottom = 1f;
        layer.AddChild(dimmer);

        // Build the tutorial panel — stone-textured to match the filter pane.
        var panel = new PanelContainer
        {
            Name = "SlayTheStatsTutorialPanel",
        };
        panel.AddThemeStyleboxOverride("panel", BuildPanelStyle());
        panel.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f);
        panel.MouseFilter = Control.MouseFilterEnum.Ignore; // clicks still dismiss

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        var title = new Label { Text = L.T("welcome.title") };
        title.AddThemeColorOverride("font_color", Gold);
        ApplyGameFont(title, 24);
        vbox.AddChild(title);

        var body = new Label
        {
            Text = L.T("welcome.body"),
            AutowrapMode = TextServer.AutowrapMode.Off,
        };
        body.AddThemeColorOverride("font_color", Cream);
        ApplyGameFont(body, 18);
        vbox.AddChild(body);

        var hint = new Label { Text = L.T("welcome.hint_dismiss") };
        hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.72f, 0.65f, 1f));
        ApplyGameFont(hint, 13);
        vbox.AddChild(hint);

        layer.AddChild(panel);

        // Arrow (a big ▶ / ◀ label) pointing from the panel toward the button.
        var arrow = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        arrow.AddThemeColorOverride("font_color", Gold);
        ApplyGameFont(arrow, 48);
        layer.AddChild(arrow);

        tree.Root.AddChild(layer);
        _tutorialPanel = panel;

        // Position the panel + arrow after layout settles (one more frame so
        // the panel's combined minimum size is known).
        tree.ProcessFrame += DeferredPosition;
        void DeferredPosition()
        {
            tree.ProcessFrame -= DeferredPosition;
            if (!GodotObject.IsInstanceValid(panel) || !GodotObject.IsInstanceValid(anchorButton))
                return;

            var viewport = panel.GetViewport();
            if (viewport == null) return;
            var vpSize = viewport.GetVisibleRect().Size;
            var btnRect = anchorButton.GetGlobalRect();
            var panelSize = panel.GetCombinedMinimumSize();

            // Place the panel centered vertically on the button, to its right
            // (card library button is on the left sidebar) or to its left
            // (relic collection button is bottom-left corner) — whichever has
            // more room.
            const float gap = 72f;
            float panelX, arrowX;
            string arrowGlyph;
            // Negative delta animates the arrow toward the left (button is on
            // the left), positive toward the right.
            float arrowAnimDelta;
            if (btnRect.Position.X + btnRect.Size.X + gap + panelSize.X <= vpSize.X)
            {
                // Panel to the right of the button, arrow pointing ◀ at the button.
                panelX = btnRect.Position.X + btnRect.Size.X + gap;
                arrowX = btnRect.Position.X + btnRect.Size.X + 8;
                arrowGlyph = "◀";
                arrowAnimDelta = -10f;
            }
            else
            {
                // Panel to the left of the button, arrow pointing ▶.
                panelX = btnRect.Position.X - gap - panelSize.X;
                arrowX = btnRect.Position.X - 48;
                arrowGlyph = "▶";
                arrowAnimDelta = 10f;
            }
            var panelY = Math.Clamp(
                btnRect.Position.Y + (btnRect.Size.Y - panelSize.Y) * 0.5f,
                16f, vpSize.Y - panelSize.Y - 16f);

            panel.AnchorLeft = 0f; panel.AnchorTop = 0f;
            panel.AnchorRight = 0f; panel.AnchorBottom = 0f;
            panel.GlobalPosition = new Vector2(panelX, panelY);

            arrow.Text = arrowGlyph;
            arrow.AnchorLeft = 0f; arrow.AnchorTop = 0f;
            arrow.AnchorRight = 0f; arrow.AnchorBottom = 0f;
            arrow.GlobalPosition = new Vector2(
                arrowX,
                btnRect.Position.Y + (btnRect.Size.Y - 48) * 0.5f);

            // Looping back-and-forth tween on the arrow's position so it draws
            // the eye to the SlayTheStats button. Killed automatically when the
            // tutorial layer is freed on dismiss.
            var basePos = arrow.Position;
            var arrowTween = arrow.CreateTween().SetLoops();
            arrowTween.TweenProperty(arrow, "position",
                    new Vector2(basePos.X + arrowAnimDelta, basePos.Y), 0.45)
                .SetTrans(Tween.TransitionType.Sine)
                .SetEase(Tween.EaseType.InOut);
            arrowTween.TweenProperty(arrow, "position",
                    basePos, 0.45)
                .SetTrans(Tween.TransitionType.Sine)
                .SetEase(Tween.EaseType.InOut);
        }

        // Dismiss on any click. Persist TutorialSeen so it doesn't come back.
        dimmer.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed)
                DismissTutorial(layer);
        };
    }

    private static void DismissTutorial(CanvasLayer layer)
    {
        SlayTheStatsConfig.TutorialSeen = true;
        try { BaseLib.Config.ModConfig.SaveDebounced<SlayTheStatsConfig>(); }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] Failed to save TutorialSeen: {e.Message}");
        }
        if (GodotObject.IsInstanceValid(layer))
            layer.QueueFree();
        _tutorialPanel = null;
    }

    private static void UpdateButtonActiveState(Button button)
    {
        bool active = HasActiveFilters();
        button.AddThemeColorOverride("font_color", active ? ActiveFilterColor : MutedButton);
    }

    /// <summary>
    /// True iff any filter currently differs from the user's saved defaults.
    /// Used to highlight the SlayTheStats sidebar button — the highlight signals
    /// "you've changed something this session", not "filters are non-empty".
    /// </summary>
    internal static bool HasActiveFilters()
    {
        return SlayTheStatsConfig.IsNonDefault("AscensionMin")
            || SlayTheStatsConfig.IsNonDefault("AscensionMax")
            || SlayTheStatsConfig.IsNonDefault("VersionMin")
            || SlayTheStatsConfig.IsNonDefault("VersionMax")
            || SlayTheStatsConfig.IsNonDefault("ClassFilter")
            || SlayTheStatsConfig.IsNonDefault("FilterProfile")
            || SlayTheStatsConfig.IsNonDefault("GroupUpgrades")
            || SlayTheStatsConfig.IsNonDefault("IncludeMultiplayer");
    }

    // ── Filter pane builder ─────────────────────────────────────────────��───

    /// <summary>
    /// Opens the filter pane as a standalone overlay (e.g. from the mod settings menu).
    /// Adds it to the scene root via a high CanvasLayer with a close button — used to
    /// edit the saved filter defaults outside of the compendium.
    /// </summary>
    internal static void OpenStandalonePane()
    {
        try
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            if (tree?.Root == null) return;

            // Reuse an existing standalone overlay if one is already open.
            var existing = tree.Root.GetNodeOrNull<CanvasLayer>("SlayTheStatsStandaloneFilterLayer");
            if (existing != null && GodotObject.IsInstanceValid(existing))
            {
                existing.QueueFree();
                return;
            }

            var layer = new CanvasLayer
            {
                Name = "SlayTheStatsStandaloneFilterLayer",
                Layer = 100,
            };

            // Dimmed full-screen background to capture clicks and dim the menu beneath.
            var bg = new ColorRect
            {
                Color = new Color(0, 0, 0, 0.55f),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            bg.AnchorRight = 1f;
            bg.AnchorBottom = 1f;
            layer.AddChild(bg);

            Action close = () =>
            {
                if (GodotObject.IsInstanceValid(layer)) layer.QueueFree();
            };
            bg.GuiInput += (InputEvent ev) =>
            {
                if (ev is InputEventMouseButton mb && mb.Pressed) close();
            };

            var pane = BuildFilterPane(close);
            pane.Visible = true;
            layer.AddChild(pane);
            RegisterPane(pane);

            tree.Root.AddChild(layer);

            // Center after the pane has computed its size.
            pane.Ready += () => CenterPaneOnViewport(pane);
            tree.ProcessFrame += DeferredCenter;
            void DeferredCenter()
            {
                tree.ProcessFrame -= DeferredCenter;
                CenterPaneOnViewport(pane);
                SyncAllControls();
            }

            if (SlayTheStatsConfig.DebugMode)
                MainFile.Logger.Info("[SlayTheStats] Standalone filter pane opened");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] OpenStandalonePane failed: {e.Message}");
        }
    }

    private static void CenterPaneOnViewport(PanelContainer pane)
    {
        if (!GodotObject.IsInstanceValid(pane)) return;
        var viewport = pane.GetViewport();
        if (viewport == null) return;
        var vpSize = viewport.GetVisibleRect().Size;
        var paneSize = pane.GetCombinedMinimumSize();
        pane.AnchorLeft = 0f; pane.AnchorTop = 0f;
        pane.AnchorRight = 0f; pane.AnchorBottom = 0f;
        pane.GlobalPosition = new Vector2(
            (vpSize.X - paneSize.X) * 0.5f,
            (vpSize.Y - paneSize.Y) * 0.5f);
    }

    /// <summary>
    /// Stepper control with the same look as a small spin box but extended with
    /// "Lowest" / "Highest" sentinels at the edges of the 0–10 range. Stepping
    /// down from 0 selects "Lowest" (= -∞, int.MinValue) and stepping up from
    /// 10 selects "Highest" (= +∞, int.MaxValue). Sentinels are stored in the
    /// underlying int field so saving/loading round-trips through the existing
    /// SimpleModConfig serialiser.
    /// </summary>
    private partial class AscensionStepper : HBoxContainer
    {
        public const int LowestSentinel = int.MinValue;
        public const int HighestSentinel = int.MaxValue;
        public const int MinExplicit = 0;

        /// <summary>
        /// Explicit upper bound for the stepper — dynamic, derived from the
        /// highest ascension actually present in the data. As runs start
        /// landing on higher ascensions (extensions / mods), this grows
        /// automatically. Falls back to 10 (current STS2 ceiling) on a
        /// fresh install before any runs are parsed. Instance field (not
        /// const) so each newly built stepper re-reads it — the pane is
        /// rebuilt on every show, so stale values aren't an issue.
        /// </summary>
        public readonly int MaxExplicit;

        private int _value;
        private Label _display = null!;
        public event Action<int>? ValueChanged;

        public int Value
        {
            get => _value;
            set { _value = value; if (_display != null) _display.Text = Format(value, MaxExplicit); }
        }

        public Label DisplayLabel => _display;

        public AscensionStepper(int initial)
        {
            AddThemeConstantOverride("separation", 4);
            Alignment = AlignmentMode.Center;

            // Read the data's actual ascension ceiling. Never lower than 10
            // (the current STS2 ceiling) because an empty / single-run
            // fresh install shouldn't stop the user from navigating up to
            // the known max; only EXPANDS above 10 once extension/mod data
            // arrives.
            int dataMax = 10;
            try
            {
                var ascs = StatsAggregator.GetDistinctAscensions(MainFile.Db);
                if (ascs.Count > 0) dataMax = Math.Max(10, ascs[^1]);
            }
            catch { /* keep fallback */ }
            MaxExplicit = dataMax;

            // Label on the left, vertically-stacked ▲/▼ arrow buttons on the right.
            // Widened so "Lowest" / "Highest" labels fit without clipping at the
            // larger font size.
            _display = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CustomMinimumSize = new Vector2(96, 0),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            };
            _display.AddThemeColorOverride("font_color", Cream);
            ApplyGameFont(_display, 19);
            AddChild(_display);

            var arrows = new VBoxContainer();
            arrows.AddThemeConstantOverride("separation", 0);
            arrows.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            AddChild(arrows);

            var up = MakeArrowButton("▲");
            up.Pressed += () => Step(+1);
            arrows.AddChild(up);

            var down = MakeArrowButton("▼");
            down.Pressed += () => Step(-1);
            arrows.AddChild(down);

            // Clamp any out-of-range explicit value down to the current
            // ceiling. Handles old persisted configs that stored e.g.
            // AscensionMax = 20 when the actual data ceiling is 10 — without
            // this, the stepper would display "A20" but clicking up from
            // A10 would jump straight to Highest and the user couldn't get
            // back above 10. Sentinels pass through untouched.
            if (initial != LowestSentinel && initial != HighestSentinel)
            {
                if (initial > MaxExplicit) initial = MaxExplicit;
                if (initial < MinExplicit) initial = MinExplicit;
            }
            Value = initial;
        }

        private void Step(int dir)
        {
            int next = _value;
            if (dir > 0)
            {
                if (_value == LowestSentinel)        next = MinExplicit;
                else if (_value == HighestSentinel)  next = HighestSentinel;
                else if (_value >= MaxExplicit)      next = HighestSentinel;
                else                                 next = _value + 1;
            }
            else
            {
                if (_value == HighestSentinel)       next = MaxExplicit;
                else if (_value == LowestSentinel)   next = LowestSentinel;
                else if (_value <= MinExplicit)      next = LowestSentinel;
                else                                 next = _value - 1;
            }
            if (next != _value)
            {
                Value = next;
                ValueChanged?.Invoke(next);
            }
        }

        public static string Format(int value, int maxExplicit = 10)
        {
            if (value == LowestSentinel)  return L.T("filter.sentinel.lowest");
            if (value == HighestSentinel) return L.T("filter.sentinel.highest");
            // Clamp any out-of-range explicit values displayed through the
            // stepper Format helper — same reasoning as the stepper's
            // constructor clamp. Sentinels fall through the branches above.
            int clamped = value;
            if (clamped > maxExplicit) clamped = maxExplicit;
            if (clamped < MinExplicit) clamped = MinExplicit;
            return $"A{clamped}";
        }

        private static Button MakeArrowButton(string glyph)
        {
            var btn = new Button { Text = glyph, CustomMinimumSize = new Vector2(26, 17) };
            btn.AddThemeColorOverride("font_color", Cream);
            btn.AddThemeColorOverride("font_hover_color", Gold);
            btn.AddThemeColorOverride("font_pressed_color", Gold);
            ApplyGameFont(btn, 12);
            var normal = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.06f, 0.05f, 0.6f),
                BorderColor = new Color(0.4f, 0.35f, 0.25f, 0.5f),
            };
            normal.SetBorderWidthAll(1);
            normal.SetCornerRadiusAll(3);
            normal.ContentMarginLeft = 4; normal.ContentMarginRight = 4;
            normal.ContentMarginTop = 2;  normal.ContentMarginBottom = 2;
            btn.AddThemeStyleboxOverride("normal", normal);
            var hover = (StyleBoxFlat)normal.Duplicate();
            hover.BgColor = new Color(0.12f, 0.09f, 0.07f, 0.7f);
            hover.BorderColor = new Color(0.6f, 0.5f, 0.3f, 0.6f);
            btn.AddThemeStyleboxOverride("hover", hover);
            return btn;
        }
    }

    /// <summary>
    /// Builds the SlayTheStats filter pane. Used by both the compendium card/relic filter
    /// buttons (full pane) and the bestiary filter button (subset — no class filter, no
    /// group-card-upgrades toggle, since the bestiary already exposes per-character scoring
    /// in its own row and group-upgrades is a card-aggregation concern).
    /// </summary>
    /// <param name="onClose">If provided, a small ✕ button is added to the title row that
    /// invokes this callback. Used by the bestiary which doesn't have an external close
    /// trigger.</param>
    /// <param name="includeClassFilter">When false, the class dropdown row is omitted.</param>
    /// <param name="includeGroupUpgrades">When false, the "Group card upgrades" toggle is
    /// omitted.</param>
    /// <param name="onFilterChanged">If provided, invoked alongside RefreshAllSortButtonStates
    /// every time a filter control mutates the config. Used by the bestiary to refresh its
    /// encounter list when filters change while the pane is open.</param>
    internal static PanelContainer BuildFilterPane(
        Action? onClose = null,
        bool includeClassFilter = true,
        bool includeGroupUpgrades = true,
        Action? onFilterChanged = null)
    {
        // Local helper invoked at the end of every filter mutation. Wraps the existing sidebar
        // suffix refresh and forwards to any caller-supplied callback.
        // Assigned when the Save Defaults button is built near the bottom of the pane;
        // invoked from NotifyChanged so the button's "dirty" green highlight tracks live.
        Action? refreshSaveDefaultsHighlight = null;

        void NotifyChanged()
        {
            RefreshAllSortButtonStates();
            refreshSaveDefaultsHighlight?.Invoke();
            onFilterChanged?.Invoke();
        }

        var pane = new FilterPanelContainer();
        pane.Name = "SlayTheStatsFilterPane";
        pane.Visible = false;
        pane.ZIndex = 90;

        // Position is set dynamically by RepositionPaneNextToButton when the
        // pane is shown, based on the sidebar button's actual global rect.
        pane.GrowHorizontal = Control.GrowDirection.End;
        pane.GrowVertical = Control.GrowDirection.End;

        // Use the game's tooltip stone texture for the panel background
        // (same asset the tooltips use — matches the game aesthetic).
        pane.AddThemeStyleboxOverride("panel", BuildPanelStyle());
        pane.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f); // cooler steel-blue tint

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        pane.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        // ── Title (with optional close button) ──
        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(titleRow);

        var title = new Label();
        title.Text = L.T("filter.pane.title");
        title.AddThemeColorOverride("font_color", Gold);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        ApplyGameFont(title, 24);
        titleRow.AddChild(title);

        if (onClose != null)
        {
            var closeBtn = new Button();
            closeBtn.Text = "✕";
            closeBtn.CustomMinimumSize = new Vector2(28, 28);
            closeBtn.AddThemeColorOverride("font_color", Cream);
            closeBtn.AddThemeColorOverride("font_hover_color", Gold);
            ApplyGameFont(closeBtn, 16);
            closeBtn.Pressed += () => onClose();
            titleRow.AddChild(closeBtn);
        }

        AddSeparator(vbox);

        // ── Version range ──
        // Uses the same sentinel pattern as the ascension filter: Lowest /
        // Highest are stored in the config as the special strings
        // SlayTheStatsConfig.VersionLowest / VersionHighest so the filter
        // auto-tracks new versions as they appear in the data.
        var verRow = new HBoxContainer();
        verRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(verRow);
        verRow.AddChild(MakeLabel(L.T("filter.row.version"), 120));

        var versions = StatsAggregator.GetDistinctVersions(MainFile.Db);
        var verValues = new List<string> { SlayTheStatsConfig.VersionLowest };
        verValues.AddRange(versions);
        verValues.Add(SlayTheStatsConfig.VersionHighest);
        var verLabels = verValues.Select(FormatVersionValue).ToList();

        var verMin = MakeVersionDropdown(verLabels, verValues, SlayTheStatsConfig.VersionMin);
        verRow.AddChild(verMin);
        verRow.AddChild(MakeLabel("to"));
        var verMax = MakeVersionDropdown(verLabels, verValues, SlayTheStatsConfig.VersionMax);
        verRow.AddChild(verMax);

        HighlightIfNonDefault(verMin, SlayTheStatsConfig.IsNonDefault("VersionMin"));
        HighlightIfNonDefault(verMax, SlayTheStatsConfig.IsNonDefault("VersionMax"));

        verMin.ItemSelected += (idx) =>
        {
            var v = verValues[(int)idx];
            SlayTheStatsConfig.VersionMin = v;
            // Mirror the ascension stepper cross-clamp behaviour: if min crossed
            // max, drag max along with it. Sentinel-aware via CompareVersionValues.
            if (CompareVersionValues(SlayTheStatsConfig.VersionMax, v) < 0)
            {
                SlayTheStatsConfig.VersionMax = v;
                SelectVersionValue(verMax, verValues, v);
                HighlightIfNonDefault(verMax, SlayTheStatsConfig.IsNonDefault("VersionMax"));
            }
            HighlightIfNonDefault(verMin, SlayTheStatsConfig.IsNonDefault("VersionMin"));
            NotifyChanged();
        };
        verMax.ItemSelected += (idx) =>
        {
            var v = verValues[(int)idx];
            SlayTheStatsConfig.VersionMax = v;
            if (CompareVersionValues(SlayTheStatsConfig.VersionMin, v) > 0)
            {
                SlayTheStatsConfig.VersionMin = v;
                SelectVersionValue(verMin, verValues, v);
                HighlightIfNonDefault(verMin, SlayTheStatsConfig.IsNonDefault("VersionMin"));
            }
            HighlightIfNonDefault(verMax, SlayTheStatsConfig.IsNonDefault("VersionMax"));
            NotifyChanged();
        };

        // ── Profile filter ──
        var profiles = StatsAggregator.GetDistinctProfiles(MainFile.Db);

        var profRow = new HBoxContainer();
        profRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(profRow);
        profRow.AddChild(MakeLabel(L.T("filter.row.profile"), 120));

        var profSelect = MakeDropdown(profiles, SlayTheStatsConfig.FilterProfile);
        profSelect.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        profRow.AddChild(profSelect);

        HighlightIfNonDefault(profSelect, SlayTheStatsConfig.IsNonDefault("FilterProfile"));

        profSelect.ItemSelected += (idx) =>
        {
            SlayTheStatsConfig.FilterProfile = idx == 0 ? "" : profSelect.GetItemText((int)idx);
            HighlightIfNonDefault(profSelect, SlayTheStatsConfig.IsNonDefault("FilterProfile"));
            NotifyChanged();
        };

        // ── Ascension range ──
        // Stepper buttons (◀ ▶) instead of a SpinBox so we can extend the range
        // beyond a fixed numeric span with "Lowest" / "Highest" sentinel values
        // (-∞ / +∞). Stepping down from 0 selects "Lowest"; stepping up from 10
        // selects "Highest". Sentinels make the filter robust against mods that
        // add negative or 11+ ascensions.
        var ascRow = new HBoxContainer();
        ascRow.AddThemeConstantOverride("separation", 6);
        vbox.AddChild(ascRow);
        ascRow.AddChild(MakeLabel(L.T("filter.row.ascension"), 120));

        var ascMin = new AscensionStepper(SlayTheStatsConfig.AscensionMin);
        ascRow.AddChild(ascMin);
        ascRow.AddChild(MakeLabel("to"));
        var ascMax = new AscensionStepper(SlayTheStatsConfig.AscensionMax);
        ascRow.AddChild(ascMax);

        HighlightStepperIfNonDefault(ascMin, SlayTheStatsConfig.IsNonDefault("AscensionMin"));
        HighlightStepperIfNonDefault(ascMax, SlayTheStatsConfig.IsNonDefault("AscensionMax"));

        ascMin.ValueChanged += (val) =>
        {
            SlayTheStatsConfig.AscensionMin = val;
            // Push max up if min crossed it. Int comparison is sentinel-aware
            // because LowestSentinel = MinValue and HighestSentinel = MaxValue,
            // so e.g. setting min = Highest correctly drags max to Highest too,
            // and setting max = Lowest correctly drags min to Lowest.
            if (ascMax.Value < val)
            {
                ascMax.Value = val;
                SlayTheStatsConfig.AscensionMax = val;
                HighlightStepperIfNonDefault(ascMax, SlayTheStatsConfig.IsNonDefault("AscensionMax"));
            }
            HighlightStepperIfNonDefault(ascMin, SlayTheStatsConfig.IsNonDefault("AscensionMin"));
            NotifyChanged();
        };
        ascMax.ValueChanged += (val) =>
        {
            SlayTheStatsConfig.AscensionMax = val;
            if (ascMin.Value > val)
            {
                ascMin.Value = val;
                SlayTheStatsConfig.AscensionMin = val;
                HighlightStepperIfNonDefault(ascMin, SlayTheStatsConfig.IsNonDefault("AscensionMin"));
            }
            HighlightStepperIfNonDefault(ascMax, SlayTheStatsConfig.IsNonDefault("AscensionMax"));
            NotifyChanged();
        };

        // ── Class filter dropdown ──
        // Values aligned with item index: ["", "__class__", "CHARACTER.IRONCLAD", ...]
        // Skipped on the bestiary side: the bestiary already exposes per-character scoring
        // via its own "Score by:" chip row, so the class dropdown would be redundant there.
        OptionButton? classSelect = null;
        List<string>? classValues = null;
        if (includeClassFilter)
        {
            var classRow = new HBoxContainer();
            classRow.AddThemeConstantOverride("separation", 6);
            vbox.AddChild(classRow);
            classRow.AddChild(MakeLabel(L.T("filter.row.class"), 120));

            // "All" (empty string) intentionally omitted — cross-class stats on a class
            // card are low-signal (few runs introduce other-class cards) and misleading
            // (mixes in runs where the card was never relevant). Users who really want
            // to see e.g. an Ironclad card's Silent stats can pick "Silent" explicitly.
            // Legacy "" values are migrated to "__class__" in SlayTheStatsConfig.Sanitize.
            classValues = new List<string> { SlayTheStatsConfig.ClassFilterClassSpecific };
            var classLabels = new List<string> { L.T("filter.class.auto") };
            foreach (var ch in GetOrderedCharacters(MainFile.Db))
            {
                classValues.Add(ch);
                classLabels.Add(FormatCharName(ch));
            }
            classSelect = new OptionButton();
            classSelect.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            classSelect.AddThemeColorOverride("font_color", Cream);
            classSelect.AddThemeColorOverride("font_hover_color", Gold);
            classSelect.AddThemeColorOverride("font_focus_color", Gold);
            ApplyGameFont(classSelect, 16);
            for (int i = 0; i < classLabels.Count; i++)
                classSelect.AddItem(classLabels[i], i);
            SelectClassFilter(classSelect, classValues, SlayTheStatsConfig.ClassFilter);
            classRow.AddChild(classSelect);

            HighlightIfNonDefault(classSelect, SlayTheStatsConfig.IsNonDefault("ClassFilter"));

            var classSelectLocal = classSelect;
            var classValuesLocal = classValues;
            classSelect.ItemSelected += (idx) =>
            {
                var i = (int)idx;
                SlayTheStatsConfig.ClassFilter = (i >= 0 && i < classValuesLocal.Count) ? classValuesLocal[i] : SlayTheStatsConfig.ClassFilterClassSpecific;
                HighlightIfNonDefault(classSelectLocal, SlayTheStatsConfig.IsNonDefault("ClassFilter"));
                NotifyChanged();
            };
        }

        AddSeparator(vbox);

        // ── Toggles ──
        // Prefer cloning the game's NLibraryStatTickbox (matches the compendium's
        // own "Multiplayer Cards" / "View Upgrades" checkboxes). Falls back to
        // a Godot CheckBox if the template hasn't been cached yet — happens if
        // the relic page is opened before the card library has ever loaded.
        // Skipped on the bestiary side: GroupCardUpgrades is a card-aggregation
        // concern with no effect on encounter stats.
        Action<bool>? setGroupUpgrades = null;
        Action highlightGroupUpgrades = () => { };
        if (includeGroupUpgrades)
        {
            Control groupUpgradesControl;
            if (_tickboxTemplate != null && GodotObject.IsInstanceValid(_tickboxTemplate))
            {
                var clonedTickbox = CloneTickbox(_tickboxTemplate, L.T("filter.group_upgrades"), SlayTheStatsConfig.GroupCardUpgrades);
                clonedTickbox.Toggled += (box) =>
                {
                    SlayTheStatsConfig.GroupCardUpgrades = box.IsTicked;
                    highlightGroupUpgrades();
                    NotifyChanged();
                };
                groupUpgradesControl = clonedTickbox;
                setGroupUpgrades = v => clonedTickbox.IsTicked = v;
                // Tint the tickbox's inner MegaLabel green when GroupCardUpgrades
                // differs from the saved default — matches the highlight pattern
                // used by the other filter rows.
                highlightGroupUpgrades = () =>
                {
                    if (!GodotObject.IsInstanceValid(clonedTickbox)) return;
                    // NLibraryStatTickbox stores its label at the direct child path
                    // "Label" — match how the game's own _Ready looks it up. Falls
                    // back to a recursive search if the layout differs unexpectedly.
                    var label = (clonedTickbox.GetNodeOrNull<Control>("Label")
                                ?? clonedTickbox.FindChild("Label", true, false)) as Control;
                    if (label == null)
                    {
                        if (SlayTheStatsConfig.DebugMode)
                            MainFile.Logger.Warn("[SlayTheStats] highlightGroupUpgrades: tickbox Label not found");
                        return;
                    }

                    bool nonDefault = SlayTheStatsConfig.IsNonDefault("GroupUpgrades");
                    var color = nonDefault ? ActiveFilterColor : Colors.White;

                    // Set font_color via theme override only. Don't also set Modulate
                    // to the same color — Modulate multiplies into the rendered color,
                    // so white * white * (font green) = pure green; but green * white *
                    // (font green) = green² which is much darker than the green used
                    // in the other filter rows. Force Modulate back to white in case
                    // an earlier render-pass had set it.
                    label.AddThemeColorOverride("font_color", color);
                    label.Modulate = Colors.White;
                };
            }
            else
            {
                var fallback = MakeCheckButton(L.T("filter.group_upgrades"), SlayTheStatsConfig.GroupCardUpgrades);
                fallback.Toggled += (pressed) =>
                {
                    SlayTheStatsConfig.GroupCardUpgrades = pressed;
                    highlightGroupUpgrades();
                    NotifyChanged();
                };
                groupUpgradesControl = fallback;
                setGroupUpgrades = v => fallback.SetPressedNoSignal(v);
                highlightGroupUpgrades = () =>
                    fallback.AddThemeColorOverride(
                        "font_color",
                        SlayTheStatsConfig.IsNonDefault("GroupUpgrades") ? ActiveFilterColor : Colors.White);
            }
            vbox.AddChild(groupUpgradesControl);
            // Initial highlight — deferred so the cloned tickbox's _Ready has run
            // (and its MegaLabel reference is initialised) before we look it up.
            var tickboxTree = (SceneTree)Engine.GetMainLoop();
            tickboxTree.ProcessFrame += DeferredGroupUpgradesHighlight;
            void DeferredGroupUpgradesHighlight()
            {
                tickboxTree.ProcessFrame -= DeferredGroupUpgradesHighlight;
                highlightGroupUpgrades();
            }

            AddSeparator(vbox);
        }

        // ── Multiplayer toggle ──
        // Mirrors the GroupCardUpgrades tickbox style. Unconditional (present on all
        // surfaces — card/relic, bestiary, event) because game_mode scoping applies
        // universally to aggregated stats.
        Action<bool>? setIncludeMultiplayer;
        Action highlightIncludeMultiplayer = () => { };
        {
            Control includeMultiplayerControl;
            if (_tickboxTemplate != null && GodotObject.IsInstanceValid(_tickboxTemplate))
            {
                var clonedTickbox = CloneTickbox(_tickboxTemplate, L.T("filter.include_multiplayer"), SlayTheStatsConfig.IncludeMultiplayer);
                clonedTickbox.Toggled += (box) =>
                {
                    SlayTheStatsConfig.IncludeMultiplayer = box.IsTicked;
                    highlightIncludeMultiplayer();
                    NotifyChanged();
                };
                includeMultiplayerControl = clonedTickbox;
                setIncludeMultiplayer = v => clonedTickbox.IsTicked = v;
                highlightIncludeMultiplayer = () =>
                {
                    if (!GodotObject.IsInstanceValid(clonedTickbox)) return;
                    var label = (clonedTickbox.GetNodeOrNull<Control>("Label")
                                ?? clonedTickbox.FindChild("Label", true, false)) as Control;
                    if (label == null)
                    {
                        if (SlayTheStatsConfig.DebugMode)
                            MainFile.Logger.Warn("[SlayTheStats] highlightIncludeMultiplayer: tickbox Label not found");
                        return;
                    }
                    bool nonDefault = SlayTheStatsConfig.IsNonDefault("IncludeMultiplayer");
                    var color = nonDefault ? ActiveFilterColor : Colors.White;
                    label.AddThemeColorOverride("font_color", color);
                    label.Modulate = Colors.White;
                };
            }
            else
            {
                var fallback = MakeCheckButton(L.T("filter.include_multiplayer"), SlayTheStatsConfig.IncludeMultiplayer);
                fallback.Toggled += (pressed) =>
                {
                    SlayTheStatsConfig.IncludeMultiplayer = pressed;
                    highlightIncludeMultiplayer();
                    NotifyChanged();
                };
                includeMultiplayerControl = fallback;
                setIncludeMultiplayer = v => fallback.SetPressedNoSignal(v);
                highlightIncludeMultiplayer = () =>
                    fallback.AddThemeColorOverride(
                        "font_color",
                        SlayTheStatsConfig.IsNonDefault("IncludeMultiplayer") ? ActiveFilterColor : Colors.White);
            }
            vbox.AddChild(includeMultiplayerControl);
            var mpTree = (SceneTree)Engine.GetMainLoop();
            mpTree.ProcessFrame += DeferredIncludeMultiplayerHighlight;
            void DeferredIncludeMultiplayerHighlight()
            {
                mpTree.ProcessFrame -= DeferredIncludeMultiplayerHighlight;
                highlightIncludeMultiplayer();
            }

            AddSeparator(vbox);
        }

        // ── Action buttons ──
        var row1 = new HBoxContainer();
        row1.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(row1);

        row1.AddChild(MakeActionButton(L.T("filter.button.clear"), () =>
        {
            SlayTheStatsConfig.ClearAllFilters();
            SyncAllControls();
            NotifyChanged();
        }));
        row1.AddChild(MakeActionButton(L.T("filter.button.reset"), () =>
        {
            SlayTheStatsConfig.RestoreDefaults();
            SyncAllControls();
            NotifyChanged();
        }));
        var saveDefaultsBtn = MakeActionButton(L.T("filter.button.save_defaults"), () =>
        {
            SlayTheStatsConfig.SaveDefaults();
            try { BaseLib.Config.ModConfig.SaveDebounced<SlayTheStatsConfig>(); }
            catch (System.Exception e)
            {
                MainFile.Logger.Warn($"[SlayTheStats] Failed to save config: {e.Message}");
            }
            SyncAllControls();
            NotifyChanged();
        });
        row1.AddChild(saveDefaultsBtn);
        // Mirrors the same green highlight the per-field controls use when a filter
        // differs from its saved default — pulls the Save Defaults button into focus
        // whenever there's something worth saving.
        refreshSaveDefaultsHighlight = () =>
        {
            if (!GodotObject.IsInstanceValid(saveDefaultsBtn)) return;
            var dirty = HasActiveFilters();
            saveDefaultsBtn.AddThemeColorOverride("font_color", dirty ? ActiveFilterColor : Cream);
            saveDefaultsBtn.AddThemeColorOverride("font_hover_color", dirty ? ActiveFilterColor : Gold);
        };
        refreshSaveDefaultsHighlight();

        // ── Sync callback: refresh all controls from config when pane opens ──
        _syncCallbacks.Add(() =>
        {
            if (!GodotObject.IsInstanceValid(pane)) return;

            SelectVersionValue(verMin, verValues, SlayTheStatsConfig.VersionMin);
            SelectVersionValue(verMax, verValues, SlayTheStatsConfig.VersionMax);
            HighlightIfNonDefault(verMin, SlayTheStatsConfig.IsNonDefault("VersionMin"));
            HighlightIfNonDefault(verMax, SlayTheStatsConfig.IsNonDefault("VersionMax"));

            SelectOptionByText(profSelect, SlayTheStatsConfig.FilterProfile);
            HighlightIfNonDefault(profSelect, SlayTheStatsConfig.IsNonDefault("FilterProfile"));

            ascMin.Value = SlayTheStatsConfig.AscensionMin;
            ascMax.Value = SlayTheStatsConfig.AscensionMax;
            HighlightStepperIfNonDefault(ascMin, SlayTheStatsConfig.IsNonDefault("AscensionMin"));
            HighlightStepperIfNonDefault(ascMax, SlayTheStatsConfig.IsNonDefault("AscensionMax"));

            if (classSelect != null && classValues != null)
            {
                SelectClassFilter(classSelect, classValues, SlayTheStatsConfig.ClassFilter);
                HighlightIfNonDefault(classSelect, SlayTheStatsConfig.IsNonDefault("ClassFilter"));
            }
            setGroupUpgrades?.Invoke(SlayTheStatsConfig.GroupCardUpgrades);
            highlightGroupUpgrades();
            setIncludeMultiplayer?.Invoke(SlayTheStatsConfig.IncludeMultiplayer);
            highlightIncludeMultiplayer();
            refreshSaveDefaultsHighlight?.Invoke();
        });

        return pane;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the panel background using the game's stone tooltip texture.
    /// Falls back to a dark flat style if the texture can't be loaded.
    /// </summary>
    internal static StyleBox BuildPanelStyle()
    {
        var tex = ResourceLoader.Load<Texture2D>("res://images/ui/hover_tip.png");
        if (tex != null)
        {
            var sb = new StyleBoxTexture();
            sb.Texture             = tex;
            sb.TextureMarginLeft   = 55f;
            sb.TextureMarginRight  = 91f;
            sb.TextureMarginTop    = 43f;
            sb.TextureMarginBottom = 32f;
            // All four content margins equal so the pane's interior padding is
            // symmetric (was asymmetric: left=22, top=16, right=16, bottom=12).
            sb.ContentMarginLeft   = 16f;
            sb.ContentMarginRight  = 16f;
            sb.ContentMarginTop    = 12f;
            sb.ContentMarginBottom = 12f;
            return sb;
        }
        var flat = new StyleBoxFlat();
        flat.BgColor = new Color(0.11f, 0.07f, 0.06f, 0.94f);
        flat.SetContentMarginAll(12);
        flat.SetCornerRadiusAll(3);
        return flat;
    }

    private static readonly Color TextShadow = new(0f, 0f, 0f, 0.85f);

    /// <summary>
    /// Applies the mod's game font + size + a 1px black drop shadow. The
    /// shadow matches the look of the cloned NLibraryStatTickbox / vanilla
    /// compendium UI text — without it our pane labels look anemic on the
    /// stone tooltip background.
    /// </summary>
    internal static void ApplyGameFont(Control control, int size = 18)
    {
        var font = TooltipHelper.GetKreonBoldFont() ?? TooltipHelper.GetKreonFont();
        if (font != null)
            control.AddThemeFontOverride("font", font);
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_shadow_color", TextShadow);
        control.AddThemeConstantOverride("shadow_offset_x", 1);
        control.AddThemeConstantOverride("shadow_offset_y", 1);
    }

    internal static Label MakeLabel(string text, float minWidth = 0)
    {
        var label = new Label();
        label.Text = text;
        label.AddThemeColorOverride("font_color", Cream);
        ApplyGameFont(label, 19);
        if (minWidth > 0)
            label.CustomMinimumSize = new Vector2(minWidth, 0);
        return label;
    }

    /// <summary>
    /// Builds a version-range OptionButton with Lowest / Highest sentinel
    /// entries at the ends. Indices map 1:1 with <paramref name="values"/>,
    /// which stores the raw config strings (sentinel sentinels or version ids).
    /// </summary>
    private static OptionButton MakeVersionDropdown(List<string> labels, List<string> values, string selected)
    {
        var btn = new OptionButton();
        btn.CustomMinimumSize = new Vector2(150, 0);
        btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        btn.AddThemeColorOverride("font_color", Cream);
        btn.AddThemeColorOverride("font_hover_color", Gold);
        btn.AddThemeColorOverride("font_focus_color", Gold);
        ApplyGameFont(btn, 19);
        ApplyDropdownStyle(btn);
        for (int i = 0; i < labels.Count; i++)
            btn.AddItem(labels[i], i);
        SelectVersionValue(btn, values, selected);
        return btn;
    }

    /// <summary>
    /// Sentinel-aware semantic version comparator. VersionLowest is below
    /// every concrete version; VersionHighest is above every concrete version.
    /// Mirrors the int comparison used by AscensionStepper sentinels so the
    /// version cross-clamp behaviour matches.
    /// </summary>
    private static int CompareVersionValues(string a, string b)
    {
        if (a == b) return 0;
        if (a == SlayTheStatsConfig.VersionLowest)  return -1;
        if (b == SlayTheStatsConfig.VersionLowest)  return  1;
        if (a == SlayTheStatsConfig.VersionHighest) return  1;
        if (b == SlayTheStatsConfig.VersionHighest) return -1;
        return AggregationFilter.CompareVersions(a, b);
    }

    private static string FormatVersionValue(string v)
    {
        if (v == SlayTheStatsConfig.VersionLowest)  return L.T("filter.sentinel.lowest");
        if (v == SlayTheStatsConfig.VersionHighest) return L.T("filter.sentinel.highest");
        return v;
    }

    private static void SelectVersionValue(OptionButton button, List<string> values, string current)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], current, StringComparison.OrdinalIgnoreCase))
            {
                button.Selected = i;
                return;
            }
        }
        // Saved value no longer in dropdown (e.g. version was removed from data):
        // treat as Lowest for Min, Highest for Max — caller disambiguates via
        // context since both ends are valid fallbacks. Just pick Lowest here.
        button.Selected = 0;
    }

    private static OptionButton MakeDropdown(List<string> items, string selected)
    {
        var btn = new OptionButton();
        btn.CustomMinimumSize = new Vector2(110, 0);
        btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        btn.AddThemeColorOverride("font_color", Cream);
        btn.AddThemeColorOverride("font_hover_color", Gold);
        btn.AddThemeColorOverride("font_focus_color", Gold);
        ApplyGameFont(btn, 19);
        ApplyDropdownStyle(btn);
        btn.AddItem(L.T("filter.dropdown.any"), 0);
        for (int i = 0; i < items.Count; i++)
            btn.AddItem(items[i], i + 1);
        SelectOptionByText(btn, selected);
        return btn;
    }

    /// <summary>Applies a visible bordered background to an OptionButton dropdown so it
    /// stands out against the dark filter pane / tooltip background. Slightly brighter
    /// than the surrounding pane with a warm border that matches the action buttons.</summary>
    private static void ApplyDropdownStyle(OptionButton btn)
    {
        var normal = new StyleBoxFlat();
        normal.BgColor = new Color(0.18f, 0.15f, 0.12f, 0.85f);
        normal.SetBorderWidthAll(1);
        normal.BorderColor = new Color(0.55f, 0.45f, 0.30f, 0.7f);
        normal.SetCornerRadiusAll(3);
        normal.ContentMarginLeft = 8;
        normal.ContentMarginRight = 8;
        normal.ContentMarginTop = 4;
        normal.ContentMarginBottom = 4;
        btn.AddThemeStyleboxOverride("normal", normal);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.24f, 0.20f, 0.16f, 0.92f);
        hover.BorderColor = new Color(0.75f, 0.62f, 0.40f, 0.85f);
        btn.AddThemeStyleboxOverride("hover", hover);

        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(0.12f, 0.10f, 0.08f, 0.95f);
        btn.AddThemeStyleboxOverride("pressed", pressed);

        var focus = (StyleBoxFlat)normal.Duplicate();
        focus.BorderColor = new Color(0.85f, 0.70f, 0.45f, 1f);
        focus.SetBorderWidthAll(2);
        btn.AddThemeStyleboxOverride("focus", focus);
    }

    private static SpinBox MakeSpinBox(int min, int max, int value)
    {
        var sb = new SpinBox();
        sb.MinValue = min;
        sb.MaxValue = max;
        sb.Value = value;
        sb.CustomMinimumSize = new Vector2(65, 0);
        sb.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return sb;
    }

    /// <summary>
    /// Square-style checkbox (Godot CheckBox) to match the card library's
    /// rarity filters (NCardRarityTickbox). CheckBox draws a tickbox on the
    /// left of the label, unlike CheckButton which draws a slider toggle.
    /// </summary>
    /// <summary>
    /// Builds a "Group card upgrades"-style checkbox for reuse outside BuildFilterPane
    /// (e.g. the bestiary settings pane). Prefers the cached game-native
    /// NLibraryStatTickbox template; falls back to a plain CheckBox if not cached yet.
    /// Returns the control to add to the parent container.
    /// </summary>
    internal static Control BuildStyledCheckbox(string label, bool initialValue, Action<bool> onToggle)
    {
        // Warm the template from a cold NCardLibrary instance if the user hasn't
        // opened the card library this session — same three-strategy resolver used
        // by ResolveSortButtonTemplate. Without this the bestiary / relic page
        // would always fall back to the unstyled CheckBox.
        if (_tickboxTemplate == null || !GodotObject.IsInstanceValid(_tickboxTemplate))
            WarmTemplatesFromColdCardLibrary();

        if (_tickboxTemplate != null && GodotObject.IsInstanceValid(_tickboxTemplate))
        {
            var clone = CloneTickbox(_tickboxTemplate, label, initialValue);
            clone.Toggled += (box) => onToggle(box.IsTicked);
            return clone;
        }

        var fallback = MakeCheckButton(label, initialValue);
        fallback.Toggled += (pressed) => onToggle(pressed);
        return fallback;
    }

    private static CheckBox MakeCheckButton(string text, bool initial)
    {
        var cb = new CheckBox();
        cb.Text = text;
        cb.ButtonPressed = initial;
        cb.AddThemeColorOverride("font_color", Cream);
        cb.AddThemeColorOverride("font_hover_color", Gold);
        cb.AddThemeColorOverride("font_pressed_color", Gold);
        ApplyGameFont(cb, 16);
        return cb;
    }

    private static Button MakeActionButton(string text, Action action)
    {
        var btn = new Button();
        btn.Text = text;
        btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        btn.AddThemeColorOverride("font_color", Cream);
        btn.AddThemeColorOverride("font_hover_color", Gold);
        btn.AddThemeColorOverride("font_pressed_color", Gold);
        ApplyGameFont(btn, 17);

        var normal = new StyleBoxFlat();
        normal.BgColor = new Color(0.08f, 0.06f, 0.05f, 0.6f);
        normal.SetBorderWidthAll(1);
        normal.BorderColor = new Color(0.4f, 0.35f, 0.25f, 0.5f);
        normal.SetCornerRadiusAll(3);
        normal.ContentMarginLeft = 8;
        normal.ContentMarginRight = 8;
        normal.ContentMarginTop = 6;
        normal.ContentMarginBottom = 6;
        btn.AddThemeStyleboxOverride("normal", normal);

        var hover = (StyleBoxFlat)normal.Duplicate();
        hover.BgColor = new Color(0.12f, 0.09f, 0.07f, 0.7f);
        hover.BorderColor = new Color(0.6f, 0.5f, 0.3f, 0.6f);
        btn.AddThemeStyleboxOverride("hover", hover);

        var pressed = (StyleBoxFlat)normal.Duplicate();
        pressed.BgColor = new Color(0.05f, 0.04f, 0.03f, 0.8f);
        btn.AddThemeStyleboxOverride("pressed", pressed);

        btn.Pressed += action;
        return btn;
    }

    internal static void AddSeparator(VBoxContainer vbox)
    {
        var sep = new HSeparator();
        sep.AddThemeConstantOverride("separation", 4);
        sep.AddThemeStyleboxOverride("separator", new StyleBoxLine
        {
            Color = new Color(0.5f, 0.45f, 0.35f, 0.3f),
            Thickness = 1,
        });
        vbox.AddChild(sep);
    }

    // Canonical character order matching the character select screen (from ModelDb.AllCharacters).
    private static readonly string[] CharacterOrder =
    [
        "CHARACTER.IRONCLAD",
        "CHARACTER.SILENT",
        "CHARACTER.REGENT",
        "CHARACTER.NECROBINDER",
        "CHARACTER.DEFECT",
    ];

    /// <summary>
    /// Returns characters present in the DB, sorted in canonical game order.
    /// Characters not in the canonical list are appended alphabetically at the end.
    /// </summary>
    private static List<string> GetOrderedCharacters(StatsDb db)
    {
        var present = new HashSet<string>(StatsAggregator.GetDistinctCharacters(db));
        var ordered = new List<string>();
        foreach (var c in CharacterOrder)
        {
            if (present.Remove(c))
                ordered.Add(c);
        }
        // Append any unknown characters alphabetically.
        var remaining = present.ToList();
        remaining.Sort(StringComparer.Ordinal);
        ordered.AddRange(remaining);
        return ordered;
    }

    private static string FormatCharName(string character)
    {
        if (string.IsNullOrEmpty(character)) return "";
        var entry = character.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase)
            ? character["CHARACTER.".Length..]
            : character;
        if (entry.Length == 0) return character;
        var fallback = char.ToUpper(entry[0]) + entry[1..].ToLower();
        return L.CharacterName(entry, fallback);
    }

    /// <summary>
    /// Colors a control's text green when its value differs from the default, or
    /// restores the default light text color when it matches.
    /// Works with SpinBox (targets the inner LineEdit) and OptionButton.
    /// </summary>
    private static void HighlightIfNonDefault(Control control, bool isNonDefault)
    {
        var color = isNonDefault ? ActiveFilterColor : Cream;

        if (control is SpinBox spinBox)
        {
            spinBox.GetLineEdit()?.AddThemeColorOverride("font_color", color);
        }
        else if (control is OptionButton optBtn)
        {
            optBtn.AddThemeColorOverride("font_color", color);
            optBtn.AddThemeColorOverride("font_hover_color", color);
            optBtn.AddThemeColorOverride("font_focus_color", color);
        }
    }

    private static void HighlightStepperIfNonDefault(AscensionStepper stepper, bool isNonDefault)
    {
        var color = isNonDefault ? ActiveFilterColor : Cream;
        stepper.DisplayLabel.AddThemeColorOverride("font_color", color);
    }

    private static void SelectClassFilter(OptionButton button, List<string> values, string current)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], current, StringComparison.OrdinalIgnoreCase))
            {
                button.Selected = i;
                return;
            }
        }
        button.Selected = 0;
    }

    private static void SelectOptionByText(OptionButton button, string text)
    {
        if (string.IsNullOrEmpty(text)) { button.Selected = 0; return; }
        for (int i = 0; i < button.ItemCount; i++)
        {
            if (string.Equals(button.GetItemText(i), text, StringComparison.OrdinalIgnoreCase))
            {
                button.Selected = i;
                return;
            }
        }
        button.Selected = 0;
    }
}
