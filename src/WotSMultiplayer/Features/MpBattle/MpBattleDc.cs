using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// M7: damage control on Player 2's copy. Before this the client's damage-control panel never changed (all damage
    /// happens on the host) and P2's clicks only changed P2's own copy.
    ///   49 BattleDamageControl host→client, every 0.3 s, only what changed (all of it every 15 s and when P2 is ready)
    ///      {ship count, per ship: sea index, parties in use, flags (1 auto DC, 2 scuttled), cell count, changed cells:
    ///       cell index, status bits, integrity, max integrity, flooding, repair time}
    /// Cells are numbered in ordinal order of their ids (same unit data on both PCs → same order; a count mismatch is skipped).
    /// The client writes the values and mirrors the six status lists through vanilla ModifyCellIDInStatusList, so its own
    /// side effects follow (knocked-out mounts in the weapon panel, speed/turn penalties, efficiency, roster icons, hull
    /// shading), and lights / puts out fires on exactly the host's burning cells (vanilla SetFire).
    /// P2's orders (BattleCmd 19–22): a repair party to / from a cell, auto damage control, counter-flooding, scuttling.
    /// The host applies them with the vanilla panel's rules (DamageControlInterface.SelectCell / SetAutoDC / ScuttleShip).
    /// </summary>
    internal static partial class MpBattle
    {
        private const byte MsgDc = 49;
        private const float DcInterval = 0.3f, DcKeyframeInterval = 15f;

        // status bits: the cell's own flags, then membership of DamageableSea's status lists
        private const ushort BBurning = 1, BRepair = 2, BOffline = 4, BDestroyed = 8,
            LBurning = 16, LFlooded = 32, LOffline = 64, LDamaged = 128, LDestroyed = 256, LRepair = 512;

        private struct DcCell
        {
            public ushort Bits, Integrity, Flood, Repair;
            public byte MaxIntegrity;

            public bool SameAs(DcCell o) =>
                Bits == o.Bits && Integrity == o.Integrity && Flood == o.Flood && Repair == o.Repair && MaxIntegrity == o.MaxIntegrity;
        }

        private sealed class DcShip
        {
            public byte Parties, Flags;
            public DcCell[] Cells;
        }

        private static float lastDc, lastDcKey;
        private static readonly Dictionary<Unit, DcShip> dcSent = new Dictionary<Unit, DcShip>();
        private static readonly Dictionary<Unit, string[]> dcOrder = new Dictionary<Unit, string[]>();
        private static readonly HashSet<Unit> dcSynced = new HashSet<Unit>();
        private static bool dcErrorLogged;

        /// <summary>Client: > 0 while the vanilla damage-control panel handles a cell click (SelectCell).</summary>
        internal static int DcUiDepth;

        /// <summary>Client: true while host state is being written (UI callbacks it triggers are not P2's orders).</summary>
        internal static bool DcApplying;

        private static void DcReset()
        {
            dcSent.Clear();
            dcOrder.Clear();
            dcSynced.Clear();
            DcUiDepth = 0;
            DcApplying = false;
            dcErrorLogged = false;
            lastDcKey = Time.realtimeSinceStartup;
        }

        /// <summary>Host: the next update sends every cell of every ship (P2 just finished loading the battle).</summary>
        private static void DcForceFull() => dcSent.Clear();

        private static string[] CellOrder(Unit u)
        {
            string[] ids;
            if (dcOrder.TryGetValue(u, out ids)) return ids;
            var list = new List<string>(u.damageableSea.cellVariables.Keys);
            list.Sort(StringComparer.Ordinal);
            ids = list.ToArray();
            dcOrder[u] = ids;
            return ids;
        }

        // ---------- host ----------

        private static void HostSendDc()
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastDc < DcInterval) return;
            lastDc = now;
            if (now - lastDcKey >= DcKeyframeInterval)
            {
                lastDcKey = now;
                dcSent.Clear();
            }
            EngagementManager em = EngagementManager.instance;
            if (em == null || em.allSeaUnits == null || em.allSeaUnits.Count == 0) return;
            var parts = new List<byte[]>();
            for (int i = 0; i < em.allSeaUnits.Count && i < ushort.MaxValue; i++)
            {
                Unit u = em.allSeaUnits[i];
                if (u == null || u.isDestroyed || u.damageableSea == null || u.damageableSea.cellVariables == null) continue;
                byte[] part = EncodeDcShip(i, u);
                if (part != null) parts.Add(part);
            }
            if (parts.Count == 0) return;
            SendBattleAll(MsgDc, Protocol.Build(w =>
            {
                w.Write((ushort)parts.Count);
                foreach (byte[] p in parts) w.Write(p);
            }));
        }

        private static ushort U16(float v) => (ushort)Mathf.Clamp(Mathf.RoundToInt(v), 0, ushort.MaxValue);

        private static DcCell ReadCell(DamageableSea ds, string id)
        {
            var c = new DcCell();
            CellVariable cv;
            if (!ds.cellVariables.TryGetValue(id, out cv) || cv == null) return c;
            if (cv.isBurning) c.Bits |= BBurning;
            if (cv.isUnderRepair) c.Bits |= BRepair;
            if (cv.isOffline) c.Bits |= BOffline;
            if (cv.isDestroyed) c.Bits |= BDestroyed;
            if (ds.cellsBurning.Contains(id)) c.Bits |= LBurning;
            if (ds.cellsFlooded.Contains(id)) c.Bits |= LFlooded;
            if (ds.cellsOffline.Contains(id)) c.Bits |= LOffline;
            if (ds.cellsDamaged.Contains(id)) c.Bits |= LDamaged;
            if (ds.cellsDestroyed.Contains(id)) c.Bits |= LDestroyed;
            if (ds.cellsUnderRepair.Contains(id)) c.Bits |= LRepair;
            c.Integrity = U16(cv.integrity * 100f);
            c.MaxIntegrity = (byte)Mathf.Clamp(Mathf.RoundToInt(cv.maxIntegrity), 0, 255);
            c.Flood = U16(cv.isFlooded * 1000f);
            c.Repair = (ushort)Mathf.Clamp(Mathf.CeilToInt(cv.repairTime), 0, ushort.MaxValue);
            return c;
        }

        private static byte[] EncodeDcShip(int index, Unit u)
        {
            DamageableSea ds = u.damageableSea;
            string[] ids = CellOrder(u);
            var now = new DcShip
            {
                Parties = (byte)Mathf.Clamp(ds.partiesInUse, 0, 255),
                Flags = (byte)((ds.autoDC ? 1 : 0) | (ds.scuttled ? 2 : 0)),
                Cells = new DcCell[ids.Length],
            };
            for (int c = 0; c < ids.Length; c++) now.Cells[c] = ReadCell(ds, ids[c]);
            DcShip before;
            bool full = !dcSent.TryGetValue(u, out before) || before.Cells.Length != ids.Length;
            var changed = new List<int>();
            for (int c = 0; c < ids.Length; c++)
                if (full || !before.Cells[c].SameAs(now.Cells[c])) changed.Add(c);
            if (!full && changed.Count == 0 && before.Parties == now.Parties && before.Flags == now.Flags) return null;
            dcSent[u] = now;
            return Protocol.Build(w =>
            {
                w.Write((ushort)index);
                w.Write(now.Parties);
                w.Write(now.Flags);
                w.Write((ushort)ids.Length);
                w.Write((ushort)changed.Count);
                foreach (int c in changed)
                {
                    DcCell x = now.Cells[c];
                    w.Write((ushort)c);
                    w.Write(x.Bits);
                    w.Write(x.Integrity);
                    w.Write(x.MaxIntegrity);
                    w.Write(x.Flood);
                    w.Write(x.Repair);
                }
            });
        }

        /// <summary>Host: Player 2's damage-control orders, with the vanilla panel's rules.</summary>
        private static void HostDc(Unit u, BattleCmd cmd, BinaryReader r)
        {
            DamageableSea ds = u.damageableSea;
            if (u.unitSea == null || ds == null || ds.cellVariables == null) return;
            switch (cmd)
            {
                case BattleCmd.DcParty:
                {
                    string id = r.ReadString();
                    bool toCell = r.ReadBoolean();
                    CellVariable cv;
                    if (!ds.cellVariables.TryGetValue(id, out cv) || cv == null || cv.isUnderRepair == toCell) return;
                    if (toCell && u.unitData.damageParties - ds.partiesInUse <= 0) return; // no party free (SelectCell)
                    ds.MovePartyToCell(id, toCell);
                    return;
                }
                case BattleCmd.AutoDc:
                {
                    bool on = r.ReadBoolean();
                    ds.autoDC = on;
                    if (on) ds.CheckAutoDamageControl();
                    return;
                }
                case BattleCmd.CounterFlood:
                {
                    string id = r.ReadString();
                    Cell cell;
                    CellVariable cv;
                    if (!id.Contains(",") || !u.unitData.cellDictionary.TryGetValue(id, out cell) || cell == null || !cell.canFlood) return;
                    if (!ds.cellVariables.TryGetValue(id, out cv) || cv == null) return;
                    ds.autoDC = false; // the panel switches auto damage control off when counter-flooding
                    ds.FloodCompartment(cv, 1f);
                    return;
                }
                case BattleCmd.Scuttle:
                    Toasts.Show("Player 2 scuttled " + u.name + ".");
                    ds.scuttled = true;
                    ds.SinkShip();
                    return;
            }
        }

        /// <summary>Host log: the preset's combat difficulty multipliers per side (overridden to 1.0 in MP by D45).</summary>
        private static void LogCombatDifficulty()
        {
            try
            {
                float[] m = OptionsManager.instance != null ? OptionsManager.instance.difficultyMultipliers : null;
                if (m == null) return;
                var sb = new StringBuilder();
                string[] names = Enum.GetNames(typeof(Difficulty));
                for (int i = 0; i < names.Length && i * 2 + 1 < m.Length; i++) sb.Append($" {names[i]} {m[i * 2]:0.##}/{m[i * 2 + 1]:0.##}");
                int preset = LanguageManager.instance != null && LanguageManager.instance.combatDifficultyDropdown != null
                    ? LanguageManager.instance.combatDifficultyDropdown.value : -1;
                Plugin.Log.LogInfo($"MP battle: combat difficulty preset {preset} would give (host / Player 2):{sb} — not used in MP, all 1.0 (D45)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("MP battle: could not read the combat difficulty: " + e.Message);
            }
            LogScuttleButtons();
        }

        private static bool scuttleButtonsLogged;

        /// <summary>
        /// Once per game run: where the vanilla scuttle button is (scene-wired, so the code never names it; not found by eye in
        /// the sub's damage panel). Logs every Button whose click calls ScuttleShip, with its hierarchy path and state.
        /// </summary>
        private static void LogScuttleButtons()
        {
            if (scuttleButtonsLogged) return;
            scuttleButtonsLogged = true;
            try
            {
                var found = new List<string>();
                foreach (UnityEngine.UI.Button b in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>())
                {
                    if (b == null || !b.gameObject.scene.IsValid()) continue; // prefabs/assets, not scene objects
                    for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                        if (b.onClick.GetPersistentMethodName(i) == "ScuttleShip")
                        {
                            found.Add($"{PathOf(b.transform)} (active {b.gameObject.activeInHierarchy}, interactable {b.interactable})");
                            break;
                        }
                }
                DamageControlInterface dci = DamageControlInterface.instance;
                string confirm = dci != null && dci.scuttleConfirm != null ? PathOf(dci.scuttleConfirm.transform) : "-";
                Plugin.Log.LogInfo("MP battle: scuttle buttons: " + (found.Count > 0 ? string.Join("; ", found.ToArray()) : "none found") + "; confirm panel " + confirm);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("MP battle: scuttle button search failed: " + e.Message);
            }
        }

        private static string PathOf(Transform t)
        {
            string p = t.name;
            for (Transform x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        // ---------- client ----------

        internal static void SendDc(Unit u, BattleCmd cmd, Action<BinaryWriter> extra = null)
        {
            if (!ClientActive || firstApply || u == null || u.faction != Faction.Player || u.isDestroyed || u.unitSea == null) return;
            Plugin.Log.LogInfo($"MP battle: P2 damage control {cmd} for {u.name}");
            SendControl(u, cmd, extra);
        }

        private struct DcChange
        {
            public int Index;
            public DcCell Cell;
        }

        private static void ClientDc(byte[] payload)
        {
            EngagementManager em = EngagementManager.instance;
            if (em == null || em.allSeaUnits == null) return;
            using (var r = Protocol.Reader(payload))
            {
                int ships = r.ReadUInt16();
                for (int s = 0; s < ships; s++)
                {
                    int index = r.ReadUInt16();
                    byte parties = r.ReadByte();
                    byte flags = r.ReadByte();
                    int total = r.ReadUInt16();
                    int n = r.ReadUInt16();
                    var changes = new DcChange[n];
                    for (int i = 0; i < n; i++)
                    {
                        changes[i].Index = r.ReadUInt16();
                        changes[i].Cell = new DcCell
                        {
                            Bits = r.ReadUInt16(),
                            Integrity = r.ReadUInt16(),
                            MaxIntegrity = r.ReadByte(),
                            Flood = r.ReadUInt16(),
                            Repair = r.ReadUInt16(),
                        };
                    }
                    Unit u = index < em.allSeaUnits.Count ? em.allSeaUnits[index] : null;
                    if (u == null || u.isDestroyed || u.damageableSea == null || u.damageableSea.cellVariables == null) continue;
                    string[] ids = CellOrder(u);
                    if (ids.Length != total) continue; // different unit data: don't guess
                    try { ApplyDcShip(u, ids, parties, flags, changes); }
                    catch (Exception e)
                    {
                        if (!dcErrorLogged) Plugin.Log.LogError("MP battle: damage control for " + u.name + " failed: " + e);
                        dcErrorLogged = true;
                    }
                }
            }
        }

        private static void ApplyDcShip(Unit u, string[] ids, byte parties, byte flags, DcChange[] changes)
        {
            DamageableSea ds = u.damageableSea;
            DamageControlInterface dci = DamageControlInterface.instance;
            bool shown = dci != null && dci.currentUnit == u;
            bool auto = (flags & 1) != 0;
            bool prevSuppress = SuppressForward;
            DcApplying = true;
            SuppressForward = true;
            try
            {
                dcSynced.Add(u);
                ds.partiesInUse = parties;
                ds.autoDC = auto;
                if ((flags & 2) != 0) ds.scuttled = true;
                foreach (DcChange ch in changes)
                {
                    if (ch.Index < 0 || ch.Index >= ids.Length) continue;
                    string id = ids[ch.Index];
                    CellVariable cv;
                    if (!ds.cellVariables.TryGetValue(id, out cv) || cv == null || cv.parentCell == null) continue;
                    ApplyDcCell(ds, id, cv, ch.Cell);
                    if (shown) dci.UpdateDamageControlDisplay(id);
                }
                if (shown)
                {
                    dci.UpdateDamageControlPartiesLeft();
                    dci.UpdateDamageControlSummary();
                    if (dci.dcToggles != null && dci.dcToggles.Length > 1 && dci.dcToggles[1] != null && dci.dcToggles[1].isOn != auto)
                        dci.dcToggles[1].isOn = auto;
                }
            }
            finally
            {
                DcApplying = false;
                SuppressForward = prevSuppress;
            }
        }

        private static void ApplyDcCell(DamageableSea ds, string id, CellVariable cv, DcCell c)
        {
            cv.integrity = c.Integrity / 100f;
            cv.maxIntegrity = c.MaxIntegrity;
            cv.isFlooded = c.Flood / 1000f;
            cv.isOffline = (c.Bits & BOffline) != 0;
            cv.isUnderRepair = (c.Bits & BRepair) != 0;
            if ((c.Bits & BDestroyed) != 0) cv.isDestroyed = true; // never comes back
            bool burning = (c.Bits & BBurning) != 0;
            if (cv.isBurning != burning)
            {
                ds.SetFire(cv, burning); // vanilla fire effects on this cell's zone
                cv.isBurning = burning;
            }
            // lists last, through the vanilla method so its side effects follow (penalties, mounts, roster, shading)
            SyncStatusList(ds, id, ds.cellsDestroyed, (c.Bits & LDestroyed) != 0);
            SyncStatusList(ds, id, ds.cellsOffline, (c.Bits & LOffline) != 0);
            SyncStatusList(ds, id, ds.cellsFlooded, (c.Bits & LFlooded) != 0);
            SyncStatusList(ds, id, ds.cellsDamaged, (c.Bits & LDamaged) != 0);
            SyncStatusList(ds, id, ds.cellsUnderRepair, (c.Bits & LRepair) != 0);
            SyncStatusList(ds, id, ds.cellsBurning, (c.Bits & LBurning) != 0);
            cv.isOffline = (c.Bits & BOffline) != 0; // (adding a flooded magazine sets it; the host's value wins)
            cv.repairTime = c.Repair;
        }

        private static void SyncStatusList(DamageableSea ds, string id, List<string> list, bool want)
        {
            if (list.Contains(id) != want) ds.ModifyCellIDInStatusList(id, list, want);
        }
    }

    /// <summary>Client: Player 2's damage-control panel sends orders to the host instead of changing the local copy.</summary>
    [HarmonyPatch]
    internal static class MpBattleClientDcPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageControlInterface), nameof(DamageControlInterface.SelectCell))]
        private static void SelectBefore(ref bool __state)
        {
            __state = MpBattle.ClientActive;
            if (__state) MpBattle.DcUiDepth++;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(DamageControlInterface), nameof(DamageControlInterface.SelectCell))]
        private static Exception SelectAfter(Exception __exception, bool __state)
        {
            if (__state) MpBattle.DcUiDepth = Math.Max(0, MpBattle.DcUiDepth - 1);
            return __exception;
        }

        /// <summary>A cell click sends or recalls a repair party: forward it; the host's update shows the result.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.MovePartyToCell))]
        private static bool Party(DamageableSea __instance, string id, bool toCell)
        {
            if (!MpBattle.ClientActive || MpBattle.DcUiDepth == 0) return true;
            MpBattle.SendDc(__instance.parentUnit, BattleCmd.DcParty, w => { w.Write(id ?? ""); w.Write(toCell); });
            return false;
        }

        /// <summary>A cell click in counter-flood mode.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.FloodCompartment))]
        private static bool CounterFlood(DamageableSea __instance, CellVariable cv)
        {
            if (!MpBattle.ClientActive || MpBattle.DcUiDepth == 0 || cv == null || cv.parentCell == null) return true;
            MpBattle.SendDc(__instance.parentUnit, BattleCmd.CounterFlood, w => w.Write(cv.parentCell.cellID ?? ""));
            return false;
        }

        /// <summary>Repair parties are assigned by the host's automation only.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.CheckAutoDamageControl))]
        private static bool NoLocalAutoDc() => !MpBattle.ClientActive;

        /// <summary>The AUTO toggle (also flipped by vanilla when counter-flooding): forward real changes.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageControlInterface), nameof(DamageControlInterface.SetAutoDC))]
        private static void AutoDc(DamageControlInterface __instance)
        {
            if (!MpBattle.ClientActive || MpBattle.DcApplying) return;
            Unit u = __instance.currentUnit;
            if (u == null || u.unitSea == null || u.damageableSea == null || __instance.dcToggles == null || __instance.dcToggles.Length < 2) return;
            bool want = __instance.dcToggles[1].isOn;
            if (want != u.damageableSea.autoDC) MpBattle.SendDc(u, BattleCmd.AutoDc, w => w.Write(want));
        }

        /// <summary>Scuttle (after vanilla's confirm step): the host sinks the ship; the client shows it via the snapshot.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageControlInterface), nameof(DamageControlInterface.ScuttleShip))]
        private static bool Scuttle(DamageControlInterface __instance, bool actual)
        {
            if (!MpBattle.ClientActive || !actual) return true;
            Unit u = __instance.currentUnit;
            if (u != null && !u.isDestroyed && u.faction == Faction.Player)
            {
                MpBattle.SendDc(u, BattleCmd.Scuttle);
                Toasts.Show("Scuttle order sent to the host.");
            }
            if (__instance.scuttleConfirm != null) __instance.scuttleConfirm.SetActive(false);
            return false;
        }
    }
}
