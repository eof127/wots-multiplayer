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
    /// M6d: the rest of Player 2's unit controls in battle.
    /// (1) Control state, polled on the client 5x a second and sent when it changes (BattleCmd.State): radar, active sonar,
    ///     searchlights, hazard evasion, auto AA, smoke, silent running. (Altitude / depth: BattleCmd.Height from the slider.)
    ///     The client's puppets never change these themselves (no AI there), so any change came from Player 2's UI, whichever
    ///     button, toggle or key did it. The host applies them with the same rules as the vanilla UI.
    /// (2) Actions, forwarded where vanilla carries them out (outermost call only, nothing while the client shows a unit
    ///     being destroyed): bomb doors, landing gear, follow / stop / return to formation, form up, kamikaze, blow ballast,
    ///     fixed guns (strafing / dogfight guns).
    /// (1b) Host → client feedback of the control state (message 51, v0.16.1).
    /// Player 2's disengage / retreat: MpBattleRetreat (D47).
    /// </summary>
    internal static partial class MpBattle
    {
        private struct Controls
        {
            // 1 radar, 2 sonar, 4 searchlights, 8 no evasion, 16 auto AA, 32 smoke, 64 silent,
            // 128 periscope raised (wanted), 256 looking through the periscope / binoculars (ZoomedCamera on) — subs only
            public ushort Flags;
            // (altitude / depth is not polled since protocol 10: the client's value isn't P2's intent until P2 moves the
            // slider — it sent the load-time 0 and the slider's minimum; BattleCmd.Height from HeightSlider instead)
        }

        private const float ControlPollInterval = 0.2f;
        private static readonly Dictionary<Unit, Controls> lastControls = new Dictionary<Unit, Controls>();
        private static float lastControlPoll;
        private static int forwardNest;

        /// <summary>Client: true while vanilla code runs for our own reasons (showing a destroyed unit): nothing is forwarded.</summary>
        internal static bool SuppressForward;

        // ---------- (1) control state ----------

        private static Controls ReadControls(Unit u)
        {
            var c = new Controls();
            if (u.usingRadar) c.Flags |= 1;
            if (u.usingActiveSonar) c.Flags |= 2;
            if (u.unitAI.noNavEvade) c.Flags |= 8;
            if (u.unitAI.autoAAA) c.Flags |= 16;
            if (u.unitSea != null)
            {
                if (u.unitAISea != null && u.unitAISea.searchlights != null && u.unitAISea.searchlights.enabled) c.Flags |= 4;
                if (u.unitAI.smokeTimer > 0f) c.Flags |= 32;
                if (u.unitSea.runningSilent) c.Flags |= 64;
                MovingMesh p = u.unitSea.periscope;
                if (u.unitData.unitSubtype == UnitSubType.Submarine && p != null)
                {
                    if (PeriscopeWantedUp(p)) c.Flags |= 128;
                    if (ZoomedCamera.instance != null && ZoomedCamera.instance.enabled) c.Flags |= 256;
                }
            }
            return c;
        }

        /// <summary>Raising, or up and not moving (MovingPart: currentState = direction, isResting = fully down).</summary>
        private static bool PeriscopeWantedUp(MovingPart p) => p.currentState > 0f || (p.currentState == 0f && !p.isResting);

        /// <summary>Client (MpBattle.Update): send Player 2's control changes. The first look at a unit sends its state too.</summary>
        private static void ClientPollControls()
        {
            if (firstApply || Time.realtimeSinceStartup - lastControlPoll < ControlPollInterval) return; // (still loading)
            lastControlPoll = Time.realtimeSinceStartup;
            EngagementManager em = EngagementManager.instance;
            if (em == null || em.allSeaUnits == null) return;
            PollList(em.allSeaUnits);
            PollList(em.allAirUnits);
        }

        private static void PollList(List<Unit> units)
        {
            foreach (Unit u in units)
            {
                if (u == null || u.faction != Faction.Player || u.isDestroyed || u.unitAI == null) continue;
                Controls now = ReadControls(u);
                Controls before;
                bool known = lastControls.TryGetValue(u, out before);
                if (known && before.Flags == now.Flags) continue;
                lastControls[u] = now;
                // a subordinate's first look only records: its copy's defaults must not overwrite its commander's settings
                if (!known && Multiplayer.Session.IsSubordinate) continue;
                byte kind;
                int index;
                if (!UnitRef(u, out kind, out index)) continue;
                localControlAt[u] = Time.realtimeSinceStartup;
                SendBattleOrder(Protocol.Build(w =>
                {
                    w.Write(kind); w.Write(index); w.Write((byte)BattleCmd.State);
                    w.Write(now.Flags);
                }));
            }
        }

        // ---------- (1b) host → client feedback of the control state ----------
        // The host can change P2's states itself: a refused order (radar on a ship without radar), a destroyed conning tower,
        // smoke running out, vanilla lowering a periscope. Without this, P2's toggles kept showing what P2 last asked for.
        //   51 BattleControls host→client {count, [kind, index, flags]}: P2's units whose state changed, all of them every 3 s.
        // The client applies the host's state unless P2 changed that unit in the last 1.5 s (the order may still be on its way),
        // then records it as already sent so the poll above doesn't send it back.

        private const byte MsgControls = 51;
        private const ushort EchoMask = 0xFF; // bit 256 (looking through the periscope) is each player's own camera
        private const float EchoInterval = 0.5f, EchoRefreshInterval = 3f, LocalControlGrace = 1.5f;
        private static readonly Dictionary<Unit, ushort> hostSentControls = new Dictionary<Unit, ushort>();
        private static readonly Dictionary<Unit, float> localControlAt = new Dictionary<Unit, float>();
        private static float lastEcho, lastEchoRefresh;
        private static bool echoLogged;

        internal static void ControlEchoReset()
        {
            hostSentControls.Clear();
            localControlAt.Clear();
            lastEcho = lastEchoRefresh = 0f;
            echoLogged = false;
        }

        /// <summary>Host (MpBattle.Update): send P2's units' control state when it changes, and all of it every few seconds.</summary>
        private static void HostSendControls()
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastEcho < EchoInterval) return;
            lastEcho = now;
            bool refresh = now - lastEchoRefresh >= EchoRefreshInterval;
            if (refresh) lastEchoRefresh = now;
            SendControlsFor(IsGuestUnit, refresh, team: 1);
            if (HostTeamHasSubordinates) SendControlsFor(IsHostTeamUnit, refresh, team: 0); // stage 4: the host's units, to its team
        }

        /// <summary>Host: one team's units' control state (Player 2's team: P2's units; the host's team: the host's).</summary>
        private static void SendControlsFor(Func<Unit, bool> pick, bool refresh, int team)
        {
            EngagementManager em = EngagementManager.instance;
            if (em == null || em.allSeaUnits == null) return;
            var units = new List<Unit>();
            var flags = new List<ushort>();
            CollectEcho(em.allSeaUnits, pick, refresh, units, flags);
            CollectEcho(em.allAirUnits, pick, refresh, units, flags);
            if (units.Count == 0) return;
            byte[] payload = Protocol.Build(w =>
            {
                int n = 0;
                var refs = new List<KeyValuePair<byte, int>>();
                foreach (Unit u in units)
                {
                    byte kind;
                    int index;
                    UnitRef(u, out kind, out index);
                    refs.Add(new KeyValuePair<byte, int>(kind, index));
                    n++;
                }
                w.Write(n);
                for (int i = 0; i < n; i++) { w.Write(refs[i].Key); w.Write(refs[i].Value); w.Write(flags[i]); }
            });
            if (team == 1) SendBattleTeam1(MsgControls, payload);
            else SendBattleTeam0(MsgControls, payload);
        }

        private static void CollectEcho(List<Unit> list, Func<Unit, bool> pick, bool refresh, List<Unit> units, List<ushort> flags)
        {
            if (list == null) return;
            foreach (Unit u in list)
            {
                if (!pick(u) || u.isDestroyed || u.unitAI == null) continue;
                byte kind;
                int index;
                if (!UnitRef(u, out kind, out index)) continue;
                ushort f = (ushort)(ReadControls(u).Flags & EchoMask);
                ushort before;
                if (!refresh && hostSentControls.TryGetValue(u, out before) && before == f) continue;
                hostSentControls[u] = f;
                units.Add(u);
                flags.Add(f);
            }
        }

        /// <summary>Client: the host's state for P2's units.</summary>
        private static void ClientControls(byte[] payload)
        {
            if (firstApply) return; // still loading; the next refresh comes within 3 s
            using (var r = Protocol.Reader(payload))
            {
                int n = r.ReadInt32();
                if (n < 0 || n > 512) return;
                for (int i = 0; i < n; i++)
                {
                    Unit u = ResolveUnit(r.ReadByte(), r.ReadInt32());
                    ushort f = r.ReadUInt16();
                    if (u == null || u.faction != Faction.Player || u.isDestroyed || u.unitAI == null) continue;
                    float at;
                    if (localControlAt.TryGetValue(u, out at) && Time.realtimeSinceStartup - at < LocalControlGrace) continue;
                    ushort local = (ushort)(ReadControls(u).Flags & EchoMask);
                    if (local == f) continue;
                    SuppressForward = true;
                    try { ApplyHostControls(u, f, local); }
                    finally { SuppressForward = false; }
                    lastControls[u] = ReadControls(u); // already the host's state: don't send it back
                    if (!echoLogged || Plugin.DebugInput.Value)
                    {
                        echoLogged = true;
                        Plugin.Log.LogInfo($"MP battle: host changed P2's {u.name} controls {local} → {f} (radar 1, sonar 2, lights 4, no evade 8, AA 16, smoke 32, silent 64, periscope 128)");
                    }
                }
            }
        }

        /// <summary>Client: set the puppet's state (the fields the vanilla UI reads) and the visible toggles, without running
        /// the toggles' click handlers (they would flip the state again).</summary>
        private static void ApplyHostControls(Unit u, ushort f, ushort local)
        {
            ushort diff = (ushort)(f ^ local);
            CombatInterface ci = CombatInterface.instance;
            bool current = ci != null && ci.currentUnit == u;
            if ((diff & 1) != 0)
            {
                u.usingRadar = (f & 1) != 0;
                if (current)
                {
                    SetToggleQuiet(ci.radarOnOff, u.usingRadar);
                    // the toolbar's radar indicator and the map's radar sweep (vanilla SetRadarAnimation) too: they stayed off
                    // when the host's state turned P2's radar on (user test 2026-11-26)
                    if (ci.sensorToggles != null && ci.sensorToggles.Length > 0) SetToggleQuiet(ci.sensorToggles[0], u.usingRadar);
                    if (ci.radarWedge != null) ci.radarWedge.gameObject.SetActive(u.usingRadar);
                }
            }
            if ((diff & 2) != 0)
            {
                u.usingActiveSonar = (f & 2) != 0;
                if (current)
                {
                    SetToggleQuiet(ci.activeSonarOnOff, u.usingActiveSonar);
                    if (ci.sensorToggles != null && ci.sensorToggles.Length > 1) SetToggleQuiet(ci.sensorToggles[1], u.usingActiveSonar);
                }
            }
            if ((diff & 8) != 0) { u.unitAI.noNavEvade = (f & 8) != 0; if (current) SetToggleQuiet(ci.navEvade, !u.unitAI.noNavEvade); }
            if ((diff & 16) != 0) { u.unitAI.autoAAA = (f & 16) != 0; if (current) SetToggleQuiet(ci.autoAAAToggle, u.unitAI.autoAAA); }
            if (u.unitSea == null) return;
            if ((diff & 4) != 0)
            {
                bool lights = (f & 4) != 0;
                Searchlight sl = u.unitAISea != null ? u.unitAISea.searchlights : null;
                if (sl != null)
                {
                    if (!lights) sl.DisableAllLights();
                    sl.enabled = lights;
                    if (current) SetToggleQuiet(ci.searchlightOnOff, lights);
                }
            }
            if ((diff & 32) != 0)
            {
                bool smoke = (f & 32) != 0;
                if ((u.unitAI.smokeTimer > 0f) != smoke) u.unitSea.LaySmoke(smoke);
                if (current) SetToggleQuiet(ci.laySmokeToggle, u.unitAI.smokeTimer > 0f);
            }
            if ((diff & 64) != 0) u.unitSea.runningSilent = (f & 64) != 0;
            if ((diff & 128) != 0 && u.unitData.unitSubtype == UnitSubType.Submarine)
            {
                MovingMesh p = u.unitSea.periscope;
                bool up = (f & 128) != 0;
                if (p != null && PeriscopeWantedUp(p) != up)
                {
                    p.WakeMovingPart(up ? 1 : -1);
                    if (!u.unitSea.movingParts.Contains(p)) u.unitSea.movingParts.Add(p);
                }
            }
        }

        private static void SetToggleQuiet(UnityEngine.UI.Toggle t, bool on)
        {
            if (t == null || t.isOn == on) return;
            var ev = t.onValueChanged;
            int n = ev.GetPersistentEventCount();
            for (int i = 0; i < n; i++) ev.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            try { t.isOn = on; }
            finally
            {
                for (int i = 0; i < n; i++) ev.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.RuntimeOnly);
            }
        }

        /// <summary>Host: Player 2's control state, applied with the vanilla UI's rules (CombatInterface.SetSensors etc.).</summary>
        private static void HostState(Unit u, BinaryReader r)
        {
            ushort f = r.ReadUInt16();
            bool sub = u.unitData.unitSubtype == UnitSubType.Submarine;
            Plugin.Log.LogInfo($"MP battle: {orderWho} controls {u.name}: radar {(f & 1) != 0}, sonar {(f & 2) != 0}, lights {(f & 4) != 0}, " +
                               $"evade {(f & 8) == 0}, AA {(f & 16) != 0}, smoke {(f & 32) != 0}, silent {(f & 64) != 0}" +
                               (sub ? $", periscope {(f & 128) != 0}, scope view {(f & 256) != 0}" : ""));
            if (sub && u.unitSea != null)
            {
                HostPeriscope(u, (f & 128) != 0);
                guestScopeView[u] = (f & 256) != 0;
            }
            UnitData d = u.unitData;
            if (u.unitSea != null)
            {
                u.usingRadar = (f & 1) != 0 && (d.surfaceRadarStrength > 0f || d.airRadarStrength > 0f);
                u.usingActiveSonar = (f & 2) != 0 && d.activeSonarStrength > 0f;
                bool lights = (f & 4) != 0;
                Searchlight sl = u.unitAISea != null ? u.unitAISea.searchlights : null;
                if (sl != null && sl.enabled != lights)
                {
                    if (!lights) sl.DisableAllLights();
                    sl.enabled = lights;
                }
                bool noEvade = (f & 8) != 0;
                if (u.unitAI.noNavEvade != noEvade)
                {
                    u.unitAI.noNavEvade = noEvade;
                    if (noEvade && u.unitAI.currentAIAction == AIAction.Evade_Navigation)
                    {
                        u.unitAI.currentAIAction = u.unitAI.previousAIAction;
                        if (u.unitAI.currentAIAction == AIAction.Follow_Nav_Points) u.unitAI.atZero = false;
                    }
                }
                bool aaa = (f & 16) != 0;
                if (u.unitAI.autoAAA != aaa && !u.unitAI.blockAutoAAA) u.unitAI.SetAutoAAA(aaa);
                bool smoke = (f & 32) != 0;
                if ((u.unitAI.smokeTimer > 0f) != smoke) u.unitSea.LaySmoke(smoke);
                u.unitSea.runningSilent = (f & 64) != 0;
            }
        }

        /// <summary>Host: raise / lower a sub's periscope like CombatInterface.MovePeriscope / CrashDive.</summary>
        private static void HostPeriscope(Unit u, bool up)
        {
            MovingMesh p = u.unitSea.periscope;
            if (p == null || PeriscopeWantedUp(p) == up) return;
            if (up)
            {
                // MovePeriscope only works while the conning tower cell is functional (Utilities.ToggleMovingPart)
                CellVariable cv = null;
                try { cv = Utilities.GetFirstDeckCellVariableOfType(u.damageableSea, CellType.Conning); } catch (Exception) { }
                if (cv != null && !Utilities.IsCellFunctional(cv)) return;
            }
            p.WakeMovingPart(up ? 1 : -1);
            if (!u.unitSea.movingParts.Contains(p)) u.unitSea.movingParts.Add(p);
        }

        // ---------- (2) forwarded actions ----------

        /// <summary>Client, prefix of a forwarded vanilla call: 0 = not counted, 1 = nested/not ours, 2 = forward when done.</summary>
        internal static byte ForwardEnter(Unit u)
        {
            if (!ClientActive) return 0;
            bool outer = forwardNest == 0;
            forwardNest++;
            // firstApply: the battle is still being built here (vanilla forms up groups at load; the host does its own)
            return (byte)(outer && !SuppressForward && !firstApply && SetupDepth == 0 && u != null && u.faction == Faction.Player && !u.isDestroyed ? 2 : 1);
        }

        internal static bool ForwardExit(byte state, Exception e)
        {
            if (state == 0) return false;
            forwardNest = Math.Max(0, forwardNest - 1);
            return state == 2 && e == null;
        }

        internal static void SendControl(Unit u, BattleCmd cmd, Action<BinaryWriter> extra = null)
        {
            byte kind;
            int index;
            if (!UnitRef(u, out kind, out index)) return;
            SendBattleOrder(Protocol.Build(w =>
            {
                w.Write(kind); w.Write(index); w.Write((byte)cmd);
                if (extra != null) extra(w);
            }));
        }

        internal static void WriteUnitRef(BinaryWriter w, Unit target)
        {
            byte kind = 255;
            int index = -1;
            if (target != null && !UnitRef(target, out kind, out index)) kind = 255;
            w.Write(kind);
            w.Write(index);
        }

        private static void HostControl(Unit u, BattleCmd cmd, BinaryReader r)
        {
            EngagementAI ai = EngagementAI.instance;
            switch (cmd)
            {
                case BattleCmd.BombDoors:
                case BattleCmd.Gear:
                {
                    int s = r.ReadInt32();
                    if (u.unitAir == null || (s != 1 && s != -1)) return;
                    if (cmd == BattleCmd.BombDoors) { if (u.unitAir.bombDoors.Count > 0) u.unitAir.SetBombDoors(s); }
                    else u.unitAir.SetLandingGear(s);
                    return;
                }
                case BattleCmd.Follow:
                {
                    Unit t = ResolveUnit(r.ReadByte(), r.ReadInt32());
                    float spacing = r.ReadSingle();
                    if (t == null || t == u || t.isDestroyed || t.faction != u.faction || float.IsNaN(spacing)) return;
                    u.unitAI.focusedUnit = t; // vanilla follows the current unit's target
                    u.unitAI.followingUnit = t;
                    ai.BeginFollowingUnit(u, t, Mathf.Clamp(spacing, 0f, 100000f));
                    return;
                }
                case BattleCmd.StopFollow: ai.StopFollowingUnit(u); return;
                case BattleCmd.ReturnFollow: ai.ReturnFollowingUnit(u); return;
                case BattleCmd.FormUp:
                {
                    bool air = r.ReadBoolean();
                    int fi = r.ReadInt32();
                    float spacing = r.ReadSingle();
                    int n = r.ReadInt32();
                    if (n < 0 || n > 64) return;
                    var units = new List<Unit>();
                    for (int i = 0; i < n; i++)
                    {
                        Unit m = ResolveUnit(r.ReadByte(), r.ReadInt32());
                        if (m != null && !m.isDestroyed && m.faction == u.faction && !units.Contains(m)) units.Add(m);
                    }
                    List<Vector3[]> patterns = air ? Config.instance.formationAirList : Config.instance.formationSeaList;
                    if (fi < 0 || fi >= patterns.Count || units.Count < 2 || float.IsNaN(spacing)) return;
                    if (!units.Contains(u)) units.Insert(0, u);
                    ai.FormUpUnits(u, units, patterns[fi], Mathf.Clamp(spacing, 0f, 100000f), fi);
                    return;
                }
                case BattleCmd.Kamikaze:
                {
                    Unit t = ResolveUnit(r.ReadByte(), r.ReadInt32());
                    if (u.unitAir == null || t == null || t.unitSea == null || t.faction == u.faction) return;
                    u.unitAI.focusedUnit = t;
                    ai.KamikazeApproach(u);
                    return;
                }
                case BattleCmd.Ballast:
                    if (u.unitSea == null) return;
                    GuestBallastOrder = true; // (the AI's automatic blow at test depth is off for P2's subs: MpBattleGuestRules)
                    try { ai.BlowBallast(u); }
                    finally { GuestBallastOrder = false; }
                    return;
                case BattleCmd.FixedGuns:
                {
                    bool on = r.ReadBoolean();
                    if (u.weaponParticle != null) u.weaponParticle.FireAll(on);
                    return;
                }
                case BattleCmd.DcParty:
                case BattleCmd.AutoDc:
                case BattleCmd.CounterFlood:
                case BattleCmd.Scuttle:
                    HostDc(u, cmd, r);
                    return;
                case BattleCmd.Disengage: HostGuestDisengage(r.ReadBoolean()); return;
                case BattleCmd.Retreat: HostRetreat(r.ReadBoolean()); return;
                case BattleCmd.AllowRetreat: HostAllowedByGuest(); return;
                case BattleCmd.Classify: HostClassify(r); return;
            }
        }
    }

    /// <summary>Client: Player 2's actions that vanilla carries out in these methods are forwarded to the host.</summary>
    [HarmonyPatch]
    internal static class MpBattleClientControlPatches
    {
        // bomb doors / landing gear (UnitAir)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UnitAir), nameof(UnitAir.SetBombDoors))]
        [HarmonyPatch(typeof(UnitAir), nameof(UnitAir.SetLandingGear))]
        private static void AirPartBefore(UnitAir __instance, ref byte __state) => __state = MpBattle.ForwardEnter(__instance);

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(UnitAir), nameof(UnitAir.SetBombDoors))]
        private static Exception BombDoorsAfter(Exception __exception, UnitAir __instance, int state, byte __state)
        {
            if (MpBattle.ForwardExit(__state, __exception)) MpBattle.SendControl(__instance, BattleCmd.BombDoors, w => w.Write(state));
            return __exception;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(UnitAir), nameof(UnitAir.SetLandingGear))]
        private static Exception GearAfter(Exception __exception, UnitAir __instance, int state, byte __state)
        {
            if (MpBattle.ForwardExit(__state, __exception)) MpBattle.SendControl(__instance, BattleCmd.Gear, w => w.Write(state));
            return __exception;
        }

        // follow / formation (EngagementAI)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.BeginFollowingUnit))]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.StopFollowingUnit))]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.ReturnFollowingUnit))]
        private static void FollowBefore(Unit unitFollowing, ref byte __state) => __state = MpBattle.ForwardEnter(unitFollowing);

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.BeginFollowingUnit))]
        private static Exception FollowAfter(Exception __exception, Unit unitFollowing, Unit unitToFollow, float spacingDistance, byte __state)
        {
            if (MpBattle.ForwardExit(__state, __exception))
                MpBattle.SendControl(unitFollowing, BattleCmd.Follow, w => { MpBattle.WriteUnitRef(w, unitToFollow); w.Write(spacingDistance); });
            return __exception;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.StopFollowingUnit))]
        private static Exception StopFollowAfter(Exception __exception, Unit unitFollowing, byte __state)
        {
            if (MpBattle.ForwardExit(__state, __exception)) MpBattle.SendControl(unitFollowing, BattleCmd.StopFollow);
            return __exception;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.ReturnFollowingUnit))]
        private static Exception ReturnFollowAfter(Exception __exception, Unit unitFollowing, byte __state)
        {
            if (MpBattle.ForwardExit(__state, __exception)) MpBattle.SendControl(unitFollowing, BattleCmd.ReturnFollow);
            return __exception;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.FormUpUnits))]
        private static void FormUpBefore(Unit leadUnit, ref byte __state) => __state = MpBattle.ForwardEnter(leadUnit);

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.FormUpUnits))]
        private static Exception FormUpAfter(Exception __exception, Unit leadUnit, List<Unit> units, float spacingDistance, int formationIndex, byte __state)
        {
            if (MpBattle.ForwardExit(__state, __exception) && units != null)
            {
                var copy = new List<Unit>(units);
                MpBattle.SendControl(leadUnit, BattleCmd.FormUp, w =>
                {
                    w.Write(leadUnit.unitData.unitType == UnitType.Air);
                    w.Write(formationIndex);
                    w.Write(spacingDistance);
                    w.Write(copy.Count);
                    foreach (Unit m in copy) MpBattle.WriteUnitRef(w, m);
                });
            }
            return __exception;
        }

        // kamikaze / blow ballast (EngagementAI)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.KamikazeApproach))]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.BlowBallast))]
        private static void UnitActionBefore(Unit unit, ref byte __state) => __state = MpBattle.ForwardEnter(unit);

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.KamikazeApproach))]
        private static Exception KamikazeAfter(Exception __exception, Unit unit, byte __state)
        {
            if (MpBattle.ForwardExit(__state, __exception))
                MpBattle.SendControl(unit, BattleCmd.Kamikaze, w => MpBattle.WriteUnitRef(w, unit.unitAI.focusedUnit));
            return __exception;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.BlowBallast))]
        private static Exception BallastAfter(Exception __exception, Unit unit, byte __state)
        {
            if (MpBattle.ForwardExit(__state, __exception)) MpBattle.SendControl(unit, BattleCmd.Ballast);
            return __exception;
        }

        // fixed guns (CombatInterface toggles the current unit's gun particles)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.FireFixedGuns))]
        private static void FixedGuns(CombatInterface __instance)
        {
            Unit u = __instance.currentUnit;
            if (!MpBattle.ClientActive || u == null || u.isDestroyed || u.faction != Faction.Player || u.weaponParticle == null) return;
            bool on = !u.weaponParticle.isActiveAndEnabled; // the value vanilla is about to set
            MpBattle.SendControl(u, BattleCmd.FixedGuns, w => w.Write(on));
        }
    }
}
