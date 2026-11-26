using HarmonyLib;
using WotSMultiplayer.Core;

namespace WotSMultiplayer.Features.MpCampaign
{
    // In a multiplayer campaign the Enemy side is the other human, so the strategic AI must not act for it
    // (PLAN.md §3). These run on both machines while ModState.MultiplayerActive; single-player is untouched.
    // Entry points from docs/GAME_INTERNALS.md (CampaignAI list).

    [HarmonyPatch]
    internal static class CampaignAiOffPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.CreateStandingMissions))]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.MakeStrategicDecision))]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.CheckRecreateMissions))]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.CheckStrategicBombing))]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.CheckAirfieldLaunchAirstrikeOnMobile))]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.CheckCarrierLaunchAirstrikeOnLocation))]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.OnPlayerSpotted))]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.CheckUnsafeLocations))]
        [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.CheckAISeaIntercept))]
        private static bool SkipVoid() => !ModState.MultiplayerActive;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.CheckCarrierLaunchAirstrikeOnMobile),
            new[] { typeof(MobileMapObject), typeof(MobileMapObject), typeof(bool), typeof(bool), typeof(bool) })]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.CheckCarrierLaunchAirstrikeOnMobile),
            new[] { typeof(string), typeof(MobileMapObject), typeof(MobileMapObject), typeof(bool), typeof(bool), typeof(bool) })]
        private static bool SkipCarrierStrike() => !ModState.MultiplayerActive;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.CheckAirfieldLaunchFighterAirSupport))]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.OnRequestAirCover))]
        private static bool SkipBool(ref bool __result)
        {
            if (!ModState.MultiplayerActive) return true;
            __result = false;
            return false;
        }
    }
}
