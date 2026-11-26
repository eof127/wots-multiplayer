using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Random = UnityEngine.Random;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// M7 (D43 continued): the remaining places where the battle AI treats every non-player unit as the AI's, which on the
    /// host means Player 2's. Each one takes the player's branch for Player 2's units instead:
    ///   EngagementAI.ChangeAIAction — the AI's follow-ups (a ship at its last waypoint picks a target and intercepts it;
    ///     torpedo-run manoeuvres) don't run; the player gets none.
    ///   EngagementAI.OnTargetDestroyed — after a kill or a low-level attack: the player's planes climb and fly off / take the
    ///     nearest detected enemy plane; the AI's pick the "best" new sea target on their own.
    ///   UnitAI.FixedUpdate — an idle AI plane (no action, moving) is switched to "retreat"; a player's plane just circles.
    ///   UnitAIAir.RetreatUnit (via MpBattleAiOffPatches) — the short fly-off after an attack: the player's 500-unit leg, not
    ///     the AI's 15 km run (it was blocked outright before, which left the plane flying straight on, out of the battle).
    /// Submarines (v0.15.0):
    ///   UnitSea.UpdateMovementData blows a non-player sub's ballast when it passes its test depth — off for P2's subs (their
    ///     own Blow Ballast order still works).
    ///   UnitAISea.CheckNavigation makes a non-player sub dive to 75 % of test depth near other ships or while retreating — P2's
    ///     subs get the player's hazard evasion (turning) instead.
    ///   SensorManager.DetectSea: a submerged sub only sees with its periscope up and "ZoomedCamera.instance.enabled" — the
    ///     host's own periscope/binocular view. For P2's subs it is P2's view (control state flag 256).
    ///   EngagementAI.OnDetectUnit (replaced for P2's side): the player's sub dives to -3 when it detects aircraft — kept.
    /// </summary>
    internal static partial class MpBattle
    {
        /// <summary>Host: Player 2 is looking through the periscope / binoculars (per P2 sub, from the control state).</summary>
        private static readonly Dictionary<Unit, bool> guestScopeView = new Dictionary<Unit, bool>();

        /// <summary>Host: true while a P2 Blow Ballast order runs (the AI's automatic blow is refused for P2's subs).</summary>
        internal static bool GuestBallastOrder;

        internal static void GuestRulesReset()
        {
            guestScopeView.Clear();
            GuestBallastOrder = false;
        }

        /// <summary>SensorManager.DetectSea's periscope-view test, per observer (transpiled in).</summary>
        public static bool PeriscopeViewFor(Unit observer)
        {
            bool v;
            if (IsGuestUnit(observer)) return guestScopeView.TryGetValue(observer, out v) && v;
            return ZoomedCamera.instance.enabled; // vanilla
        }

        /// <summary>Host: the player's reaction of a sub that detects aircraft (vanilla OnDetectUnit, any side): dive to -3.</summary>
        internal static void GuestSubDetectsAir(Unit leadUnit, Unit detectedUnit)
        {
            if (leadUnit == null || detectedUnit == null || detectedUnit.unitAir == null) return;
            Unit lead = leadUnit.unitAI != null && leadUnit.unitAI.followingUnit != null ? leadUnit.unitAI.followingUnit : leadUnit;
            if (lead.unitAir != null || lead.unitSea == null || lead.unitData.unitSubtype != UnitSubType.Submarine) return;
            if (lead.wantedHeight == 0f && lead.unitSea.ballastBlown == 0f) lead.wantedHeight = -3f;
        }
        /// <summary>Host: UnitAIAir.RetreatUnit's player branch (the fly-off after an attack run), without its UI parts.</summary>
        internal static void GuestAirRetreat(Unit u)
        {
            UnitAI ai = u.unitAI;
            if (ai.currentAIAction == AIAction.Follow_Nav_Points) return;
            ai.navPoints.Clear();
            Vector3 p = u.transform.position;
            Vector3 pos = ai.focusedUnit != null
                ? p + (p - ai.focusedUnit.transform.position).normalized * -500f // (vanilla: 500 on along the attack line)
                : p + (p - Vector3.zero).normalized * 500f;
            HostAddWaypoint(u, pos);
            u.wantedSpeed = u.unitData.maxSpeed;
        }

        /// <summary>Host: EngagementAI.OnTargetDestroyed's player branch for one of Player 2's units.</summary>
        internal static void GuestTargetDestroyed(Unit attacker)
        {
            EngagementAI ai = EngagementAI.instance;
            attacker.unitAI.CeaseFiringAllWeapons();
            if (attacker.unitAir == null) return;
            if (attacker.wantedHeight < 2f)
            {
                attacker.wantedHeight = attacker.unitAIAir.SetAircraftWantedHeight(Random.Range(150, 300));
                ai.ChangeAIAction(attacker, AIAction.Retreat);
                return;
            }
            Unit unit = ai.GetClosestUnit(attacker, ai.playerAir); // Player 2's enemies' planes = the host's
            if (unit != null && unit.detected)
            {
                if (!ai.CheckAttackerIsAirPlayerControlled(attacker, unit))
                {
                    attacker.unitAI.focusedUnit = unit;
                    ai.AttackUnit(attacker);
                }
            }
            else if (attacker.unitAI.followingUnit != null)
            {
                ai.ChangeAIAction(attacker, AIAction.Follow_Unit);
            }
            else if (!ai.CheckAttackerIsAirPlayerControlled(attacker, unit))
            {
                ai.ChangeAIAction(attacker, AIAction.Nothing);
                attacker.wantedTurnRate = Random.value < 0.5f ? -1f : 1f;
            }
        }
    }

    [HarmonyPatch]
    internal static class MpBattleGuestRulePatches
    {
        /// <summary>ChangeAIAction: the player's branch (set the action, refresh the action text) for Player 2's units.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.ChangeAIAction))]
        private static bool ChangeAction(Unit unit, AIAction aiAction)
        {
            if (!MpBattle.IsGuestUnit(unit) || unit.unitAI == null) return true;
            if (unit.weaponParticle != null) unit.weaponParticle.FireAll(false);
            unit.unitAI.currentAIAction = aiAction;
            if (CombatInterface.instance != null && CombatInterface.instance.currentUnit == unit) CombatInterface.instance.UpdateActionTextDisplay();
            return false; // (the player's branch only re-sorts the host's own roster leaders here)
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.OnTargetDestroyed))]
        private static bool TargetDestroyed(Unit attacker)
        {
            if (!MpBattle.IsGuestUnit(attacker) || attacker.unitAI == null) return true;
            MpBattle.GuestTargetDestroyed(attacker);
            return false;
        }

        /// <summary>Host log: one of Player 2's planes releases its payload (bomb, torpedo, rockets) in an attack run.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UnitAIAir), nameof(UnitAIAir.FireSimpleWeapon))]
        private static void Released(UnitAIAir __instance, int weaponIndexToAttackWith)
        {
            Unit u = __instance.parentUnit;
            if (!MpBattle.IsGuestUnit(u) || u.weapons == null || weaponIndexToAttackWith < 0 || weaponIndexToAttackWith >= u.weapons.Count) return;
            Plugin.Log.LogInfo($"MP battle: P2 {u.name} releases {u.weapons[weaponIndexToAttackWith].weaponType} at {(__instance.focusedUnit != null ? __instance.focusedUnit.name : "?")}");
        }

        /// <summary>UnitAI.FixedUpdate turns an idle, moving non-player plane into "retreat": undo it for Player 2's planes.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UnitAI), "FixedUpdate")]
        private static void IdleBefore(UnitAI __instance, ref bool __state)
        {
            Unit u = __instance.parentUnit;
            __state = __instance.currentAIAction == AIAction.Nothing && u != null && u.unitAir != null && MpBattle.IsGuestUnit(u);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UnitAI), "FixedUpdate")]
        private static void IdleAfter(UnitAI __instance, bool __state)
        {
            if (__state && __instance.currentAIAction == AIAction.Retreat) __instance.currentAIAction = AIAction.Nothing;
        }

        /// <summary>The AI's automatic blow at test depth (UnitSea.UpdateMovementData) is refused for P2's subs.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.BlowBallast))]
        private static bool Ballast(Unit unit) => !MpBattle.IsGuestUnit(unit) || MpBattle.GuestBallastOrder;

        /// <summary>
        /// UnitAISea.CheckNavigation: its only faction tests are the AI subs' deep dives; for P2's subs the method runs as for a
        /// player's (the unit's faction is set to Player for the duration of the call and restored after).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UnitAISea), nameof(UnitAISea.CheckNavigation))]
        private static void NavBefore(UnitAISea __instance, ref int __state)
        {
            __state = -1;
            Unit u = __instance.parentUnit;
            if (u == null || u.unitData.unitSubtype != UnitSubType.Submarine || !MpBattle.IsGuestUnit(u)) return;
            __state = (int)u.faction;
            u.faction = Faction.Player;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(UnitAISea), nameof(UnitAISea.CheckNavigation))]
        private static Exception NavAfter(Exception __exception, UnitAISea __instance, int __state)
        {
            if (__state >= 0 && __instance.parentUnit != null) __instance.parentUnit.faction = (Faction)__state;
            return __exception;
        }
    }

    /// <summary>SensorManager.DetectSea: "ZoomedCamera.instance.enabled" → MpBattle.PeriscopeViewFor(observingUnit).</summary>
    [HarmonyPatch(typeof(SensorManager), "DetectSea")]
    internal static class MpBattleGuestPeriscopeViewPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo instance = AccessTools.Field(typeof(ZoomedCamera), nameof(ZoomedCamera.instance));
            MethodInfo getEnabled = AccessTools.PropertyGetter(typeof(Behaviour), nameof(Behaviour.enabled));
            MethodInfo replacement = AccessTools.Method(typeof(MpBattle), nameof(MpBattle.PeriscopeViewFor));
            var code = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i < code.Count - 1; i++)
            {
                if (!code[i].LoadsField(instance) || !code[i + 1].Calls(getEnabled)) continue;
                var load = new CodeInstruction(OpCodes.Ldarg_1); // observingUnit (instance method: arg 0 = this)
                load.labels.AddRange(code[i].labels);
                load.blocks.AddRange(code[i].blocks);
                code[i] = load;
                code[i + 1] = new CodeInstruction(OpCodes.Call, replacement);
                replaced++;
            }
            if (replaced != 1) Plugin.Log.LogWarning($"MP battle: periscope view patch matched {replaced} place(s) in DetectSea (expected 1)");
            return code;
        }
    }
}
