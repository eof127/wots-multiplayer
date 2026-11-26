using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using WorldMapStrategyKit;
using WotSMultiplayer.Core;
using WotSMultiplayer.Features.MpCampaign;
using WotSMultiplayer.Features.TimeControl;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// M6a: live battle hand-off and a synced view for Player 2.
    ///   40 BattleStart    host→client {terrain pixel coords, gzip(EngagementData JSON before Setup)}
    ///   41 BattleSnapshot host→client {sea units, air units: position, rotation, speed, flags} at 10 Hz
    ///   42 BattleEnd      host→client {}
    ///   43 CombatStarted  host→client {} (host pressed Start: deployment over)
    /// The host runs the vanilla campaign battle (GenerateCampaignEngagement). The client builds the same battle from the
    /// same unit groups with sides swapped (vanilla InitialiseEngagement(..., switchSides: true)), so its own ships are
    /// "player" units. Units are created in group order on both PCs, so the index in allSeaUnits / allAirUnits is the
    /// unit's id. Client units are puppets: no local movement, physics or AI; they follow the host's snapshots.
    /// The snapshot also carries the host's battle clock and (MpBattleDamage) fires / hull damage per ship.
    /// See MpBattleOrders (P2's orders), MpBattleFx (shots), MpBattleWeapons (weapon panel), MpBattleControls, MpBattleTurrets,
    /// MpBattleTorpedoes, MpBattleDc (damage control). On by default since D44 ([Multiplayer] LiveBattles, the host's setting decides).
    /// Additional players, stage 4 (D57): subordinates on the campaign map join the battle too. Everyone gets the same battle
    /// messages; Player 2's team builds it with sides swapped, the host's team without (its own units are the host's).
    /// The per-side feedback (51 controls, 53 mount state, 52 retreat bars, 54 report points) goes to each team for its own
    /// units. Subordinates' orders (44) run for their team's units unless the commander has the unit selected or locked
    /// (TeamLocks); Start and the retreat decisions stay with the commanders.
    /// </summary>
    internal static partial class MpBattle
    {
        private const byte MsgStart = 40, MsgSnap = 41, MsgEnd = 42, MsgCombatStarted = 43, MsgOrder = 44, MsgReady = 45, MsgShots = 46;
        private const float SnapshotInterval = 0.1f;

        internal static bool HostActive;   // host: an MP battle is running
        internal static bool ClientActive; // client: showing the host's battle
        private static bool pendingCapture;
        private static float lastSnap;
        private static bool clientStartAllowed;

        private struct UnitState
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public float Speed, ActualSpeed; // ActualSpeed: client UI maths (intercepts, solutions) reads currentActualSpeed
            public float WantedSpeed, WantedHeight; // the host's orders, so P2's sliders start from the real values
            public byte Flags; // 1 active, 2 destroyed, 4 present (unit exists on the host)
            public byte[] Fires, Shade; // sea units only (MpBattleDamage)
            public byte Fx;             // sea units: 1 laying smoke, 2 sub periscope up (protocol 14)
            public short AaTarget;      // sea units: index in allAirUnits of the close-AA target, -1 none (protocol 14)
            public byte AirSmoke;       // air units: smoke trails from damage (0–2) (protocol 15)
        }

        private static UnitState[] seaTargets = new UnitState[0], airTargets = new UnitState[0];
        private static readonly HashSet<Unit> puppetized = new HashSet<Unit>();
        private static bool firstApply;

        internal static bool LiveBattlesEnabled => Plugin.LiveBattles.Value;

        internal static void Init()
        {
            Multiplayer.Session.OnGameMessage += OnMessage;
            Multiplayer.Session.OnSubordinateGameMessage += OnSubordinateMessage;
        }

        // ---------- host: who gets battle messages (stage 4) ----------

        /// <summary>Host: someone is in the battle with the host (Player 2 or a subordinate).</summary>
        private static bool AnyoneInBattle => Multiplayer.Session.IsConnected || Multiplayer.Session.HasSubordinates();

        /// <summary>Host: the same message for everyone in the battle.</summary>
        private static void SendBattleAll(byte type, byte[] payload)
        {
            if (Multiplayer.Session.IsConnected) Multiplayer.Session.SendGame(type, payload);
            Multiplayer.Session.SendGameToSubordinates(type, payload);
        }

        /// <summary>Host: Player 2's team (Player 2 and its subordinates).</summary>
        private static void SendBattleTeam1(byte type, byte[] payload)
        {
            if (Multiplayer.Session.IsConnected) Multiplayer.Session.SendGame(type, payload);
            Multiplayer.Session.SendGameToSubordinates(type, payload, team: 1);
        }

        /// <summary>Host: the host's own team (its subordinates).</summary>
        private static void SendBattleTeam0(byte type, byte[] payload) => Multiplayer.Session.SendGameToSubordinates(type, payload, team: 0);

        private static bool HostTeamHasSubordinates => Multiplayer.Session.HasSubordinates(0);

        /// <summary>Host, MP battle: one of the host's own units (its team's subordinates may command them).</summary>
        internal static bool IsHostTeamUnit(Unit u) => HostActive && u != null && u.faction == Faction.Player;

        internal static bool BattleUnitRef(Unit u, out byte kind, out int index) => UnitRef(u, out kind, out index);

        internal static Unit BattleUnitAt(byte kind, int index) => ResolveUnit(kind, index);

        // ---------- host ----------

        /// <summary>Host: a fought encounter → the vanilla "Begin" path for the host's force.</summary>
        internal static void HostStart(GameObjectAnimator hostGoa, GameObjectAnimator guestGoa = null)
        {
            if (hostGoa == null) return;
            MpReinforcements.SetAnchors(hostGoa, guestGoa); // D55: the encounter's forces; nearby task forces join them
            pendingCapture = true;
            HostActive = true;
            hostReady = guestReady = false;
            TeamLocks.BattleReset();
            DcReset();
            GuestRulesReset();
            ControlEchoReset();
            RetreatReset();
            ReportReset();
            CampaignInterface.instance.currentMapObject = hostGoa;
            Features.MpCampaign.MpTacticalOff.AllowEnterWorld = true; // the TACTICAL button itself is off in MP (D50)
            try { CampaignInterface.instance.MobileMapObjectEnterWorld(); } // → CampaignManager.GenerateCampaignEngagement
            finally { Features.MpCampaign.MpTacticalOff.AllowEnterWorld = false; }
            Plugin.Log.LogInfo("MP battle: host started the campaign battle");
        }

        /// <summary>Host, prefix of EngagementManager.InitialiseEngagement: send the battle definition before Setup.</summary>
        internal static void HostCapture()
        {
            if (!pendingCapture) return;
            pendingCapture = false;
            EngagementData ed = EngagementManager.instance.engagementData;
            JObject j = MiniJson.ParseObject(JsonUtility.ToJson(ed));
            SnapshotCodec.StripObjectRefs(j);
            byte[] packed = SnapshotCodec.Pack(MiniJson.Write(j));
            int[] px = CampaignManager.instance.campaignData.lastEncounterPixelCoords ?? new int[0];
            SendBattleAll(MsgStart, Protocol.Build(w =>
            {
                w.Write(px.Length);
                foreach (int v in px) w.Write(v);
                w.Write(packed.Length);
                w.Write(packed);
            }));
            Plugin.Log.LogInfo($"MP battle: sent battle definition ({ed.unitGroups.Count} groups, {packed.Length / 1024} KB)");
        }

        internal static void HostCombatStarted()
        {
            if (!HostActive || !AnyoneInBattle) return;
            SendBattleAll(MsgCombatStarted, new byte[0]);
            try { HostSendEnv(); } catch (Exception e) { Plugin.Log.LogWarning("MP battle: environment at start failed: " + e.Message); } // late loaders
        }

        internal static void HostEnded()
        {
            if (!HostActive) return;
            HostActive = false;
            DcReset();
            ControlEchoReset();
            RetreatReset();
            if (AnyoneInBattle) SendBattleAll(MsgEnd, new byte[0]);
            Plugin.Log.LogInfo("MP battle: host battle ended");
        }

        private static void SendSnapshot()
        {
            EngagementManager em = EngagementManager.instance;
            if (em == null || em.allSeaUnits == null || em.allSeaUnits.Count + em.allAirUnits.Count == 0) return;
            SendBattleAll(MsgSnap, Protocol.Build(w =>
            {
                w.Write(em.dateTime.Ticks);
                w.Write(em.engagementTime);
                WriteUnits(w, em.allSeaUnits, sea: true);
                WriteUnits(w, em.allAirUnits, sea: false);
            }));
        }

        private static void WriteUnits(BinaryWriter w, List<Unit> units, bool sea)
        {
            w.Write(units.Count);
            foreach (Unit u in units)
            {
                if (u == null) { w.Write((byte)0); continue; }
                Transform t = u.transform;
                byte flags = (byte)((u.gameObject.activeSelf ? 1 : 0) | (u.isDestroyed ? 2 : 0) | 4);
                w.Write(flags);
                w.Write(t.position.x); w.Write(t.position.y); w.Write(t.position.z);
                Quaternion q = t.rotation;
                w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
                w.Write(u.currentSpeed);
                w.Write(u.currentActualSpeed);
                w.Write(u.wantedSpeed);
                w.Write(u.wantedHeight);
                if (sea) WriteSeaDamage(w, u);
                else w.Write((byte)AirSmokeTrails(u)); // protocol 15: damaged planes trail smoke on P2's copy too
            }
        }

        // ---------- both ----------

        internal static void Update()
        {
            if (!ModState.MultiplayerActive)
            {
                HostActive = ClientActive = false;
                return;
            }
            if (HostActive && Multiplayer.Session.IsHost && AnyoneInBattle && Time.realtimeSinceStartup - lastSnap >= SnapshotInterval)
            {
                lastSnap = Time.realtimeSinceStartup;
                try { SendSnapshot(); }
                catch (Exception e) { Plugin.Log.LogError("MP battle snapshot failed: " + e); }
            }
            if (HostActive && Multiplayer.Session.IsHost)
            {
                try { HostCheckCombatOver(); }
                catch (Exception e) { Plugin.Log.LogError("MP battle combat-over report failed: " + e); }
                try { FlushShots(); FlushTorps(); }
                catch (Exception e) { Plugin.Log.LogError("MP battle shots failed: " + e); }
                if (AnyoneInBattle)
                {
                    try { HostSendTurrets(); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle turrets failed: " + e); }
                    try { HostSendDc(); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle damage control failed: " + e); }
                    try { HostSendControls(); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle control feedback failed: " + e); }
                    try { HostUpdateDisengage(); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle disengage update failed: " + e); }
                    try { HostSendAmmo(); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle ammo update failed: " + e); }
                }
            }
            if (ClientActive)
            {
                try { ClientApply(); }
                catch (Exception e) { Plugin.Log.LogError("MP battle apply failed: " + e); }
                if (pendingCombatStart && !firstApply && SetupDepth == 0)
                {
                    try { ClientCombatStarted(); }
                    catch (Exception e) { pendingCombatStart = false; Plugin.Log.LogError("MP battle deferred start failed: " + e); }
                }
                try { ClientPollControls(); }
                catch (Exception e) { Plugin.Log.LogError("MP battle control poll failed: " + e); }
                ClientRetreatTick();
            }
        }

        private static void OnMessage(byte type, byte[] payload)
        {
            if (!ModState.MultiplayerActive) return;
            if (Multiplayer.Session.IsHost)
            {
                if (type == MsgOrder || type == MsgReady) HostOnOrder(type, payload, 1);
                return;
            }
            if (type == MsgShots)
            {
                if (ClientActive) ClientShots(payload);
                return;
            }
            if (type == MsgTorps)
            {
                if (ClientActive) ClientTorps(payload);
                return;
            }
            if (type == MsgDc)
            {
                if (ClientActive)
                {
                    try { ClientDc(payload); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle damage-control message failed: " + e.Message); }
                }
                return;
            }
            if (type == MsgAmmo)
            {
                if (ClientActive)
                {
                    try { ClientAmmo(payload); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle ammo message failed: " + e.Message); }
                }
                return;
            }
            if (type == MsgDisengage)
            {
                if (ClientActive)
                {
                    try { ClientDisengageState(payload); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle disengage message failed: " + e.Message); }
                }
                return;
            }
            if (type == MsgControls)
            {
                if (ClientActive)
                {
                    try { ClientControls(payload); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle control feedback message failed: " + e.Message); }
                }
                return;
            }
            if (type == MsgCombatOver)
            {
                if (ClientActive)
                {
                    try { ClientCombatOver(payload); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle combat-over message failed: " + e); }
                }
                return;
            }
            if (type == MsgEnv)
            {
                if (ClientActive)
                {
                    try { ClientEnv(payload); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle environment message failed: " + e.Message); }
                }
                return;
            }
            if (type == MsgTurrets)
            {
                if (ClientActive)
                {
                    try { ClientTurrets(payload); }
                    catch (Exception e) { Plugin.Log.LogError("MP battle turrets message failed: " + e.Message); }
                }
                return;
            }
            if (type < MsgStart || type > MsgCombatStarted) return;
            try
            {
                using (var r = Protocol.Reader(payload))
                {
                    if (type == MsgStart)
                    {
                        int n = r.ReadInt32();
                        if (n < 0 || n > 16) throw new ArgumentException("bad pixel-coordinate count " + n);
                        var px = new int[n];
                        for (int i = 0; i < n; i++) px[i] = r.ReadInt32();
                        int len = r.ReadInt32();
                        if (len < 0 || len > Protocol.MaxFrameBytes) throw new ArgumentException("bad battle definition size " + len);
                        byte[] packed = r.ReadBytes(len);
                        if (Multiplayer.Session.IsSubordinate && (MpCampaign.MpCampaign.Phase != MpPhase.Running || !TimeControlDriver.CampaignMapActive()))
                        {
                            Toasts.Show("A battle has started, but you are not on the campaign map: you join the next one.", 8f);
                            return;
                        }
                        ClientEnter(SnapshotCodec.Unpack(packed), px);
                    }
                    else if (type == MsgSnap && ClientActive)
                    {
                        ClientClock(r.ReadInt64(), r.ReadSingle());
                        seaTargets = ReadUnits(r, sea: true);
                        airTargets = ReadUnits(r, sea: false);
                        stateFresh = true;
                        if (++snapsReceived % 50 == 0) LogSyncError();
                    }
                    else if (type == MsgCombatStarted && ClientActive) ClientCombatStarted();
                    else if (type == MsgEnd && ClientActive) ClientLeave();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("MP battle message " + type + " failed: " + e);
            }
        }

        private static UnitState[] ReadUnits(BinaryReader r, bool sea)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 2000) throw new ArgumentException("bad unit count " + n);
            var a = new UnitState[n];
            for (int i = 0; i < n; i++)
            {
                byte f = r.ReadByte();
                a[i].Flags = f;
                if ((f & 4) == 0) continue; // null unit on the host
                a[i].Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                a[i].Rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                a[i].Speed = r.ReadSingle();
                a[i].ActualSpeed = r.ReadSingle();
                a[i].WantedSpeed = r.ReadSingle();
                a[i].WantedHeight = r.ReadSingle();
                if (sea) ReadSeaDamage(r, ref a[i]);
                else a[i].AirSmoke = r.ReadByte();
            }
            return a;
        }

        // ---------- client ----------

        /// <summary>The client's version of GenerateCampaignEngagement: same battle, sides swapped, no campaign side effects.</summary>
        private static void ClientEnter(string engagementJson, int[] pixelCoords)
        {
            CampaignManager cm = CampaignManager.instance;
            CampaignInterface ci = CampaignInterface.instance;
            ClientActive = true;
            firstApply = true;
            clientStartAllowed = false;
            pendingCombatStart = false;
            snapsReceived = 0;
            puppetized.Clear();
            appliedShade.Clear();
            lastControls.Clear();
            localOrderAt.Clear();
            forwardNest = 0;
            turretTargets = new short[0][];
            DcReset();
            EnvReset();
            ControlEchoReset();
            RetreatReset();
            ReportReset();
            TeamLocks.BattleReset();
            stateFresh = false;
            seaTargets = new UnitState[0];
            airTargets = new UnitState[0];
            TimeControlDriver.InCampaignBattle = true;

            if (pixelCoords.Length > 0)
            {
                cm.campaignData.lastEncounterPixelCoords = pixelCoords;
                TerrainTiles.instance.xyPixelCoord = pixelCoords;
            }
            CombatInterface.instance.gameObject.SetActive(false);
            if (ci.enablePlotCourse)
            {
                ci.PlotCourse();
                ci.DestroyPathLines();
                ci.DestroyPathLine();
            }
            // The recognition manual (CombatInterface tab 4) lives under the campaign UI between battles; vanilla
            // GenerateCampaignEngagement moves it into the battle UI. That path doesn't run here, so P2 had no recognition
            // panel in battle (user report, 2026-11-25). Same three lines as vanilla:
            try
            {
                UnitReference ur = UnitReference.instance;
                CombatInterface cbi = CombatInterface.instance;
                if (ur != null && cbi != null && cbi.tabs != null && cbi.tabs.Length > 4 && ur.unitRefPositions != null && ur.unitRefPositions.Length > 2)
                {
                    ur.wasLeftOpen = cbi.tabs[4].activeSelf;
                    ur.unitRefPositions[1].transform.position = cbi.tabs[4].transform.GetChild(0).position;
                    ur.SwitchUnitReferenceTransform(ur.unitRefPositions[2], isOn: false);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("MP battle: moving the recognition panel failed: " + e.Message); }
            if (EngagementPanel.instance != null && EngagementPanel.instance.gameObject.activeSelf) EngagementPanel.instance.gameObject.SetActive(false);
            EngagementManager.instance.ClearEngagementData();
            EngagementData ed = EngagementManager.instance.engagementData;
            JsonUtility.FromJsonOverwrite(engagementJson, ed);
            ed.factionPlayed = 0; // the host's view; switchSides below makes P2's groups the "player" side
            // (a subordinate of the host's team keeps the host's view: its own units are the host's)
            bool swap = !Multiplayer.Session.IsSubordinate || Multiplayer.Session.LocalTeam == 1;
            cm.SwichToWorld(forward: true);
            EngagementManager.instance.InitialiseEngagement(string.Empty, loadFromFile: false, switchSides: swap);
            if (Multiplayer.Session.IsSubordinate)
                Toasts.Show("Battle! Help command your team's ships and planes. The unit your commander has selected and the ones they " +
                            "locked take no orders from you; the commanders press Start and decide on retreating.", 12f);
            else Toasts.Show("Battle! Command your ships: speed, rudder and map waypoints. Press Start when ready; the battle begins when both players are ready.");
            Plugin.Log.LogInfo($"MP battle: client entered the battle ({ed.unitGroups.Count} groups{(swap ? "" : ", host's side")})");
        }

        private static bool pendingCombatStart; // subordinate: the host started while this copy's battle was still loading

        private static void ClientCombatStarted()
        {
            if (Multiplayer.Session.IsSubordinate && (firstApply || SetupDepth > 0))
            {
                pendingCombatStart = true; // (Player 2 can't be here: the host waits for its Start)
                return;
            }
            pendingCombatStart = false;
            clientStartAllowed = true;
            try { EngagementManager.instance.StartCombat(checkLegal: false); }
            finally { clientStartAllowed = false; }
            if (EngagementAI.instance != null) EngagementAI.instance.enabled = false;
        }

        /// <summary>Client, message 42: the host has left the battle.</summary>
        private static void ClientLeave()
        {
            if (ActionReport.instance != null && ActionReport.instance.gameObject.activeSelf)
            {
                // P2 is reading their own action report: leave when they press Continue (CloseActionReport → QuitMission)
                ClientHostLeftDuringReport();
                Toasts.Show("The host has closed the battle. Press Continue on your report to return to the map.");
                return;
            }
            Plugin.Log.LogInfo("MP battle: host ended the battle, client returns to the map");
            CombatInterface.instance.QuitMission(false); // vanilla exit (campaign write-back is blocked on the client) → ClientCleanup
            Toasts.Show("The battle is over. Results appear on the map with the next update.");
        }

        /// <summary>Client: the battle scene is closing (QuitMission postfix, or ClientLeave).</summary>
        internal static void ClientCleanup()
        {
            if (!ClientActive) return;
            ClientActive = false;
            puppetized.Clear();
            appliedShade.Clear();
            lastControls.Clear();
            DcReset();
            EnvReset();
            ControlEchoReset();
            RetreatReset();
            ReportReset();
        }

        /// <summary>
        /// Client: the battle clock runs locally at the host's speed (TimeControlDriver mirrors it); snap it to the host's
        /// when it drifts, so time of day, lighting and the clock text match.
        /// </summary>
        private static void ClientClock(long hostTicks, float hostEngagementTime)
        {
            EngagementManager em = EngagementManager.instance;
            if (em == null) return;
            if (Math.Abs(em.engagementTime - hostEngagementTime) > 2f) em.engagementTime = hostEngagementTime;
            if (Math.Abs((em.dateTime - new DateTime(hostTicks)).TotalSeconds) > 2.0) em.dateTime = new DateTime(hostTicks);
        }

        private static void ClientApply()
        {
            EngagementManager em = EngagementManager.instance;
            if (em == null || em.allSeaUnits == null) return;
            if (EngagementAI.instance != null && EngagementAI.instance.enabled) EngagementAI.instance.enabled = false;
            float k = firstApply ? 1f : 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime);
            bool fresh = stateFresh;
            stateFresh = false;
            bool applied = ApplyList(em.allSeaUnits, seaTargets, k, true, fresh) | ApplyList(em.allAirUnits, airTargets, k, false, fresh);
            if (applied) firstApply = false;
            ClientApplyTurrets(1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));
        }

        private static bool ApplyList(List<Unit> units, UnitState[] targets, float k, bool sea, bool fresh)
        {
            bool any = false;
            int n = Math.Min(units.Count, targets.Length);
            for (int i = 0; i < n; i++)
            {
                Unit u = units[i];
                UnitState s = targets[i];
                if (u == null || (s.Flags & 4) == 0) continue;
                Puppetize(u);
                if (u.unitAI != null && u.unitAI.enabled) u.unitAI.enabled = false; // vanilla UI (waypoints) re-enables it
                bool active = (s.Flags & 1) != 0;
                Transform t = u.transform;
                t.position = Vector3.Lerp(t.position, s.Pos, k);
                t.rotation = Quaternion.Slerp(t.rotation, s.Rot, k);
                u.currentSpeed = s.Speed;
                u.currentActualSpeed = s.ActualSpeed; // movement (which sets it) is off on the client
                // the host's orders (its AI changes them too: attack runs, formations); not right after P2 changed one,
                // so a snapshot sent before the host had P2's order doesn't undo it on screen
                if (fresh && !RecentLocalOrder(u))
                {
                    u.wantedSpeed = s.WantedSpeed;
                    u.wantedHeight = s.WantedHeight;
                }
                ClientUnitDamage(u, s, sea, fresh);
                if (u.gameObject.activeSelf != active) u.gameObject.SetActive(active); // also hides sunk / departed units
                any = true;
            }
            return any;
        }

        /// <summary>A client unit only shows the host's state: no physics, no AI.</summary>
        private static void Puppetize(Unit u)
        {
            if (puppetized.Contains(u)) return;
            puppetized.Add(u);
            if (u.unitRigidbody != null)
            {
                u.unitRigidbody.velocity = Vector3.zero;
                u.unitRigidbody.angularVelocity = Vector3.zero;
                u.unitRigidbody.isKinematic = true;
            }
            if (u.unitAI != null) u.unitAI.enabled = false;
            // (auto damage control: the flag shows the host's value (MpBattleDc); CheckAutoDamageControl never runs here)
        }

        internal static bool ClientStartAllowed => clientStartAllowed;
    }

    // ---------- patches ----------

    /// <summary>Host: capture the battle definition when an MP battle initialises.</summary>
    [HarmonyPatch(typeof(EngagementManager), nameof(EngagementManager.InitialiseEngagement))]
    internal static class MpBattleInitPatch
    {
        private static void Prefix()
        {
            if (ModState.MultiplayerActive && Multiplayer.Session.IsHost && MpBattle.HostActive) MpBattle.HostCapture();
        }
    }

    /// <summary>
    /// Start ends the deployment phase. MP: the battle begins when both players have pressed Start (client: sends "ready";
    /// host: waits for it). The host's real start is mirrored to the client (message 43).
    /// </summary>
    [HarmonyPatch(typeof(EngagementManager), nameof(EngagementManager.StartCombat))]
    internal static class MpBattleStartCombatPatch
    {
        private static bool Prefix()
        {
            if (MpBattle.ClientActive) return MpBattle.ClientStartAllowed || MpBattle.ClientPressedStart();
            if (ModState.MultiplayerActive && Multiplayer.Session.IsHost && MpBattle.HostActive) return MpBattle.HostMayStart();
            return true;
        }

        private static void Postfix()
        {
            // postfixes also run when the prefix skipped the start: only a real start (deployment over) is mirrored
            if (ModState.MultiplayerActive && Multiplayer.Session.IsHost && !EngagementManager.instance.strategicCombatMode)
                MpBattle.HostCombatStarted();
        }
    }

    [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.QuitMission))]
    internal static class MpBattleQuitPatch
    {
        private static void Postfix(bool confirm)
        {
            if (confirm || !ModState.MultiplayerActive) return;
            if (Multiplayer.Session.IsHost) MpBattle.HostEnded();
            else MpBattle.ClientCleanup(); // P2 closed their report (or the host's end arrived): the battle scene is gone
        }
    }

    /// <summary>
    /// Client: units are puppets; the host moves them. Only the per-unit movement step is skipped: the rest of
    /// EngagementManager.FixedUpdate (battle clock, time-of-day text) keeps running.
    /// </summary>
    [HarmonyPatch]
    internal static class MpBattleClientMovementOffPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UnitSea), nameof(UnitSea.ApplyUnitMovement))]
        [HarmonyPatch(typeof(UnitAir), nameof(UnitAir.ApplyUnitMovement))]
        private static bool Prefix() => !MpBattle.ClientActive;
    }

    /// <summary>Client: a battle never writes results into its campaign copy (the host's snapshots carry them).</summary>
    [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.ExportWorldData))]
    internal static class MpBattleClientNoExportPatch
    {
        private static bool Prefix() => !(ModState.MultiplayerActive && !Multiplayer.Session.IsHost);
    }

    /// <summary>
    /// MP: vanilla autosaves when a campaign battle starts (GenerateCampaignEngagement → SaveCampaign("Autosave")),
    /// which would overwrite the player's single-player autosave with the multiplayer campaign. In MP every save goes to
    /// a separate "Multiplayer …" file instead (found in the 2026-11-24 battle test).
    /// </summary>
    [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.SaveCampaign))]
    internal static class MpSaveNamePatch
    {
        private static void Prefix(ref string filename)
        {
            if (!ModState.MultiplayerActive || string.IsNullOrEmpty(filename) || filename.StartsWith("Multiplayer")) return;
            string renamed = "Multiplayer " + (Multiplayer.Session.IsHost ? "(Host) " : "(Player " + (Multiplayer.Session.LocalPlayerIndex + 1) + ") ") + filename;
            Plugin.Log.LogInfo($"MP: save '{filename}' redirected to '{renamed}' (keeps your single-player saves untouched)");
            filename = renamed;
        }
    }

    /// <summary>
    /// Host, MP: after a battle vanilla may send a damaged Enemy task force home through the AI's missions
    /// (ProcessMobileObjectPostCombat → CampaignAI.ApplyNavDataToMobile). In MP that force is Player 2's.
    /// </summary>
    [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.ApplyNavDataToMobile))]
    internal static class MpBattleNoAiRetreatPatch
    {
        private static bool Prefix() => !ModState.MultiplayerActive;
    }
}
