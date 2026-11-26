namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>A player's answer to an encounter (on the wire: append only).</summary>
    internal enum EncounterChoice : byte { None = 0, Engage = 1, Ignore = 2 }

    /// <summary>How an encounter ended (on the wire: append only).</summary>
    internal enum EncounterOutcome : byte { Pending = 0, Ignored = 1, Fight = 2, Cancelled = 3 }

    /// <summary>What the ignore rule needs to know about one side's force.</summary>
    internal struct EncounterSide
    {
        public bool IsAir;       // an air group (MobileMapObject.type == 1)
        public bool LeadIsScout; // first unit's subtype is Scout
        public bool LeadIsAir;   // first unit's type is Air
        public int Count;        // units in the group
        public bool SubOnly;     // only submarines
    }

    /// <summary>
    /// The two-player encounter rules (D19, D37, D39), game-independent so they can be unit-tested.
    /// </summary>
    internal static class EncounterRules
    {
        public const float DecisionSeconds = 15f; // D37

        /// <summary>
        /// Vanilla's "can ignore" test (CampaignManager.CheckMobileEncounters), seen from <paramref name="me"/>:
        /// my planes over enemy ships, scout vs scout, I'm submarines only, or the enemy is a scout seen by my
        /// ships / a single plane.
        /// </summary>
        public static bool CanIgnoreFrom(EncounterSide me, EncounterSide them)
        {
            if (me.IsAir && !them.IsAir) return true;
            if (me.LeadIsScout && them.LeadIsScout) return true;
            if (me.SubOnly) return true;
            if ((!me.IsAir && them.LeadIsScout) || (them.LeadIsAir && them.Count == 1)) return true;
            return false;
        }

        /// <summary>(D39, superseded by D49) Would vanilla let either side ignore it from its own point of view?</summary>
        public static bool VanillaIgnorable(EncounterSide a, EncounterSide b) => CanIgnoreFrom(a, b) || CanIgnoreFrom(b, a);

        /// <summary>
        /// D49 (user, 2026-11-24): in MP every encounter between the players can be ignored — it's skipped when both
        /// choose Ignore, and either player can still force the battle with Engage. (Vanilla's single-player rule only
        /// let the player ignore some encounters; that's <see cref="VanillaIgnorable"/>, kept for the log.)
        /// </summary>
        public static bool Ignorable(EncounterSide a, EncounterSide b) => true;

        /// <summary>
        /// D19 + D37. Ignorable: anyone choosing Engage → Fight at once; both Ignore → Ignored; a missing answer at the
        /// deadline counts as Ignore. Not ignorable: Engage only means "ready"; it is fought when both are ready or at
        /// the deadline.
        /// </summary>
        public static EncounterOutcome Resolve(bool ignorable, EncounterChoice host, EncounterChoice guest, bool deadlinePassed)
        {
            if (!ignorable)
            {
                if (deadlinePassed || (host == EncounterChoice.Engage && guest == EncounterChoice.Engage)) return EncounterOutcome.Fight;
                return EncounterOutcome.Pending;
            }
            if (host == EncounterChoice.Engage || guest == EncounterChoice.Engage) return EncounterOutcome.Fight;
            if (deadlinePassed) return EncounterOutcome.Ignored; // remaining "None" answers count as Ignore
            if (host == EncounterChoice.Ignore && guest == EncounterChoice.Ignore) return EncounterOutcome.Ignored;
            return EncounterOutcome.Pending;
        }

        /// <summary>Fraction of the decision time left (1 → 0) for the time bar.</summary>
        public static float TimeLeftFraction(float elapsed) =>
            elapsed <= 0f ? 1f : elapsed >= DecisionSeconds ? 0f : 1f - elapsed / DecisionSeconds;
    }
}
