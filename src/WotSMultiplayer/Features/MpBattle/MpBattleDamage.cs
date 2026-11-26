using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// M6c: damage as Player 2 sees it. The host decides all damage; its 10 Hz snapshot carries, per sea unit, the three
    /// fire zones (bow / midships / stern: how many cells burn) and the six hull damage-shading levels. Destroyed units come
    /// from the existing "destroyed" flag. The client shows them with the vanilla code paths:
    ///   sunk ship → DamageableSea.SinkShip (bubbles, map icon, roster, log, guns off); then CheckSinking each frame (sound, foam)
    ///   plane shot down → UnitAir.HitAirUnit (smoke and fire, roster); hits the water → graphic hidden + splash
    ///   escaped unit (destroyed and inactive on the host) → Unit.DestroyUnit
    ///   fires → DamageableSea.SetFire on matching cells (vanilla placement and size); hull shading → the damage shader
    /// The client's own damage-control loop (DamageableSea.Update: repairs, fire spread) is off, so only the host's state shows.
    /// </summary>
    internal static partial class MpBattle
    {
        private const int FireZones = 3, ShadeRegions = 6, MaxFiresPerZone = 20;
        private static readonly string[] ShadeProps = { "_Damage_L1", "_Damage_L2", "_Damage_L3", "_Damage_R1", "_Damage_R2", "_Damage_R3" };
        private static readonly Dictionary<Unit, long> appliedShade = new Dictionary<Unit, long>();
        private static bool stateFresh;

        // ---------- host ----------

        private static void WriteSeaDamage(BinaryWriter w, Unit u)
        {
            DamageableSea ds = u.damageableSea;
            for (int i = 0; i < FireZones; i++)
            {
                int n = 0;
                if (ds != null && ds.fires != null && ds.firesBurning != null && i < ds.fires.Length && ds.fires[i] != null && ds.fires[i].isPlaying)
                    n = Mathf.Clamp(ds.firesBurning[i], 0, MaxFiresPerZone);
                w.Write((byte)n);
            }
            for (int i = 0; i < ShadeRegions; i++)
            {
                float v = 0f;
                if (ds != null && ds.regionDamageLevels != null && ds.regionDamageTotals != null
                    && i < ds.regionDamageLevels.Length && i < ds.regionDamageTotals.Length && ds.regionDamageTotals[i] > 0f)
                    v = Mathf.Clamp01(ds.regionDamageLevels[i] / ds.regionDamageTotals[i]);
                w.Write((byte)Mathf.RoundToInt(v * 255f));
            }
            // smoke screen + close-range AA target: the client's puppets have no unit AI, so neither ever starts there
            byte fx = 0;
            if (u.unitAI != null && u.unitAI.smokeTimer > 0f) fx |= 1;
            if (u.unitSea != null && u.unitSea.periscope != null && u.unitData.unitSubtype == UnitSubType.Submarine && PeriscopeWantedUp(u.unitSea.periscope)) fx |= 2;
            w.Write(fx);
            short aa = -1;
            if (u.unitAI != null && u.unitAI.closeAAATargets != null && u.unitAI.closeAAATargets.Count > 0 && !u.isDestroyed)
                aa = (short)EngagementManager.instance.allAirUnits.IndexOf(u.unitAI.closeAAATargets[0]);
            w.Write(aa);
        }

        private static void ReadSeaDamage(BinaryReader r, ref UnitState s)
        {
            s.Fires = r.ReadBytes(FireZones);
            s.Shade = r.ReadBytes(ShadeRegions);
            s.Fx = r.ReadByte();
            s.AaTarget = r.ReadInt16();
        }

        /// <summary>
        /// Client: the host's smoke screens (P2's own come from P2's toggle + control feedback) and every ship's close-range
        /// AA (WeaponAAA fires particle tracers at unitAI.closeAAATargets[0], which only the unit AI fills).
        /// </summary>
        private static void ClientUnitFx(Unit u, UnitState s)
        {
            if (u.unitAI == null || u.unitSea == null || u.isDestroyed) return;
            if (u.faction != Faction.Player)
            {
                bool smoke = (s.Fx & 1) != 0;
                if ((u.unitAI.smokeTimer > 0f) != smoke)
                {
                    SuppressForward = true;
                    try { u.unitSea.LaySmoke(smoke); }
                    finally { SuppressForward = false; }
                    if (smokeLogs < 4)
                    {
                        smokeLogs++;
                        Plugin.Log.LogInfo($"MP battle: host's {u.name} smoke {(smoke ? "on" : "off")} (shown here: {u.unitAI.smokeTimer > 0f})");
                    }
                }
                // the host's sub periscopes (P2's own follow P2's SCOPE button + control feedback)
                MovingMesh p = u.unitSea.periscope;
                if (p != null && u.unitData.unitSubtype == UnitSubType.Submarine && PeriscopeWantedUp(p) != ((s.Fx & 2) != 0))
                {
                    p.WakeMovingPart((s.Fx & 2) != 0 ? 1 : -1);
                    if (!u.unitSea.movingParts.Contains(p)) u.unitSea.movingParts.Add(p);
                }
            }
            List<Unit> air = EngagementManager.instance.allAirUnits;
            Unit t = s.AaTarget >= 0 && air != null && s.AaTarget < air.Count ? air[s.AaTarget] : null;
            List<Unit> list = u.unitAI.closeAAATargets;
            if (list == null) return;
            if (t == null || t.isDestroyed) { if (list.Count > 0) list.Clear(); }
            else if (list.Count != 1 || list[0] != t)
            {
                list.Clear();
                list.Add(t);
                if (aaLogs < 4)
                {
                    aaLogs++;
                    Plugin.Log.LogInfo($"MP battle: {u.name} close AA on {t.name} (host's target)");
                }
            }
        }

        // ---------- client ----------

        /// <summary>Client, after the unit's transform was applied. Fresh = a new snapshot arrived since the last frame.</summary>
        private static void ClientUnitDamage(Unit u, UnitState s, bool sea, bool fresh)
        {
            bool hostActive = (s.Flags & 1) != 0;
            if (fresh && (s.Flags & 2) != 0 && !u.isDestroyed) ClientDestroy(u, sea, hostActive);
            if (sea)
            {
                // (wrecks: vanilla Unit.Update → UnitSea.UpdateMovementData → CheckSinking still runs on the client)
                if (fresh && !u.isDestroyed && u.damageableSea != null)
                {
                    // per-zone fire counts until the exact burning cells arrive (MpBattleDc)
                    if (s.Fires != null && !dcSynced.Contains(u)) ClientFires(u.damageableSea, s.Fires);
                    if (s.Shade != null) ClientShade(u, s.Shade);
                    ClientUnitFx(u, s);
                }
            }
            else if (fresh && !u.isDestroyed) ClientAirSmoke(u, s.AirSmoke);
            else if (u.isDestroyed && u.unitGraphic != null && u.unitGraphic.activeSelf && s.Pos.y < 0f)
            {
                u.unitGraphic.SetActive(false); // vanilla: the wreck hits the water and disappears
                GameObject splash;
                if (Library.instance.particleEffectsDictionary.TryGetValue("airSplashShallow", out splash) && splash != null)
                    ObjectPoolManager.CreatePooled(splash, new Vector3(s.Pos.x, 0f, s.Pos.z), Quaternion.Euler(0f, u.transform.eulerAngles.y, 0f));
            }
        }

        /// <summary>Host: how many damage smoke trails a plane has (DamageableAir.smokeParticlesAir, vanilla caps it at 2).</summary>
        private static int AirSmokeTrails(Unit u)
        {
            DamageableAir da = u.unitAir != null ? u.unitAir.damageableAir : null;
            return da != null && da.smokeParticlesAir != null ? Mathf.Clamp(da.smokeParticlesAir.Count, 0, 3) : 0;
        }

        /// <summary>
        /// Client: add the host's damage smoke trails to a puppet plane, the way DamageableAir.ApplySmallCalibreDamageToAir
        /// does (pooled "air_smokeTrail" parented to the plane, random size and colour). Hits themselves only flash here.
        /// </summary>
        private static void ClientAirSmoke(Unit u, byte want)
        {
            DamageableAir da = u.unitAir != null ? u.unitAir.damageableAir : null;
            if (da == null || da.smokeParticlesAir == null || da.smokeParticlesAir.Count >= want) return;
            GameObject prefab;
            if (Library.instance == null || !Library.instance.particleEffectsDictionary.TryGetValue("air_smokeTrail", out prefab) || prefab == null) return;
            for (int guard = 0; guard < 3 && da.smokeParticlesAir.Count < want; guard++)
            {
                GameObject go = ObjectPoolManager.CreatePooled(prefab, u.transform.position, Quaternion.identity);
                if (go == null) return;
                go.transform.SetParent(u.transform);
                ParticleSystem ps = go.GetComponent<ParticleSystem>();
                if (ps == null) return;
                da.smokeParticlesAir.Add(ps);
                ParticleSystem.MainModule main = ps.main;
                main.startSize = new ParticleSystem.MinMaxCurve(1f, UnityEngine.Random.Range(1f, 3f));
                if (Library.instance.smokeTrailColours != null) main.startColor = Library.instance.smokeTrailColours.Evaluate(UnityEngine.Random.value);
            }
        }

        private static void ClientDestroy(Unit u, bool sea, bool hostActive)
        {
            SuppressForward = true; // vanilla's own follow-up calls (new formation leader...) are the host's business
            try
            {
                if (!hostActive)
                {
                    u.DestroyUnit(); // left the battle (out of the zone / landed): no wreck
                    if (u.faction == Faction.Player && sea) Toasts.Show(u.name + " has left the battle.");
                }
                else if (sea && u.damageableSea != null) u.damageableSea.SinkShip();
                else if (!sea && u.unitAir != null) u.unitAir.HitAirUnit();
                else u.DestroyUnit();
                if (u.unitRigidbody != null) u.unitRigidbody.isKinematic = true; // stays a puppet
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("MP battle: showing a destroyed unit failed: " + e.Message);
                if (!u.isDestroyed) u.isDestroyed = true;
            }
            finally
            {
                SuppressForward = false;
            }
        }

        /// <summary>Light or put out cells in each fire zone until the count matches the host's.</summary>
        private static void ClientFires(DamageableSea ds, byte[] target)
        {
            if (ds.firesBurning == null || ds.cellVariables == null || ds.parentUnit.unitData.unitSubtype == UnitSubType.Submarine) return;
            for (int zone = 0; zone < FireZones && zone < ds.firesBurning.Length; zone++)
            {
                int want = target[zone];
                for (int guard = 0; guard < MaxFiresPerZone && ds.firesBurning[zone] != want; guard++)
                {
                    bool light = ds.firesBurning[zone] < want;
                    CellVariable cv = FindFireCell(ds, zone, burning: !light);
                    if (cv == null) break;
                    ds.SetFire(cv, light);
                    if (!light) ds.ModifyCellIDInStatusList(cv.parentCell.cellID, ds.cellsBurning, addition: false);
                }
            }
        }

        private static CellVariable FindFireCell(DamageableSea ds, int zone, bool burning)
        {
            foreach (CellVariable cv in ds.cellVariables.Values)
            {
                if (cv == null || cv.parentCell == null || !cv.parentCell.canBurn || cv.isBurning != burning) continue;
                int[] regions = cv.parentCell.regionIndex;
                if (regions != null && regions.Length > 0 && Utilities.GetShipFireIndex(regions) == zone) return cv;
            }
            return null;
        }

        private static void ClientShade(Unit u, byte[] shade)
        {
            long packed = 0;
            for (int i = 0; i < ShadeRegions; i++) packed |= (long)shade[i] << (8 * i);
            long last;
            if (appliedShade.TryGetValue(u, out last) && last == packed) return;
            appliedShade[u] = packed;
            List<Material> mats = u.damageableSea.damageShaderMats;
            if (mats == null) return;
            foreach (Material m in mats)
            {
                if (m == null) continue;
                for (int i = 0; i < ShadeRegions; i++) m.SetFloat(ShadeProps[i], shade[i] / 255f);
            }
        }
    }

    /// <summary>Client: no local damage control (repairs, fire spread); the host's damage state is shown instead.</summary>
    [HarmonyPatch(typeof(DamageableSea), "Update")]
    internal static class MpBattleClientNoDamageControlPatch
    {
        private static bool Prefix() => !MpBattle.ClientActive;
    }

    /// <summary>Client: the remaining ways damage can start locally (kamikaze/crash, magazine, cell destruction).</summary>
    [HarmonyPatch]
    internal static class MpBattleClientNoDamagePatches2
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.ApplySimpleDamageToShip))]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.ApplyMagazineDamage))]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.DestroyCell))]
        private static bool NoLocalDamage() => !MpBattle.ClientActive;
    }
}
