using HarmonyLib;
using WorldMapStrategyKit;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// D54 (user, 2026-11-25): after a battle the other side's task forces fade out over time on both maps.
    /// Vanilla CampaignManager.ProcessMobileObjectPostCombat (via ExportWorldData, host only in MP) sets the spotted timer of
    /// every faction-1 mobile that fought to 0 and hides its marker at once; with the post-battle cooldown it can't be
    /// spotted again either, so P2's surviving force vanished from the host's map the moment the battle ended. Faction 0's
    /// mobiles are never cleared, which is why P2 (who sees the host's fleets through that same timer) already saw them
    /// fade. Here, in MP on the host, a surviving faction-1 mobile is treated as just spotted instead: full spotted timer,
    /// back in spottedContacts, marker redrawn. Vanilla Check5Min then fades it as usual (sea 90 min, air 15 min of game
    /// time). Nothing else changes: the cooldown still blocks new spotting and new encounters.
    /// </summary>
    [HarmonyPatch(typeof(CampaignManager), "ProcessMobileObjectPostCombat")]
    internal static class MpPostBattleGhostPatch
    {
        private static void Postfix(CampaignManager __instance, MobileMapObject mmo)
        {
            if (!ModState.MultiplayerActive || !Multiplayer.Session.IsHost) return;
            if (mmo == null || mmo.currentFaction == 0 || mmo.unitPrefabs == null || mmo.unitPrefabs.Count == 0) return;
            GameObjectAnimator goa = mmo.gameObjectAnimator;
            if (goa == null || !__instance.otherMobile.Contains(goa)) return; // destroyed (all sunk / shot down)
            mmo.spottedTimer = Utilities.GetMaxSpotTime(mmo);
            if (!__instance.spottedContacts.Contains(mmo))
            {
                __instance.spottedContacts.Add(mmo);
                if (mmo.type == 0 && Config.instance.vagueCampaignIntel) mmo.intelData = __instance.GetIntelOnSeaGroup(mmo);
            }
            CampaignInterface.instance.SetContactColor(mmo);
            Plugin.Log.LogInfo($"MP: '{mmo.mobileName}' stays on the host's map after the battle and fades out (D54)");
        }
    }
}
