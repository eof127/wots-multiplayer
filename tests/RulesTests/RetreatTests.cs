using System;
using WotSMultiplayer.Features.MpBattle;

/// <summary>Tests for the D48 retreat rule.</summary>
internal static class RetreatTests
{
    private static RetreatSide Side(bool dis, float elapsed, float needed, float doneAt = -1f, float hit = -1f, bool allowed = false) =>
        new RetreatSide { Disengaging = dis, Elapsed = elapsed, Needed = needed, CountdownDoneAt = doneAt, LastHitAt = hit, AllowedByOther = allowed };

    public static void Run(Action<bool, string> check)
    {
        RetreatSide idle = Side(false, 0f, 0f);

        // agreement
        check(RetreatRules.Agreement(Side(true, 0f, 175f), Side(true, 0f, 175f)), "retreat: both disengaging = agreement, at once");
        check(RetreatRules.Agreement(Side(true, 5f, 175f, allowed: true), idle), "retreat: the other player's Allow = agreement");
        check(!RetreatRules.Agreement(Side(true, 5f, 175f), idle), "retreat: one side alone is no agreement");
        check(!RetreatRules.Agreement(idle, Side(true, 5f, 175f)), "retreat: not disengaging → nothing to agree to");

        // countdown first
        check(!RetreatRules.CanForce(Side(true, 100f, 175f), 1000f), "retreat: countdown still running → can't force");
        check(!RetreatRules.CanRetreat(Side(true, 100f, 175f), idle, 1000f), "retreat: countdown running, no agreement → refused");

        // then 30 s without new hits (game time)
        RetreatSide done = Side(true, 175f, 175f, doneAt: 500f);
        check(Math.Abs(RetreatRules.QuietLeft(done, 510f) - 20f) < 1e-3, "retreat: quiet check starts when the countdown ends (20 s left after 10)");
        check(!RetreatRules.CanForce(done, 529f), "retreat: 29 s quiet → not yet");
        check(RetreatRules.CanForce(done, 530f), "retreat: 30 s quiet → may force");
        RetreatSide hitLate = Side(true, 175f, 175f, doneAt: 500f, hit: 520f);
        check(!RetreatRules.CanForce(hitLate, 545f) && RetreatRules.CanForce(hitLate, 550f), "retreat: a new hit restarts the 30 s");
        RetreatSide hitEarly = Side(true, 175f, 175f, doneAt: 500f, hit: 400f);
        check(RetreatRules.CanForce(hitEarly, 530f), "retreat: hits before the countdown ended don't count against the 30 s");
        check(RetreatRules.QuietLeft(Side(true, 50f, 175f), 1000f) == RetreatRules.QuietSeconds, "retreat: quiet time full until the countdown is done");

        // user's case: the 30 s had passed (bar green), then a hit before the player confirmed → refused, timer restarts
        RetreatSide greenThenHit = Side(true, 175f, 175f, doneAt: 500f, hit: 535f);
        check(RetreatRules.CanForce(Side(true, 175f, 175f, doneAt: 500f), 531f), "retreat: green at 531 s without hits");
        check(!RetreatRules.CanForce(greenThenHit, 536f), "retreat: hit at 535 s after going green → the retreat is refused");
        check(Math.Abs(RetreatRules.QuietLeft(greenThenHit, 536f) - 29f) < 1e-3, "retreat: ... and the 30 s start again from the hit");
        check(RetreatRules.CanForce(greenThenHit, 565f), "retreat: 30 s after that hit it is allowed again");
        check(RetreatRules.HitRestarted(greenThenHit) && !RetreatRules.HitRestarted(Side(true, 175f, 175f, doneAt: 500f, hit: 400f)),
              "retreat: a hit after the countdown counts as a restart (for the message)");

        // zero countdown (nobody detected): the 30 s still apply
        RetreatSide zero = Side(true, 0f, 0f, doneAt: 10f);
        check(!RetreatRules.CanForce(zero, 20f) && RetreatRules.CanForce(zero, 40f), "retreat: 0 s countdown → 30 s quiet still needed");

        // not disengaging
        check(!RetreatRules.CanRetreat(idle, idle, 1e6f), "retreat: not disengaging → never");

        // the other side has nothing left (two-PC test 2026-11-25: P2's task force was sunk) → leave at once
        RetreatSide wiped = Side(false, 0f, 0f); wiped.NoUnits = true;
        check(RetreatRules.CanRetreat(idle, wiped, 0f), "retreat: other side has no units left → leave at once, no countdown");
        check(!RetreatRules.CanRetreat(wiped, Side(false, 0f, 0f), 0f), "retreat: having no units yourself doesn't let you force it");
    }
}
