using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using WorldMapStrategyKit;
using WotSMultiplayer.Core;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// M4b part 3: Player 2's task-force orders (message 34): split, swap positions, merge, formation type/spacing,
    /// rename and bombard. Each is the faction-1 version of the vanilla CampaignInterface handler.
    /// </summary>
    internal static partial class RemoteOrders
    {
        // ---------- client ----------

        private static void SendFleet(FleetCmd cmd, int id, Action<BinaryWriter> args, string toast)
        {
            Send(MsgFleetOrder, w => { w.Write(id); w.Write((byte)cmd); if (args != null) args(w); });
            if (toast != null) Toasts.Show(toast);
        }

        /// <summary>Client: Split with ships highlighted. False = let vanilla show its message (nothing highlighted).</summary>
        internal static bool SendSplit()
        {
            int id = ClientOwnSelected(0);
            if (id == BlockedId) return true;
            if (id < 0) return false;
            List<int> picked = PickedShips();
            if (picked.Count == 0) return false;
            if (picked.Count >= CampaignInterface.instance.currentMapObject.mobileMapObject.unitPrefabs.Count) return true; // vanilla: all picked = nothing to do
            SendFleet(FleetCmd.Split, id, w => OrderRules.WriteIndexes(w, picked), $"Order sent: split {picked.Count} ship(s) into a new task force.");
            return true;
        }

        internal static bool SendSwap()
        {
            int id = ClientOwnSelected(0);
            if (id == BlockedId) return true;
            if (id < 0) return false;
            List<int> picked = PickedShips();
            if (picked.Count >= 2) SendFleet(FleetCmd.Swap, id, w => { w.Write(picked[0]); w.Write(picked[1]); }, "Order sent: swap ship positions.");
            return true;
        }

        internal static void SendMerge(GameObjectAnimator target)
        {
            CampaignInterface ci = CampaignInterface.instance;
            ci.enableMerge = false;
            int id = ClientOwnSelected(0);
            if (id < 0 || target == null || target == ci.currentMapObject || target.mobileMapObject == null
                || target.mobileMapObject.currentFaction != 0 || target.mobileMapObject.type != 0) return;
            int targetId = CampaignSync.ClientIdOf(target);
            if (targetId < 0 || TeamLocks.ClientRefuses(targetId, target.mobileMapObject.mobileName)) return;
            List<int> picked = PickedShips();
            if (picked.Count == 0) { ErrorPanel.instance.SetErrorMessage("MergeSelected", string.Empty, string.Empty, string.Empty); return; }
            SendFleet(FleetCmd.Merge, id, w => { w.Write(targetId); OrderRules.WriteIndexes(w, picked); },
                $"Order sent: move {picked.Count} ship(s) into {target.mobileMapObject.mobileName}.");
        }

        internal static void SendFormation(int formation, int spacing)
        {
            int id = ClientOwnSelected(0);
            if (id >= 0) SendFleet(FleetCmd.Formation, id, w => { w.Write(formation); w.Write(spacing); }, null);
        }

        internal static void SendRename(string name)
        {
            int id = ClientOwnSelected(-1);
            if (id >= 0) SendFleet(FleetCmd.Rename, id, w => w.Write(OrderRules.CleanName(name)), null);
        }

        internal static bool SendBombard()
        {
            int id = ClientOwnSelected(0);
            if (id == BlockedId) return true;
            if (id < 0) return false;
            SendFleet(FleetCmd.Bombard, id, null, "Order sent: bombard the nearest enemy base.");
            return true;
        }

        // ---------- host ----------

        private static void HostFleetOrder(BinaryReader r)
        {
            int id = r.ReadInt32();
            FleetCmd cmd = (FleetCmd)r.ReadByte();
            GameObjectAnimator goa = HostOwn(id, 0);
            if (goa == null) return;
            MobileMapObject mmo = goa.mobileMapObject;
            CampaignInterface ci = CampaignInterface.instance;
            switch (cmd)
            {
                case FleetCmd.Split: HostSplit(goa, OrderRules.ReadIndexes(r)); break;
                case FleetCmd.Swap: HostSwap(mmo, r.ReadInt32(), r.ReadInt32()); break;
                case FleetCmd.Merge:
                    int targetId = r.ReadInt32();
                    HostMerge(goa, targetId, OrderRules.ReadIndexes(r));
                    break;
                case FleetCmd.Formation:
                    int formation = r.ReadInt32(), spacing = r.ReadInt32();
                    if (formation >= 0 && formation < ci.seaFormation.options.Count) mmo.formation = formation;
                    if (spacing >= 0 && spacing <= ci.formationSeaSpacing.maxValue) mmo.formationSpacing = spacing;
                    break;
                case FleetCmd.Rename:
                    string name = OrderRules.CleanName(r.ReadString());
                    if (name.Length > 0)
                    {
                        Plugin.Log.LogInfo($"MP order: {Who} renamed '{mmo.mobileName}' to '{name}'");
                        mmo.mobileName = name;
                        mmo.customName = true;
                    }
                    break;
                case FleetCmd.Bombard: HostBombard(goa); break;
                case FleetCmd.Rearm: HostRearm(mmo); break;
                case FleetCmd.Repeat: HostRepeat(mmo, r); break;
            }
        }

        /// <summary>Faction-1 version of CampaignInterface.SplitTaskForce: the picked ships form a new task force here.</summary>
        private static void HostSplit(GameObjectAnimator goa, List<int> picked)
        {
            CampaignManager cm = CampaignManager.instance;
            CampaignInterface ci = CampaignInterface.instance;
            MobileMapObject mmo = goa.mobileMapObject;
            List<int> idx = OrderRules.CleanIndexes(picked, mmo.unitPrefabs.Count);
            if (!OrderRules.CanSplit(idx.Count, mmo.unitPrefabs.Count)) { Notice("Split: highlight some ships, but not all of them."); return; }

            EnsureTemplate();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(mmo), template);
            template.isMoving = false; // a moving faction-1 group would be handed to the AI's navigation on creation
            template.mapWaypoints.Clear();
            template.autoMove = false;
            template.autoPath.Clear();
            GameObjectAnimator selected = ci.currentMapObject;
            GameObjectAnimator g2 = ci.CreateNewMobileObject(CampaignObjectType.Sea, template, goa.currentMap2DLocation, isLoading: false, null, string.Empty);
            ci.currentMapObject = selected;
            MobileMapObject m2 = g2.mobileMapObject;
            for (int i = mmo.unitPrefabs.Count - 1; i >= 0; i--)
            {
                if (idx.Contains(i)) RemoveShipAt(mmo, i);
                else RemoveShipAt(m2, i);
            }
            mmo.mobileName = ci.GetTaskForceAutoName(mmo);
            m2.customName = false;
            m2.mobileName = ci.GetTaskForceAutoName(m2);
            if (cm.spottedContacts.Contains(mmo) && mmo.spottedTimer > 5f)
            {
                cm.spottedContacts.Add(m2);
                m2.spottedTimer = mmo.spottedTimer;
            }
            mmo.postEngagementTimer = m2.postEngagementTimer = Mathf.Max(mmo.postEngagementTimer, m2.postEngagementTimer);
            mmo.speed = cm.GetMobileMapObjectSpeed(mmo);
            m2.speed = cm.GetMobileMapObjectSpeed(m2);
            RefreshSprite(mmo);
            RefreshSprite(m2);
            ShowAsEnemy(goa);
            ShowAsEnemy(g2);
            Plugin.Log.LogInfo($"MP order: {Who} split {m2.unitPrefabs.Count} ship(s) from {mmo.mobileName} → {m2.mobileName}");
            Notice($"New task force {m2.mobileName} ({m2.unitPrefabs.Count} ship(s)) split from {mmo.mobileName}.");
        }

        private static void HostSwap(MobileMapObject m, int a, int b)
        {
            if (a < 0 || b < 0 || a >= m.unitPrefabs.Count || b >= m.unitPrefabs.Count || a == b) return;
            OrderRules.SwapAt(m.unitPrefabs, a, b);
            OrderRules.SwapAt(m.unitDisplayNames, a, b);
            OrderRules.SwapAt(m.unitInstances, a, b);
            OrderRules.SwapAt(m.cellVariables, a, b);
            OrderRules.SwapAt(m.ammunitionRemaining, a, b);
            OrderRules.SwapAt(m.currentAircraft, a, b);
            OrderRules.SwapAt(m.aircraftCooldown, a, b);
            OrderRules.SwapAt(m.cargo, a, b);
            OrderRules.SwapAt(m.bombardments, a, b);
            OrderRules.SwapAt(m.paint, a, b);
            OrderRules.SwapAt(m.speedModifier, a, b);
        }

        /// <summary>Faction-1 version of CampaignInterface.AttemptMerge: move the picked ships into the target task force.</summary>
        private static void HostMerge(GameObjectAnimator goa, int targetId, List<int> picked)
        {
            CampaignManager cm = CampaignManager.instance;
            CampaignInterface ci = CampaignInterface.instance;
            CampaignData cd = cm.campaignData;
            GameObjectAnimator tg = HostOwn(targetId, 0);
            if (tg == null || tg == goa) { Notice("Merge: pick another of your task forces."); return; }
            if (cm.GetDistanceInMetresBetweenMapObjects(goa.currentMap2DLocation, tg.currentMap2DLocation) >= cd.interactionDistanceMetres * 2f)
            { Notice("Merge: the two task forces must be next to each other."); return; }
            MobileMapObject src = goa.mobileMapObject, dst = tg.mobileMapObject;
            List<int> idx = OrderRules.CleanIndexes(picked, src.unitPrefabs.Count);
            if (idx.Count == 0) { Notice("Merge: highlight the ships to move first."); return; }
            int fits = OrderRules.MergeFits(dst.unitPrefabs.Count, idx.Count, cd.maxShipsInTaskForce);
            int moved = 0;
            foreach (int i in idx) // highest index first, like vanilla
            {
                if (moved >= fits) break;
                dst.unitPrefabs.Add(src.unitPrefabs[i]);
                dst.unitInstances.Add(src.unitInstances[i]);
                dst.unitDisplayNames.Add(src.unitDisplayNames[i]);
                dst.cellVariables.Add(src.cellVariables[i]);
                dst.ammunitionRemaining.Add(src.ammunitionRemaining[i]);
                dst.currentAircraft.Add(src.currentAircraft[i]);
                dst.aircraftCooldown.Add(src.aircraftCooldown[i]);
                dst.cargo.Add(src.cargo[i]);
                dst.bombardments.Add(src.bombardments[i]);
                dst.paint.Add(src.paint[i]);
                dst.speedModifier.Add(src.speedModifier[i]);
                RemoveShipAt(src, i);
                moved++;
            }
            if (moved == 0) { Notice($"Merge: {dst.mobileName} is full ({cd.maxShipsInTaskForce} ships)."); return; }
            dst.postEngagementTimer = src.postEngagementTimer = Mathf.Max(src.postEngagementTimer, dst.postEngagementTimer);
            float spotted = Mathf.Max(src.spottedTimer, dst.spottedTimer);
            if (spotted > 0f)
            {
                if (!cm.spottedContacts.Contains(src)) { cm.spottedContacts.Add(src); src.spottedTimer = spotted; }
                if (!cm.spottedContacts.Contains(dst)) { cm.spottedContacts.Add(dst); dst.spottedTimer = spotted; }
            }
            src.mobileName = ci.GetTaskForceAutoName(src);
            dst.mobileName = ci.GetTaskForceAutoName(dst);
            string dstName = dst.mobileName;
            if (src.unitPrefabs.Count == 0) ci.DestroyMapMobileObject(goa);
            else
            {
                src.speed = cm.GetMobileMapObjectSpeed(src);
                RefreshSprite(src);
                ShowAsEnemy(goa);
            }
            dst.speed = cm.GetMobileMapObjectSpeed(dst);
            RefreshSprite(dst);
            ShowAsEnemy(tg);
            Plugin.Log.LogInfo($"MP order: {Who} merged {moved} ship(s) into {dstName}");
            Notice(moved < idx.Count ? $"{moved} ship(s) joined {dstName}; it is full ({cd.maxShipsInTaskForce} ships)." : $"{moved} ship(s) joined {dstName}.");
        }

        /// <summary>Faction-1 version of CampaignInterface.Bombard (target = an enemy base next to the task force).</summary>
        private static void HostBombard(GameObjectAnimator goa)
        {
            CampaignManager cm = CampaignManager.instance;
            MobileMapObject mmo = goa.mobileMapObject;
            List<int> guns = cm.GetbombardIndexes(mmo);
            if (guns.Count == 0) { Notice($"{mmo.mobileName}: none of these ships can bombard."); return; }
            MapLocation ml = NearLocation(goa, 2f);
            if (ml == null || ml.currentFaction != EnemyFaction) { Notice($"{mmo.mobileName}: move next to an enemy base first."); return; }
            cm.BombardWithMobile(mmo, guns, ml);
            Plugin.Log.LogInfo($"MP order: {Who} bombarded {ml.locationName} with {mmo.mobileName}");
            NoticeWithSound($"{mmo.mobileName} bombarded {ml.locationName}.", "Bombarded");
        }
    }

    // ---------- client patches ----------

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.SplitTaskForce))]
    internal static class ClientSplitPatch
    {
        private static bool Prefix() => !CampaignSync.IsClientPuppet || !RemoteOrders.SendSplit();
    }

    /// <summary>Only for an existing task force; swapping inside a task force being created stays local.</summary>
    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.SwapShips))]
    internal static class ClientSwapPatch
    {
        private static bool Prefix()
        {
            if (!CampaignSync.IsClientPuppet || CampaignInterface.instance.IsCreatingNewMobileGroup()) return true;
            return !RemoteOrders.SendSwap();
        }
    }

    /// <summary>Covers both the Merge order (nearest task force) and merge-by-click.</summary>
    [HarmonyPatch(typeof(CampaignInterface), "AttemptMerge")]
    internal static class ClientMergePatch
    {
        private static bool Prefix(GameObjectAnimator obj)
        {
            if (!CampaignSync.IsClientPuppet) return true;
            RemoteOrders.SendMerge(obj);
            return false;
        }
    }

    /// <summary>
    /// Formation dropdown / spacing slider. They also fire when the panel shows a task force (value set from the data),
    /// so only a value that differs from the task force's is an order. The local change stays until the next update.
    /// </summary>
    [HarmonyPatch]
    internal static class ClientFormationPatches
    {
        private static MobileMapObject OwnSea()
        {
            CampaignInterface ci = CampaignInterface.instance;
            if (!CampaignSync.IsClientPuppet || ci.currentMapObject == null || ci.IsCreatingNewMobileGroup()) return null;
            MobileMapObject mmo = ci.currentMapObject.mobileMapObject;
            return mmo != null && mmo.currentFaction == 0 && mmo.type == 0 ? mmo : null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.UpdateFormationType))]
        private static void FormationType()
        {
            MobileMapObject mmo = OwnSea();
            int v = CampaignInterface.instance.seaFormation.value;
            if (mmo != null && v != mmo.formation) RemoteOrders.SendFormation(v, -1);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.UpdateFormationDistance))]
        private static void FormationDistance()
        {
            MobileMapObject mmo = OwnSea();
            int v = (int)CampaignInterface.instance.formationSeaSpacing.value;
            if (mmo != null && v != mmo.formationSpacing) RemoteOrders.SendFormation(-1, v);
        }
    }

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.OnMobileRename))]
    internal static class ClientRenamePatch
    {
        private static void Prefix()
        {
            CampaignInterface ci = CampaignInterface.instance;
            if (CampaignSync.IsClientPuppet && ci.currentMapObject != null && ci.currentMapObject.mobileMapObject != null)
                RemoteOrders.SendRename(ci.renameField.text);
        }
    }

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.Bombard))]
    internal static class ClientBombardPatch
    {
        private static bool Prefix() => !CampaignSync.IsClientPuppet || !RemoteOrders.SendBombard();
    }
}
