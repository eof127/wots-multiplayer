using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// M6c: what Player 2 sees of the shooting. Every projectile the host creates (gun shells from WeaponMount.SpawnAmmo,
    /// bombs/rockets/torpedoes from Weapon.FireWeapon...) passes through AmmunitionMoveBallistic.InitialiseBallistics; the host
    /// collects those (prefab name, position, rotation, muzzle velocity) and sends them each frame:
    ///   46 BattleShots host→client {count, [name, pos, euler, velocity]}
    /// The client fires the same prefab from the same place, so its flight, splash and explosion look the same. Damage is
    /// never applied on the client (its units are puppets; the host's damage reaches it through unit state).
    /// </summary>
    internal static partial class MpBattle
    {
        private struct Shot
        {
            public string Name;
            public Vector3 Pos, Euler;
            public float Velocity;
            public AmmunitionMoveBallistic Amb; // host: read firedByUnitID at flush (vanilla sets it right after InitialiseBallistics)
        }

        private const int MaxShotsPerMessage = 300;
        private static readonly List<Shot> pendingShots = new List<Shot>();
        private static bool spawningClientShot;

        /// <summary>Host: a projectile was just launched.</summary>
        internal static void HostShotFired(AmmunitionMoveBallistic amb)
        {
            if (amb == null || amb.parentAmmunition == null || pendingShots.Count >= MaxShotsPerMessage) return;
            if (amb.GetComponent<AmmunitionMoveTorpedo>() != null) return; // torpedoes: MpBattleTorpedoes (48)
            string name = amb.name;
            if (string.IsNullOrEmpty(name) || !EngagementManager.instance.ammunitionDictionary.ContainsKey(name)) return;
            Transform t = amb.transform;
            pendingShots.Add(new Shot { Name = name, Pos = t.position, Euler = t.eulerAngles, Velocity = amb.parentAmmunition.velocity, Amb = amb });
        }

        /// <summary>Host: who fired, as (list kind, index) — the same on both PCs, unlike map-unit ids. 255 = unknown.</summary>
        private static void ShooterRef(AmmunitionMoveBallistic amb, out byte kind, out int index)
        {
            kind = 255;
            index = -1;
            if (amb == null || amb.parentAmmunition == null) return;
            int id = amb.parentAmmunition.firedByUnitID;
            List<MapUnit> mapUnits = Map.instance != null ? Map.instance.allMapUnits : null;
            if (mapUnits == null || id < 0 || id >= mapUnits.Count || mapUnits[id] == null) return;
            if (!UnitRef(mapUnits[id].thisUnit, out kind, out index)) kind = 255;
        }

        /// <summary>Client: a map-unit id the impact code can safely look up (it indexes allMapUnits with it).</summary>
        private static int LocalShooterId(byte kind, int index)
        {
            EngagementManager em = EngagementManager.instance;
            List<Unit> list = kind == 0 ? em.allSeaUnits : kind == 1 ? em.allAirUnits : null;
            if (list != null && index >= 0 && index < list.Count && list[index] != null) return list[index].unitID;
            List<MapUnit> mapUnits = Map.instance.allMapUnits;
            return mapUnits != null && mapUnits.Count > 0 ? mapUnits[0].thisUnit.unitID : 0;
        }

        private static void FlushShots()
        {
            if (pendingShots.Count == 0) return;
            if (!AnyoneInBattle) { pendingShots.Clear(); return; }
            Shot[] shots = pendingShots.ToArray();
            pendingShots.Clear();
            SendBattleAll(MsgShots, Protocol.Build(w =>
            {
                w.Write(shots.Length);
                foreach (Shot s in shots)
                {
                    byte kind;
                    int index;
                    ShooterRef(s.Amb, out kind, out index);
                    w.Write(s.Name);
                    w.Write(s.Pos.x); w.Write(s.Pos.y); w.Write(s.Pos.z);
                    w.Write(s.Euler.x); w.Write(s.Euler.y); w.Write(s.Euler.z);
                    w.Write(s.Velocity);
                    w.Write(kind);
                    w.Write(index);
                    // protocol 19: calibre (vanilla sets damageAmount = mountData.calibre after launching; Ammunition sizes the
                    // splash, explosion and shockwave distortion from it) and the firing gun mount for its muzzle flash
                    float damage = s.Amb != null && s.Amb.parentAmmunition != null ? s.Amb.parentAmmunition.damageAmount : 0f;
                    int weapon, mount;
                    bool flash = FiringMount(s, kind, index, out weapon, out mount);
                    w.Write(damage);
                    w.Write((short)weapon);
                    w.Write((byte)Mathf.Clamp(mount, 0, 255));
                    w.Write(flash);
                }
            }));
        }

        private static readonly Dictionary<long, float> lastFlash = new Dictionary<long, float>();

        /// <summary>
        /// Host: the gun mount (weapon index, mount index) whose barrel the shell left — vanilla SpawnAmmo creates the shell at
        /// elevateTransforms[mount]. Flash = the mount's first shell of this salvo (vanilla plays fireFX for number 0 only).
        /// </summary>
        private static bool FiringMount(Shot s, byte kind, int index, out int weapon, out int mount)
        {
            weapon = -1;
            mount = 0;
            Unit u = kind == 255 ? null : ResolveUnit(kind, index);
            if (u == null || u.weapons == null || u.unitSea == null) return false;
            float best = 9f; // squared world units (shells start at the barrel; the ship moves a little before the flush)
            for (int wi = 0; wi < u.weapons.Count; wi++)
            {
                var wm = u.weapons[wi] as WeaponMount;
                if (wm == null || wm.elevateTransforms == null || wm.ammoType == AmmoType.Torpedo) continue;
                for (int m = 0; m < wm.elevateTransforms.Length; m++)
                {
                    Transform t = wm.elevateTransforms[m];
                    if (t == null) continue;
                    float d = (t.position - s.Pos).sqrMagnitude;
                    if (d < best) { best = d; weapon = wi; mount = m; }
                }
            }
            if (weapon < 0) return false;
            long key = ((long)u.unitID << 16) | ((long)weapon << 8) | (long)mount;
            float now = Time.time, last;
            if (lastFlash.TryGetValue(key, out last) && now - last < 0.35f) return false;
            lastFlash[key] = now;
            return true;
        }

        /// <summary>Client: replay the host's projectiles (visual only).</summary>
        private static void ClientShots(byte[] payload)
        {
            try
            {
                using (var r = Protocol.Reader(payload))
                {
                    int n = r.ReadInt32();
                    if (n < 0 || n > MaxShotsPerMessage) return;
                    Dictionary<string, GameObject> dict = EngagementManager.instance.ammunitionDictionary;
                    for (int i = 0; i < n; i++)
                    {
                        string name = r.ReadString();
                        var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        var euler = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        float v = r.ReadSingle();
                        byte kind = r.ReadByte();
                        int index = r.ReadInt32();
                        float damage = r.ReadSingle();
                        int weapon = r.ReadInt16(), mount = r.ReadByte();
                        bool flash = r.ReadBoolean();
                        Unit shooter = kind == 255 ? null : ResolveUnit(kind, index);
                        if (flash) ClientMuzzleFlash(shooter, weapon, mount);
                        GameObject prefab;
                        if (!dict.TryGetValue(name, out prefab) || prefab == null) continue;
                        GameObject go = ObjectPoolManager.CreatePooled(prefab, pos, Quaternion.Euler(euler));
                        AmmunitionMoveBallistic amb = go != null ? go.GetComponent<AmmunitionMoveBallistic>() : null;
                        if (amb == null) continue;
                        amb.name = name;
                        amb.parentAmmunition.velocity = v;
                        spawningClientShot = true;
                        try { amb.InitialiseBallistics(); }
                        finally { spawningClientShot = false; }
                        amb.parentAmmunition.firedByUnitID = LocalShooterId(kind, index); // same order as vanilla
                        amb.parentAmmunition.spotFireTarget = -1; // spotting bonus is the host's business
                        if (damage > 0f) amb.parentAmmunition.damageAmount = damage; // calibre: splash, explosion, distortion size
                        if (shooter != null && shooter.unitAir != null) ClientDropOrdnance(shooter, name, pos);
                        amb.gameObject.SetActive(true);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("MP battle: replaying shots failed: " + e.Message);
            }
        }

        /// <summary>
        /// Client: the gun's muzzle flash, firing sound and blast distortion — vanilla SpawnAmmo's mountData.fireFX at the
        /// barrel (user report 2026-11-25: main guns looked like small-calibre fire on P2's copy, on both sides).
        /// </summary>
        private static void ClientMuzzleFlash(Unit u, int weapon, int mount)
        {
            if (u == null || u.weapons == null || weapon < 0 || weapon >= u.weapons.Count || !u.gameObject.activeInHierarchy) return;
            var wm = u.weapons[weapon] as WeaponMount;
            if (wm == null || wm.mountData == null || wm.mountData.fireFX == null || wm.elevateTransforms == null || mount >= wm.elevateTransforms.Length) return;
            Transform barrel = wm.elevateTransforms[mount];
            if (barrel == null) return;
            GameObject fx = ObjectPoolManager.CreatePooled(wm.mountData.fireFX, barrel.position, barrel.rotation);
            if (fx == null) return;
            fx.transform.Translate(Vector3.forward * wm.muzzleFlashZOffset);
            fx.transform.SetParent(u.transform);
        }

        /// <summary>
        /// Client: a plane dropped a bomb / torpedo / rocket / depth charge — remove that store from its model, as vanilla
        /// Weapon.FireWeapon does (it destroys the launch point, which is the store's model). P2's copy never runs the drop
        /// itself, so the stores stayed on the planes (user report 2026-11-25). The nearest remaining store of a weapon that
        /// fires this ammunition is taken.
        /// </summary>
        private static void ClientDropOrdnance(Unit plane, string ammoName, Vector3 pos)
        {
            if (plane.weapons == null) return;
            Transform best = null;
            float bestD = float.MaxValue;
            foreach (Weapon w in plane.weapons)
            {
                if (w == null || w is WeaponMount || !w.destroyBaseTransforms || w.baseTransforms == null || w.ammunitionPrefabString == null) continue;
                if (Array.IndexOf(w.ammunitionPrefabString, ammoName) < 0) continue;
                foreach (Transform t in w.baseTransforms)
                {
                    if (t == null) continue; // already dropped (destroyed)
                    float d = (t.position - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = t; }
                }
            }
            if (best != null) UnityEngine.Object.Destroy(best.gameObject);
        }

        internal static bool SpawningClientShot => spawningClientShot;
    }

    [HarmonyPatch(typeof(AmmunitionMoveBallistic), nameof(AmmunitionMoveBallistic.InitialiseBallistics))]
    internal static class MpBattleShotPatch
    {
        private static void Postfix(AmmunitionMoveBallistic __instance)
        {
            if (MpBattle.HostActive && ModState.MultiplayerActive && Multiplayer.Session.IsHost) MpBattle.HostShotFired(__instance);
        }
    }

    /// <summary>Client: replayed shells explode and splash, but never damage the puppets (the host decides damage).</summary>
    [HarmonyPatch]
    internal static class MpBattleClientNoDamagePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.ApplyDamage))]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.ApplyDamageToCell))]
        [HarmonyPatch(typeof(DamageableAir), nameof(DamageableAir.ApplyDamageToAIR))]
        private static bool NoLocalDamage() => !MpBattle.ClientActive;
    }
}
