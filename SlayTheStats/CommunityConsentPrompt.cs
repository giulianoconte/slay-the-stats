using System;
using BaseLib.Config;
using Godot;
using SlayTheStats.Community;

namespace SlayTheStats;

/// <summary>
/// First-run onboarding consent modal for the Spire Codex community-stats
/// integration (#35). A centered popup over the main menu that promotes
/// <em>Read + Share</em> (Enable) while keeping the privacy-preserving read-only
/// path discoverable, with Decline and a decide-later (close/Esc) path. The
/// when-to-show / what-each-action-does decisions live in the engine-free
/// <see cref="ConsentFlow"/> state machine; this file is the Godot shell plus the
/// two integration hooks (per-launch <see cref="MaybeShow"/> from the menu-ready
/// patch, and <see cref="OnConfigChanged"/> from the config-changed handler).
/// </summary>
internal static partial class CommunityConsentPrompt
{
    private static bool _attemptedThisLaunch;
    private static CanvasLayer? _layer;
    private static ConsentFlow.State _stateAtShow;

    /// <summary>Last Community value we observed, so a config-changed event can tell
    /// an explicit settings toggle (the only non-popup way it moves) from noise.</summary>
    private static SlayTheStatsConfig.CommunityMode _lastKnownCommunity;

    /// <summary>Seed the settings-toggle watcher with the boot-time Community value.
    /// Called once from <c>MainFile.Initialize</c> after the config has loaded.</summary>
    internal static void InitSettingsWatch()
        => _lastKnownCommunity = SlayTheStatsConfig.Community;

    /// <summary>
    /// Attempt to show the consent popup, at most once per launch. Called from
    /// <c>MainMenuReadyPatch</c> (fires on boot and every return to the menu); the
    /// once-per-launch guard means only the first menu of a session can trigger it.
    /// Ticks the re-prompt launch counter and defers the show/no-show decision to
    /// <see cref="ConsentFlow"/>.
    /// </summary>
    internal static void MaybeShow()
    {
        if (_attemptedThisLaunch) return;
        _attemptedThisLaunch = true;

        var state = SlayTheStatsConfig.CommunityConsentState;

        // Per-launch counter tick (no-op unless a re-prompt is pending). Persist only
        // when it actually moved.
        int launches = SlayTheStatsConfig.CommunityConsentDismissedLaunches;
        int ticked = ConsentFlow.TickLaunch(state, launches);
        if (ticked != launches)
        {
            SlayTheStatsConfig.CommunityConsentDismissedLaunches = ticked;
            Persist();
        }

        bool enabled = SlayTheStatsConfig.Community != SlayTheStatsConfig.CommunityMode.Off;
        if (!ConsentFlow.ShouldShow(state, ticked, enabled)) return;

        _stateAtShow = state;
        MainFile.Logger.Info($"Community consent popup shown ({(state == ConsentFlow.State.Dismissed ? "re-prompt" : "first run")}).");
        BuildPopup();
    }

