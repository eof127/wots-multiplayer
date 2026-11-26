namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>One side's retreat state, as the host tracks it (game seconds).</summary>
    internal struct RetreatSide
    {
        public bool Disengaging;      // the vanilla disengage is on (fire ceased)
        public float Elapsed, Needed; // vanilla disengage countdown
        public float CountdownDoneAt; // game time the countdown reached Needed (-1 = not yet)
        public float LastHitAt;       // game time of the last new hit from the opponent (-1 = none)
        public bool AllowedByOther;   // the other player pressed Allow
        public bool NoUnits;          // this side has nothing left in the battle (all sunk, shot down or gone)
    }

    /// <summary>
    /// D48 retreat rule, game-independent so it can be unit-tested:
    /// agreement (both disengaging, or the other player allows) ends the battle at once; otherwise the vanilla countdown,
    /// then 30 s of game time without a new hit from the opponent, then the retreating player may force the retreat.
    /// </summary>
    internal static class RetreatRules
    {
        public const float QuietSeconds = 30f;

        public static bool CountdownDone(RetreatSide s) => s.Disengaging && s.Elapsed >= s.Needed;

        /// <summary>Both want out: the retreating side is disengaging and the other side is too, or allowed it.</summary>
        public static bool Agreement(RetreatSide me, RetreatSide other) => me.Disengaging && (other.Disengaging || me.AllowedByOther);

        /// <summary>Seconds of the no-new-damage check still to go (QuietSeconds before it starts, 0 when done).</summary>
        public static float QuietLeft(RetreatSide s, float now)
        {
            if (!s.Disengaging || s.CountdownDoneAt < 0f) return QuietSeconds;
            float since = now - (s.LastHitAt > s.CountdownDoneAt ? s.LastHitAt : s.CountdownDoneAt);
            float left = QuietSeconds - since;
            return left < 0f ? 0f : left;
        }

        /// <summary>A new hit came after the countdown ended, so the 30 s were restarted (for the refusal message).</summary>
        public static bool HitRestarted(RetreatSide s) => s.CountdownDoneAt >= 0f && s.LastHitAt > s.CountdownDoneAt;

        /// <summary>
        /// The retreating player may force the retreat (countdown done + 30 s without new hits). Checked again at the moment
        /// the retreat is confirmed: a hit after the bar went green restarts the 30 s and the retreat is refused.
        /// </summary>
        public static bool CanForce(RetreatSide s, float now) => CountdownDone(s) && s.CountdownDoneAt >= 0f && QuietLeft(s, now) <= 0f;

        /// <summary>
        /// Leaving is allowed by agreement, by the countdown + 30 s rule, or when the other side has nothing left in the
        /// battle (as in the original game, where LEAVE needs no countdown once no enemies remain).
        /// </summary>
        public static bool CanRetreat(RetreatSide me, RetreatSide other, float now) => other.NoUnits || Agreement(me, other) || CanForce(me, now);
    }
}
