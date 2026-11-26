using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// M6d: turret aim on Player 2's copy. The client's mounts never aim on their own (no fire modes there), so without this
    /// its turrets stay at rest while the host's shells leave from the host's (trained) muzzles.
    ///   47 BattleTurrets host→client, 5 Hz {sea unit count, per unit: angle count, angles (short, centi-degrees)}
    /// Per sea unit, in weapon-list order, every gun/torpedo WeaponMount (AA mounts skipped to save bandwidth): for each
    /// mount position its training angle (baseTransforms[i] local Y) and, if it elevates, its elevation (elevateTransforms[i]
    /// local X). Both PCs build the same weapons from the same unit data, so the order matches; a unit whose count differs is
    /// skipped. The client eases the transforms toward the host's angles.
    /// </summary>
    internal static partial class MpBattle
    {
        private const byte MsgTurrets = 47;
        private const float TurretInterval = 0.2f;
        private static float lastTurrets;
        private static short[][] turretTargets = new short[0][];

        private static bool IsAimedMount(Weapon w, out WeaponMount wm)
        {
            wm = w as WeaponMount;
            if (wm == null || wm.baseTransforms == null) return false;
            WeaponType t = wm.weaponType;
            return t == WeaponType.Primary || t == WeaponType.Secondary || t == WeaponType.Torpedo;
        }

        private static bool Elevates(WeaponMount wm, int i) =>
            wm.mountData != null && wm.mountData.elevates && wm.elevateTransforms != null && i < wm.elevateTransforms.Length && wm.elevateTransforms[i] != null;

        private static short Centi(float degrees) => (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.DeltaAngle(0f, degrees) * 100f), -18000, 18000);

        // ---------- host ----------

        private static void HostSendTurrets()
        {
            if (Time.realtimeSinceStartup - lastTurrets < TurretInterval) return;
            lastTurrets = Time.realtimeSinceStartup;
            EngagementManager em = EngagementManager.instance;
            if (em == null || em.allSeaUnits == null || em.allSeaUnits.Count == 0) return;
            var angles = new List<short>();
            SendBattleAll(MsgTurrets, Protocol.Build(w =>
            {
                w.Write(em.allSeaUnits.Count);
                foreach (Unit u in em.allSeaUnits)
                {
                    angles.Clear();
                    if (u != null && !u.isDestroyed && u.gameObject.activeSelf && u.weapons != null)
                    {
                        foreach (Weapon wp in u.weapons)
                        {
                            WeaponMount wm;
                            if (!IsAimedMount(wp, out wm)) continue;
                            for (int i = 0; i < wm.baseTransforms.Length; i++)
                            {
                                Transform b = wm.baseTransforms[i];
                                angles.Add(b != null ? Centi(b.localEulerAngles.y) : (short)0);
                                if (Elevates(wm, i)) angles.Add(Centi(wm.elevateTransforms[i].localEulerAngles.x));
                            }
                        }
                    }
                    w.Write((ushort)angles.Count);
                    foreach (short a in angles) w.Write(a);
                }
            }));
        }

        // ---------- client ----------

        private static void ClientTurrets(byte[] payload)
        {
            using (var r = Protocol.Reader(payload))
            {
                int n = r.ReadInt32();
                if (n < 0 || n > 2000) return;
                var t = new short[n][];
                for (int u = 0; u < n; u++)
                {
                    int m = r.ReadUInt16();
                    var a = new short[m];
                    for (int i = 0; i < m; i++) a[i] = r.ReadInt16();
                    t[u] = a;
                }
                turretTargets = t;
            }
        }

        /// <summary>Client, every frame (ClientApply): ease each puppet's turrets toward the host's angles.</summary>
        private static void ClientApplyTurrets(float k)
        {
            EngagementManager em = EngagementManager.instance;
            short[][] targets = turretTargets;
            int n = Math.Min(em.allSeaUnits.Count, targets.Length);
            for (int ui = 0; ui < n; ui++)
            {
                Unit u = em.allSeaUnits[ui];
                short[] a = targets[ui];
                if (u == null || a == null || a.Length == 0 || u.isDestroyed || u.weapons == null) continue;
                if (CountAngles(u) != a.Length) continue; // different weapon layout: don't guess
                int j = 0;
                foreach (Weapon wp in u.weapons)
                {
                    WeaponMount wm;
                    if (!IsAimedMount(wp, out wm)) continue;
                    for (int i = 0; i < wm.baseTransforms.Length; i++)
                    {
                        Transform b = wm.baseTransforms[i];
                        float yaw = a[j++] / 100f;
                        if (b != null) b.localRotation = Quaternion.Slerp(b.localRotation, Quaternion.Euler(0f, yaw, 0f), k);
                        if (Elevates(wm, i))
                        {
                            float pitch = a[j++] / 100f;
                            Transform e = wm.elevateTransforms[i];
                            e.localRotation = Quaternion.Slerp(e.localRotation, Quaternion.Euler(pitch, 0f, 0f), k);
                        }
                    }
                }
            }
        }

        private static int CountAngles(Unit u)
        {
            int c = 0;
            foreach (Weapon wp in u.weapons)
            {
                WeaponMount wm;
                if (!IsAimedMount(wp, out wm)) continue;
                for (int i = 0; i < wm.baseTransforms.Length; i++) c += Elevates(wm, i) ? 2 : 1;
            }
            return c;
        }
    }
}
