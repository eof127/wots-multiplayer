namespace WotSMultiplayer.Core
{
    /// <summary>
    /// Global switches that decide whether a feature patch is active.
    /// Rule (CLAUDE.md): patches must no-op in single-player unless the user enabled them.
    /// </summary>
    internal static class ModState
    {
        /// <summary>True while a multiplayer session is running. Set by the session layer (M2+).</summary>
        internal static bool MultiplayerActive = false;

        /// <summary>Mandatory auto-resolve (decision D6): always on in MP, opt-in for single-player.</summary>
        internal static bool AutoResolveActive =>
            MultiplayerActive || Plugin.AutoResolveInSinglePlayer.Value;

        /// <summary>
        /// MP speed rules (D15–D17). For now only the single-player test mode: in a real MP campaign the votes must be
        /// networked (host-owned SpeedRules) — that is M5. Until then MP campaigns stay frozen (MpCampaign).
        /// </summary>
        internal static bool SpeedRulesActive =>
            MultiplayerActive
                ? Features.MpCampaign.MpCampaign.Phase == Features.MpCampaign.MpPhase.Running
                : Plugin.SpeedVoteTestMode.Value;
    }
}
