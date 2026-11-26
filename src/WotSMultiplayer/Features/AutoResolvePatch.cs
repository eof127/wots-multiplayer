using System;
using System.Collections.Generic;
using HarmonyLib;
using WotSMultiplayer.Core;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features
{
    /// <summary>
    /// Decision D6: encounters that offer auto-resolve are resolved automatically and seamlessly.
    ///
    /// Vanilla: CampaignManager.CheckMobileEncounters sets campaignData.canAutoResolve only for a
    /// Fighter-vs-Scout air pairing (lead unit subtypes), then calls EngagementPanel.OpenEngagementPanel,
    /// which pauses the campaign and shows the panel with an "Auto" button wired to AutoResolve().
    /// Here we intercept OpenEngagementPanel: if canAutoResolve, call the game's own AutoResolve()
    /// (no rule changes) and skip the panel, so the campaign never pauses.
    /// </summary>
    [HarmonyPatch(typeof(EngagementPanel), nameof(EngagementPanel.OpenEngagementPanel))]
    internal static class AutoResolvePatch
    {
        private static bool Prefix(EngagementPanel __instance)
        {
            if (!ModState.AutoResolveActive) return true;

            CampaignManager cm = CampaignManager.instance;
            CampaignData cd = cm != null ? cm.campaignData : null;
            if (cd == null || !cd.canAutoResolve) return true;
            if (cd.playerObjectEngaged < 0 || cd.playerObjectEngaged >= cm.playerMobile.Count ||
                cd.otherObjectEngaged < 0 || cd.otherObjectEngaged >= cm.otherMobile.Count)
                return true;

            string summary, guestSummary;
            try
            {
                MobileMapObject own = cm.playerMobile[cd.playerObjectEngaged].mobileMapObject;
                MobileMapObject other = cm.otherMobile[cd.otherObjectEngaged].mobileMapObject;
                summary = Describe(own, other);
                guestSummary = Describe(other, own); // MP: Player 2's point of view
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Auto-resolve: could not inspect encounter, showing vanilla panel. " + e);
                return true;
            }

            try
            {
                __instance.AutoResolve();
            }
            catch (Exception e)
            {
                // AutoResolve clears encounter data only at its very end, so on failure the vanilla panel can still open.
                Plugin.Log.LogError("Auto-resolve failed, falling back to the vanilla panel. " + e);
                return true;
            }

            Toasts.Show("Auto-resolved: " + summary);
            if (ModState.MultiplayerActive && Multiplayer.Session.IsHost && Multiplayer.Session.IsConnected)
                Multiplayer.Session.SendGame(27, Net.Protocol.Build(w => w.Write("Auto-resolved: " + guestSummary)));
            return false;
        }

        /// <summary>Mirrors the outcome rule in EngagementPanel.AutoResolve (fighters - scouts > 3 → scouts lost), seen from <paramref name="own"/>.</summary>
        private static string Describe(MobileMapObject own, MobileMapObject other)
        {
            Dictionary<string, UnitData> units = EngagementManager.instance.unitDataDictionary;

            bool ownAreFighters = units[own.unitPrefabs[0]].unitSubtype == UnitSubType.Fighter;
            int fighters = ownAreFighters ? own.unitPrefabs.Count : other.unitPrefabs.Count;
            int scouts = ownAreFighters ? other.unitPrefabs.Count : own.unitPrefabs.Count;
            bool scoutsLost = fighters - scouts > 3;

            if (ownAreFighters)
                return scoutsLost
                    ? own.mobileName + " intercepted enemy scouts and shot them down."
                    : own.mobileName + " engaged enemy scouts, but they escaped.";
            return scoutsLost
                ? own.mobileName + " was intercepted by enemy fighters and lost."
                : own.mobileName + " evaded enemy fighters.";
        }
    }
}
