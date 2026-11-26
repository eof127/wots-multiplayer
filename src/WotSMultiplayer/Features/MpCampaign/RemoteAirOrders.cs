using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WorldMapStrategyKit;
using WotSMultiplayer.Core;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// M4b part 3: Player 2's air operations. Launch from a base or a ship (strike panel "Done" and quick scout), then the
    /// usual course plotting (message 29), return to base, land, CAP and strategic bombing (message 33).
    /// Auto-scout patterns, repeat course and escorts are not supported yet (blocked on the client with a toast).
    /// </summary>
    internal static partial class RemoteOrders
    {
        private static MethodInfo getAirStrikeIndex, checkAirAllowed;

        // ---------- client ----------

        /// <summary>Client: the strike panel's Done (also reached by the quick-scout button) → launch order.</summary>
        internal static void SendLaunchAir()
        {
            CampaignInterface ci = CampaignInterface.instance;
            CampaignManager cm = CampaignManager.instance;
            GameObjectAnimator src = ci.currentMapObject;
            if (src == null) return;
            if (checkAirAllowed == null) checkAirAllowed = AccessTools.Method(typeof(CampaignInterface), "CheckAirAllowed");
            if (!(bool)checkAirAllowed.Invoke(ci, null)) return; // shows the vanilla "air operations hours" message
            if (getAirStrikeIndex == null) getAirStrikeIndex = AccessTools.Method(typeof(CampaignInterface), "GetAirStrikeIndex");
            int idx = (int)getAirStrikeIndex.Invoke(ci, null);
            if (idx < 0 || idx >= ci.numberToLaunch.Count || idx >= ci.prefabsToLaunch.Count || ci.numberToLaunch[idx] <= 0) return;
            string prefab = ci.prefabsToLaunch[idx];
            int n = ci.numberToLaunch[idx];
            int payload = ci.payloadDropdown.value;
            string from;
            if (src.type == 3)
            {
                MapLocation ml = src.GetComponent<MapLocation>();
                if (ml == null) return;
                from = ml.locationName;
                Send(MsgLaunchAir, w => { w.Write((byte)0); w.Write(ml.locationID); w.Write(prefab); w.Write(n); w.Write(payload); });
                CampaignSync.ExpectNewOwnMobile(1);
            }
            else
            {
                int id = ClientOwnSelected(0);
                int ship = ci.GetShipHighlightedIndex();
                if (id < 0 || ship < 0) return; // (BlockedId: the toast already said why)
                from = src.mobileMapObject.unitDisplayNames[ship];
                Send(MsgLaunchAir, w => { w.Write((byte)1); w.Write(id); w.Write(ship); w.Write(prefab); w.Write(n); w.Write(payload); });
                CampaignSync.ExpectNewOwnMobile(1);
            }
            ci.isSelectingAircraftType = false;
            ci.payloadDropdown.value = 0;
            ci.SelectMapObject(src);
            string name = EngagementManager.instance.unitDataDictionary.ContainsKey(prefab) ? EngagementManager.instance.unitDataDictionary[prefab].unitName : prefab;
            Toasts.Show($"Order sent: launch {n} {name} from {from}. The group appears with the next update; then plot its course.");
        }

        /// <summary>Client: an order for the selected own air group. Returns false if nothing was sent.</summary>
        internal static bool SendAirOrder(AirCmd cmd)
        {
            int id = ClientOwnSelected(1);
            if (id < 0) return false;
            Send(MsgAirOrder, w => { w.Write(id); w.Write((byte)cmd); });
            string what = cmd == AirCmd.ReturnToBase ? "return to base" : cmd == AirCmd.Land ? "land" : cmd == AirCmd.CapOn ? "fly CAP"
                        : cmd == AirCmd.CapOff ? "stop CAP" : "bomb the base below";
            Toasts.Show($"Order sent: {CampaignInterface.instance.currentMapObject.mobileMapObject.mobileName} — {what}.");
            return true;
        }

        // ---------- host ----------

        /// <summary>Faction-1 version of CampaignInterface.PlayerDoneNewAirStrike (same checks and bookkeeping as the player's).</summary>
        private static void HostLaunchAir(BinaryReader r)
        {
            byte kind = r.ReadByte();
            string locId = null;
            int id = -1, ship = -1;
            if (kind == 0) locId = r.ReadString();
            else { id = r.ReadInt32(); ship = r.ReadInt32(); }
            string prefab = r.ReadString();
            int requested = r.ReadInt32();
            int payloadValue = r.ReadInt32();

            CampaignManager cm = CampaignManager.instance;
            CampaignInterface ci = CampaignInterface.instance;
            CampaignData cd = cm.campaignData;
            if (!cd.airOpHours[ci.dateTime.Hour]) { Notice("No launch: outside air operations hours."); return; }
            if (!EngagementManager.instance.unitAirDictionary.ContainsKey(prefab)) { Notice("No launch: unknown aircraft type."); return; }
            UnitData air = EngagementManager.instance.unitDataDictionary[prefab];

            int maxGroup = Config.instance.maxInFlight;
            int n, j;
            Vector2 pos;
            var createdBy = new List<string>();
            string from;
            MapLocation ml = null;
            MobileMapObject carrier = null;
            List<int> onBoard = null;
            if (kind == 0)
            {
                ml = cm.GetMapLocationObject(locId);
                if (ml == null || ml.currentFaction != OF) { Notice("No launch: you don't hold that base."); return; }
                if (ml.aircraftCooldown > 0f) { Notice($"No launch: {ml.locationName} is still readying aircraft ({ci.GetSecondsToHours(ml.aircraftCooldown)})."); return; }
                List<string> types = cm.GetAirTypesAtLocation(ml);
                j = types.IndexOf(prefab);
                if (j < 0 || ml.airfieldLevel < 1) { Notice($"No launch: no {air.unitName} at {ml.locationName}."); return; }
                List<int> nums = cm.GetAirNumbersAtLocation(ml);
                int off = (ml.airfieldLevel - 1) * types.Count;
                int destroyed = ml.destroyedAir != null && j < ml.destroyedAir.Count ? ml.destroyedAir[j] : 0;
                int inAir = ml.inTheAir != null && j < ml.inTheAir.Count ? ml.inTheAir[j] : 0;
                int available = j + off < nums.Count ? nums[j + off] - inAir - destroyed : 0;
                n = OrderRules.LaunchCount(requested, available, maxGroup);
                pos = ml.gameObjectAnimator.currentMap2DLocation;
                createdBy.Add(ml.locationID);
                from = ml.locationName;
            }
            else
            {
                GameObjectAnimator tf = HostOwn(id, 0);
                if (tf == null) { Notice("No launch: that task force is gone."); return; }
                carrier = tf.mobileMapObject;
                if (ship < 0 || ship >= carrier.unitPrefabs.Count) return;
                UnitData sd = EngagementManager.instance.unitDataDictionary[carrier.unitPrefabs[ship]];
                from = carrier.unitDisplayNames[ship];
                j = sd.aircraftOnBoard.IndexOf(prefab);
                if (j < 0) { Notice($"No launch: {from} carries no {air.unitName}."); return; }
                if (carrier.cellVariables[ship] != string.Empty && !Utilities.GetSubsystemAvailable(carrier.unitPrefabs[ship], carrier.cellVariables[ship], CellType.Aircraft))
                { Notice($"No launch: {from}'s flight deck is damaged."); return; }
                float cooldown = cm.HoursShipLaunchAir(carrier, ship);
                if (cooldown > 0f) { Notice($"No launch: {from} is still readying aircraft ({ci.GetSecondsToHours(cooldown)})."); return; }
                onBoard = cm.GetIntListFromString(carrier.currentAircraft[ship]);
                n = OrderRules.LaunchCount(requested, j < onBoard.Count ? onBoard[j] : 0, maxGroup);
                pos = tf.currentMap2DLocation;
                createdBy.Add(carrier.unitPrefabs[ship]);
                createdBy.Add(carrier.unitInstances[ship].ToString());
            }
            if (n <= 0) { Notice($"No launch: no {air.unitName} ready at {from}."); return; }

            EnsureTemplate();
            ci.ClearNewGroupData(template);
            template.type = 1;
            template.currentFaction = OF;
            template.isMoving = false;
            for (int i = 0; i < n; i++)
            {
                template.unitPrefabs.Add(prefab);
                template.paint.Add(0);
            }
            template.formation = 5;
            template.formationSpacing = 50;
            UnitListData ua = EngagementManager.instance.unitAirDictionary[prefab];
            template.payload = OrderRules.Payload(payloadValue, ua.payloadIndexes, ua.payloadDisplayNames.Count);
            template.mobileName = air.unitName.ToUpper();

            if (ml != null)
            {
                ml.inTheAir[j] += n;
                cm.ApplyCooldownToAirLauncher(ml, null, -1, template);
            }
            else
            {
                onBoard[j] -= n;
                carrier.currentAircraft[ship] = cm.SetIntListToString(onBoard);
                cm.ApplyCooldownToAirLauncher(null, carrier, ship, template);
            }

            GameObjectAnimator selected = ci.currentMapObject;
            GameObjectAnimator goa = ci.CreateNewMobileObject(CampaignObjectType.Air, template, pos, isLoading: false, createdBy, from);
            ci.currentMapObject = selected;
            goa.mobileMapObject.mobileName = template.mobileName;
            ShowAsEnemy(goa);
            Plugin.Log.LogInfo($"MP order: {Who} launched {n} {prefab} from {from} (payload {string.Join(",", template.payload.ConvertAll(p => p.ToString()).ToArray())})");
            NoticeWithSound($"{n} {air.unitName} launched from {from}. Plot the group's course.", "NewAir");
        }

        // ---------- scout patterns (user request 2026-11-25: circles and patrols for P2 like the player) ----------
        // Vanilla AddScoutWaypoint / OnScoutDirectionChange / ClearScoutWaypoints build MobileMapObject.autoPath (points around
        // the carrier, or absolute points around the base) and set autoMove; CheckNextWaypoint and AirAttemptLand then fly it
        // and relaunch it as a patrol. On P2's copy the vanilla calls run as usual (path lines, distance text) and the result
        // is sent to the host (AirCmd.ScoutPattern), which applies it to the faction-1 group and starts the first leg.

        /// <summary>Client, after a vanilla scout-pattern call: send the selected own air group's pattern.</summary>
        internal static void SendScoutPattern()
        {
            int id = ClientOwnSelected(1);
            if (id < 0) return;
            MobileMapObject m = CampaignInterface.instance.currentMapObject.mobileMapObject;
            List<Vector2> path = m.autoMove ? m.autoPath : new List<Vector2>();
            int n = Math.Min(path.Count, 32);
            Send(MsgAirOrder, w =>
            {
                w.Write(id); w.Write((byte)AirCmd.ScoutPattern);
                w.Write(n);
                for (int i = 0; i < n; i++) { w.Write(path[i].x); w.Write(path[i].y); }
            });
        }

        /// <summary>
        /// Client, after vanilla AutoPlayerManager.RepeatCourse (the "repeat course" patrol toggle) ran on P2's copy: send
        /// the selected own group's autoMove flag and loop. Sea → FleetCmd.Repeat, air → AirCmd.Repeat.
        /// </summary>
        internal static void SendRepeat()
        {
            GameObjectAnimator sel = CampaignInterface.instance.currentMapObject;
            if (sel == null || sel.mobileMapObject == null) return;
            MobileMapObject m = sel.mobileMapObject;
            int id = ClientOwnSelected(m.type);
            if (id < 0) return;
            List<Vector2> path = m.autoMove ? m.autoPath : new List<Vector2>();
            int n = Math.Min(path.Count, 64);
            Action<BinaryWriter> body = w =>
            {
                w.Write(m.autoMove);
                w.Write(n);
                for (int i = 0; i < n; i++) { w.Write(path[i].x); w.Write(path[i].y); }
            };
            if (m.type == 0) SendFleet(FleetCmd.Repeat, id, body, m.autoMove ? $"Order sent: {m.mobileName} repeats its course." : $"Order sent: {m.mobileName} stops repeating.");
            else Send(MsgAirOrder, w => { w.Write(id); w.Write((byte)AirCmd.Repeat); body(w); });
        }

        /// <summary>Host: the repeat-course loop P2 set (vanilla RepeatCourse's effect: autoMove + autoPath, the course itself unchanged).</summary>
        internal static void HostRepeat(MobileMapObject mmo, BinaryReader r)
        {
            bool on = r.ReadBoolean();
            int n = r.ReadInt32();
            if (n < 0 || n > 64) return;
            var path = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                var p = new Vector2(r.ReadSingle(), r.ReadSingle());
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || Mathf.Abs(p.x) > 1f || Mathf.Abs(p.y) > 1f) return;
                path.Add(p);
            }
            if (mmo.escortingThisGOA != null) return; // vanilla: escorts don't repeat
            mmo.autoMove = on && path.Count > 0;
            mmo.autoPath = mmo.autoMove ? path : new List<Vector2>();
            Plugin.Log.LogInfo($"MP order: {Who} {(mmo.autoMove ? "repeats the course of" : "stops repeating")} '{mmo.mobileName}' ({path.Count} points)");
        }

        /// <summary>Host: vanilla AddScoutWaypoint's effect on Player 2's air group, from the finished pattern.</summary>
        private static void HostScoutPattern(GameObjectAnimator goa, BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 32) return;
            var path = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                var p = new Vector2(r.ReadSingle(), r.ReadSingle());
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || Mathf.Abs(p.x) > 1f || Mathf.Abs(p.y) > 1f) return;
                path.Add(p);
            }
            CampaignInterface ci = CampaignInterface.instance;
            MobileMapObject mmo = goa.mobileMapObject;
            if (mmo.endurance <= 0f)
            {
                NoticeWithSound($"{mmo.mobileName} is at bingo fuel and must return.", "BingoFuel");
                return;
            }
            if (path.Count == 0)
            {
                // vanilla ClearScoutWaypoints
                mmo.mapWaypoints.Clear();
                mmo.autoPath.Clear();
                mmo.autoMove = false;
                goa.Stop();
                goa.endingMap2DLocation = goa.currentMap2DLocation;
                Plugin.Log.LogInfo($"MP order: {Who} cleared the scout pattern of '{mmo.mobileName}'");
                return;
            }
            mmo.escortingThisGOA = null;
            mmo.escortingGOAIndex = -1;
            goa.isMoving = false;
            goa.Stop();
            mmo.autoPath = path;
            mmo.autoMove = true;
            goa.endingMap2DLocation = path[0];
            ci.CalculateAutoWaypoints(goa);                                        // carrier-launched: points around the ship
            if (mmo.createdBy.Count == 1) mmo.mapWaypoints = new List<Vector2>(path); // base-launched: absolute points
            if (mmo.mapWaypoints.Count > 0 && CampaignManager.instance.campaignData.airOpHours[ci.dateTime.Hour])
            {
                ci.MoveMobileToDestination(goa, mmo.mapWaypoints[0]);
                mmo.mapWaypoints.RemoveAt(0);
            }
            Plugin.Log.LogInfo($"MP order: {Who} scout pattern for '{mmo.mobileName}' ({path.Count} points)");
        }

        private static void HostAirOrder(BinaryReader r)
        {
            int id = r.ReadInt32();
            AirCmd cmd = (AirCmd)r.ReadByte();
            GameObjectAnimator goa = HostOwn(id, 1);
            if (goa == null) return;
            CampaignManager cm = CampaignManager.instance;
            MobileMapObject mmo = goa.mobileMapObject;
            string name = mmo.mobileName;
            if (cmd == AirCmd.ScoutPattern) { HostScoutPattern(goa, r); return; }
            if (cmd == AirCmd.Repeat) { HostRepeat(mmo, r); return; }
            switch (cmd)
            {
                case AirCmd.ReturnToBase:
                    // vanilla SetAirReturnToBase without its UI parts (escort dropdown, path lines)
                    ClearAirCourse(goa);
                    if (!cm.AirAttemptLand(goa)) cm.AirReturnToBase(goa);
                    Notice($"{name}: returning to base.");
                    break;
                case AirCmd.Land:
                    mmo.escortingThisGOA = null;
                    mmo.escortingGOAIndex = -1;
                    if (mmo.endurance > 0f)
                    {
                        mmo.autoMove = false;
                        mmo.autoPath.Clear();
                        mmo.mapWaypoints.Clear();
                        goa.Stop();
                    }
                    Notice(cm.AirAttemptLand(goa) ? $"{name} landed." : $"{name} is too far from its base or ship to land.");
                    break;
                case AirCmd.CapOn:
                case AirCmd.CapOff:
                    mmo.isCAP = cmd == AirCmd.CapOn;
                    break;
                case AirCmd.StrategicBomb:
                    MapLocation ml = NearLocation(goa, 2f);
                    if (ml == null || ml.currentFaction != EnemyFaction) { Notice($"{name}: fly over an enemy base first."); return; }
                    if (mmo.payload == null || mmo.payload.Count == 0) { Notice($"{name} carries no bombs."); return; }
                    cm.StrategicBombLocation(mmo, ml);
                    Plugin.Log.LogInfo($"MP order: {Who} strategic bombing of {ml.locationName} by {name}");
                    Notice($"{name} bombed {ml.locationName}.");
                    break;
            }
        }

        private static void ClearAirCourse(GameObjectAnimator goa)
        {
            MobileMapObject mmo = goa.mobileMapObject;
            mmo.escortingThisGOA = null;
            mmo.escortingGOAIndex = -1;
            if (mmo.endurance <= 0f) return;
            goa.Stop();
            goa.endingMap2DLocation = goa.currentMap2DLocation;
            mmo.mapWaypoints.Clear();
            mmo.autoMove = false;
            mmo.autoPath.Clear();
        }

        /// <summary>
        /// Host: a faction-1 air group reached the end of a leg. Same rules as the player's branch of
        /// CampaignInterface.CheckNextWaypoint: next plotted leg during air-ops hours, else back to base; the end of a
        /// plotted course → OnDestinationReached (loiters until fuel runs low, then the endurance check sends it home).
        /// </summary>
        internal static void Faction1AirLegDone(GameObjectAnimator obj)
        {
            CampaignManager cm = CampaignManager.instance;
            CampaignInterface ci = CampaignInterface.instance;
            MobileMapObject mmo = obj.mobileMapObject;
            bool airOps = cm.campaignData.airOpHours[ci.dateTime.Hour];
            // the player's air branch of vanilla CheckNextWaypoint, scout patterns included
            if (mmo.autoMove && mmo.autoPath.Count > 0 && obj.currentMap2DLocation == mmo.autoPath[mmo.autoPath.Count - 1] && mmo.mapWaypoints.Count < 2)
                cm.AirReturnToBase(obj);
            else if (mmo.mapWaypoints.Count > 0)
            {
                if (!airOps) { cm.AirReturnToBase(obj); return; }
                if (mmo.autoMove)
                {
                    int from = Math.Max(1, mmo.autoPath.Count - mmo.mapWaypoints.Count); // carrier patterns follow the ship
                    ci.CalculateAutoWaypoints(obj, from);
                }
                obj.Stop();
                ci.MoveMobileToDestination(obj, mmo.mapWaypoints[0]);
                mmo.mapWaypoints.RemoveAt(0);
            }
            else if (mmo.autoMove) cm.AirReturnToBase(obj);
            else cm.OnDestinationReached(obj);
        }
    }

    // ---------- client patches ----------

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.PlayerDoneNewAirStrike))]
    internal static class ClientLaunchAirPatch
    {
        private static bool Prefix()
        {
            if (!CampaignSync.IsClientPuppet) return true;
            RemoteOrders.SendLaunchAir();
            return false;
        }
    }

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.SetAirReturnToBase))]
    internal static class ClientAirReturnPatch
    {
        private static bool Prefix()
        {
            if (!CampaignSync.IsClientPuppet) return true;
            RemoteOrders.SendAirOrder(AirCmd.ReturnToBase);
            return false;
        }
    }

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.SetAirAttemptLanding))]
    internal static class ClientAirLandPatch
    {
        private static bool Prefix()
        {
            if (!CampaignSync.IsClientPuppet) return true;
            RemoteOrders.SendAirOrder(AirCmd.Land);
            return false;
        }
    }

    /// <summary>The CAP toggle also fires when the panel shows a group: only a real change is an order.</summary>
    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.SetCAP))]
    internal static class ClientAirCapPatch
    {
        private static void Prefix()
        {
            if (!CampaignSync.IsClientPuppet) return;
            CampaignInterface ci = CampaignInterface.instance;
            GameObjectAnimator goa = ci.currentMapObject;
            if (goa == null || goa.mobileMapObject == null || goa.mobileMapObject.type != 1) return;
            if (goa.mobileMapObject.isCAP != ci.capToggle.isOn) RemoteOrders.SendAirOrder(ci.capToggle.isOn ? AirCmd.CapOn : AirCmd.CapOff);
        }
    }

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.StrategicBomb))]
    internal static class ClientStrategicBombPatch
    {
        private static bool Prefix()
        {
            if (!CampaignSync.IsClientPuppet) return true;
            RemoteOrders.SendAirOrder(AirCmd.StrategicBomb);
            return false;
        }
    }

    /// <summary>Not yet for Player 2 (UI-bound vanilla code that would only change the client's copy).</summary>
    [HarmonyPatch]
    internal static class ClientAirNotYetPatches
    {
        // scout patterns: vanilla runs on P2's copy (path lines, distance), then the pattern goes to the host
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.AddScoutWaypoint))]
        [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.ClearScoutWaypoints))]
        private static void ScoutPatternSent()
        {
            if (CampaignSync.IsClientPuppet && !inDirectionChange) RemoteOrders.SendScoutPattern();
        }

        private static bool inDirectionChange;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.OnScoutDirectionChange))]
        private static void DirectionBefore() { if (CampaignSync.IsClientPuppet) inDirectionChange = true; }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.OnScoutDirectionChange))]
        private static Exception DirectionAfter(Exception __exception)
        {
            if (inDirectionChange) { inDirectionChange = false; if (__exception == null) RemoteOrders.SendScoutPattern(); } // one message for the 9 points
            return __exception;
        }

        // repeat course (patrol loop): vanilla runs on P2's copy, then the loop goes to the host
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AutoPlayerManager), nameof(AutoPlayerManager.RepeatCourse))]
        private static void Repeat()
        {
            if (CampaignSync.IsClientPuppet) RemoteOrders.SendRepeat();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AutoPlayerManager), nameof(AutoPlayerManager.AssignMobileMapObjectEscorting))]
        private static bool Escort()
        {
            if (!CampaignSync.IsClientPuppet) return true;
            if (AutoPlayerManager.instance.escortDropdown.value != 0)
            {
                RemoteOrders.NotYet("Escorting");
                AutoPlayerManager.instance.escortDropdown.value = 0;
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.ReArmShips))]
        private static bool Rearm()
        {
            if (!CampaignSync.IsClientPuppet) return true;
            RemoteOrders.SendRearm(); // v0.19.1: done on the host for faction 1 (RemoteResupply)
            return false;
        }
    }

    /// <summary>
    /// MP, both players: build a partial scout pattern (quarter / half circle…) point by point. Vanilla AddScoutWaypoint starts
    /// a new pattern when the group is moving; in single-player you click the points while paused, but in MP the campaign keeps
    /// running (the game's automatic pauses are undone, D22), so the group was already flying after the first point and each
    /// further click started over — only the full circle (one call for all points) worked (user test 2026-11-26). Clicks on
    /// the same group within 20 s now keep adding to its pattern, as when paused.
    /// </summary>
    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.AddScoutWaypoint))]
    internal static class MpScoutPatternBuildPatch
    {
        private static GameObjectAnimator lastGroup;
        private static float lastClick = -100f;

        private static void Prefix(CampaignInterface __instance)
        {
            if (!ModState.MultiplayerActive) return;
            GameObjectAnimator g = __instance.currentMapObject;
            if (g == null || g.mobileMapObject == null || g.mobileMapObject.type != 1) return;
            float now = Time.realtimeSinceStartup;
            MobileMapObject m = g.mobileMapObject;
            bool continuing = g == lastGroup && now - lastClick < 20f && m.autoMove && m.autoPath != null && m.autoPath.Count > 0;
            lastGroup = g;
            lastClick = now;
            if (continuing && g.isMoving) g.isMoving = false; // vanilla's own reset minus clearing the pattern
        }
    }

    // ---------- host: no AI for Player 2's aircraft ----------

    [HarmonyPatch]
    internal static class HostAirAiOffPatches
    {
        /// <summary>
        /// Every 5 minutes vanilla points the Enemy side's airborne groups at the nearest spotted player contact
        /// (CampaignManager.CheckAirEndurance → GetNewSpottedWaypoint). In MP those are Player 2's planes: they fly where
        /// Player 2 sends them.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CampaignAI), nameof(CampaignAI.GetNewSpottedWaypoint))]
        private static bool NoChase(ref int __result)
        {
            if (!ModState.MultiplayerActive) return true;
            __result = -1;
            return false;
        }

        /// <summary>
        /// When planes land on an Enemy carrier, vanilla checks the AI's mission and may send the carrier home
        /// (CheckAICarrierGroupAirWingSufficient). In MP that decision is Player 2's.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.CheckAICarrierGroupAirWingSufficient))]
        private static bool NoCarrierRecall(ref bool __result)
        {
            if (!ModState.MultiplayerActive) return true;
            __result = true;
            return false;
        }
    }
}
