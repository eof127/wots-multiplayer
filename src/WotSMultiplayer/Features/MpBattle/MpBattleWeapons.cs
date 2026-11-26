using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// M6c: Player 2's manual fire control (the weapon panel's FIRE / HOLD / CEASE for guns and torpedo mounts).
    /// Vanilla CombatInterface.FireWeapon(mode) works out everything from the UI (selected mounts, shell type, spotting,
    /// target, fire-at-position point, spread, torpedo salvo) and ends in WeaponMount.SetWeaponMountToMode. On Player 2's copy
    /// that final call is sent to the host (BattleCmd.Mount) instead of running locally; the host makes the same call on
    /// its copy of the mount. Every other SetWeaponMountToMode on the client (auto-gunnery, AttackUnit, AA) is skipped:
    /// the host runs those for its units, and the client's puppets must not fire shells of their own.
    /// Weapons that fire through their own Weapon.FireWeapon (torpedo rooms, bombs, depth charges, rockets) are forwarded
    /// with BattleCmd.Fire (below). Not yet: torpedoes at a map position (the tube reads the host's own UI waypoint).
    /// </summary>
    internal static partial class MpBattle
    {
        private const int MaxMountIndexes = 32;
        private static bool uiFireWeapon;

        internal static bool UiFireWeapon { get => uiFireWeapon; set => uiFireWeapon = value; }

        /// <summary>Client: forward one vanilla SetWeaponMountToMode call made by the weapon panel.</summary>
        internal static void SendMount(WeaponMount wm, int mode, List<int> indexes, int ammoType, bool spot, Unit target,
                                       bool positionOnly, Vector3 pos, int spread)
        {
            Unit u = wm.parentUnit;
            byte kind;
            int index;
            if (u == null || u.faction != Faction.Player || !UnitRef(u, out kind, out index)) return;
            int wi = u.weapons.IndexOf(wm);
            if (wi < 0) return;
            byte tkind = 255;
            int tindex = -1;
            if (target != null && !UnitRef(target, out tkind, out tindex)) tkind = 255;
            SendBattleOrder(Protocol.Build(w =>
            {
                w.Write(kind); w.Write(index); w.Write((byte)BattleCmd.Mount);
                w.Write(wi);
                w.Write((byte)mode);
                int n = indexes == null ? -1 : Math.Min(indexes.Count, MaxMountIndexes);
                w.Write(n);
                for (int i = 0; i < n; i++) w.Write(indexes[i]);
                w.Write(ammoType);
                w.Write(spot);
                w.Write(tkind); w.Write(tindex);
                w.Write(positionOnly);
                w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
                w.Write(spread);
                w.Write(wm.fireAtPosition);
                w.Write(wm.currentSalvoSize);
                w.Write(wm.salvoSpread);
                int[] ntf = wm.numberToFire;
                int m = ntf == null ? 0 : Math.Min(ntf.Length, MaxMountIndexes);
                w.Write(m);
                for (int i = 0; i < m; i++) w.Write(ntf[i]);
            }));
        }

        /// <summary>Host: the same SetWeaponMountToMode call on Player 2's unit.</summary>
        private static void HostMount(Unit u, BinaryReader r)
        {
            int wi = r.ReadInt32();
            int mode = r.ReadByte();
            int n = r.ReadInt32();
            if (n > MaxMountIndexes) return;
            List<int> indexes = null;
            if (n >= 0)
            {
                indexes = new List<int>(n);
                for (int i = 0; i < n; i++) indexes.Add(r.ReadInt32());
            }
            int ammoType = r.ReadInt32();
            bool spot = r.ReadBoolean();
            byte tkind = r.ReadByte();
            int tindex = r.ReadInt32();
            bool positionOnly = r.ReadBoolean();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            int spread = r.ReadInt32();
            bool mountFireAtPosition = r.ReadBoolean();
            int salvo = r.ReadInt32();
            float salvoSpread = r.ReadSingle();
            int m = r.ReadInt32();
            if (m < 0 || m > MaxMountIndexes) return;
            var ntf = new int[m];
            for (int i = 0; i < m; i++) ntf[i] = r.ReadInt32();

            if (wi < 0 || wi >= u.weapons.Count || mode < 0 || mode > 2) return;
            WeaponMount wm = u.weapons[wi] as WeaponMount;
            if (wm == null) return;
            if (indexes != null)
                foreach (int i in indexes)
                    if (i < 0 || i >= MaxMountIndexes) return;
            // An empty torpedo mount told to fire makes vanilla MountHasAmmo read a second ammo column that torpedo mounts
            // don't have (IndexOutOfRange, seen in the 2026-11-25 test): leave empty torpedo mounts out of a fire order.
            if (wm.ammoType == AmmoType.Torpedo && mode == 0 && wm.ammoRemaining != null && !wm.isLinked)
            {
                var all = indexes ?? Enumerable.Range(0, wm.baseTransforms.Length).ToList();
                indexes = all.Where(i => i < wm.ammoRemaining.GetLength(0) && wm.ammoRemaining[i, 0] > 0).ToList();
                if (indexes.Count == 0) { Plugin.Log.LogInfo($"MP battle: P2 {u.name} torpedo mount {wi}: no torpedoes left"); return; }
            }

            EngagementManager em = EngagementManager.instance;
            Unit target = null;
            if (tkind == 0 || tkind == 1)
            {
                List<Unit> tlist = tkind == 0 ? em.allSeaUnits : em.allAirUnits;
                if (tindex >= 0 && tindex < tlist.Count) target = tlist[tindex];
            }
            if (target != null && target != u && mode != 2) u.unitAI.focusedUnit = target; // vanilla fires at the current unit's target
            if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)) pos = Vector3.zero;

            wm.fireAtPosition = mountFireAtPosition;
            if (wm.weaponType == WeaponType.Torpedo)
            {
                wm.currentSalvoSize = Mathf.Clamp(salvo, 0, 99);
                wm.salvoSpread = float.IsNaN(salvoSpread) ? 0f : Mathf.Clamp(salvoSpread, 0f, 90f);
                if (wm.numberToFire != null)
                    for (int i = 0; i < ntf.Length && i < wm.numberToFire.Length; i++) wm.numberToFire[i] = Mathf.Clamp(ntf[i], 0, 99);
            }
            wm.SetWeaponMountToMode(mode, indexes, ammoType, spot, target, positionOnly, pos, spread);
            wm.fireAtPosition = false; // vanilla resets it after the call
            Plugin.Log.LogInfo($"MP battle: P2 {wm.weaponType} mount {wi} → mode {mode} (mounts {(indexes == null ? "all" : indexes.Count.ToString())}, target {(target != null ? target.name : "none")})");
        }

        // ---------- weapons that fire through Weapon.FireWeapon (M6d) ----------
        // Torpedo rooms (subs), bombs, aerial torpedoes/depth charges, depth-charge racks, rockets: the UI sets flags
        // (fire, fireAll, salvo size/spread, target, repeats) or calls FireWeapon directly (DropPayloads, DirectFireWeapon);
        // either way the weapon's own FireWeapon runs. On the client that call is sent to the host with the settings and
        // never runs locally (no fake projectiles; the host's real ones arrive as shots).

        private const byte FireBase = 0, FireRepeat = 1, FireTube = 2;

        /// <summary>Client: one of Player 2's weapons was told to fire.</summary>
        internal static void SendFire(Weapon w)
        {
            Unit u = w.parentUnit;
            byte kind;
            int index;
            if (u == null || u.isDestroyed || u.faction != Faction.Player || !UnitRef(u, out kind, out index)) return;
            int wi = u.weapons.IndexOf(w);
            if (wi < 0) return;
            var tube = w as WeaponTorpedoTube;
            var rep = w as WeaponRepeat;
            byte tkind = 255;
            int tindex = -1;
            if (tube != null)
            {
                if (tube.fireAtPosition)
                {
                    Toasts.Show("Not yet in multiplayer: torpedoes at a map position. Pick a target instead.");
                    return;
                }
                if (tube.targetUnit == null || !UnitRef(tube.targetUnit, out tkind, out tindex)) return; // vanilla needs a target
            }
            SendBattleOrder(Protocol.Build(wr =>
            {
                wr.Write(kind); wr.Write(index); wr.Write((byte)BattleCmd.Fire);
                wr.Write(wi);
                wr.Write(tube != null ? FireTube : rep != null ? FireRepeat : FireBase);
                wr.Write(w.fireAll);
                if (tube != null) { wr.Write(tube.salvoSize); wr.Write(tube.salvoSpread); wr.Write(tkind); wr.Write(tindex); }
                else if (rep != null) { wr.Write(rep.numberOfRepeats); wr.Write(rep.repeatInterval); }
            }));
        }

        /// <summary>Host: fire Player 2's weapon the way the vanilla UI would.</summary>
        private static void HostFire(Unit u, BinaryReader r)
        {
            int wi = r.ReadInt32();
            byte type = r.ReadByte();
            bool fireAll = r.ReadBoolean();
            if (wi < 0 || wi >= u.weapons.Count) return;
            Weapon w = u.weapons[wi];
            if (w == null || w is WeaponMount) return;
            if (type == FireTube)
            {
                int salvo = r.ReadInt32();
                float spread = r.ReadSingle();
                byte tkind = r.ReadByte();
                int tindex = r.ReadInt32();
                var tube = w as WeaponTorpedoTube;
                Unit target = ResolveUnit(tkind, tindex);
                if (tube == null || target == null || target == u || target.unitSea == null) return;
                u.unitAI.focusedUnit = target; // the tube reads the firing unit's target for the solution
                tube.targetUnit = target;
                tube.fireAtPosition = false;
                tube.salvoSize = Mathf.Clamp(salvo, 1, Math.Max(1, tube.numberTubesReady));
                tube.salvoSpread = float.IsNaN(spread) ? 0f : Mathf.Clamp(spread, 0f, 90f);
                tube.fire = true; // vanilla: WeaponTorpedoTube.Update fires it
                Plugin.Log.LogInfo($"MP battle: P2 torpedo salvo {tube.salvoSize} at {target.name}");
                return;
            }
            if (type == FireRepeat)
            {
                int repeats = r.ReadInt32();
                float interval = r.ReadSingle();
                var rep = w as WeaponRepeat;
                if (rep == null) return;
                if (rep.gameObject.activeSelf && rep.currentRepeats > 0) return; // already firing a run
                rep.numberOfRepeats = Mathf.Clamp(repeats, 1, 99);
                if (!float.IsNaN(interval)) rep.repeatInterval = Mathf.Clamp(interval, 0.05f, 60f);
                rep.fireAll = fireAll;
                rep.FireWeapon(); // as DropPayloads / DirectFireWeapon (also what the fire flag leads to)
                Plugin.Log.LogInfo($"MP battle: P2 {rep.weaponType} fired ({rep.numberOfRepeats}x)");
                return;
            }
            w.fireAll = fireAll;
            w.gameObject.SetActive(true);
            w.fire = true;
            Plugin.Log.LogInfo($"MP battle: P2 {w.weaponType} fired");
        }

        private static Unit ResolveUnit(byte kind, int index)
        {
            EngagementManager em = EngagementManager.instance;
            if (em == null || (kind != 0 && kind != 1)) return null;
            List<Unit> list = kind == 0 ? em.allSeaUnits : em.allAirUnits;
            return index >= 0 && index < list.Count ? list[index] : null;
        }

        /// <summary>Client: a weapon's FireWeapon was called. Forward Player 2's; never fire locally.</summary>
        internal static void ClientWeaponFire(Weapon w)
        {
            try { if (!SuppressForward) SendFire(w); }
            catch (Exception e) { Plugin.Log.LogError("MP battle: sending a weapon order failed: " + e.Message); }
            w.fire = false;
            var rep = w as WeaponRepeat;
            if (rep != null)
            {
                rep.currentRepeats = 0;
                rep.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>Client: weapons never fire locally; Player 2's are forwarded to the host (MpBattle.ClientWeaponFire).</summary>
    [HarmonyPatch]
    internal static class MpBattleClientWeaponFirePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Weapon), nameof(Weapon.FireWeapon))]
        [HarmonyPatch(typeof(WeaponRepeat), nameof(WeaponRepeat.FireWeapon))]
        [HarmonyPatch(typeof(WeaponTorpedoTube), nameof(WeaponTorpedoTube.FireWeapon))]
        private static bool Prefix(Weapon __instance)
        {
            if (!MpBattle.ClientActive) return true;
            MpBattle.ClientWeaponFire(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.FireWeapon))]
    internal static class MpBattleClientFireWeaponPatch
    {
        private static bool Prefix()
        {
            if (!MpBattle.ClientActive) return true;
            MpBattle.UiFireWeapon = true;
            return true;
        }

        private static Exception Finalizer(Exception __exception)
        {
            MpBattle.UiFireWeapon = false;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(WeaponMount), nameof(WeaponMount.SetWeaponMountToMode))]
    internal static class MpBattleClientMountPatch
    {
        private static bool Prefix(WeaponMount __instance, int mode, List<int> indexes, int fireAmmoType, bool spot, Unit unit,
                                   bool positionOnly, Vector3 pos, int shellSpreadIndex)
        {
            if (!MpBattle.ClientActive) return true;
            if (MpBattle.UiFireWeapon)
            {
                try { MpBattle.SendMount(__instance, mode, indexes, fireAmmoType, spot, unit, positionOnly, pos, shellSpreadIndex); }
                catch (Exception e) { Plugin.Log.LogError("MP battle: sending a weapon order failed: " + e.Message); }
            }
            return false; // the client's puppets never fire on their own
        }
    }
}
