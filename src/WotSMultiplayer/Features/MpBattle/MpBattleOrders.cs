using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Features.MpCampaign;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>Player 2's helm orders in battle (message 44). Values are on the wire: append only.</summary>
    internal enum BattleCmd : byte
    {
        Speed = 0, Turn = 1, Waypoint = 2, Target = 3, FireAtWill = 4, Cease = 5, Attack = 6, Mount = 7,
        Fire = 8, State = 9, BombDoors = 10, Gear = 11, Follow = 12, StopFollow = 13, ReturnFollow = 14, FormUp = 15,
        Kamikaze = 16, Ballast = 17, FixedGuns = 18,
        DcParty = 19, AutoDc = 20, CounterFlood = 21, Scuttle = 22, // MpBattleDc
        Height = 23, // altitude / depth slider (was part of the polled control state before protocol 10)
        Disengage = 24, Retreat = 25, // MpBattleRetreat (D47)
        AllowRetreat = 26,            // P2 allows the host's retreat (D48)
        Classify = 27,                // P2 classified a host ship with the recognition manual (MpBattleRecognition)
    }

    /// <summary>
    /// M6b: Player 2 commands their own ships and planes in a live battle.
    ///   44 BattleOrder client→host {unit kind 0 sea / 1 air, index in allSeaUnits / allAirUnits, BattleCmd, values}
    ///   45 BattleReady client→host {} (Player 2 pressed Start)
    /// The client's vanilla helm UI runs on its puppet unit (so the panel shows the order); the same order goes to the host,
    /// which applies it to the real unit exactly like the vanilla handler (CombatInterface.SpeedSlider / TurnSlider /
    /// SetUnitWaypointPosition). On the host Player 2's units are the "other" side; the battle AI's commander layer for
    /// that side is switched off (MpBattleAiOffPatches) and they get the player's defaults and automation instead.
    /// </summary>
    internal static partial class MpBattle
    {
        private static bool hostReady, guestReady;
        /// <summary>Host: who gave the battle order being run ("P2", "P3" …), for the log.</summary>
        private static string orderWho = "P2";
        private static int snapsReceived;

        // ---------- start: both players ready ----------

        /// <summary>Client pressed Start: tell the host, don't start locally.</summary>
        internal static bool ClientPressedStart()
        {
            if (Multiplayer.Session.IsSubordinate)
            {
                Toasts.Show("The commanders (Player 1 and Player 2) start the battle.");
                return false;
            }
            Multiplayer.Session.SendGame(MsgReady, new byte[0]);
            Toasts.Show("Ready. The battle starts when the host is ready too.");
            return false;
        }

        /// <summary>Host pressed Start: only when Player 2 is ready as well (or gone).</summary>
        internal static bool HostMayStart()
        {
            hostReady = true;
            if (guestReady || !Multiplayer.Session.IsConnected) return true;
            Toasts.Show("Waiting for Player 2 to press Start.");
            return false;
        }

        // ---------- client: orders ----------

        private static bool UnitRef(Unit u, out byte kind, out int index)
        {
            EngagementManager em = EngagementManager.instance;
            kind = 0;
            index = -1;
            if (u == null || em == null || em.allSeaUnits == null) return false;
            index = em.allSeaUnits.IndexOf(u);
            if (index < 0)
            {
                index = em.allAirUnits.IndexOf(u);
                kind = 1;
            }
            return index >= 0;
        }

        /// <summary>
        /// Client: the speed (Speed), rudder (Turn) or altitude/depth (Height) the vanilla slider just set on one of our units
        /// → host. Only real changes by Player 2: the slider handlers also run when the panel is redrawn (then
        /// lockSliderInput is set and vanilla only updates the text), and nothing is sent while the battle is still loading.
        /// </summary>
        internal static void SendHelm(Unit u, BattleCmd cmd, float before)
        {
            if (u == null || u.faction != Faction.Player || u.isDestroyed || u.lockSliderInput || firstApply || SetupDepth > 0) return;
            float v = cmd == BattleCmd.Speed ? u.wantedSpeed : cmd == BattleCmd.Turn ? u.wantedTurnRate : u.wantedHeight;
            if (Mathf.Approximately(before, v)) return;
            byte kind;
            int index;
            if (!UnitRef(u, out kind, out index)) return;
            if (SendBattleOrder(Protocol.Build(w => { w.Write(kind); w.Write(index); w.Write((byte)cmd); w.Write(v); })))
                localOrderAt[u] = Time.realtimeSinceStartup; // (refused: the next snapshot shows the real value at once)
        }

        /// <summary>
        /// Client: every battle order (message 44) goes through here. A subordinate's order for a unit its commander has
        /// selected or locked, or a retreat decision, isn't sent (a toast says why); the host checks the same again.
        /// </summary>
        internal static bool SendBattleOrder(byte[] payload)
        {
            NetSession s = Multiplayer.Session;
            byte kind, cmd;
            int index;
            if (s.IsSubordinate && LockRules.ReadBattleOrderHead(payload, out kind, out index, out cmd))
            {
                if (LockRules.IsCommanderOnlyBattleCmd(cmd)) { Toasts.Show("Only your commander decides on disengaging and retreating."); return false; }
                if ((BattleCmd)cmd == BattleCmd.Classify && s.LocalTeam == 0) return false; // the recognition bonus is Player 2's side's
                if (TeamLocks.ClientRefusesBattle(kind, index)) return false;
            }
            s.SendGame(MsgOrder, payload);
            return true;
        }

        /// <summary>
        /// > 0 while vanilla's battle setup coroutine (EngagementData.ApplyEngagementData) runs a step: it sets the current
        /// unit's height/rudder/speed sliders itself (seen sending a load-time altitude as Player 2's order).
        /// </summary>
        internal static int SetupDepth;

        /// <summary>Client: when Player 2 last changed one of this unit's sliders (snapshots don't overwrite it for a moment).</summary>
        private static readonly Dictionary<Unit, float> localOrderAt = new Dictionary<Unit, float>();

        private static bool RecentLocalOrder(Unit u)
        {
            float t;
            return localOrderAt.TryGetValue(u, out t) && Time.realtimeSinceStartup - t < 1.5f;
        }

        internal static void SendWaypoint(Unit u, Vector3 pos)
        {
            if (u == null || u.faction != Faction.Player) return;
            byte kind;
            int index;
            if (!UnitRef(u, out kind, out index)) return;
            SendBattleOrder(Protocol.Build(w => { w.Write(kind); w.Write(index); w.Write((byte)BattleCmd.Waypoint); w.Write(pos.x); w.Write(pos.z); }));
        }

        /// <summary>Client: target chosen (vanilla accepted it) for one of our units → host. Attack = the V key (also manoeuvre).</summary>
        internal static void SendTarget(Unit u, Unit target, BattleCmd cmd = BattleCmd.Target)
        {
            if (u == null || target == null || u.faction != Faction.Player) return;
            byte kind, tkind;
            int index, tindex;
            if (!UnitRef(u, out kind, out index) || !UnitRef(target, out tkind, out tindex)) return;
            SendBattleOrder(Protocol.Build(w => { w.Write(kind); w.Write(index); w.Write((byte)cmd); w.Write(tkind); w.Write(tindex); }));
        }

        /// <summary>Client: fire at will on/off (FireAtWill) or cease all fire (Cease) for one of our units → host.</summary>
        internal static void SendGunnery(Unit u, BattleCmd cmd, bool on)
        {
            if (u == null || u.faction != Faction.Player) return;
            byte kind;
            int index;
            if (!UnitRef(u, out kind, out index)) return;
            SendBattleOrder(Protocol.Build(w => { w.Write(kind); w.Write(index); w.Write((byte)cmd); w.Write(on); }));
        }

        /// <summary>Client diagnostics: how far the puppet of sea unit 0 is from the host's position.</summary>
        private static void LogSyncError()
        {
            EngagementManager em = EngagementManager.instance;
            if (em == null || em.allSeaUnits == null || em.allSeaUnits.Count == 0 || seaTargets.Length == 0 || em.allSeaUnits[0] == null) return;
            Vector3 local = em.allSeaUnits[0].transform.position, host = seaTargets[0].Pos;
            float err = new Vector2(local.x - host.x, local.z - host.z).magnitude;
            // every ship's host position (x,z world units; 1 unit = 10 m): lets a test steer the two sides together
            var where = new System.Text.StringBuilder();
            for (int i = 0; i < seaTargets.Length && i < 12; i++)
                if ((seaTargets[i].Flags & 4) != 0) where.Append($" {(i < em.allSeaUnits.Count && em.allSeaUnits[i] != null ? em.allSeaUnits[i].name : "?")}@{seaTargets[i].Pos.x:F0},{seaTargets[i].Pos.z:F0}");
            Plugin.Log.LogInfo($"MP battle sync: {em.allSeaUnits[0].name} off by {err:F2}; units host {seaTargets.Length}+{airTargets.Length}, here {em.allSeaUnits.Count}+{em.allAirUnits.Count};{where}");
        }

        // ---------- host: orders ----------

        /// <summary>Host: a subordinate's battle order — refused if its commander has that unit selected or locked.</summary>
        private static void OnSubordinateMessage(int index, byte type, byte[] payload)
        {
            if (type != MsgOrder || !HostActive || !ModState.MultiplayerActive) return;
            int team = Multiplayer.Session.SubordinateTeam(index);
            byte kind, cmd;
            int unit;
            if (team < 0 || !LockRules.ReadBattleOrderHead(payload, out kind, out unit, out cmd)) return;
            if (LockRules.IsCommanderOnlyBattleCmd(cmd) || ((BattleCmd)cmd == BattleCmd.Classify && team == 0)) return;
            BlockReason why = TeamLocks.BattleTeam(team).Blocked(LockRules.BattleUnitId(kind, unit));
            if (why != BlockReason.None)
            {
                string name = TeamLocks.UnitName(ResolveUnit(kind, unit));
                Multiplayer.Session.SendGameToPlayer(index, 27, Protocol.Build(w => w.Write("Order refused: " + TeamLocks.Describe(why, team, name) + ".")));
                Plugin.Log.LogInfo($"MP battle: P{index + 1} order {(BattleCmd)cmd} for {name} refused ({why})");
                return;
            }
            HostOnOrder(type, payload, index);
        }

        /// <summary>Host: a battle order from Player 2 (sender 1) or a subordinate (2+), for its team's units only.</summary>
        private static void HostOnOrder(byte type, byte[] payload, int sender)
        {
            if (!HostActive) return;
            // the host's own team (a team-0 subordinate) commands the host's units; Player 2's team the other side
            bool hostSide = sender >= 2 && Multiplayer.Session.SubordinateTeam(sender) == 0;
            string who = sender == 1 ? "P2" : "P" + (sender + 1);
            orderWho = who;
            try
            {
                if (type == MsgReady)
                {
                    if (sender != 1) return; // only Player 2 is asked to be ready
                    guestReady = true;
                    DcForceFull(); // P2's battle is loaded now: send every damage-control cell
                    HostSendEnv(); // and the environment again, in case the first copy arrived before P2's battle existed
                    Toasts.Show("Player 2 is ready.");
                    if (hostReady && EngagementManager.instance.strategicCombatMode) EngagementManager.instance.StartCombat(true);
                    return;
                }
                using (var r = Protocol.Reader(payload))
                {
                    byte kind = r.ReadByte();
                    int index = r.ReadInt32();
                    var cmd = (BattleCmd)r.ReadByte();
                    EngagementManager em = EngagementManager.instance;
                    List<Unit> list = kind == 0 ? em.allSeaUnits : em.allAirUnits;
                    if (list == null || index < 0 || index >= list.Count) return;
                    Unit u = list[index];
                    if (u == null || (u.faction == Faction.Player) != hostSide || u.isDestroyed) return; // only the sender's team's units
                    if (cmd != BattleCmd.Speed && cmd != BattleCmd.Turn && cmd != BattleCmd.State) Plugin.Log.LogInfo($"MP battle: {who} order {cmd} for {u.name}");
                    switch (cmd)
                    {
                        case BattleCmd.Speed:
                        {
                            float v = r.ReadSingle();
                            Plugin.Log.LogInfo($"MP battle: {who} speed {u.name} → {Utilities.ConvertMetrePerSecondToKnots(v):F0} kn");
                            HostSetSpeed(u, v);
                            break;
                        }
                        case BattleCmd.Turn: HostSetTurn(u, r.ReadSingle()); break;
                        case BattleCmd.Height: HostSetHeight(u, r.ReadSingle()); break;
                        case BattleCmd.Waypoint: HostAddWaypoint(u, new Vector3(r.ReadSingle(), 0f, r.ReadSingle())); break;
                        case BattleCmd.Target:
                        case BattleCmd.Attack:
                        {
                            byte tkind = r.ReadByte();
                            int tindex = r.ReadInt32();
                            List<Unit> tlist = tkind == 0 ? em.allSeaUnits : em.allAirUnits;
                            if (tindex < 0 || tindex >= tlist.Count || tlist[tindex] == null || tlist[tindex] == u) break;
                            Unit target = tlist[tindex];
                            // CombatInterface.AttackUnit's rules: planes only attack a detected target; and never a friendly
                            // (the client forwards V even when vanilla refused it there)
                            if (cmd == BattleCmd.Attack && (target.faction == u.faction || target.isDestroyed || (u.unitAir != null && !target.detected)))
                            {
                                Plugin.Log.LogInfo($"MP battle: {who} attack by {u.name} on {target.name} refused (friendly, destroyed or not detected)");
                                break;
                            }
                            HostSetTarget(u, target);
                            if (cmd == BattleCmd.Attack) HostAttack(u);
                            break;
                        }
                        case BattleCmd.FireAtWill: HostFireAtWill(u, r.ReadBoolean()); break;
                        case BattleCmd.Mount: HostMount(u, r); break;
                        case BattleCmd.Fire: HostFire(u, r); break;
                        case BattleCmd.State: HostState(u, r); break;
                        default: HostControl(u, cmd, r); break;
                        case BattleCmd.Cease:
                            r.ReadBoolean();
                            u.unitAI.CeaseFiringAllWeapons();
                            EngagementAI.instance.ApplyFireAtWillToUnit(u, false);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("MP battle order failed: " + e);
            }
        }

        /// <summary>Same rules as CombatInterface.SpeedSlider.</summary>
        private static void HostSetSpeed(Unit u, float v)
        {
            if (u.unitAI.currentAIAction == AIAction.Follow_Unit || u.unitAI.currentManeuvre != Maneuvre.None) return;
            if (float.IsNaN(v)) return;
            if (u.unitSea != null)
            {
                v = Mathf.Clamp(v, -5f, u.unitData.maxSpeed);
                if (Mathf.Abs(v) < Utilities.ConvertKnotToMetrePerSecond(0.5f)) v = 0f;
            }
            else v = Mathf.Clamp(v, 0f, u.unitData.maxSpeed * 1.5f);
            u.wantedSpeed = v;
        }

        /// <summary>Same rules as CombatInterface.TurnSlider (the rudder is ignored while following waypoints or a leader).</summary>
        private static void HostSetTurn(Unit u, float v)
        {
            AIAction a = u.unitAI.currentAIAction;
            if (a == AIAction.Follow_Unit || a == AIAction.Follow_Nav_Points || u.unitAI.currentManeuvre != Maneuvre.None || float.IsNaN(v)) return;
            u.wantedTurnRate = Mathf.Clamp(v, -u.unitData.maxTurnRate, u.unitData.maxTurnRate);
        }

        /// <summary>Same rules as CombatInterface.HeightSlider (planes in formation or mid-manoeuvre keep their height).</summary>
        private static void HostSetHeight(Unit u, float h)
        {
            if (float.IsNaN(h) || float.IsInfinity(h) || u.unitData.dive <= 0f) return;
            if (u.unitAI.currentAIAction == AIAction.Follow_Unit && u.unitSea == null) return;
            if (u.unitAI.currentManeuvre != Maneuvre.None) return;
            u.wantedHeight = Mathf.Clamp(h, -1000f, 5000f);
            Plugin.Log.LogInfo($"MP battle: {orderWho} height {u.name} → {u.wantedHeight:F1}");
        }

        /// <summary>CombatInterface.SetUnitWaypointPosition without its UI parts.</summary>
        private static void HostAddWaypoint(Unit u, Vector3 pos)
        {
            if (float.IsNaN(pos.x) || float.IsNaN(pos.z)) return;
            u.unitAI.navPoints.Add(pos);
            if (u.unitAI.currentAIAction != AIAction.Follow_Nav_Points)
            {
                EngagementAI.instance.ChangeAIAction(u, AIAction.Follow_Nav_Points);
                u.unitAI.enabled = true;
                if (u.unitData.unitType == UnitType.Sea && u.wantedSpeed < Config.instance.defaultNavigationSpeed)
                    u.wantedSpeed = Config.instance.defaultNavigationSpeed;
            }
        }

        private static bool CombatStarted => EngagementManager.instance != null && !EngagementManager.instance.strategicCombatMode;

        /// <summary>CombatInterface.SetFocusUnitForCurrentUnit without its UI parts (map colours, panels).</summary>
        private static void HostSetTarget(Unit u, Unit target)
        {
            u.unitAI.focusedUnit = target;
            UnitSea sea = u.unitSea;
            if (sea != null && sea.director != null && target.unitData.unitType == UnitType.Sea && target.mapUnit != null)
            {
                for (int i = 0; i < sea.director.trackingTargets.Length; i++)
                {
                    if (sea.director.trackingTargets[i] != null) continue;
                    sea.director.trackingTargets[i] = target.mapUnit;
                    sea.director.CalculateTMAAgainstTarget(sea.director.trackingTargets[i], Config.instance.directorTMARate, Config.instance.directorMaxSolution);
                    sea.director.enabled = true;
                }
            }
            if (sea != null && u.unitAI.autoGunnery && CombatStarted)
            {
                u.unitAISea.FireSecondaryGuns();
                u.unitAISea.FirePrimaryGuns();
            }
        }

        /// <summary>The per-unit part of CombatInterface.AttackUnit (the V key) without its UI parts.</summary>
        private static void HostAttack(Unit u)
        {
            if (u.unitAir != null) EngagementAI.instance.StopFollowingUnit(u);
            // vanilla keeps a ship's waypoints, but a player's ship then intercepts (UnitAISea.AttackUnit's player branch)
            // while a non-player one keeps following them; clearing them gives P2's ships the player's behaviour
            u.unitAI.navPoints.Clear();
            EngagementAI.instance.AttackUnit(u);
        }

        /// <summary>Host, MP battle: is this one of Player 2's units (the host's "enemy" side)?</summary>
        internal static bool IsGuestUnit(Unit u) => HostActive && u != null && u.faction != Faction.Player;

        /// <summary>CombatInterface.EnableAtWill / CeaseAutoFire without their UI parts.</summary>
        private static void HostFireAtWill(Unit u, bool on)
        {
            EngagementAI.instance.ApplyFireAtWillToUnit(u, on);
            if (on && u.unitSea != null && CombatStarted)
            {
                u.unitAISea.FireSecondaryGuns();
                u.unitAISea.FirePrimaryGuns();
            }
        }

        // ---------- host: Player 2's side gets the player's rules, not the AI's ----------

        /// <summary>Host: the player's starting defaults (EngagementAI.InitialiseEngagementAI) for Player 2's units.</summary>
        internal static void GuestSideDefaults()
        {
            EngagementAI ai = EngagementAI.instance;
            foreach (Unit u in ai.otherSea)
            {
                u.unitAI.autoAAA = true;
                u.usingRadar = Config.instance.radarDefaultOn;
                u.usingActiveSonar = Config.instance.activeSonarDefaultOn;
                if (u.unitData.unitSubtype != UnitSubType.Submarine) ai.ApplyFireAtWillToUnit(u, Config.instance.autoGunneryDefaultOn);
                u.unitAI.noNavEvade = !Config.instance.autoHazardEvasionDefaultOn;
            }
            foreach (Unit u in ai.otherAir)
            {
                // vanilla gives the AI's campaign air groups random waypoints; a player's planes wait for orders
                u.unitAI.navPoints.Clear();
                u.unitAI.currentAIAction = AIAction.Nothing;
                bool dogfight = Config.instance.autoDogfightDefaultOn &&
                                (u.unitData.unitSubtype == UnitSubType.Fighter || !Config.instance.autoDogfightFightersOnly) && u.weaponParticle != null;
                ai.ApplyFireAtWillToUnit(u, dogfight);
            }
            Plugin.Log.LogInfo($"MP battle: Player 2's side set up like a player ({ai.otherSea.Count} ships, {ai.otherAir.Count} planes)");
            LogCombatDifficulty();
        }

        /// <summary>Host: Player 2's side detected something → the player's reaction (auto-gunnery opens fire), not the AI's.</summary>
        internal static void GuestSideDetected(Unit detectedUnit)
        {
            EngagementAI ai = EngagementAI.instance;
            EngagementManager.instance.engagementData.playerEverDetected = true;
            if (detectedUnit.unitSea != null)
            {
                foreach (Unit u in ai.otherSea)
                    if (u.unitAI.autoGunnery)
                    {
                        u.unitAISea.FireSecondaryGuns();
                        u.unitAISea.FirePrimaryGuns();
                    }
                return;
            }
            foreach (Unit u in ai.otherAir)
            {
                Unit f = u.unitAI.focusedUnit;
                if (detectedUnit.unitAir != null && u.unitAI.autoGunnery && (f == null || f.faction != Faction.Player || f.unitAir == null))
                    ai.AIRvsAIR(u);
            }
        }
    }

    // ---------- patches ----------

    /// <summary>
    /// Host, MP battle: the battle AI's commander layer doesn't act for Player 2's side (target selection with attack
    /// manoeuvres, damage/aggression-based retreat, coordinated air reactions, AI disengage, initial AI behaviours).
    /// The unit layer (UnitAI: following orders, gunnery, AA, evasion) stays, as it does for the player's own units.
    /// </summary>
    [HarmonyPatch]
    internal static class MpBattleAiOffPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), "CheckDefensiveConditions")]
        [HarmonyPatch(typeof(EngagementAI), "CheckUnitsAttacking")]
        [HarmonyPatch(typeof(EngagementAI), "AssignEnemyBehaviours")]
        [HarmonyPatch(typeof(EngagementAI), "WarnOtherUnits")]
        private static bool Skip() => !MpBattle.HostActive;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.Disengage))]
        private static bool NoAiDisengage(bool player) => player || !MpBattle.HostActive;

        /// <summary>
        /// The unit-level AI retreat (after damage, torpedo launches, intercepts...) steers a non-player ship away at full
        /// speed and makes it lay smoke; for Player 2's units that would take the helm away (seen in the M6c test).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UnitAI), nameof(UnitAI.RetreatUnit))]
        [HarmonyPatch(typeof(UnitAISea), nameof(UnitAISea.RetreatUnit))]
        [HarmonyPatch(typeof(UnitAIAir), nameof(UnitAIAir.RetreatUnit))]
        private static bool NoAiRetreat(UnitAI __instance)
        {
            Unit u = __instance.parentUnit;
            if (!MpBattle.IsGuestUnit(u)) return true;
            // planes: the player's short fly-off after an attack run (MpBattleGuestRules), not the AI's long run
            if (u.unitAir != null) MpBattle.GuestAirRetreat(u);
            return false;
        }

        /// <summary>
        /// The AI's "escape under smoke" rule removes non-player ships from the battle (EngagementAI.Update → ParkUnit).
        /// Player 2's units only leave by sailing out of the zone, like the player's own (CheckUnitsOutOfZone).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.ParkUnit))]
        private static bool OnlyOutOfZone(EngagementAI __instance, Unit unit) =>
            !MpBattle.IsGuestUnit(unit) || (__instance.unitsOutOfZone != null && __instance.unitsOutOfZone.Contains(unit));

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.OnDetectUnit))]
        private static bool Detect(Unit leadUnit, Unit detectedUnit)
        {
            if (!MpBattle.HostActive || leadUnit == null || leadUnit.faction == Faction.Player || detectedUnit == null) return true;
            MpBattle.GuestSubDetectsAir(leadUnit, detectedUnit);
            MpBattle.GuestSideDetected(detectedUnit);
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.InitialiseEngagementAI))]
        private static void Init()
        {
            if (MpBattle.HostActive) MpBattle.GuestSideDefaults();
        }
    }

    /// <summary>Both: count the steps of vanilla's battle setup coroutine (MpBattle.SetupDepth).</summary>
    [HarmonyPatch]
    internal static class MpBattleSetupStepPatch
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(EngagementData), "ApplyEngagementData"));

        private static void Prefix() => MpBattle.SetupDepth++;

        private static Exception Finalizer(Exception __exception)
        {
            MpBattle.SetupDepth = Math.Max(0, MpBattle.SetupDepth - 1);
            return __exception;
        }
    }

    /// <summary>Client: the vanilla helm UI acted on one of our (puppet) units → send the order to the host.</summary>
    [HarmonyPatch]
    internal static class MpBattleClientHelmPatches
    {
        // speed / rudder / altitude-depth sliders (also driven by their keys): the value before the call, sent if it changed
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.SpeedSlider))]
        private static void SpeedBefore(CombatInterface __instance, ref float __state) =>
            __state = __instance.currentUnit != null ? __instance.currentUnit.wantedSpeed : float.NaN;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.SpeedSlider))]
        private static void Speed(CombatInterface __instance, float __state)
        {
            if (MpBattle.ClientActive) MpBattle.SendHelm(__instance.currentUnit, BattleCmd.Speed, __state);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.TurnSlider))]
        private static void TurnBefore(CombatInterface __instance, ref float __state) =>
            __state = __instance.currentUnit != null ? __instance.currentUnit.wantedTurnRate : float.NaN;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.TurnSlider))]
        private static void Turn(CombatInterface __instance, float __state)
        {
            if (MpBattle.ClientActive) MpBattle.SendHelm(__instance.currentUnit, BattleCmd.Turn, __state);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.HeightSlider))]
        private static void HeightBefore(CombatInterface __instance, ref float __state) =>
            __state = __instance.currentUnit != null ? __instance.currentUnit.wantedHeight : float.NaN;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.HeightSlider))]
        private static void Height(CombatInterface __instance, float __state)
        {
            if (MpBattle.ClientActive) MpBattle.SendHelm(__instance.currentUnit, BattleCmd.Height, __state);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.SetFocusUnitForCurrentUnit))]
        private static void Target(CombatInterface __instance, Unit unit)
        {
            Unit u = __instance.currentUnit;
            if (MpBattle.ClientActive && u != null && unit != null && u.unitAI.focusedUnit == unit) MpBattle.SendTarget(u, unit);
        }

        /// <summary>The V key: every selected unit attacks the current unit's target (vanilla sets their targets first).</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.AttackUnit))]
        private static void Attack(CombatInterface __instance)
        {
            if (!MpBattle.ClientActive || __instance.currentUnit == null) return;
            Unit target = __instance.currentUnit.unitAI.focusedUnit;
            if (target == null) return; // vanilla said "no target"
            foreach (Unit u in RosterInterface.instance.GetSelectedRosterUnits())
                if (u != null && u.unitAI.focusedUnit == target) MpBattle.SendTarget(u, target, BattleCmd.Attack);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.EnableAtWill))]
        private static void AtWillOn(CombatInterface __instance)
        {
            if (MpBattle.ClientActive && __instance.currentUnit != null && __instance.currentUnit.unitSea != null)
                MpBattle.SendGunnery(__instance.currentUnit, BattleCmd.FireAtWill, true);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.CeaseAutoFire))]
        private static void AtWillOff(CombatInterface __instance)
        {
            if (MpBattle.ClientActive && __instance.currentUnit != null && __instance.currentUnit.unitSea != null)
                MpBattle.SendGunnery(__instance.currentUnit, BattleCmd.FireAtWill, false);
        }

        /// <summary>The weapon panel's AT WILL toggle: fire at will (ships) / auto-dogfight (planes) for every selected unit.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.SetAutoGunnery))]
        private static void AutoGunnery()
        {
            if (!MpBattle.ClientActive) return;
            foreach (Unit u in RosterInterface.instance.GetSelectedRosterUnits())
                if (u != null) MpBattle.SendGunnery(u, BattleCmd.FireAtWill, u.unitAI.autoGunnery);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.CeaseFiring))]
        private static void Cease()
        {
            if (!MpBattle.ClientActive) return;
            foreach (Unit u in RosterInterface.instance.GetSelectedRosterUnits()) MpBattle.SendGunnery(u, BattleCmd.Cease, false);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.SetUnitWaypointPosition))]
        private static void WaypointBefore(Unit unit, ref int __state) => __state = unit != null && unit.unitAI != null ? unit.unitAI.navPoints.Count : -1;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.SetUnitWaypointPosition))]
        private static void Waypoint(Unit unit, Vector3 pos, int __state)
        {
            // only waypoints vanilla accepted (it refuses ones too close to the ship)
            if (MpBattle.ClientActive && __state >= 0 && unit.unitAI.navPoints.Count > __state) MpBattle.SendWaypoint(unit, pos);
        }
    }
}