    /// <summary>
    /// Config-changed hook. An explicit Community settings toggle (the only path that
    /// moves the value outside this popup) resolves the consent flow — the player has
    /// engaged deliberately, so any pending re-prompt is cancelled and an open popup is
    /// closed. No-op once already resolved.
    /// </summary>
    internal static void OnConfigChanged()
    {
        var now = SlayTheStatsConfig.Community;
        if (now == _lastKnownCommunity) return;
        _lastKnownCommunity = now;

        if (SlayTheStatsConfig.CommunityConsentState == ConsentFlow.State.Resolved) return;
        SlayTheStatsConfig.CommunityConsentState = ConsentFlow.OnSettingsToggle();
        Persist();
        Close();
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    private static void OnEnable()
    {
        SlayTheStatsConfig.Community = SlayTheStatsConfig.CommunityMode.ReadShare;
        _lastKnownCommunity = SlayTheStatsConfig.Community; // so OnConfigChanged doesn't re-handle
        SlayTheStatsConfig.CommunityConsentState = ConsentFlow.OnDecided();
        Persist();
        MainFile.Logger.Info("Community consent: Enabled (Read + Share) from onboarding popup.");
        // Setting the property in code doesn't raise BaseLib's ConfigChanged, so kick the
        // first fetch ourselves — otherwise baselines wouldn't appear until next launch.
        CommunityStats.MaybeRefresh();
        Close();
    }

    private static void OnDecline()
    {
        SlayTheStatsConfig.CommunityConsentState = ConsentFlow.OnDecided();
        Persist();
        MainFile.Logger.Info("Community consent: Declined from onboarding popup.");
        Close();
    }

    private static void OnDismiss()
    {
        var (state, launches) = ConsentFlow.OnDismiss(_stateAtShow);
        SlayTheStatsConfig.CommunityConsentState = state;
        SlayTheStatsConfig.CommunityConsentDismissedLaunches = launches;
        Persist();
        MainFile.Logger.Info($"Community consent: decide-later dismiss → {state}.");
        Close();
    }

    private static void Persist()
    {
        try { ModConfig.SaveDebounced<SlayTheStatsConfig>(); }
        catch (Exception e) { MainFile.Logger.Warn($"Community consent: config save failed: {e.Message}"); }
    }

    private static void Close()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer))
            _layer.QueueFree();
        _layer = null;
    }

    // ── UI ───────────────────────────────────────────────────────────────────

    private static void BuildPopup()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        if (tree?.Root == null) return;

        // Defer one frame so the menu has laid out before we overlay (matches the
        // first-run tutorial; avoids a one-frame mis-sized panel).
        tree.ProcessFrame += DeferredBuild;
        void DeferredBuild()
        {
            tree.ProcessFrame -= DeferredBuild;
            if (_layer != null && GodotObject.IsInstanceValid(_layer)) return; // already up

            var layer = new ConsentModalLayer { Name = "SlayTheStatsConsentLayer", Layer = 100 };
            layer.OnEscape = OnDismiss;

            // Full-screen click-catcher: a click outside the panel is decide-later.
            var dimmer = new ColorRect
            {
                Color = new Color(0, 0, 0, 0.6f),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            dimmer.AnchorRight = 1f;
            dimmer.AnchorBottom = 1f;
            dimmer.GuiInput += (InputEvent ev) =>
            {
                if (ev is InputEventMouseButton mb && mb.Pressed) OnDismiss();
            };
            layer.AddChild(dimmer);

            // Center the panel; Ignore so clicks outside the panel fall through to the
            // dimmer, while the panel's own Stop filter keeps button clicks contained.
            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.AnchorRight = 1f;
            center.AnchorBottom = 1f;
            layer.AddChild(center);

            var panel = new PanelContainer { Name = "SlayTheStatsConsentPanel" };
            panel.AddThemeStyleboxOverride("panel", CompendiumFilterPatch.BuildPanelStyle());
            panel.SelfModulate = new Color(0.60f, 0.68f, 0.88f, 1f);
            panel.MouseFilter = Control.MouseFilterEnum.Stop;
            center.AddChild(panel);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 22);
            margin.AddThemeConstantOverride("margin_right", 22);
            margin.AddThemeConstantOverride("margin_top", 18);
            margin.AddThemeConstantOverride("margin_bottom", 18);
            panel.AddChild(margin);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 12);
            margin.AddChild(vbox);

            var title = new Label { Text = L.T("community.consent.title") };
            title.AddThemeColorOverride("font_color", ThemeStyle.GoldColor);
            CompendiumFilterPatch.ApplyGameFont(title, ThemeStyle.TitlePrimary);
            vbox.AddChild(title);

            vbox.AddChild(MakeBody(L.T("community.consent.body")));

            var hint = new Label
            {
                Text = L.T("community.consent.readonly_hint"),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(BodyWidth, 0),
            };
            hint.AddThemeColorOverride("font_color", new Color(ThemeStyle.HintR, ThemeStyle.HintG, ThemeStyle.HintB, 1f));
            CompendiumFilterPatch.ApplyGameFont(hint, 15);
            vbox.AddChild(hint);

            var buttons = new HBoxContainer();
            buttons.AddThemeConstantOverride("separation", 12);
            buttons.AddChild(CompendiumFilterPatch.MakeActionButton(L.T("community.consent.enable"), OnEnable, primary: true));
            buttons.AddChild(CompendiumFilterPatch.MakeActionButton(L.T("community.consent.decline"), OnDecline));
            vbox.AddChild(buttons);

            var dismissHint = new Label
            {
                Text = L.T("community.consent.decide_later"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            dismissHint.AddThemeColorOverride("font_color", new Color(ThemeStyle.HintR, ThemeStyle.HintG, ThemeStyle.HintB, 1f));
            CompendiumFilterPatch.ApplyGameFont(dismissHint, ThemeStyle.HintSize);
            vbox.AddChild(dismissHint);

            tree.Root.AddChild(layer);
            _layer = layer;
        }
    }

    private const float BodyWidth = 560f;

    private static Label MakeBody(string text)
    {
        var body = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(BodyWidth, 0),
        };
        body.AddThemeColorOverride("font_color", ThemeStyle.CreamColor);
        CompendiumFilterPatch.ApplyGameFont(body, 18);
        return body;
    }

    /// <summary>CanvasLayer that consumes Escape as decide-later. A CanvasLayer is a
    /// Node, so it receives <c>_UnhandledKeyInput</c>; consuming the event keeps the
    /// main menu from also acting on Esc while the modal is up.</summary>
    private partial class ConsentModalLayer : CanvasLayer
    {
        public Action? OnEscape;

        public override void _Ready() => SetProcessUnhandledKeyInput(true);

        public override void _UnhandledKeyInput(InputEvent @event)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            {
                OnEscape?.Invoke();
                GetViewport().SetInputAsHandled();
            }
        }
    }
}
