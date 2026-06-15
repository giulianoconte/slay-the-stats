namespace SlayTheStats.Community;

/// <summary>
/// Pure, engine-free state machine for the Spire Codex onboarding consent popup
/// (#35). Decides whether the first-run consent modal should appear on a given
/// launch and computes the persisted-state transitions for each user action. The
/// Godot UI (<c>CommunityConsentPrompt</c>) is a thin shell over this, so the full
/// machine is unit-testable without the engine.
///
/// <para>State diagram (design doc Area 6 — "Onboarding consent popup"):</para>
/// <code>
///   Unset ──Enable───▶ Resolved
///         ──Decline──▶ Resolved
///         ──close/Esc▶ Dismissed ──[ReshowAfterLaunches]──▶ re-show
///                                    ├─Enable/Decline▶ Resolved
///                                    └─close/Esc──────▶ Resolved
///   (any explicit Community settings toggle ⇒ Resolved; cancels a pending re-prompt)
/// </code>
/// Enable/Decline are terminal; only a decide-later dismissal parks a single
/// re-prompt; the re-prompt's own dismissal is terminal too.
/// </summary>
public static class ConsentFlow
{
    /// <summary>How many launches must elapse after a decide-later dismissal before
    /// the popup re-appears exactly once.</summary>
    public const int ReshowAfterLaunches = 10;

    public enum State
    {
        /// <summary>Never shown / not yet decided — show on first menu of this launch.</summary>
        Unset,
        /// <summary>Shown once and decide-later dismissed — a single re-prompt is pending.</summary>
        Dismissed,
        /// <summary>Terminal: Enable, Decline, the re-prompt's dismissal, or an explicit
        /// Community settings toggle. The popup never appears again.</summary>
        Resolved,
    }

    /// <summary>Per-launch counter bookkeeping. While a re-prompt is pending
    /// (<see cref="State.Dismissed"/>) each launch ticks the counter up by one;
    /// in any other state the counter is left alone. Pure — returns the new value;
    /// the caller persists it. Call once per launch, before <see cref="ShouldShow"/>.</summary>
    public static int TickLaunch(State state, int dismissedLaunches)
        => state == State.Dismissed ? dismissedLaunches + 1 : dismissedLaunches;

    /// <summary>Whether to show the popup this launch, given the already-ticked
    /// counter and whether community stats are already on. We never nag a player who
    /// has already engaged (via Settings or a prior Enable): <paramref name="communityEnabled"/>
    /// short-circuits to no-show — the settings-toggle hook also marks such a flow
    /// <see cref="State.Resolved"/>, this is the belt-and-suspenders for a config edited on disk.</summary>
    public static bool ShouldShow(State state, int dismissedLaunches, bool communityEnabled)
    {
        if (state == State.Resolved) return false;
        if (communityEnabled) return false;
        return state switch
        {
            State.Unset     => true,
            State.Dismissed => dismissedLaunches >= ReshowAfterLaunches,
            _               => false,
        };
    }

    /// <summary>Transition for the [Enable] and [Decline] buttons — both terminal.</summary>
    public static State OnDecided() => State.Resolved;

    /// <summary>Transition for close/Esc (decide-later). The first dismissal from
    /// <see cref="State.Unset"/> parks a single re-prompt (Dismissed, counter reset
    /// to 0); a dismissal of the re-prompt itself is terminal.</summary>
    public static (State state, int dismissedLaunches) OnDismiss(State state)
        => state == State.Unset ? (State.Dismissed, 0) : (State.Resolved, 0);

    /// <summary>Transition for an explicit Community settings toggle — terminal,
    /// cancels any pending re-prompt. Idempotent once already <see cref="State.Resolved"/>.</summary>
    public static State OnSettingsToggle() => State.Resolved;
}
