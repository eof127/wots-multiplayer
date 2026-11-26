using System.Collections.Generic;
using HarmonyLib;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// In vanilla the Enemy side's economy is AI-style (GAME_INTERNALS "Economy asymmetry"):
    ///  - weekly CP is paid via commandPointSpent[1] -= income, and only while in "deficit" (enemyWeeklyCPDeficitOnly),
    ///    with a deficit test that even uses the player's numbers;
    ///  - repaired ships refund CP only to the player.
    /// In MP faction 1 is a human, so the host pays its weekly income every week (same arithmetic as the player's bonus
    /// points) and refunds repaired ships like the player's. Host only; single-player untouched.
    /// </summary>
    [HarmonyPatch(typeof(CampaignManager), "CheckWeekly")]
    internal static class Faction1WeeklyIncomePatch
    {
        private static bool Active => ModState.MultiplayerActive && Multiplayer.Session.IsHost;

        private static void Prefix(CampaignManager __instance, ref int __state)
        {
            if (!Active) return;
            __instance.campaignData.enemyWeeklyCPDeficitOnly = false; // always pay faction 1
            __state = __instance.campaignData.commandPointSpent[1];
        }

        private static void Postfix(CampaignManager __instance, int __state)
        {
            if (!Active) return;
            int income = __state - __instance.campaignData.commandPointSpent[1];
            if (income != 0 && Multiplayer.Session.IsConnected)
                RemoteOrders.NoticeWithSound($"Weekly command points: +{income}.", "AdditionalCommand");
        }
    }

    [HarmonyPatch(typeof(CampaignManager), "CheckRepairedShips")]
    internal static class Faction1RepairRefundPatch
    {
        private static void Prefix(CampaignManager __instance, ref int __state)
        {
            __state = 0;
            if (!ModState.MultiplayerActive || !Multiplayer.Session.IsHost) return;
            CampaignData cd = __instance.campaignData;
            if (!cd.refundPostRepair) return;
            // Ships at 1 day remaining finish their repair during this call (vanilla decrements, removes at 0).
            for (int i = 0; i < cd.enemyOwnedRepairDays.Count; i++)
                if (cd.enemyOwnedRepairDays[i] == 1 && EngagementManager.instance.unitSeaDictionary.ContainsKey(cd.enemyOwnedShipClasses[i]))
                    __state += (int)(EngagementManager.instance.unitSeaDictionary[cd.enemyOwnedShipClasses[i]].cost * cd.releaseRefund);
        }

        private static void Postfix(CampaignManager __instance, int __state)
        {
            if (__state <= 0) return;
            __instance.campaignData.commandPointSpent[1] -= __state;
            if (Multiplayer.Session.IsConnected)
                RemoteOrders.NoticeWithSound($"Ships finished repair: +{__state} command points.", "AdditionalCommandRR");
        }
    }
}
