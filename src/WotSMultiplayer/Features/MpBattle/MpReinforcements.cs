using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WorldMapStrategyKit;
using WotSMultiplayer.Core;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// D55 (user, 2026-11-25): MP battles with several task forces per side.
    /// Vanilla CampaignManager.GenerateCampaignEngagement adds, around the encountering force, every air group within 50 km
    /// (up to Config.maxAirPerSide planes) but a ship group only if its side has no ships in the battle yet; groups of
    /// faction 1 still in a post-battle cooldown are skipped. In MP battles (host) the private GetValidNeighbouringMobiles is
    /// replaced by the same selection plus:
    ///   - the two forces of the encounter always take part (the guest's force first, even if another of P2's fleets is
    ///     nearer to the host's force);
    ///   - every other ship task force of either side within [Battle] MpReinforcementRadiusKm (20) of its own side's ship
    ///     force in the encounter joins too, up to [Battle] MpMaxTaskForcesPerSide (3) ship groups per side;
    ///   - groups in a post-battle cooldown may join too, for both sides alike (user's choice; vanilla let only the player's
    ///     side's cooling-down groups join).
    /// Every group starts at its campaign-map offset (scaled by MpSpawnDistanceFactor, MpBattleSpawn). The battle's result
    /// is written back to every group that took part (vanilla ExportWorldData already walks all of them), and all of them
    /// get the post-battle cooldown. P2's copy builds the same battle from the same group list.
    /// </summary>
    internal static class MpReinforcements
    {
        private static GameObjectAnimator hostAnchor, guestAnchor;
        private static bool active;
        // every group already in this battle: vanilla later drops the enemy ship group from goasInBattle when the fleets are
        // < 6 km apart (for its carrier-strike check) and then runs the selection a third time, which must not add it again
        private static readonly HashSet<GameObjectAnimator> joined = new HashSet<GameObjectAnimator>();
        private static readonly MethodInfo GenGroup = AccessTools.Method(typeof(CampaignManager), "GenerateUnitGroupData");
        private static readonly MethodInfo Heading = AccessTools.Method(typeof(CampaignManager), "GetHeadingFromObj");
        private const float VanillaRadius = 50000f;

        /// <summary>Host: the encounter's two forces (MpEncounters → MpBattle.HostStart).</summary>
        internal static void SetAnchors(GameObjectAnimator host, GameObjectAnimator guest)
        {
            hostAnchor = host;
            guestAnchor = guest;
        }

        internal static void Begin()
        {
            joined.Clear();
            active = ModState.MultiplayerActive && MpBattle.HostActive && GenGroup != null && Heading != null;
        }

        internal static void End()
        {
            active = false;
            hostAnchor = guestAnchor = null;
            joined.Clear();
        }

        /// <summary>Prefix of GetValidNeighbouringMobiles: true = run vanilla.</summary>
        internal static bool Run(CampaignManager cm, List<GameObjectAnimator> goas, GameObjectAnimator central)
        {
            if (!active || goas == null || central == null) return true;
            try
            {
                float radius = Mathf.Max(0f, Plugin.MpReinforcementRadiusKm.Value) * 1000f;
                int maxSea = Mathf.Clamp(Plugin.MpMaxTaskForcesPerSide.Value, 1, 8);
                foreach (GameObjectAnimator g in cm.goasInBattle) if (g != null) joined.Add(g); // the encountering force(s)

                // the guest's encounter force first, so it is the side's "own" fleet even if another P2 fleet is nearer
                if (guestAnchor != null && goas.Contains(guestAnchor) && !joined.Contains(guestAnchor) && Valid(guestAnchor))
                    Add(cm, guestAnchor, central, "the encounter force");

                foreach (GameObjectAnimator g in goas)
                {
                    if (!Valid(g) || joined.Contains(g)) continue;
                    MobileMapObject m = g.mobileMapObject;
                    int f = m.currentFaction == 0 ? 0 : 1;
                    // D55 (user): a group in its post-battle cooldown may join too, for both sides (vanilla skipped faction 1's)
                    float d = cm.GetDistanceInMetresBetweenMapObjects(central.currentMap2DLocation, g.currentMap2DLocation);
                    if (d >= VanillaRadius) continue;
                    if (m.type == 0)
                    {
                        if (!cm.seaPresent[f]) { Add(cm, g, central, "first ship group of its side (vanilla)"); continue; }
                        GameObjectAnimator anchor = f == 0 ? hostAnchor : guestAnchor;
                        if (radius <= 0f || anchor == null || anchor.mobileMapObject == null || anchor.mobileMapObject.type != 0) continue;
                        if (!joined.Contains(anchor) || SeaGroups(f) >= maxSea) continue;
                        float toAnchor = cm.GetDistanceInMetresBetweenMapObjects(anchor.currentMap2DLocation, g.currentMap2DLocation);
                        if (toAnchor < radius) Add(cm, g, central, $"{toAnchor / 1000f:0.0} km from {anchor.mobileMapObject.mobileName}");
                    }
                    else if (cm.airNumbers[f] + m.unitPrefabs.Count <= Config.instance.maxAirPerSide)
                        Add(cm, g, central, null);
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("MP battle: reinforcement selection failed, using the game's own: " + e.Message);
                return true;
            }
        }

        private static bool Valid(GameObjectAnimator g) =>
            g != null && g.mobileMapObject != null && g.mobileMapObject.unitPrefabs != null && g.mobileMapObject.unitPrefabs.Count > 0;

        private static int SeaGroups(int faction)
        {
            int n = 0;
            foreach (GameObjectAnimator g in joined)
                if (g != null && g.mobileMapObject != null && g.mobileMapObject.type == 0 && (g.mobileMapObject.currentFaction == 0 ? 0 : 1) == faction) n++;
            return n;
        }

        /// <summary>The same steps as vanilla GetValidNeighbouringMobiles for one group.</summary>
        private static void Add(CampaignManager cm, GameObjectAnimator g, GameObjectAnimator central, string why)
        {
            MobileMapObject m = g.mobileMapObject;
            int f = m.currentFaction;
            Vector2 off = (g.currentMap2DLocation - central.currentMap2DLocation) * cm.campaignData.mapWidthMetres * 0.1f;
            float heading = (float)Heading.Invoke(cm, new object[] { g });
            string group = (string)GenGroup.Invoke(cm, new object[] { g, new Vector3(off.x, 0f, off.y / 2f), heading, m.speed / 0.514444f, f });
            EngagementManager.instance.engagementData.unitGroups.Add(group);
            int side = f == 0 ? 0 : 1;
            if (m.type == 0) cm.seaPresent[side] = true;
            else cm.airNumbers[side] += m.unitPrefabs.Count;
            cm.goasInBattle.Add(g);
            joined.Add(g);
            if (why != null) Plugin.Log.LogInfo($"MP battle: '{m.mobileName}' ({(side == 0 ? "host" : "Player 2")}, {m.unitPrefabs.Count} {(m.type == 0 ? "ships" : "planes")}) joins: {why} (D55)");
        }
    }

    [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.GenerateCampaignEngagement))]
    internal static class MpReinforcementScopePatch
    {
        private static void Prefix() => MpReinforcements.Begin();

        private static Exception Finalizer(Exception __exception)
        {
            MpReinforcements.End();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(CampaignManager), "GetValidNeighbouringMobiles")]
    internal static class MpReinforcementSelectPatch
    {
        private static bool Prefix(CampaignManager __instance, List<GameObjectAnimator> goas, GameObjectAnimator centralGoa) =>
            MpReinforcements.Run(__instance, goas, centralGoa);
    }
}
