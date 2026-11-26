using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;

namespace WotSMultiplayer.Features.TimeControl
{
    // Player speed inputs become proposals (D15-D17) while ModState.SpeedRulesActive.
    // Vanilla behaviour is untouched otherwise (every prefix returns true).

    /// <summary>Campaign speed keys (, . /) and speed buttons. All callers are player input.</summary>
    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.SetTimeCompression))]
    internal static class CampaignSetTimeCompressionPatch
    {
        private static bool Prefix(int amount)
        {
            if (!ModState.SpeedRulesActive || TimeControlDriver.Phase != GamePhase.Campaign) return true;
            if (amount == -1) TimeControlDriver.LocalSlower();
            else TimeControlDriver.LocalFaster(toMax: amount == 100);
            return false;
        }
    }

    /// <summary>Campaign pause key (TimePause). The pause *button* is rewired in TimeControlDriver.</summary>
    [HarmonyPatch(typeof(CampaignInterface), "CheckCampaignInput")]
    internal static class CampaignPauseKeyPatch
    {
        private static bool Prefix()
        {
            if (!ModState.SpeedRulesActive || TimeControlDriver.Phase != GamePhase.Campaign) return true;
            if (!Input.anyKeyDown || InputManager.instance == null) return true;
            if (!InputManager.instance.GetButtonDown("TimePause")) return true;
            TimeControlDriver.LocalPauseToggle();
            return false; // TimePause is the first branch of the vanilla if/else chain; nothing else is skipped
        }
    }

    /// <summary>Battle compression toggle (F9 key, speed button) and the vanilla air-attack auto slow-down.</summary>
    [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.SetTimeCompression))]
    internal static class CombatSetTimeCompressionPatch
    {
        private static bool Prefix()
        {
            if (!ModState.SpeedRulesActive || TimeControlDriver.Phase != GamePhase.Battle) return true;
            if (TimeControlDriver.InAirAttackScope)
            {
                TimeControlDriver.BattleRules.SystemSetIndex(0, "Aircraft attacking");
                return false;
            }
            if (Time.timeScale > 1f) TimeControlDriver.LocalSlower();
            else TimeControlDriver.LocalFaster();
            return false;
        }
    }

    /// <summary>Battle pause (Space key, pause button, optional torpedo-detect pause) → pause proposal.</summary>
    [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.PauseCombat))]
    internal static class CombatPausePatch
    {
        private static bool Prefix()
        {
            if (!ModState.SpeedRulesActive || TimeControlDriver.Phase != GamePhase.Battle) return true;
            TimeControlDriver.LocalPauseToggle();
            return false;
        }
    }

    /// <summary>Marks UnitAIAir.AttackUnit so its call to SetTimeCompression counts as a system slow-down.</summary>
    [HarmonyPatch(typeof(UnitAIAir), nameof(UnitAIAir.AttackUnit))]
    internal static class AirAttackScopePatch
    {
        private static void Prefix() => TimeControlDriver.InAirAttackScope = true;
        private static void Finalizer() => TimeControlDriver.InAirAttackScope = false;
    }

    /// <summary>
    /// StartCombat returns early when deployment is illegal (ship on land / too close); the Start button then stays
    /// visible. On success vanilla hides it, sets strategicCombatMode = false and SetTimeRate(1).
    /// </summary>
    [HarmonyPatch(typeof(EngagementManager), nameof(EngagementManager.StartCombat))]
    internal static class BattleStartPatch
    {
        private static void Postfix(EngagementManager __instance)
        {
            if (__instance.startCombatButton != null && __instance.startCombatButton.activeSelf) return; // failed legality check
            TimeControlDriver.OnBattleStarted();
        }
    }

    /// <summary>
    /// A campaign battle begins here (encounter "Begin", or loading a save made mid-battle). The campaign UI stays
    /// enabled during battles, so this flag is what tells the driver we are no longer on the map.
    /// </summary>
    [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.GenerateCampaignEngagement))]
    internal static class CampaignBattleBeginPatch
    {
        private static void Prefix() => TimeControlDriver.InCampaignBattle = true;
    }

    /// <summary>QuitMission(confirm: false) ends a mission (also called by ActionReport.CloseActionReport).</summary>
    [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.QuitMission))]
    internal static class QuitMissionPatch
    {
        private static void Postfix(bool confirm)
        {
            if (!confirm) TimeControlDriver.InCampaignBattle = false;
        }
    }

    [HarmonyPatch(typeof(EngagementManager), nameof(EngagementManager.ClearEngagementScene))]
    internal static class BattleEndPatch
    {
        private static void Postfix() => TimeControlDriver.OnBattleEnded();
    }
}
