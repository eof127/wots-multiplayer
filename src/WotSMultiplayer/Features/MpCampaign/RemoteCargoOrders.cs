using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using WorldMapStrategyKit;
using WotSMultiplayer.Core;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// M4b part 3: Player 2's cargo (message 35): the per-ship transfer buttons of the cargo panel (load or unload one
    /// cargo type, all or a tenth) and "Unload all". CargoInterface.SetCargoChange already works per faction; the host
    /// adds the player's rules that vanilla only applies to faction 0 (troops landed → location takeover check), and
    /// skips the AI-only habit of auto-upgrading a base after unloading.
    /// </summary>
    internal static partial class RemoteOrders
    {
        private static readonly string[] CargoNames = { "troops", "supplies", "engineering", "fuel" };

        // ---------- client ----------

        internal static bool SendCargoTransfer(int cargoIndex, bool partial)
        {
            int id = ClientOwnSelected(0);
            int ship = CampaignInterface.instance.GetShipHighlightedIndex();
            if (id < 0 || ship < 0) return false;
            Send(MsgCargoOrder, w => { w.Write(id); w.Write((byte)CargoCmd.Transfer); w.Write(ship); w.Write(cargoIndex); w.Write(partial); });
            CargoInterface.instance.gameObject.SetActive(false);
            Toasts.Show($"Order sent: {(partial ? "transfer part of the" : "transfer the")} {CargoName(cargoIndex)} of " +
                        $"{CampaignInterface.instance.currentMapObject.mobileMapObject.unitDisplayNames[ship]}. Reopen Manage Cargo after the next update.");
            return true;
        }

        internal static bool SendUnloadAll()
        {
            int id = ClientOwnSelected(0);
            if (id < 0) return false;
            Send(MsgCargoOrder, w => { w.Write(id); w.Write((byte)CargoCmd.UnloadAll); });
            Toasts.Show($"Order sent: {CampaignInterface.instance.currentMapObject.mobileMapObject.mobileName} unloads all cargo.");
            return true;
        }

        private static string CargoName(int i) => i >= 0 && i < CargoNames.Length ? CargoNames[i] : "cargo";

        private static string DescribeCargo(string cargo)
        {
            List<int> c = CargoInterface.instance.GetCargoOnBoard(cargo);
            if (c == null) return "?";
            var parts = new List<string>();
            for (int i = 0; i < c.Count && i < CargoNames.Length; i++) if (c[i] != 0) parts.Add(c[i] + " " + CargoNames[i]);
            return parts.Count == 0 ? "empty" : string.Join(", ", parts.ToArray());
        }

        // ---------- host ----------

        private static void HostCargoOrder(BinaryReader r)
        {
            int id = r.ReadInt32();
            CargoCmd cmd = (CargoCmd)r.ReadByte();
            int ship = -1, cargoIndex = -1;
            bool partial = false;
            if (cmd == CargoCmd.Transfer) { ship = r.ReadInt32(); cargoIndex = r.ReadInt32(); partial = r.ReadBoolean(); }
            GameObjectAnimator goa = HostOwn(id, 0);
            if (goa == null) return;
            CampaignManager cm = CampaignManager.instance;
            MobileMapObject mmo = goa.mobileMapObject;
            MapLocation ml = cm.GetClosestLocation(goa); // vanilla range (interaction distance)
            if (ml == null) { Notice($"{mmo.mobileName}: move next to a base to handle cargo."); return; }

            if (cmd == CargoCmd.Transfer)
            {
                if (ship < 0 || ship >= mmo.unitPrefabs.Count || cargoIndex < 0 || cargoIndex > 3) return;
                List<int> onBoard = CargoInterface.instance.GetCargoOnBoard(mmo.cargo[ship]);
                if (onBoard == null) return;
                int carrying = CargoInterface.instance.GetCurrentCargoIndex(onBoard, string.Empty);
                string shipName = mmo.unitDisplayNames[ship];
                if (onBoard[cargoIndex] <= 0 && carrying != -1) { Notice($"{shipName} already carries {CargoName(carrying)}; unload it first."); return; }
                string before = mmo.cargo[ship];
                CargoInterface.instance.SetCargoChange(mmo, ship, cargoIndex, ml, partial);
                if (cargoIndex == 0) cm.CheckLocationTakeover(ml); // vanilla does this for the player's troops only
                Plugin.Log.LogInfo($"MP order: {Who} cargo {shipName} at {ml.locationName}: {before} → {mmo.cargo[ship]}");
                Notice($"{shipName} at {ml.locationName}: cargo {DescribeCargo(before)} → {DescribeCargo(mmo.cargo[ship])}.");
            }
            else if (cmd == CargoCmd.UnloadAll)
            {
                // CampaignManager.RemoveAllCargoFromShips without the AI's automatic base upgrade and campaign pause
                mmo.isMoving = false;
                mmo.unloadTimer = 0f;
                int unloaded = 0;
                for (int i = 0; i < mmo.cargo.Count; i++)
                {
                    List<int> c = cm.GetIntListFromString(mmo.cargo[i]);
                    for (int j = 0; j < c.Count; j++)
                        if (c[j] != 0) { CargoInterface.instance.SetCargoChange(mmo, i, j, ml); unloaded++; }
                }
                cm.CheckLocationTakeover(ml);
                if (unloaded == 0) { Notice($"{mmo.mobileName} carries no cargo."); return; }
                if (OF == 1 && ml.currentFaction == 0 && ml.troops[1] > 0) // Player 2's side landed troops at the host's base
                    ErrorPanel.instance.SetErrorMessage("LocationAttacked", ml.locationName, string.Empty, string.Empty);
                Plugin.Log.LogInfo($"MP order: {Who} unloaded {mmo.mobileName} at {ml.locationName}");
                NoticeWithSound($"{mmo.mobileName} unloaded its cargo at {ml.locationName}.", "UnloadedCargo");
            }
        }
    }

    // ---------- client patches (cargo panel buttons and the "Unload all" order) ----------

    [HarmonyPatch]
    internal static class ClientCargoPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CargoInterface), nameof(CargoInterface.LoadCargo))]
        private static bool Load(int cargoIndex)
        {
            if (!CampaignSync.IsClientPuppet) return true;
            RemoteOrders.SendCargoTransfer(cargoIndex, false);
            return false; // never change the client's copy of the cargo locally
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CargoInterface), nameof(CargoInterface.LoadPartialCargo))]
        private static bool LoadPartial(int cargoIndex)
        {
            if (!CampaignSync.IsClientPuppet) return true;
            RemoteOrders.SendCargoTransfer(cargoIndex, true);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CargoInterface), nameof(CargoInterface.UnloadCargo))]
        private static bool UnloadAll()
        {
            if (!CampaignSync.IsClientPuppet) return true;
            RemoteOrders.SendUnloadAll();
            return false;
        }
    }
}
