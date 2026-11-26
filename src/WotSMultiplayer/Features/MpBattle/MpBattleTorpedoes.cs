using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// M6d: torpedoes on Player 2's copy. Every torpedo passes through AmmunitionMoveTorpedo.InitialiseTorpedo: ship
    /// torpedoes from Weapon.SpawnTorpedo (instantiated; offset + intercept point, then gyro angle, turn mode, run speed,
    /// run time) and aerial torpedoes from Weapon.FireWeapon (pooled; launch speed, then a ballistic drop). Its run is
    /// deterministic from those values, so the host sends them and the client launches the same torpedo the vanilla way:
    ///   48 BattleTorpedoes host→client {count, [name, aerial, pos, rot, velocity, offset, intercept, turnImmediate,
    ///                                    runSpeed, timeToRun, shooter kind+index]}
    /// Torpedoes are left out of the shot replay (46), which would only reproduce the launch arc. Damage stays host-only.
    /// </summary>
    internal static partial class MpBattle
    {
        private const byte MsgTorps = 48;
        private const int MaxTorpsPerMessage = 100;

        private struct TorpLaunch
        {
            public AmmunitionMoveTorpedo T;
            public string Name;
            public bool Aerial;
            public Vector3 Pos, Intercept;
            public Quaternion Rot;
            public float Velocity, Offset;
        }

        private static readonly List<TorpLaunch> pendingTorps = new List<TorpLaunch>();

        /// <summary>Host, postfix of InitialiseTorpedo: position, offset and intercept as launched (the gyro adjusts them later).</summary>
        /// <summary>
        /// > 0 inside Ammunition.CheckWaterHit: a torpedo that was dropped or launched through the air is initialised a
        /// second time when it enters the water. That is the same torpedo (P2's copy replays the air part and enters the
        /// water itself), so it is not sent again — it was, and P2 saw two torpedoes (and two hits) per real one.
        /// </summary>
        internal static int InWaterHit;

        internal static void HostTorpedoLaunched(AmmunitionMoveTorpedo t)
        {
            if (InWaterHit > 0) return;
            if (t == null || t.parentAmmunition == null || pendingTorps.Count >= MaxTorpsPerMessage) return;
            string name = t.name;
            if (string.IsNullOrEmpty(name) || !EngagementManager.instance.ammunitionDictionary.ContainsKey(name)) return;
            pendingTorps.Add(new TorpLaunch
            {
                T = t,
                Name = name,
                Aerial = t.parentAmmunition.ammoType == AmmoType.Aerial_Torpedo,
                Pos = t.transform.position,
                Rot = t.transform.rotation,
                Velocity = t.parentAmmunition.velocity,
                Offset = t.offset,
                Intercept = t.interceptPos,
            });
        }

        /// <summary>Host (Update, the frame after launch): the rest of the values are set by now.</summary>
        private static void FlushTorps()
        {
            if (pendingTorps.Count == 0) return;
            if (!AnyoneInBattle) { pendingTorps.Clear(); return; }
            TorpLaunch[] torps = pendingTorps.ToArray();
            pendingTorps.Clear();
            int aerial = 0;
            foreach (TorpLaunch l in torps) if (l.Aerial) aerial++;
            Plugin.Log.LogInfo($"MP battle: {torps.Length} torpedo launch(es) sent to Player 2 ({aerial} aerial)");
            SendBattleAll(MsgTorps, Protocol.Build(w =>
            {
                w.Write(torps.Length);
                foreach (TorpLaunch l in torps)
                {
                    AmmunitionMoveTorpedo t = l.T;
                    AmmunitionTimer timer = t != null ? t.GetComponent<AmmunitionTimer>() : null;
                    w.Write(l.Name);
                    w.Write(l.Aerial);
                    w.Write(l.Pos.x); w.Write(l.Pos.y); w.Write(l.Pos.z);
                    w.Write(l.Rot.x); w.Write(l.Rot.y); w.Write(l.Rot.z); w.Write(l.Rot.w);
                    w.Write(l.Velocity);
                    w.Write(l.Offset);
                    w.Write(l.Intercept.x); w.Write(l.Intercept.y); w.Write(l.Intercept.z);
                    w.Write(t != null && t.turnImmediate);
                    w.Write(t != null ? t.runSpeed : 0f);
                    w.Write(timer != null ? timer.timeToRun : 0f);
                    byte kind = 255;
                    int index = -1;
                    int id = t != null && t.parentAmmunition != null ? t.parentAmmunition.firedByUnitID : -1;
                    List<MapUnit> mapUnits = Map.instance != null ? Map.instance.allMapUnits : null;
                    if (mapUnits != null && id >= 0 && id < mapUnits.Count && mapUnits[id] != null && !UnitRef(mapUnits[id].thisUnit, out kind, out index)) kind = 255;
                    w.Write(kind);
                    w.Write(index);
                }
            }));
        }

        /// <summary>Client: launch the host's torpedoes (visual; their hits do no damage here).</summary>
        private static void ClientTorps(byte[] payload)
        {
            try
            {
                using (var r = Protocol.Reader(payload))
                {
                    int n = r.ReadInt32();
                    if (n < 0 || n > MaxTorpsPerMessage) return;
                    Dictionary<string, GameObject> dict = EngagementManager.instance.ammunitionDictionary;
                    for (int i = 0; i < n; i++)
                    {
                        string name = r.ReadString();
                        bool aerial = r.ReadBoolean();
                        var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        float velocity = r.ReadSingle();
                        float offset = r.ReadSingle();
                        var intercept = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        bool turnImmediate = r.ReadBoolean();
                        float runSpeed = r.ReadSingle();
                        float timeToRun = r.ReadSingle();
                        byte kind = r.ReadByte();
                        int index = r.ReadInt32();
                        GameObject prefab;
                        if (!dict.TryGetValue(name, out prefab) || prefab == null) continue;
                        LaunchTorpedo(prefab, name, aerial, pos, rot, velocity, offset, intercept, turnImmediate, runSpeed, timeToRun, LocalShooterId(kind, index));
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("MP battle: replaying torpedoes failed: " + e.Message);
            }
        }

        private static void LaunchTorpedo(GameObject prefab, string name, bool aerial, Vector3 pos, Quaternion rot, float velocity, float offset,
                                          Vector3 intercept, bool turnImmediate, float runSpeed, float timeToRun, int shooterId)
        {
            if (aerial)
            {
                // as Weapon.FireWeapon for an aerial torpedo
                GameObject go = ObjectPoolManager.CreatePooled(prefab, pos, rot);
                AmmunitionMoveBallistic amb = go != null ? go.GetComponent<AmmunitionMoveBallistic>() : null;
                AmmunitionMoveTorpedo t = go != null ? go.GetComponent<AmmunitionMoveTorpedo>() : null;
                if (amb == null || t == null) return;
                amb.name = name;
                amb.parentAmmunition.velocity = velocity;
                amb.parentAmmunition.firedByUnitID = shooterId;
                amb.parentAmmunition.spotFireTarget = -1;
                amb.parentAmmunition.ammunitionTimer.timeToRun = float.PositiveInfinity;
                t.InitialiseTorpedo();
                amb.InitialiseBallistics();
                go.SetActive(true);
                return;
            }
            // as Weapon.SpawnTorpedo
            GameObject tgo = UnityEngine.Object.Instantiate(prefab, pos, rot);
            AmmunitionMoveTorpedo torp = tgo.GetComponent<AmmunitionMoveTorpedo>();
            if (torp == null) { UnityEngine.Object.Destroy(tgo); return; }
            tgo.name = name;
            tgo.SetActive(true);
            torp.offset = offset;
            torp.interceptPos = intercept;
            torp.parentAmmunition.firedByUnitID = shooterId;
            torp.parentAmmunition.spotFireTarget = -1;
            torp.InitialiseTorpedo();
            torp.CalculateGyroAngle();
            torp.turnImmediate = turnImmediate;
            if (tgo.transform.position.y > 0f)
            {
                AmmunitionMoveBallistic b = tgo.GetComponent<AmmunitionMoveBallistic>();
                if (b != null) b.InitialiseBallistics();
                torp.enabled = false;
            }
            if (runSpeed > 0f) torp.runSpeed = runSpeed;
            AmmunitionTimer timer = tgo.GetComponent<AmmunitionTimer>();
            if (timer != null && timeToRun > 0f) timer.timeToRun = timeToRun;
        }
    }

    [HarmonyPatch(typeof(AmmunitionMoveTorpedo), nameof(AmmunitionMoveTorpedo.InitialiseTorpedo))]
    internal static class MpBattleTorpedoPatch
    {
        private static void Postfix(AmmunitionMoveTorpedo __instance)
        {
            if (MpBattle.HostActive && ModState.MultiplayerActive && Multiplayer.Session.IsHost) MpBattle.HostTorpedoLaunched(__instance);
        }
    }

    /// <summary>Marks the water entry of an air-launched torpedo (its second InitialiseTorpedo): see MpBattle.InWaterHit.</summary>
    [HarmonyPatch(typeof(Ammunition), nameof(Ammunition.CheckWaterHit))]
    internal static class MpBattleTorpedoWaterEntryPatch
    {
        private static void Prefix() => MpBattle.InWaterHit++;

        private static Exception Finalizer(Exception __exception)
        {
            MpBattle.InWaterHit = Math.Max(0, MpBattle.InWaterHit - 1);
            return __exception;
        }
    }
}
