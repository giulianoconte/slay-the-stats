using SlayTheStats.Community;
using Xunit;
using static SlayTheStats.Community.ConsentFlow;

namespace SlayTheStats.Tests;

/// <summary>
/// Unit coverage for the Spire Codex onboarding consent state machine (#35). The
/// Godot popup (CommunityConsentPrompt) is a thin shell over these pure functions,
/// so the full transition table is exercised here without the engine.
/// </summary>
public class ConsentFlowTests
{
    // ── ShouldShow ─────────────────────────────────────────────────────────

    [Fact]
    public void FirstLaunch_Unset_NotEnabled_Shows()
        => Assert.True(ShouldShow(State.Unset, 0, communityEnabled: false));

    [Fact]
    public void Resolved_NeverShows()
    {
        Assert.False(ShouldShow(State.Resolved, 0, false));
        Assert.False(ShouldShow(State.Resolved, ReshowAfterLaunches, false));
    }

    [Fact]
    public void AlreadyEnabled_NeverShows_EvenWhenUnset()
        => Assert.False(ShouldShow(State.Unset, 0, communityEnabled: true));

    [Fact]
    public void Dismissed_BelowThreshold_DoesNotShow()
        => Assert.False(ShouldShow(State.Dismissed, ReshowAfterLaunches - 1, false));

    [Fact]
    public void Dismissed_AtThreshold_Shows()
        => Assert.True(ShouldShow(State.Dismissed, ReshowAfterLaunches, false));

    [Fact]
    public void Dismissed_AtThreshold_ButEnabled_DoesNotShow()
        => Assert.False(ShouldShow(State.Dismissed, ReshowAfterLaunches, communityEnabled: true));

    // ── TickLaunch ─────────────────────────────────────────────────────────

    [Fact]
    public void TickLaunch_OnlyIncrementsWhileDismissed()
    {
        Assert.Equal(0, TickLaunch(State.Unset, 0));
        Assert.Equal(5, TickLaunch(State.Resolved, 5));
        Assert.Equal(4, TickLaunch(State.Dismissed, 3));
    }

    // ── Transitions ────────────────────────────────────────────────────────

    [Fact]
    public void EnableOrDecline_IsTerminal()
        => Assert.Equal(State.Resolved, OnDecided());

    [Fact]
    public void Dismiss_FromUnset_ParksSingleReprompt()
    {
        var (state, launches) = OnDismiss(State.Unset);
        Assert.Equal(State.Dismissed, state);
        Assert.Equal(0, launches);
    }

    [Fact]
    public void Dismiss_FromReprompt_IsTerminal()
    {
        var (state, _) = OnDismiss(State.Dismissed);
        Assert.Equal(State.Resolved, state);
    }

    [Fact]
    public void SettingsToggle_IsTerminal()
        => Assert.Equal(State.Resolved, OnSettingsToggle());

    // ── End-to-end launch sequences ────────────────────────────────────────

    [Fact]
    public void RepromptFiresExactlyTenLaunchesAfterDismiss()
    {
        // Launch 1: Unset → shown → decide-later dismiss.
        var state = State.Unset;
        int launches = 0;
        launches = TickLaunch(state, launches);                 // Unset: no tick
        Assert.True(ShouldShow(state, launches, false));
        (state, launches) = OnDismiss(state);                   // → Dismissed, 0

        // Launches 2..10: counter climbs 1..9, never re-shows.
        for (int i = 0; i < ReshowAfterLaunches - 1; i++)
        {
            launches = TickLaunch(state, launches);
            Assert.False(ShouldShow(state, launches, false));
        }

        // Launch 11: counter hits the threshold → re-prompt.
        launches = TickLaunch(state, launches);
        Assert.Equal(ReshowAfterLaunches, launches);
        Assert.True(ShouldShow(state, launches, false));

        // Decline the re-prompt → terminal, never shows again.
        state = OnDecided();
        launches = TickLaunch(state, launches);
        Assert.False(ShouldShow(state, launches, false));
    }

    [Fact]
    public void SettingsToggleDuringPendingReprompt_CancelsIt()
    {
        var state = State.Dismissed;
        int launches = 5;

        // User flips the Community setting before the re-prompt would fire.
        state = OnSettingsToggle();
        launches = TickLaunch(state, launches);                 // Resolved: no tick
        Assert.Equal(5, launches);
        Assert.False(ShouldShow(state, launches, false));
        Assert.False(ShouldShow(state, ReshowAfterLaunches, false));
    }
}
