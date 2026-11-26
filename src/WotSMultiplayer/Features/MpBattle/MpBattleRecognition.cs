using System;
using System.IO;
using HarmonyLib;
using WotSMultiplayer.Core;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// Ship recognition for Player 2 (user report 2026-11-25: P2 had no recognition manual in battle).
    /// MpBattle.ClientEnter moves the manual into P2's battle UI, as vanilla GenerateCampaignEngagement does on the host.
    /// Classifying (UnitReference.ClassifyContact) then works on P2's copy as in vanilla: the contact's label changes and
    /// classifiedModifier is set there. Gunnery is worked out on the host, though: vanilla Director adds the TARGET's
    /// classifiedModifier to the best firing solution. So P2's manual classification is sent to the host
    /// (BattleCmd.Classify: target, prefab) and the host sets classifiedAs / classifiedAsSubtype / classifiedModifier on its
    /// copy of that ship with vanilla's CalculateClassifiedModifer (right class: class + type bonus; right type only: type
    /// bonus; wrong: none). The host's own label for its ship is not touched. Unchanged vanilla rule for the non-player
    /// side: a target with no bonus yet gets the full bonus once the solution passes Config.autoClassifyAt.
    /// </summary>
    internal static partial class MpBattle
    {
        /// <summary>Client: P2 classified a host ship by hand.</summary>
        internal static void ClientClassified(Unit target)
        {
            if (!ClientActive || target == null || target.faction == Faction.Player || target.unitSea == null) return;
            string prefab = target.classifiedAs;
            if (string.IsNullOrEmpty(prefab)) return;
            Unit carrier = CombatInterface.instance != null ? CombatInterface.instance.currentUnit : null;
            if (carrier == null || carrier.faction != Faction.Player || carrier.isDestroyed) carrier = FirstOwnUnit();
            if (carrier == null) return;
            SendControl(carrier, BattleCmd.Classify, w => { WriteUnitRef(w, target); w.Write(prefab); });
        }

        /// <summary>Host: apply P2's classification to the host's copy of the ship (gunnery bonus for P2's directors).</summary>
        private static void HostClassify(BinaryReader r)
        {
            Unit t = ResolveUnit(r.ReadByte(), r.ReadInt32());
            string prefab = r.ReadString();
            if (t == null || t.faction != Faction.Player || t.unitSea == null || t.isDestroyed || string.IsNullOrEmpty(prefab)) return;
            EngagementManager em = EngagementManager.instance;
            UnitData data;
            if (em == null || UnitReference.instance == null || !em.unitDataDictionary.TryGetValue(prefab, out data) || data == null) return;
            t.classifiedAs = prefab;
            t.classifiedAsSubtype = data.unitSubtype;
            t.classifiedModifier = UnitReference.instance.CalculateClassifiedModifer(t);
            Plugin.Log.LogInfo($"MP battle: P2 classified {t.name} as {prefab} → solution bonus {t.classifiedModifier:0.###}");
        }
    }

    [HarmonyPatch(typeof(UnitReference), nameof(UnitReference.ClassifyContact))]
    internal static class MpBattleClassifyPatch
    {
        private static void Postfix(Unit targetUnit, bool auto)
        {
            if (auto || !MpBattle.ClientActive) return;
            try { MpBattle.ClientClassified(targetUnit); }
            catch (Exception e) { Plugin.Log.LogWarning("MP battle: sending the classification failed: " + e.Message); }
        }
    }
}
