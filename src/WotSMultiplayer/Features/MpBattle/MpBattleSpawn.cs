using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// D52 (user, 2026-11-25): multiplayer battles start a bit closer. Vanilla CampaignManager.GenerateCampaignEngagement
    /// places every group at its campaign-map offset from the first one ((pos2 − pos1) × mapWidth × 0.1 world units, the
    /// north–south part halved), so a battle starts at about the distance the fleets met at — often 12–20 km by day or by
    /// radar, which made retreating easy. Historical Guadalcanal night actions opened fire at ~8–14 km (Washington vs
    /// Kirishima 8,400 yd; South Dakota 15,700 yd; Savo Island ~10,000 yd) after radar contact at ~16–25 km.
    /// Here every group's offset is scaled by [Battle] MpSpawnDistanceFactor (0.75: 16 km → 12 km), never below 5 km
    /// (about the night lookout limit) unless the offset was already smaller. Host only, MP battles only.
    /// </summary>
    [HarmonyPatch(typeof(CampaignManager), "GenerateUnitGroupData")]
    internal static class MpBattleSpawnPatch
    {
        private const float FloorUnits = 500f; // 5 km (world units are 10 m)

        private static void Prefix(ref Vector3 position)
        {
            if (!ModState.MultiplayerActive || !MpBattle.HostActive) return;
            float f = Mathf.Clamp(Plugin.MpSpawnDistanceFactor.Value, 0.3f, 1.5f);
            if (Mathf.Approximately(f, 1f)) return;
            float d = new Vector2(position.x, position.z).magnitude;
            if (d < 1f) return; // the first group sits at the origin
            float nd = Mathf.Max(d * f, Mathf.Min(d, FloorUnits));
            position *= nd / d;
            Plugin.Log.LogInfo($"MP battle: group spawn offset {d / 100f:0.0} km → {nd / 100f:0.0} km (factor {f:0.##}, D52)");
        }
    }
}
