using System;
using WotSMultiplayer.Features.MpCampaign;

/// <summary>Tests for the two-player encounter rules (D19, D37, D39).</summary>
internal static class EncounterTests
{
    private static EncounterSide Fleet(int n = 3) => new EncounterSide { Count = n };
    private static EncounterSide Subs() => new EncounterSide { Count = 2, SubOnly = true };
    private static EncounterSide Strike(int n = 8) => new EncounterSide { IsAir = true, LeadIsAir = true, Count = n };
    private static EncounterSide Scout() => new EncounterSide { IsAir = true, LeadIsAir = true, LeadIsScout = true, Count = 1 };

    public static void Run(Action<bool, string> check)
    {
        const EncounterChoice N = EncounterChoice.None, E = EncounterChoice.Engage, I = EncounterChoice.Ignore;

        // D49: in MP every encounter can be ignored by both players
        check(EncounterRules.Ignorable(Fleet(), Fleet()) && EncounterRules.Ignorable(Strike(), Strike()),
              "encounter: D49 surface fleets / strike groups meeting are ignorable in MP");
        // vanilla's ignorability (either side's own view; D39, now only logged)
        check(!EncounterRules.VanillaIgnorable(Fleet(), Fleet()), "encounter: vanilla: surface fleets meeting can't be ignored");
        check(EncounterRules.VanillaIgnorable(Strike(), Fleet()) && EncounterRules.VanillaIgnorable(Fleet(), Strike()), "encounter: vanilla: planes over ships ignorable (either order)");
        check(EncounterRules.VanillaIgnorable(Subs(), Fleet()) && EncounterRules.VanillaIgnorable(Fleet(), Subs()), "encounter: vanilla: submarine-only force ignorable");
        check(EncounterRules.VanillaIgnorable(Scout(), Fleet()) && EncounterRules.VanillaIgnorable(Scout(), Strike()), "encounter: vanilla: lone scout ignorable");
        check(!EncounterRules.VanillaIgnorable(Strike(), Strike()), "encounter: vanilla: two strike groups can't be ignored");
        check(EncounterRules.VanillaIgnorable(Strike(1), Strike()), "encounter: vanilla: a single plane is ignorable");
        check(EncounterRules.CanIgnoreFrom(Strike(), Fleet()) && !EncounterRules.CanIgnoreFrom(Fleet(), Strike()),
              "encounter: vanilla view is one-sided (attacked ships can't ignore)");

        // resolution, ignorable encounter
        check(EncounterRules.Resolve(true, N, N, false) == EncounterOutcome.Pending, "encounter: nobody chose yet → pending");
        check(EncounterRules.Resolve(true, I, N, false) == EncounterOutcome.Pending, "encounter: one Ignore → wait for the other");
        check(EncounterRules.Resolve(true, I, I, false) == EncounterOutcome.Ignored, "encounter: both Ignore → ignored");
        check(EncounterRules.Resolve(true, E, N, false) == EncounterOutcome.Fight, "encounter: host Engage → fight at once");
        check(EncounterRules.Resolve(true, I, E, false) == EncounterOutcome.Fight, "encounter: guest Engage → fight even if host ignores");
        check(EncounterRules.Resolve(true, I, N, true) == EncounterOutcome.Ignored, "encounter: timeout counts as Ignore");
        check(EncounterRules.Resolve(true, N, N, true) == EncounterOutcome.Ignored, "encounter: nobody answered → ignored");

        // resolution, unavoidable encounter
        check(EncounterRules.Resolve(false, E, N, false) == EncounterOutcome.Pending, "encounter: unavoidable waits for both to be ready");
        check(EncounterRules.Resolve(false, E, E, false) == EncounterOutcome.Fight, "encounter: unavoidable, both ready → fight");
        check(EncounterRules.Resolve(false, N, N, true) == EncounterOutcome.Fight, "encounter: unavoidable, timeout → fight");
        check(EncounterRules.Resolve(false, I, I, false) != EncounterOutcome.Ignored, "encounter: Ignore has no effect when unavoidable");

        // time bar
        check(EncounterRules.TimeLeftFraction(0f) == 1f && EncounterRules.TimeLeftFraction(15f) == 0f && EncounterRules.TimeLeftFraction(20f) == 0f,
              "encounter: time bar full at start, empty at 15 s");
        check(Math.Abs(EncounterRules.TimeLeftFraction(7.5f) - 0.5f) < 1e-4, "encounter: time bar half at 7.5 s");
    }
}
