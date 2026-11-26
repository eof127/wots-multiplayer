using System;
using Ceto;
using HarmonyLib;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// M7 polish: one battle environment on both PCs. Vanilla EngagementData.SetupEnvironmentParameters rolls the wind,
    /// thermal layer, sonar duct, wave shape and fire/flood spread at random on each PC, so P2's conditions panel showed a
    /// layer the host didn't have (seen: host "No Layer", P2 "246 ft, 0.66") and the sea/smoke looked different. Only the
    /// host's values count (its sensors and damage decide everything), so the host sends them:
    ///   50 BattleEnv host→client {count, floats...} after its own roll and again when P2 presses Start.
    /// The client applies them after its own roll (or on arrival, if that already happened). Balance values are untouched:
    /// these are the host's rolled conditions, copied.
    /// </summary>
    internal static partial class MpBattle
    {
        private const byte MsgEnv = 50;
        private const int EnvCount = 15;
        private static float[] pendingEnv;  // client: the host's values
        private static bool clientEnvRolled; // client: its own SetupEnvironmentParameters has run in this battle
        private static bool envLogged;

        internal static void EnvReset()
        {
            pendingEnv = null;
            clientEnvRolled = false;
            envLogged = false;
            ammoLogs = smokeLogs = aaLogs = 0;
        }

        /// <summary>Host: after its roll, and when P2 is ready (in case P2's battle loaded after the first message).</summary>
        internal static void HostSendEnv()
        {
            EngagementData ed = EngagementData.instance;
            EngagementManager em = EngagementManager.instance;
            if (!HostActive || !AnyoneInBattle || ed == null || em == null || em.waveSpectrum == null || Ocean.Instance == null) return;
            WaveSpectrum ws = em.waveSpectrum;
            float[] v =
            {
                ed.currentVisibilityModifier, ed.currentMaxSeaVisibility, ed.currentRadarModifier, ed.currentSonarModifier,
                ed.currentLayerDepth, ed.currentLayerModifier, ed.currentDuctModifier,
                ws.windSpeed, Ocean.Instance.windDir, ed.windDirectionForce.x, ed.windDirectionForce.y,
                ws.choppyness, ws.waveSpeed, ed.baseFireSpreadP, ed.baseFloodSpreadP,
            };
            SendBattleAll(MsgEnv, Protocol.Build(w =>
            {
                w.Write((byte)v.Length);
                foreach (float f in v) w.Write(f);
            }));
        }

        private static void ClientEnv(byte[] payload)
        {
            using (var r = Protocol.Reader(payload))
            {
                int n = r.ReadByte();
                var v = new float[n];
                for (int i = 0; i < n; i++) v[i] = r.ReadSingle();
                pendingEnv = v;
            }
            if (clientEnvRolled) ApplyEnv();
        }

        /// <summary>Client: its own roll just happened (vanilla setup coroutine); replace it with the host's values.</summary>
        internal static void ClientEnvRolled()
        {
            clientEnvRolled = true;
            ApplyEnv();
        }

        private static void ApplyEnv()
        {
            float[] v = pendingEnv;
            EngagementData ed = EngagementData.instance;
            EngagementManager em = EngagementManager.instance;
            if (v == null || v.Length < EnvCount || ed == null || em == null) return;
            ed.currentVisibilityModifier = v[0];
            ed.currentMaxSeaVisibility = v[1];
            ed.currentRadarModifier = v[2];
            ed.currentSonarModifier = v[3];
            ed.currentLayerDepth = v[4];
            ed.currentLayerModifier = v[5];
            ed.currentDuctModifier = v[6];
            ed.windDirectionForce = new UnityEngine.Vector2(v[9], v[10]);
            ed.baseFireSpreadP = v[13];
            ed.baseFloodSpreadP = v[14];
            if (em.waveSpectrum != null)
            {
                em.waveSpectrum.windSpeed = v[7];
                em.waveSpectrum.choppyness = v[11];
                em.waveSpectrum.waveSpeed = v[12];
            }
            if (Ocean.Instance != null) Ocean.Instance.windDir = v[8];
            try
            {
                if (EnvironmentManager.instance != null) EnvironmentManager.instance.SetupFog(); // fog density follows visibility
                CombatInterface ci = CombatInterface.instance;
                if (ci != null && ci.conditionsText != null && ci.conditionsText.Length > 1 && ci.conditionsText[1] != null)
                    ci.conditionsText[1].text = em.GetConditionsData(); // the objectives panel's Conditions column
            }
            catch (Exception e) { Plugin.Log.LogWarning("MP battle: environment refresh failed: " + e.Message); }
            if (!envLogged)
            {
                envLogged = true;
                string layer = v[5] >= 1f ? "no layer" : $"layer at {-v[4] * 10f:0} m ×{v[5]:0.00}, duct ×{v[6]:0.0}";
                Plugin.Log.LogInfo($"MP battle: using the host's environment: visibility {v[0] * 100f:0} %, wind {v[7] * 2f:0.0} kn from {v[8]:0}°, {layer}");
            }
        }
    }

    [HarmonyPatch(typeof(EngagementData), nameof(EngagementData.SetupEnvironmentParameters))]
    internal static class MpBattleEnvPatch
    {
        private static void Postfix(bool randomise)
        {
            if (!randomise || !ModState.MultiplayerActive) return;
            try
            {
                if (MpBattle.HostActive && Multiplayer.Session.IsHost) MpBattle.HostSendEnv();
                else if (MpBattle.ClientActive) MpBattle.ClientEnvRolled();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("MP battle environment sync failed: " + e.Message);
            }
        }
    }
}
