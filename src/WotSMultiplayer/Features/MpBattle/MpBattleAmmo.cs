using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// v0.17.1: Player 2's ammunition as the host has it. P2's weapons only fire on the host (the client forwards the order and
    /// never fires locally), so on P2's copy torpedo tubes kept showing 6/6, the tube bar stayed full and magazines never ran
    /// down (user report). The host sends P2's units' weapon state twice a second:
    ///   53 BattleAmmo host→client {count, [unit kind, index, weapon count, [tag, fired, available, numberFired, lastUsed,
    ///      tag-specific: 1 tube {maxTorps, ready, loading, timers[]}, 2 mount {rows, cols, ammo[,], actionTimers[]},
    ///      3 repeat {currentRepeats}]]}
    /// and P2's copy copies the values into its puppet weapons, so the vanilla weapon panel shows them.
    /// </summary>
    internal static partial class MpBattle
    {
        private const byte MsgAmmo = 53;
        private const float AmmoInterval = 0.5f;
        private static float lastAmmo;
        private static int ammoLogs, smokeLogs, aaLogs; // client: a few log lines per battle (EnvReset clears them)

        /// <summary>Host (MpBattle.Update).</summary>
        private static void HostSendAmmo()
        {
            if (Time.realtimeSinceStartup - lastAmmo < AmmoInterval) return;
            lastAmmo = Time.realtimeSinceStartup;
            SendAmmoFor(IsGuestUnit, team: 1);
            if (HostTeamHasSubordinates) SendAmmoFor(IsHostTeamUnit, team: 0); // stage 4: the host's units, to its team
        }

        /// <summary>Host: one team's units' weapon and mount state.</summary>
        private static void SendAmmoFor(Func<Unit, bool> pick, int team)
        {
            EngagementManager em = EngagementManager.instance;
            if (em == null || em.allSeaUnits == null) return;
            var units = new List<Unit>();
            foreach (List<Unit> list in new[] { em.allSeaUnits, em.allAirUnits })
                if (list != null)
                    foreach (Unit u in list)
                        if (pick(u) && !u.isDestroyed && u.weapons != null && u.weapons.Count > 0) units.Add(u);
            if (units.Count == 0) return;
            byte[] payload = Protocol.Build(w =>
            {
                var refs = new List<Unit>();
                foreach (Unit u in units)
                {
                    byte k;
                    int i;
                    if (UnitRef(u, out k, out i)) refs.Add(u);
                }
                w.Write(refs.Count);
                foreach (Unit u in refs)
                {
                    byte kind;
                    int index;
                    UnitRef(u, out kind, out index);
                    w.Write(kind);
                    w.Write(index);
                    w.Write((short)u.weapons.Count);
                    foreach (Weapon wp in u.weapons) WriteWeapon(w, wp);
                }
            });
            if (team == 1) SendBattleTeam1(MsgAmmo, payload);
            else SendBattleTeam0(MsgAmmo, payload);
        }

        private static void WriteWeapon(BinaryWriter w, Weapon wp)
        {
            if (wp == null) { w.Write((byte)255); return; }
            var tube = wp as WeaponTorpedoTube;
            var mount = wp as WeaponMount;
            var rep = wp as WeaponRepeat;
            byte tag = (byte)(tube != null ? 1 : mount != null ? 2 : rep != null ? 3 : 0);
            w.Write(tag);
            w.Write(wp.totalNumberFired);
            w.Write(wp.totalNumberAvailable);
            w.Write(wp.numberFired);
            w.Write(wp.lastUsedTimer);
            switch (tag)
            {
                case 1:
                    w.Write(tube.maxTorps);
                    w.Write(tube.numberTubesReady);
                    w.Write(tube.loadingTube);
                    WriteFloats(w, tube.reloadTimers);
                    break;
                case 2:
                {
                    int[,] a = mount.ammoRemaining;
                    int rows = a != null ? a.GetLength(0) : 0, cols = a != null ? a.GetLength(1) : 0;
                    w.Write((short)rows);
                    w.Write((short)cols);
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++) w.Write(a[r, c]);
                    WriteFloats(w, mount.actionTimers);
                    WriteMountState(w, mount); // protocol 19
                    break;
                }
                case 3:
                    w.Write(rep.currentRepeats);
                    break;
            }
        }

        // Per-mount firing state (user report 2026-11-25: on P2's copy the weapon panel showed no red "firing" circle and no
        // shell type, and ship torpedo mounts looked wrong). P2's mounts never run their orders locally, so the vanilla panel
        // (CombatInterface.UpdateGunneryData: targetUnits / hold / aim / firingAmmoType per mount) had nothing to show.
        // The host sends, per mount: flags (1 aim, 2 hold, 4 done, 8 fire at position, 16 reloading, 32 knocked out), the
        // shell type, the target and the torpedo salvo count; plus the mount's salvo size and torpedoes fired in it.
        private const byte MAim = 1, MHold = 2, MDone = 4, MAtPos = 8, MReload = 16, MKnocked = 32;

        private static void WriteMountState(BinaryWriter w, WeaponMount m)
        {
            int n = m.aim != null ? m.aim.Length : 0;
            w.Write((short)n);
            for (int i = 0; i < n; i++)
            {
                byte f = (byte)((m.aim[i] ? MAim : 0) | (At(m.hold, i) ? MHold : 0) | (At(m.done, i) ? MDone : 0) |
                                (At(m.mountFireAtPosition, i) ? MAtPos : 0) | (At(m.isWeaponReloading, i) ? MReload : 0) |
                                (At(m.knockedOut, i) ? MKnocked : 0));
                w.Write(f);
                w.Write((byte)(m.firingAmmoType != null && i < m.firingAmmoType.Length ? m.firingAmmoType[i] : 0));
                WriteUnitRef(w, m.targetUnits != null && i < m.targetUnits.Length ? m.targetUnits[i] : null);
                w.Write((short)(m.numberToFire != null && i < m.numberToFire.Length ? m.numberToFire[i] : 0));
            }
            w.Write((short)m.currentSalvoSize);
            w.Write((short)m.totalTorpsFiredInSalvo);
        }

        private static bool At(bool[] a, int i) => a != null && i < a.Length && a[i];

        /// <summary>Client: the mounts' firing state. True if a flag, shell type or target changed (the panel shows them).</summary>
        private static bool ReadMountState(BinaryReader r, WeaponMount m)
        {
            bool changed = false;
            int n = r.ReadInt16();
            if (n < 0 || n > 64) throw new ArgumentException("bad mount count " + n);
            bool ok = m != null && m.aim != null && m.aim.Length == n;
            for (int i = 0; i < n; i++)
            {
                byte f = r.ReadByte();
                int ammo = r.ReadByte();
                Unit target = ResolveUnit(r.ReadByte(), r.ReadInt32());
                int toFire = r.ReadInt16();
                if (!ok) continue;
                byte before = (byte)((m.aim[i] ? MAim : 0) | (At(m.hold, i) ? MHold : 0) | (At(m.done, i) ? MDone : 0) |
                                     (At(m.mountFireAtPosition, i) ? MAtPos : 0) | (At(m.isWeaponReloading, i) ? MReload : 0) |
                                     (At(m.knockedOut, i) ? MKnocked : 0));
                if ((before & (MAim | MHold | MDone | MKnocked)) != (f & (MAim | MHold | MDone | MKnocked))) changed = true;
                if (m.firingAmmoType != null && i < m.firingAmmoType.Length && m.firingAmmoType[i] != ammo) changed = true;
                if (m.targetUnits != null && i < m.targetUnits.Length && m.targetUnits[i] != target) changed = true;
                m.aim[i] = (f & MAim) != 0;
                Set(m.hold, i, (f & MHold) != 0);
                Set(m.done, i, (f & MDone) != 0);
                Set(m.mountFireAtPosition, i, (f & MAtPos) != 0);
                Set(m.isWeaponReloading, i, (f & MReload) != 0);
                Set(m.knockedOut, i, (f & MKnocked) != 0);
                int types = m.ammunitionPrefabString != null ? m.ammunitionPrefabString.Length : 1;
                if (m.firingAmmoType != null && i < m.firingAmmoType.Length) m.firingAmmoType[i] = Mathf.Clamp(ammo, 0, Math.Max(0, types - 1));
                if (m.targetUnits != null && i < m.targetUnits.Length) m.targetUnits[i] = target;
                if (m.numberToFire != null && i < m.numberToFire.Length) m.numberToFire[i] = toFire;
            }
            int salvo = r.ReadInt16(), firedInSalvo = r.ReadInt16();
            if (ok) { m.currentSalvoSize = salvo; m.totalTorpsFiredInSalvo = firedInSalvo; }
            return changed;
        }

        private static void Set(bool[] a, int i, bool v) { if (a != null && i < a.Length) a[i] = v; }

        private static void WriteFloats(BinaryWriter w, float[] a)
        {
            int n = a != null ? a.Length : 0;
            w.Write((short)n);
            for (int i = 0; i < n; i++) w.Write(a[i]);
        }

        private static float[] ReadFloats(BinaryReader r)
        {
            int n = r.ReadInt16();
            if (n < 0 || n > 256) throw new ArgumentException("bad float count " + n); // (Unity's Mono has no InvalidDataException)
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = r.ReadSingle();
            return a;
        }

        private static void ClientAmmo(byte[] payload)
        {
            if (firstApply) return;
            CombatInterface ci = CombatInterface.instance;
            bool refreshPanel = false;
            using (var r = Protocol.Reader(payload))
            {
                int n = r.ReadInt32();
                if (n < 0 || n > 512) return;
                for (int i = 0; i < n; i++)
                {
                    Unit u = ResolveUnit(r.ReadByte(), r.ReadInt32());
                    int wn = r.ReadInt16();
                    bool own = u != null && u.faction == Faction.Player && u.weapons != null;
                    bool shown = own && ci != null && ci.currentUnit == u;
                    for (int j = 0; j < wn; j++)
                    {
                        Weapon wp = own && j < u.weapons.Count ? u.weapons[j] : null;
                        bool changed = ReadWeapon(r, wp); // always reads, so a mismatch only skips that weapon
                        // the weapon panel shows the current weapon (and the linked mounts after it)
                        if (changed && shown && j >= ci.currentWeaponIndex && j < ci.currentWeaponIndex + 8) refreshPanel = true;
                    }
                }
            }
            // Vanilla redraws the panel from DeactivateMount after each salvo; P2's mounts never run that, so torpedo counts
            // ("left / total"), firing circles and shell types only updated when P2 re-selected the weapon (user test
            // 2026-11-26: ship torpedo counts didn't go down; sub tubes use the timed reload bars and were fine).
            if (refreshPanel && ci.tabs != null && ci.tabs.Length > 3 && ci.tabs[3].gameObject.activeSelf)
            {
                try { ci.UpdateGunneryData(ci.currentWeaponIndex); }
                catch (Exception e) { Plugin.Log.LogWarning("MP battle: weapon panel refresh failed: " + e.Message); }
            }
        }

        /// <summary>Client: one weapon's state from the host. True if something the weapon panel shows changed.</summary>
        private static bool ReadWeapon(BinaryReader r, Weapon wp)
        {
            byte tag = r.ReadByte();
            if (tag == 255) return false;
            int fired = r.ReadInt32(), available = r.ReadInt32(), numberFired = r.ReadInt32();
            float lastUsed = r.ReadSingle();
            var tube = wp as WeaponTorpedoTube;
            var mount = wp as WeaponMount;
            var rep = wp as WeaponRepeat;
            bool match = wp != null && tag == (byte)(tube != null ? 1 : mount != null ? 2 : rep != null ? 3 : 0);
            bool changed = match && (wp.totalNumberFired != fired || wp.totalNumberAvailable != available);
            if (match)
            {
                wp.totalNumberFired = fired;
                wp.totalNumberAvailable = available;
                wp.numberFired = numberFired;
                wp.lastUsedTimer = lastUsed;
            }
            switch (tag)
            {
                case 1:
                {
                    int maxTorps = r.ReadInt32(), ready = r.ReadInt32(), loading = r.ReadInt32();
                    float[] timers = ReadFloats(r);
                    if (match)
                    {
                        if (ready != tube.numberTubesReady && ammoLogs < 6)
                        {
                            ammoLogs++;
                            Plugin.Log.LogInfo($"MP battle: P2 {tube.parentUnit.name} {tube.name}: tubes ready {tube.numberTubesReady} → {ready} (host), torpedoes left {available + ready}");
                        }
                        if (tube.numberTubesReady != ready) changed = true;
                        tube.maxTorps = maxTorps;
                        tube.numberTubesReady = ready;
                        tube.loadingTube = loading;
                        if (tube.reloadTimers != null && tube.reloadTimers.Length == timers.Length) Array.Copy(timers, tube.reloadTimers, timers.Length);
                    }
                    break;
                }
                case 2:
                {
                    int rows = r.ReadInt16(), cols = r.ReadInt16();
                    if (rows < 0 || cols < 0 || rows * cols > 4096) throw new ArgumentException("bad ammo table");
                    var a = new int[rows, cols];
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < cols; x++) a[y, x] = r.ReadInt32();
                    float[] timers = ReadFloats(r);
                    if (match)
                    {
                        int[,] mine = mount.ammoRemaining;
                        if (mine != null && mine.GetLength(0) == rows && mine.GetLength(1) == cols)
                        {
                            for (int y = 0; y < rows && !changed; y++)
                                for (int x = 0; x < cols; x++) if (mine[y, x] != a[y, x]) { changed = true; break; }
                            Array.Copy(a, mine, a.Length);
                        }
                        if (mount.actionTimers != null && mount.actionTimers.Length == timers.Length) Array.Copy(timers, mount.actionTimers, timers.Length);
                    }
                    if (ReadMountState(r, match ? mount : null)) changed = true;
                    break;
                }
                case 3:
                {
                    int repeats = r.ReadInt32();
                    if (match && rep.currentRepeats != repeats) { rep.currentRepeats = repeats; changed = true; }
                    break;
                }
            }
            return changed;
        }
    }

    /// <summary>
    /// Client: weapon mounts show the host's state (MpBattle.ReadMountState) and never act on it. Vanilla UpdateMounts would
    /// aim, fire (local shells) or reset mounts from that state, so on P2's copy it only runs the reload timers down between
    /// the host's updates (smooth reload bars). Turrets are turned by MpBattleTurrets.
    /// </summary>
    [HarmonyPatch]
    internal static class MpBattleClientMountsOffPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(WeaponMount), nameof(WeaponMount.UpdateMounts))]
        [HarmonyPatch(typeof(WeaponMountLinked), nameof(WeaponMountLinked.UpdateMounts))]
        private static bool Prefix(WeaponMount __instance, float duration)
        {
            if (!MpBattle.ClientActive) return true;
            float[] t = __instance.actionTimers;
            if (t != null)
                for (int i = 0; i < t.Length; i++)
                    if (t[i] > 0f) t[i] = Mathf.Max(0f, t[i] - duration * (__instance.parentUnit != null ? __instance.parentUnit.efficiency : 1f));
            return false;
        }
    }

    /// <summary>
    /// Client: close-range AA tracers and strafing now play on P2's copy, and their particles collide there too. Hits on the
    /// client only show the hit flash: the host decides the damage (otherwise a plane could be "shot down" only on P2's copy).
    /// </summary>
    [HarmonyPatch]
    internal static class MpBattleClientSmallCalibrePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageableAir), nameof(DamageableAir.ApplySmallCalibreDamageToAir))]
        private static bool Air(DamageableAir __instance, Vector3 pos, bool noFlashes)
        {
            if (!MpBattle.ClientActive) return true;
            if (!noFlashes) Flash(__instance.transform, pos);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.ApplySmallCalibreDamage))]
        private static bool Sea(DamageableSea __instance, Vector3 pos)
        {
            if (!MpBattle.ClientActive) return true;
            Flash(__instance.transform, pos);
            return false;
        }

        private static void Flash(Transform parent, Vector3 pos)
        {
            GameObject fx;
            if (Library.instance == null || !Library.instance.particleEffectsDictionary.TryGetValue("aircraftHitFlashes", out fx) || fx == null) return;
            GameObject go = ObjectPoolManager.CreatePooled(fx, pos, Quaternion.identity);
            if (go != null) go.transform.SetParent(parent);
        }
    }
}
