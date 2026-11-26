using System;
using System.Collections.Generic;
using HarmonyLib;
using WorldMapStrategyKit;
using WotSMultiplayer.Core;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// Player 2 rearms / resupplies a task force (the fleet panel's second order list → "Resupply Ships"; was blocked by D35).
    /// The client sends FleetCmd.Rearm; the host runs the game's own CampaignManager.ReArmShips → ResupplyShips.ReArmShips
    /// on P2's fleet, with three faction-1 corrections while it runs:
    ///   - at-sea resupply looks for supply ships among P2's fleets (vanilla IsAtSeaSufficient walks playerMobile only);
    ///   - P2's home port counts as a home port (vanilla GetNearestPort asks IsHomePort(port, 0));
    ///   - the game's result messages ("Rearmed", "not enough supplies", "too far from a port"…) go to P2 as a notice
    ///     instead of the host's screen, and the host's own resupply panel isn't closed.
    /// Port stocks / supply-ship cargo used and the fleet's refilled ammunition reach P2 with the next campaign update.
    /// </summary>
    internal static partial class RemoteOrders
    {
        internal static bool HostRearmRunning;
        /// <summary>The rearm running is for faction 1 (Player 2's side): the faction-1 corrections apply.</summary>
        internal static bool HostRearmFaction1;
        private static readonly List<string> rearmMessages = new List<string>();

        /// <summary>Client: the selected own task force asks for a rearm.</summary>
        internal static void SendRearm()
        {
            int id = ClientOwnSelected(0);
            if (id < 0) return;
            SendFleet(FleetCmd.Rearm, id, null, "Order sent: rearm / resupply this task force.");
        }

        private static void HostRearm(MobileMapObject mmo)
        {
            if (mmo == null || mmo.currentFaction != OF || mmo.type != 0) return;
            rearmMessages.Clear();
            rearmRefs.Clear();
            AlignShipLists(mmo);
            HostRearmRunning = true;
            HostRearmFaction1 = OF == 1; // a team-0 subordinate's rearm is the player's own (no corrections needed)
            bool failed = false;
            try { CampaignManager.instance.ReArmShips(mmo); }
            catch (Exception e) { failed = true; Plugin.Log.LogWarning("MP order: " + Who + " rearm hit an error: " + e); }
            finally { HostRearmRunning = HostRearmFaction1 = false; }
            // the game's own result message decides what P2 sees (with its sound: RemoteFeedback); an error after a
            // successful rearm (the "Rearmed" message was already given) is only logged
            bool rearmed = rearmRefs.Contains("Rearmed") || rearmRefs.Contains("RearmedCarrierWarning");
            if (failed && !rearmed) rearmMessages.Add("Rearm failed on the host.");
            string text = rearmMessages.Count > 0 ? string.Join(" ", rearmMessages.ToArray()) : "Rearm order done.";
            Plugin.Log.LogInfo($"MP order: {Who} rearm of '{mmo.mobileName}': {text}");
            string dictRef = rearmRefs.Count > 0 ? rearmRefs[rearmRefs.Count - 1] : null;
            NoticeWithSound($"{mmo.mobileName}: {text}", dictRef);
        }

        private static readonly List<string> rearmRefs = new List<string>();

        /// <summary>
        /// The per-ship lists of a task force must be as long as its ship list (vanilla indexes them by ship). P2's forces
        /// can get out of step after splits, merges or losses: extra entries are dropped, missing ones get the empty value.
        /// </summary>
        private static void AlignShipLists(MobileMapObject m)
        {
            int n = m.unitPrefabs != null ? m.unitPrefabs.Count : 0;
            Align(m.unitDisplayNames, n, ""); Align(m.cellVariables, n, ""); Align(m.ammunitionRemaining, n, "");
            Align(m.currentAircraft, n, ""); Align(m.aircraftCooldown, n, ""); Align(m.cargo, n, "");
            Align(m.bombardments, n, 0); Align(m.paint, n, 0); Align(m.speedModifier, n, 1f);
        }

        private static void Align<T>(List<T> list, int n, T empty)
        {
            if (list == null) return;
            if (list.Count > n) list.RemoveRange(n, list.Count - n);
            while (list.Count < n) list.Add(empty);
        }

        /// <summary>Host, during a P2 rearm: the game's message text, for P2.</summary>
        internal static void CaptureRearmMessage(string dictRef, string s0, string s1, string s2)
        {
            string text = dictRef;
            try
            {
                Dictionary<string, string> m = ErrorPanel.instance != null ? ErrorPanel.instance.messages : null;
                if (m != null && m.ContainsKey(dictRef)) text = m[dictRef].Replace("<0>", s0).Replace("<1>", s1).Replace("<2>", s2);
            }
            catch { }
            rearmMessages.Add(text);
            rearmRefs.Add(dictRef);
        }
    }

    [HarmonyPatch]
    internal static class HostRearmFaction1Patches
    {
        // vanilla ResupplyShips.IsAtSeaSufficient walks CampaignManager.playerMobile: show it P2's fleets while it runs
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ResupplyShips), "IsAtSeaSufficient")]
        private static void AtSeaBefore(ref bool __state)
        {
            __state = RemoteOrders.HostRearmRunning && RemoteOrders.HostRearmFaction1;
            if (__state) Swap();
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(ResupplyShips), "IsAtSeaSufficient")]
        private static Exception AtSeaAfter(Exception __exception, bool __state)
        {
            if (__state) Swap();
            return __exception;
        }

        private static void Swap()
        {
            CampaignManager cm = CampaignManager.instance;
            List<GameObjectAnimator> p = cm.playerMobile;
            cm.playerMobile = cm.otherMobile;
            cm.otherMobile = p;
        }

        // GetNearestPort asks IsHomePort(port, 0): for P2's rearm that means P2's home ports
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.IsHomePort))]
        private static void HomePort(ref int faction)
        {
            if (RemoteOrders.HostRearmRunning && RemoteOrders.HostRearmFaction1 && faction == 0) faction = 1;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ErrorPanel), nameof(ErrorPanel.SetErrorMessage))]
        private static bool Message(string dictRef, string s0, string s1, string s2)
        {
            if (!RemoteOrders.HostRearmRunning) return true;
            RemoteOrders.CaptureRearmMessage(dictRef, s0, s1, s2);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ResupplyPanel), nameof(ResupplyPanel.CloseResupplyPanel))]
        private static bool KeepHostPanel() => !RemoteOrders.HostRearmRunning;

        // Vanilla ResetAircraft refills each ship's planes as aircraftTotal[j] − in the air[j] for j < 3, but aircraftTotal
        // only has one entry per plane type the ship carries: a cruiser with one floatplane type made it read past the end
        // (ArgumentOutOfRange in the 2026-11-25 test — P2's Japanese cruisers; vanilla's own Japanese side never rearms).
        // For P2's rearm the same refill runs with the real number of plane types.
        private static readonly System.Reflection.MethodInfo isAircraftUpdated = AccessTools.Method(typeof(ResupplyShips), "IsAircraftUpdated");

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ResupplyShips), "ResetAircraft")]
        private static bool SafeResetAircraft(ResupplyShips __instance, MobileMapObject mmo)
        {
            if (!RemoteOrders.HostRearmRunning || isAircraftUpdated == null || mmo == null || mmo.currentAircraft == null) return true;
            CampaignManager cm = CampaignManager.instance;
            for (int i = 0; i < mmo.currentAircraft.Count && i < mmo.unitPrefabs.Count; i++)
            {
                string prefab = mmo.unitPrefabs[i];
                if (string.IsNullOrEmpty(mmo.currentAircraft[i]) || !(bool)isAircraftUpdated.Invoke(__instance, new object[] { prefab })) continue;
                UnitData d;
                if (!EngagementManager.instance.unitDataDictionary.TryGetValue(prefab, out d) || d == null || d.aircraftTotal == null) continue;
                int inst = mmo.unitInstances != null && i < mmo.unitInstances.Count ? mmo.unitInstances[i] : 0;
                int[] inAir = cm.GetShipAircraftAlreadyInAir(prefab, inst, mmo.currentFaction);
                var list = new List<int>(d.aircraftTotal.Count);
                for (int j = 0; j < d.aircraftTotal.Count; j++) list.Add(d.aircraftTotal[j] - (inAir != null && j < inAir.Length ? inAir[j] : 0));
                if (list.Count > 0) mmo.currentAircraft[i] = cm.SetIntListToString(list);
            }
            return false;
        }
    }
}
