using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WorldMapStrategyKit;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// M4b: Player 2's campaign orders. The client (a puppet showing its side as "Player") intercepts the vanilla UI
    /// actions and sends them to the host, which executes them for faction 1 with the game's own functions.
    /// The result reaches the client through the next snapshot.
    ///   28 CreateTaskForce  {locationId, template MobileMapObject JSON, cost, name}
    ///   29 SetCourse        {mpId, x, y, append}
    ///   30 Upgrade          {locationId, baseType}
    ///   31 Disband          {mpId, ship indexes}
    ///   32 LaunchAir        {kind 0: locationId | kind 1: mpId + ship index; prefab, number, payload dropdown value}  (RemoteAirOrders.cs)
    ///   33 AirOrder         {mpId, AirCmd}                                                                         (RemoteAirOrders.cs)
    ///   34 FleetOrder       {mpId, FleetCmd, args}                                                                 (RemoteFleetOrders.cs)
    ///   35 CargoOrder       {mpId, CargoCmd, args}                                                                 (RemoteCargoOrders.cs)
    /// Additional players, stage 3 (D57): subordinates send the same messages. The host runs a team-1 subordinate's orders
    /// exactly like Player 2's (faction 1) and a team-0 subordinate's for the host's own side (faction 0, the player's lists
    /// and command points), after checking the team's locks (TeamLocks: the commander's selection and locked forces).
    /// Notices go back to whoever gave the order; successes are also told to the team's commander.
    /// </summary>
    internal static partial class RemoteOrders
    {
        private const byte MsgCreateTaskForce = 28, MsgSetCourse = 29, MsgUpgrade = 30, MsgDisband = 31,
                           MsgLaunchAir = 32, MsgAirOrder = 33, MsgFleetOrder = 34, MsgCargoOrder = 35;

        internal static void Init()
        {
            Multiplayer.Session.OnGameMessage += OnMessage;
            // additional players, stage 3: subordinates give campaign orders too (team- and lock-checked on the host)
            Multiplayer.Session.SubordinateMaySend = t => LockRules.IsOrder(t) || t == LockRules.BattleOrder; // + battle orders (stage 4)
            Multiplayer.Session.OnSubordinateGameMessage += OnSubordinateMessage;
        }

        // ---------- host: whose order is running ----------
        // sender: 1 = Player 2, 2+ = a subordinate (player index); OF ("own faction"): the faction the order runs for
        // (0 = the host's side for a team-0 subordinate, 1 = Player 2's side). Both are reset after every order.
        private static int sender = 1, OF = 1;

        private static Faction OwnFaction => OF == 0 ? Faction.Player : Faction.Enemy;
        private static int EnemyFaction => 1 - OF;
        private static string Who => sender == 1 ? "P2" : $"P{sender + 1} {PlayerName(sender)}";

        private static CampaignData CD => CampaignManager.instance.campaignData;
        private static List<string> OwnedClasses => OF == 0 ? CD.playerOwnedShipClasses : CD.enemyOwnedShipClasses;
        private static List<int> OwnedInstances => OF == 0 ? CD.playerOwnedShipInstances : CD.enemyOwnedShipInstances;
        private static List<int> OwnedRepairDays => OF == 0 ? CD.playerOwnedRepairDays : CD.enemyOwnedRepairDays;

        internal static string PlayerName(int index)
        {
            foreach (PlayerInfo pi in Multiplayer.Session.Players) if (pi.Index == index && !string.IsNullOrEmpty(pi.Name)) return pi.Name;
            return "Player " + (index + 1);
        }

        /// <summary>Host: the host's command-point display after a team-0 subordinate spent or got back points.</summary>
        private static void RefreshHostCommandPoints()
        {
            if (OF == 0) CampaignInterface.instance.SetCommandTotal();
        }

        // ---------- client side ----------

        private static bool firstLeg;

        /// <summary>PlotCourse just toggled: a new plotting session starts with a fresh route (vanilla clears waypoints).</summary>
        internal static void OnPlotCourseToggled()
        {
            if (CampaignInterface.instance.enablePlotCourse) firstLeg = true;
        }

        /// <summary>Client: a map click while plotting the selected own mobile → send one course leg. False = refused (locked).</summary>
        internal static bool OnClientCourseClick(GameObjectAnimator goa, Vector2 dest)
        {
            int id = CampaignSync.ClientIdOf(goa);
            if (id < 0) return true;
            if (TeamLocks.ClientRefuses(id, goa.mobileMapObject != null ? goa.mobileMapObject.mobileName : "")) return false;
            bool append = !firstLeg;
            firstLeg = false;
            Multiplayer.Session.SendGame(MsgSetCourse, Protocol.Build(w => { w.Write(id); w.Write(dest.x); w.Write(dest.y); w.Write(append); }));
            return true;
        }

        /// <summary>Client: "Done" on a new task force → send it to the host instead of creating it locally.</summary>
        internal static void SendCreateTaskForce()
        {
            CampaignInterface ci = CampaignInterface.instance;
            CampaignManager cm = CampaignManager.instance;
            MapLocation port = cm.landMapLocations.Find(ml => ml.gameObjectAnimator == ci.currentMapObject);
            if (port == null) { Toasts.Show("Select the port first."); return; }
            if (ci.newTaskForce.unitPrefabs.Count == 0) { Toasts.Show("Add at least one ship."); return; }
            int available = cm.campaignData.commandPoints[0] + cm.campaignData.commandBonusPoints[0] - cm.campaignData.commandPointSpent[0];
            if (ci.currentCommandCost > available) { ErrorPanel.instance.SetErrorMessage("NotEnoughCommand", ci.currentCommandCost.ToString(), string.Empty, string.Empty); return; }

            JObject tf = MiniJson.ParseObject(JsonUtility.ToJson(ci.newTaskForce));
            SnapshotCodec.StripObjectRefs(tf);
            string name = ci.renameField != null && !string.IsNullOrEmpty(ci.renameField.text) ? ci.renameField.text : ci.GetTaskForceAutoName(ci.newTaskForce, false);
            string json = MiniJson.Write(tf);
            int cost = ci.currentCommandCost;
            Multiplayer.Session.SendGame(MsgCreateTaskForce, Protocol.Build(w => { w.Write(port.locationID); w.Write(json); w.Write(cost); w.Write(name ?? ""); }));
            CampaignSync.ExpectNewOwnMobile(0); // vanilla selects a new task force: so does P2's copy when it arrives

            ci.newTaskForce.unitPrefabs.Clear();
            // close the "new task force" toolbar like vanilla does (it selects the new force, which hides it): left open,
            // IsCreatingNewMobileGroup stayed true and the new force wasn't selected when it arrived (user test 2026-11-26)
            if (ci.mobileMapSeaToolbars != null && ci.mobileMapSeaToolbars.Length > 0 && ci.mobileMapSeaToolbars[0] != null)
                ci.mobileMapSeaToolbars[0].SetActive(false);
            ci.SelectMapObject(port.gameObjectAnimator);
            Toasts.Show($"Order sent: form task force \"{name}\" at {port.locationName}. It appears with the next update.");
        }

        /// <summary>Client: port/airfield upgrade button.</summary>
        internal static void SendUpgrade(int baseType)
        {
            MapLocation ml = CampaignInterface.instance.currentMapObject != null ? CampaignInterface.instance.currentMapObject.GetComponent<MapLocation>() : null;
            if (ml == null) return;
            Multiplayer.Session.SendGame(MsgUpgrade, Protocol.Build(w => { w.Write(ml.locationID); w.Write(baseType); }));
            Toasts.Show($"Order sent: upgrade the {(baseType == 0 ? "port" : "airfield")} at {ml.locationName}.");
        }

        /// <summary>Client: release the highlighted ships of the selected own task force at a friendly port.</summary>
        internal static bool SendDisband()
        {
            CampaignInterface ci = CampaignInterface.instance;
            int id = CampaignSync.ClientIdOf(ci.currentMapObject);
            if (id < 0) return false;
            if (TeamLocks.ClientRefuses(id, ci.currentMapObject.mobileMapObject.mobileName)) return true;
            var picked = new List<int>();
            for (int i = 0; i < ci.shipRosters.Count; i++)
                if (ci.shipRosters[i].gameObject.activeSelf && ci.shipRosters[i].highlightToggle.isOn) picked.Add(i);
            if (picked.Count == 0) return false; // let vanilla show "highlight a ship first"
            Multiplayer.Session.SendGame(MsgDisband, Protocol.Build(w =>
            {
                w.Write(id);
                w.Write(picked.Count);
                foreach (int i in picked) w.Write(i);
            }));
            if (ci.disbandConfirmButton != null) ci.disbandConfirmButton.SetActive(false);
            Toasts.Show($"Order sent: release {picked.Count} ship(s) from {ci.currentMapObject.mobileMapObject.mobileName}.");
            return true;
        }

        // ---------- host side ----------

        private static MobileMapObject template;
        private static System.Reflection.MethodInfo removeAtIndex;

        private static void HostUpgrade(string locationId, int baseType)
        {
            CampaignManager cm = CampaignManager.instance;
            MapLocation ml = cm.landMapLocations.Find(l => l.locationID == locationId);
            if (ml == null || ml.currentFaction != OF) { Notice("Upgrade not possible: you don't hold that location."); return; }
            bool ok = baseType == 0 ? cm.IsPortUpgradable(ml, OwnFaction) : cm.IsAirfieldUpgradable(ml, OwnFaction);
            if (!ok)
            {
                int level = baseType == 0 ? ml.portLevel : ml.airfieldLevel;
                int max = baseType == 0 ? cm.campaignData.maxPort[OF] : cm.campaignData.maxAirfield[OF];
                Notice(level >= max ? $"{ml.locationName}: already at the maximum level." : $"{ml.locationName}: not enough supplies, fuel or engineering to upgrade.");
                return;
            }
            cm.ApplyUpgradeLocation(ml, baseType, player: OF == 0);
            Plugin.Log.LogInfo($"MP order: {Who} upgraded {(baseType == 0 ? "port" : "airfield")} at {ml.locationName}");
            NoticeWithSound($"{ml.locationName}: {(baseType == 0 ? "port" : "airfield")} upgraded to level {(baseType == 0 ? ml.portLevel : ml.airfieldLevel)}.",
                            baseType == 0 ? "UpgradePort" : "UpgradeAirfield");
        }

        /// <summary>Faction-1 version of CampaignInterface.DisbandTaskForce (release ships at a friendly port; they go into repair).</summary>
        private static void HostDisband(int id, List<int> indexes)
        {
            CampaignManager cm = CampaignManager.instance;
            CampaignData cd = cm.campaignData;
            GameObjectAnimator goa = HostOwn(id, 0);
            if (goa == null) return;
            MobileMapObject mmo = goa.mobileMapObject;
            MapLocation port = cm.GetClosestLocation(goa, false);
            bool nearPort = port != null && cm.GetDistanceInMetresBetweenMapObjects(port.GetComponent<GameObjectAnimator>().currentMap2DLocation, goa.currentMap2DLocation) <= cd.interactionDistanceMetres * 2f;
            if (!nearPort || port.currentFaction != OF || (port.portLevel < cd.portLevelToDisband && !cm.IsHomePort(port, OF)))
            {
                Notice($"Release not possible: {mmo.mobileName} must be at one of your ports (port level {cd.portLevelToDisband}+ or a home port).");
                return;
            }
            indexes = OrderRules.CleanIndexes(indexes, mmo.unitPrefabs.Count);
            int released = 0;
            foreach (int i in indexes)
            {
                string prefab = mmo.unitPrefabs[i];
                // like vanilla: cargo still aboard is unloaded at the port first
                if (i < mmo.cargo.Count && mmo.cargo[i] != "0|0|0|0")
                    CargoInterface.instance.SetCargoChange(mmo, i, CargoInterface.instance.GetCurrentCargoIndex(null, mmo.cargo[i]), port);
                int owned = cm.GetIndexInDoubleList(prefab, mmo.unitInstances[i], OwnedClasses, OwnedInstances);
                if (owned != -1) OwnedRepairDays[owned] = cm.GetNumberOfDaysToRepair(mmo.cellVariables[i], OwnFaction);
                if (!cd.refundPostRepair && EngagementManager.instance.unitSeaDictionary.ContainsKey(prefab))
                    cd.commandPointSpent[OF] -= (int)(EngagementManager.instance.unitSeaDictionary[prefab].cost * cd.releaseRefund);
                RemoveShipAt(mmo, i);
                released++;
            }
            string name = mmo.mobileName;
            if (mmo.unitPrefabs.Count == 0) CampaignInterface.instance.DestroyMapMobileObject(goa);
            else if (CampaignInterface.instance.currentMapObject == goa) CampaignInterface.instance.UpdateMobileForceDisplay(mmo, forceTop: true); // host had it open
            RefreshHostCommandPoints();
            Plugin.Log.LogInfo($"MP order: {Who} released {released} ship(s) from {name} at {port.locationName}");
            NoticeWithSound($"{released} ship(s) released from {name} at {port.locationName}.", "ShipsReleased");
        }

        private static void OnMessage(byte type, byte[] payload)
        {
            if (!CampaignSync.IsHostRunning || !LockRules.IsOrder(type)) return;
            Run(1, 1, type, payload);
        }

        /// <summary>Host: a subordinate's order — refused if it touches a force its commander has selected or locked.</summary>
        private static void OnSubordinateMessage(int index, byte type, byte[] payload)
        {
            if (!CampaignSync.IsHostRunning || !LockRules.IsOrder(type)) return;
            int team = Multiplayer.Session.SubordinateTeam(index);
            if (team < 0) return;
            int blockedId;
            BlockReason why = LockRules.FirstBlocked(TeamLocks.HostTeam(team), LockRules.OrderTargets(type, payload), out blockedId);
            if (why != BlockReason.None)
            {
                GameObjectAnimator g = CampaignSync.HostFind(blockedId);
                string name = g != null && g.mobileMapObject != null ? g.mobileMapObject.mobileName : "that force";
                SendNotice(index, "Order refused: " + TeamLocks.Describe(why, team, name) + ".", null);
                Plugin.Log.LogInfo($"MP order: P{index + 1} {PlayerName(index)} refused ({why}: {name})");
                return;
            }
            Run(index, team, type, payload);
        }

        /// <summary>Host: run one order message for a player (1 = Player 2, 2+ = subordinate) and faction.</summary>
        private static void Run(int from, int faction, byte type, byte[] payload)
        {
            sender = from;
            OF = faction;
            try
            {
                using (var r = Protocol.Reader(payload))
                {
                    if (type == MsgCreateTaskForce) HostCreateTaskForce(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadString());
                    else if (type == MsgSetCourse) HostSetCourse(r.ReadInt32(), new Vector2(r.ReadSingle(), r.ReadSingle()), r.ReadBoolean());
                    else if (type == MsgUpgrade) HostUpgrade(r.ReadString(), r.ReadInt32());
                    else if (type == MsgDisband)
                    {
                        int id = r.ReadInt32();
                        int n = r.ReadInt32();
                        if (n < 0 || n > 64) return;
                        var idx = new List<int>();
                        for (int i = 0; i < n; i++) idx.Add(r.ReadInt32());
                        HostDisband(id, idx);
                    }
                    else if (type == MsgLaunchAir) HostLaunchAir(r);
                    else if (type == MsgAirOrder) HostAirOrder(r);
                    else if (type == MsgFleetOrder) HostFleetOrder(r);
                    else if (type == MsgCargoOrder) HostCargoOrder(r);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("MP order " + type + " from " + Who + " failed: " + e);
                Notice("Your order could not be carried out (error logged on the host).");
            }
            finally
            {
                sender = 1;
                OF = 1;
            }
        }

        /// <summary>A notice for whoever gave the order being run (Player 2 outside an order).</summary>
        private static void Notice(string msg) => SendNotice(sender, msg, null);

        /// <summary>
        /// A notice plus the vanilla campaign message key of the same event (e.g. "NewSea", "ShipsReleased"): the receiving
        /// copy plays that message's sound exactly as vanilla does for the player (the Click2 "snap" for successes).
        /// A subordinate's success is also told to its team's commander.
        /// </summary>
        internal static void NoticeWithSound(string msg, string dictRef)
        {
            SendNotice(sender, msg, dictRef ?? "");
            if (sender < 2) return;
            string line = $"{PlayerName(sender)} (Player {sender + 1}): {msg}";
            if (OF == 0) Toasts.Show(line);
            else Multiplayer.Session.SendGame(27, Protocol.Build(w => w.Write(line)));
        }

        private static void SendNotice(int to, string msg, string dictRef)
        {
            byte[] p = Protocol.Build(w => { w.Write(msg); if (dictRef != null) w.Write(dictRef); });
            if (to <= 1) Multiplayer.Session.SendGame(27, p);
            else Multiplayer.Session.SendGameToPlayer(to, 27, p);
        }

        // ---------- shared helpers ----------

        private static void Send(byte type, Action<System.IO.BinaryWriter> write) => Multiplayer.Session.SendGame(type, Protocol.Build(write));

        /// <summary>Hidden MobileMapObject used as the data source for CreateNewMobileObject on the host.</summary>
        private static void EnsureTemplate()
        {
            if (template != null) return;
            GameObject go = new GameObject("WotSMP_RemoteTaskForceTemplate");
            go.SetActive(false);
            template = go.AddComponent<MobileMapObject>();
        }

        /// <summary>Host: one of the ordering side's mobiles by mpId (faction OF; type 0 sea / 1 air / -1 any), or null.</summary>
        private static GameObjectAnimator HostOwn(int id, int type)
        {
            GameObjectAnimator goa = CampaignSync.HostFind(id);
            if (goa == null || goa.mobileMapObject == null || goa.mobileMapObject.currentFaction != OF) return null;
            if (type >= 0 && goa.mobileMapObject.type != type) return null;
            return goa;
        }

        /// <summary>
        /// Client: mpId of the selected mobile if it is one of this player's own (type -1 = any), else -1; BlockedId (−2) if
        /// this is a subordinate and the commander has it selected or locked (a toast says so; callers then block vanilla too).
        /// </summary>
        private static int ClientOwnSelected(int type)
        {
            GameObjectAnimator goa = CampaignInterface.instance.currentMapObject;
            if (goa == null || goa.mobileMapObject == null || goa.mobileMapObject.currentFaction != 0) return -1;
            if (type >= 0 && goa.mobileMapObject.type != type) return -1;
            int id = CampaignSync.ClientIdOf(goa);
            return id >= 0 && TeamLocks.ClientRefuses(id, goa.mobileMapObject.mobileName) ? BlockedId : id;
        }

        private const int BlockedId = -2;

        /// <summary>Client: highlighted (and visible) ship rows of the task-force panel.</summary>
        private static List<int> PickedShips()
        {
            CampaignInterface ci = CampaignInterface.instance;
            var picked = new List<int>();
            for (int i = 0; i < ci.shipRosters.Count; i++)
                if (ci.shipRosters[i].gameObject.activeSelf && ci.shipRosters[i].highlightToggle.isOn) picked.Add(i);
            return picked;
        }

        private static void RemoveShipAt(MobileMapObject mmo, int i)
        {
            if (removeAtIndex == null) removeAtIndex = AccessTools.Method(typeof(CampaignInterface), "RemoveMapMobileDataAtIndex");
            removeAtIndex.Invoke(CampaignInterface.instance, new object[] { mmo, i });
        }

        /// <summary>Host: the closest base if it is within <paramref name="factor"/> × the interaction distance, else null.</summary>
        private static MapLocation NearLocation(GameObjectAnimator goa, float factor)
        {
            CampaignManager cm = CampaignManager.instance;
            MapLocation ml = cm.GetClosestLocation(goa, false);
            if (ml == null) return null;
            float d = cm.GetDistanceInMetresBetweenMapObjects(ml.gameObjectAnimator.currentMap2DLocation, goa.currentMap2DLocation);
            return d <= cm.campaignData.interactionDistanceMetres * factor ? ml : null;
        }

        /// <summary>Host view of one of Player 2's mobiles: drawn like any enemy (hidden until spotted).</summary>
        private static void ShowAsEnemy(GameObjectAnimator goa)
        {
            if (goa == null || goa.mobileMapObject == null || goa.mobileMapObject.currentFaction != 1) return; // the host's own: vanilla colours
            SpriteRenderer sr = goa.GetComponentInChildren<SpriteRenderer>();
            if (goa.mobileMapObject.spottedTimer > 0f) CampaignInterface.instance.SetContactColor(goa.mobileMapObject);
            else if (sr != null) sr.color = CampaignInterface.instance.noContactColor;
        }

        private static void RefreshSprite(MobileMapObject mmo)
        {
            SpriteRenderer sr = mmo.GetComponentInChildren<SpriteRenderer>();
            if (sr != null) sr.sprite = CampaignInterface.instance.mobileMapSprites[CampaignManager.instance.GetMobileSpriteIndex(mmo)];
        }

        private static float lastBlockedToast = -10f;

        /// <summary>Client: an order Player 2 can't give yet. Blocks the local action (it would only drift out of sync).</summary>
        internal static void NotYet(string what)
        {
            if (Time.realtimeSinceStartup - lastBlockedToast < 3f) return;
            lastBlockedToast = Time.realtimeSinceStartup;
            Toasts.Show($"{what} is not available to remote players yet.");
        }

        private static void HostCreateTaskForce(string locationId, string json, int cost, string name)
        {
            CampaignManager cm = CampaignManager.instance;
            CampaignInterface ci = CampaignInterface.instance;
            CampaignData cd = cm.campaignData;
            MapLocation port = cm.landMapLocations.Find(ml => ml.locationID == locationId);
            if (port == null || port.currentFaction != OF) { Notice("Task force not formed: you no longer hold that port."); return; }
            int available = cd.commandPoints[OF] + cd.commandBonusPoints[OF] - cd.commandPointSpent[OF];
            if (cost > available) { Notice($"Task force not formed: it costs {cost} CP, you have {available}."); return; }

            EnsureTemplate();
            ci.ClearNewGroupData(template);
            JsonUtility.FromJsonOverwrite(json, template);
            template.currentFaction = OF;
            template.isMoving = false;

            // Same ship checks/bookkeeping as CampaignInterface.AddShipToTaskForce does for the player:
            // each named ship must be free (not already at sea, sunk, unavailable or under repair) and is then registered
            // as owned, so it cannot be used twice.
            for (int i = 0; i < template.unitPrefabs.Count; i++)
            {
                string prefab = template.unitPrefabs[i];
                int inst = i < template.unitInstances.Count ? template.unitInstances[i] : 0;
                int state = OF == 0 ? cm.IsPlayerShipInstanceAvailable(prefab, inst) : cm.IsEnemyShipInstanceAvailable(prefab, inst);
                if (state != -1)
                {
                    string why = state == 0 ? "already in use" : state == 2 ? "sunk" : state == 3 ? "under repair" : "not available";
                    Notice($"Task force not formed: {Utilities.GetShipDisplayName(prefab, string.Empty, inst)} is {why}.");
                    return;
                }
            }
            for (int i = 0; i < template.unitPrefabs.Count; i++)
                cm.ModifyShipInLists(true, template.unitPrefabs[i], i < template.unitInstances.Count ? template.unitInstances[i] : 0, 0,
                    OwnedClasses, OwnedInstances, OwnedRepairDays);

            cd.commandPointSpent[OF] += cost;
            GameObjectAnimator selected = ci.currentMapObject;
            GameObjectAnimator goa = ci.CreateNewMobileObject(CampaignObjectType.Sea, template, port.gameObjectAnimator.currentMap2DLocation, isLoading: false, null, string.Empty);
            ci.currentMapObject = selected;
            goa.mobileMapObject.mobileName = string.IsNullOrEmpty(name) ? "Task Force" : name;
            // Host-side fog of war: an unspotted enemy is drawn with the "no contact" colour, like vanilla AI forces.
            ShowAsEnemy(goa);
            RefreshHostCommandPoints();
            Plugin.Log.LogInfo($"MP order: {Who} formed task force '{goa.mobileMapObject.mobileName}' at {port.locationName} ({goa.mobileMapObject.unitPrefabs.Count} ships, {cost} CP)");
            NoticeWithSound($"Task force \"{goa.mobileMapObject.mobileName}\" formed at {port.locationName} ({cost} CP).", "NewSea");
        }

        private static void HostSetCourse(int id, Vector2 dest, bool append)
        {
            GameObjectAnimator goa = HostOwn(id, -1);
            if (goa == null) return;
            MobileMapObject mmo = goa.mobileMapObject;
            if (!append)
            {
                // same reset as CampaignInterface.PlotCourse at the start of plotting
                mmo.autoMove = false;
                mmo.autoPath.Clear();
                mmo.escortingThisGOA = null;
                mmo.escortingGOAIndex = -1;
                mmo.mapWaypoints.Clear();
                if (goa.isMoving) goa.Stop();
                goa.isMoving = false;
            }
            CampaignInterface.instance.MoveMobileToDestination(goa, dest);
            Plugin.Log.LogInfo($"MP order: {Who} {(append ? "added a course leg for" : "plotted a course for")} '{mmo.mobileName}'");
        }
    }

    // ---------- patches ----------

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.PlotCourse))]
    internal static class ClientPlotCoursePatch
    {
        private static void Postfix()
        {
            if (CampaignSync.IsClientPuppet) RemoteOrders.OnPlotCourseToggled();
        }
    }

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.MoveMobileToDestination))]
    internal static class ClientCourseClickPatch
    {
        private static bool Prefix(GameObjectAnimator obj, Vector2 destination)
        {
            if (!CampaignSync.IsClientPuppet) return true;
            CampaignInterface ci = CampaignInterface.instance;
            if (ci.enablePlotCourse && obj == ci.currentMapObject) return RemoteOrders.OnClientCourseClick(obj, destination);
            return true;
        }
    }

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.PlayerDoneNewTaskForce))]
    internal static class ClientNewTaskForcePatch
    {
        private static bool Prefix()
        {
            if (!CampaignSync.IsClientPuppet) return true;
            if (CampaignInterface.instance.shipRosters[0].gameObject.activeSelf) RemoteOrders.SendCreateTaskForce();
            return false;
        }
    }

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.UpgradeLocation))]
    internal static class ClientUpgradePatch
    {
        private static bool Prefix(int baseType)
        {
            if (!CampaignSync.IsClientPuppet) return true;
            RemoteOrders.SendUpgrade(baseType);
            return false;
        }
    }

    /// <summary>Only the final call (confirm == false) is an order; the first click just shows the confirm button.</summary>
    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.DisbandTaskForce))]
    internal static class ClientDisbandPatch
    {
        private static bool Prefix(bool confirm)
        {
            if (!CampaignSync.IsClientPuppet || confirm) return true;
            return !RemoteOrders.SendDisband();
        }
    }

    /// <summary>
    /// Host: when a faction-1 force finishes a leg, vanilla hands it to the AI (sea: mission navigation via
    /// CampaignManager.GetNextNavPosition; air: nothing, it just loiters). In MP faction 1 is a human, so it follows its
    /// plotted waypoints like the player. Sea: run the player branch by flipping the faction for this call (it only
    /// touches mapWaypoints). Air: the player branch looks things up by faction (base, carrier), so it is redone for
    /// faction 1 in RemoteOrders.Faction1AirLegDone. Returning aircraft and escorts keep the vanilla code.
    /// </summary>
    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.CheckNextWaypoint))]
    internal static class HostFaction1WaypointsPatch
    {
        private static bool Prefix(GameObjectAnimator obj, ref bool __state)
        {
            __state = false;
            if (!ModState.MultiplayerActive || !Multiplayer.Session.IsHost || obj == null || obj.mobileMapObject == null) return true;
            MobileMapObject mmo = obj.mobileMapObject;
            if (mmo.currentFaction != 1) return true;
            if (mmo.type == 0)
            {
                mmo.currentFaction = 0; // run the player branch (mapWaypoints) for this call
                __state = true;
                return true;
            }
            if (mmo.type == 1 && mmo.currentCampaignAction != 1 && mmo.escortingThisGOA == null)
            {
                RemoteOrders.Faction1AirLegDone(obj);
                return false;
            }
            return true;
        }

        private static void Finalizer(GameObjectAnimator obj, bool __state) // runs even if the original throws
        {
            if (__state && obj != null && obj.mobileMapObject != null) obj.mobileMapObject.currentFaction = 1;
        }
    }

    /// <summary>
    /// Host, MP: every 5 game minutes vanilla gives each idle Enemy sea force its next AI mission waypoint
    /// (CampaignManager.GetNextNavPosition; also after patrols and on load). In MP those are Player 2's fleets, which then
    /// sailed off on their own after reaching the end of their course (seen in the M6c test). A Player 2 fleet gets the
    /// player's rule instead (CampaignManager's playerMobile loop): resume its own plotted waypoints, otherwise wait for orders.
    /// </summary>
    [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.GetNextNavPosition))]
    internal static class HostFaction1NoMissionNavPatch
    {
        private static bool Prefix(GameObjectAnimator obj)
        {
            if (!ModState.MultiplayerActive || obj == null || obj.mobileMapObject == null || obj.mobileMapObject.currentFaction == 0) return true;
            MobileMapObject mmo = obj.mobileMapObject;
            if (!obj.isMoving && mmo.mapWaypoints != null && mmo.mapWaypoints.Count > 0)
            {
                Vector2 next = mmo.mapWaypoints[0];
                obj.MoveTo(next, CampaignInterface.instance.GetMoveDuration(obj.currentMap2DLocation, next, mmo.speed), DURATION_TYPE.Route);
            }
            return false;
        }
    }
}
