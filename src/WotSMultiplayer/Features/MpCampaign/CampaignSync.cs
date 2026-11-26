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
    /// M4a: host-authoritative campaign view sync (PLAN.md §2–3).
    /// Host: every SnapshotInterval sends the side-swapped campaign state (with stable mobile IDs), gzipped.
    /// Client: runs as a puppet (own campaign sim off) and applies each snapshot in place: campaign data, bases,
    /// task forces/air groups (created/updated/teleported/destroyed by ID), clock, weather, spotted contacts.
    /// Client orders (M4b) are not sent yet: anything the client changes locally is overwritten by the next snapshot.
    /// </summary>
    internal static class CampaignSync
    {
        private const byte MsgSnapshot = 26;
        internal static float SnapshotInterval = 1f;

        // host
        private static readonly Dictionary<GameObjectAnimator, int> hostIds = new Dictionary<GameObjectAnimator, int>();
        private static int nextId = 1;
        private static float lastSnapshot;
        private static string mirrorSetupCache, mirrorSetupFor;
        internal static int SnapshotsSent, LastSnapshotBytes;

        // client
        private static readonly Dictionary<int, GameObjectAnimator> clientMobiles = new Dictionary<int, GameObjectAnimator>();
        private static bool firstSnapshotApplied;
        internal static int SnapshotsApplied;
        private static AccessTools.FieldRef<GameObjectAnimator, Vector2> currentLocRef;

        // client: smooth movement between snapshots. Each snapshot starts a glide from where the icon is now to the host's
        // position, lasting one (measured) snapshot interval, so icons move continuously ~1 interval behind the host instead
        // of jumping once a second (at 1800x a destroyer covers ~28 km per snapshot).
        private sealed class Glide
        {
            public Vector2 From, To;
            public float Start, Duration;
        }
        private static readonly Dictionary<GameObjectAnimator, Glide> glides = new Dictionary<GameObjectAnimator, Glide>();
        private static float lastSnapshotAt = -1f, snapshotGap = 1f;
        private const float SnapDistance = 0.15f; // map units (~450 km): farther = a real jump (new, captured, recreated)

        internal static bool IsHostRunning => ModState.MultiplayerActive && Multiplayer.Session.IsHost && MpCampaign.Phase == MpPhase.Running;
        internal static bool IsClientPuppet => ModState.MultiplayerActive && !Multiplayer.Session.IsHost;

        internal static void Init()
        {
            Multiplayer.Session.OnGameMessage += (type, payload) => { if (type == MsgSnapshot && IsClientPuppet) OnSnapshot(payload); };
        }

        /// <summary>Called when a new MP campaign starts on either side.</summary>
        internal static void Reset()
        {
            hostIds.Clear();
            nextId = 1;
            clientMobiles.Clear();
            glides.Clear();
            lastSnapshotAt = -1f;
            firstSnapshotApplied = false;
            SnapshotsSent = SnapshotsApplied = 0;
            TeamLocks.Reset(); // locks name mobiles by mpId, which start again here
        }

        // ---------- host ----------

        internal static void Update()
        {
            if (IsClientPuppet)
            {
                try { ClientGlide(); }
                catch (Exception e) { Plugin.Log.LogError("MP map smoothing failed: " + e.Message); glides.Clear(); }
                return;
            }
            if (!IsHostRunning || !Multiplayer.Session.IsConnected) return;
            if (TimeControl.TimeControlDriver.InCampaignBattle) return; // campaign view is not live during a host battle
            if (Time.realtimeSinceStartup - lastSnapshot < SnapshotInterval) return;
            lastSnapshot = Time.realtimeSinceStartup;
            try { SendSnapshot(); }
            catch (Exception e) { Plugin.Log.LogError("MP snapshot failed: " + e); }
        }

        /// <summary>Host: mobile by mpId (null if gone).</summary>
        internal static GameObjectAnimator HostFind(int id)
        {
            foreach (var kv in hostIds) if (kv.Value == id && kv.Key != null) return kv.Key;
            return null;
        }

        /// <summary>Host: the mpId of a mobile (assigned on first use, same as in snapshots).</summary>
        internal static int HostIdOf(GameObjectAnimator goa) => IdOf(goa);

        /// <summary>Client: a mobile (own or enemy) by mpId, or null.</summary>
        internal static GameObjectAnimator ClientFind(int id)
        {
            GameObjectAnimator goa;
            return clientMobiles.TryGetValue(id, out goa) && goa != null ? goa : null;
        }

        /// <summary>Client: mpId of one of its mobiles (-1 if unknown).</summary>
        internal static int ClientIdOf(GameObjectAnimator goa)
        {
            foreach (var kv in clientMobiles) if (kv.Value == goa) return kv.Key;
            return -1;
        }

        private static int IdOf(GameObjectAnimator goa)
        {
            int id;
            if (!hostIds.TryGetValue(goa, out id)) { id = nextId++; hostIds[goa] = id; }
            return id;
        }

        private static void SendSnapshot()
        {
            CampaignManager cm = CampaignManager.instance;
            if (cm == null || cm.campaignData == null) return;
            Theatres t = Theatres.Find(MpCampaign.Settings.Theatre);
            string clientCampaign = t.CampaignBySide[MpCampaign.Settings.ClientSide];
            if (mirrorSetupFor != clientCampaign)
            {
                mirrorSetupCache = AssetLoader.instance.ImportText("campaign/" + clientCampaign + "/setup.txt")[0];
                mirrorSetupFor = clientCampaign;
            }

            var playerIds = new List<int>();
            foreach (GameObjectAnimator g in cm.playerMobile) playerIds.Add(IdOf(g));
            var enemyIds = new List<int>();
            foreach (GameObjectAnimator g in cm.otherMobile) enemyIds.Add(IdOf(g));

            string json = cm.GetCampaignDataString();
            json = SnapshotCodec.InjectIds(json, playerIds, enemyIds);
            string mirrored = SideSwap.Swap(json, clientCampaign, mirrorSetupCache);
            byte[] packed = SnapshotCodec.Pack(mirrored);
            Multiplayer.Session.SendGame(MsgSnapshot, packed);
            // additional players (stage 2): Player 2's team sees the same mirrored map; the host's team the host's own
            // (unmirrored: "own side = faction 0" already holds, fog of war = the host's spotting)
            Multiplayer.Session.SendGameToSubordinates(MsgSnapshot, packed, team: 1);
            if (Multiplayer.Session.HasSubordinates(0)) Multiplayer.Session.SendGameToSubordinates(MsgSnapshot, SnapshotCodec.Pack(json), team: 0);
            SnapshotsSent++;
            LastSnapshotBytes = packed.Length;

            if (SnapshotsSent % 30 == 1)
            {
                // forget destroyed mobiles
                var dead = new List<GameObjectAnimator>();
                foreach (var kv in hostIds) if (kv.Key == null) dead.Add(kv.Key);
                foreach (var d in dead) hostIds.Remove(d);
                Plugin.Log.LogInfo($"MP: snapshot #{SnapshotsSent}: {mirrored.Length / 1024} KB JSON → {packed.Length / 1024} KB sent " +
                                   $"({(packed[0] == 1 ? "gzip" : "uncompressed")}), {playerIds.Count}+{enemyIds.Count} mobiles");
            }
        }

        // ---------- client ----------

        // P2's order that creates a mobile (new task force / air launch): the next own mobile of that type is selected
        private static int expectNewType = -1;
        private static float expectNewUntil;
        private static GameObjectAnimator applySelected; // the selection while a snapshot is applied (creation changes it)

        /// <summary>Client: an order was sent that will create an own mobile of this type (0 sea, 1 air).</summary>
        internal static void ExpectNewOwnMobile(int type)
        {
            expectNewType = type;
            expectNewUntil = Time.realtimeSinceStartup + 20f;
        }

        private static void OnSnapshot(byte[] payload)
        {
            if (MpCampaign.Phase != MpPhase.Running) return;
            if (CampaignManager.instance == null || CampaignInterface.instance == null || !LoadScreen.loadComplete) return;
            try
            {
                Apply(SnapshotCodec.Unpack(payload));
                SnapshotsApplied++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("MP snapshot apply failed: " + e);
            }
        }

        private static void Apply(string json)
        {
            CampaignManager cm = CampaignManager.instance;
            CampaignInterface ci = CampaignInterface.instance;
            JObject root = MiniJson.ParseObject(json);

            float now = Time.realtimeSinceStartup;
            if (lastSnapshotAt >= 0f) snapshotGap = Mathf.Lerp(snapshotGap, Mathf.Clamp(now - lastSnapshotAt, 0.2f, 3f), 0.3f);
            lastSnapshotAt = now;

            bool firstSnapshotDone = firstSnapshotApplied;
            GameObjectAnimator newlyCreatedOwn = null;
            if (!firstSnapshotApplied)
            {
                // The mobiles created by the initial load carry no IDs: replace them all once.
                foreach (GameObjectAnimator g in new List<GameObjectAnimator>(cm.playerMobile)) DestroyMobile(g);
                foreach (GameObjectAnimator g in new List<GameObjectAnimator>(cm.otherMobile)) DestroyMobile(g);
                clientMobiles.Clear();
                firstSnapshotApplied = true;
            }

            // 1. Task forces / air groups
            var seen = new HashSet<int>();
            GameObjectAnimator selected = ci.currentMapObject;
            applySelected = selected;
            bool refreshPanel = false;
            for (int list = 0; list < 2; list++)
            {
                int faction = list == 0 ? 0 : 1;
                JArray arr = root.Get(SnapshotCodec.MobileLists[list]) as JArray;
                if (arr == null) continue;
                foreach (JNode n in arr.Items)
                {
                    JObject m = MiniJson.ParseObject(((JString)n).Value);
                    int id = SnapshotCodec.GetId(m);
                    if (id < 0) continue;
                    SnapshotCodec.StripObjectRefs(m);
                    string mj = MiniJson.Write(m);
                    seen.Add(id);

                    GameObjectAnimator goa;
                    bool exists = clientMobiles.TryGetValue(id, out goa) && goa != null && goa.mobileMapObject != null;
                    if (exists && (goa.mobileMapObject.currentFaction == 0) != (faction == 0))
                    {
                        // changed sides (captured?) → recreate in the right list
                        DestroyMobile(goa);
                        exists = false;
                    }
                    if (!exists)
                    {
                        goa = CreateMobile(mj); clientMobiles[id] = goa;
                        if (faction == 0 && firstSnapshotDone && newlyCreatedOwn == null && expectNewType >= 0 &&
                            Time.realtimeSinceStartup < expectNewUntil && goa.mobileMapObject != null && goa.mobileMapObject.type == expectNewType)
                            newlyCreatedOwn = goa;
                    }
                    else if (!(ci.enablePlotCourse && goa == selected)) // don't fight the player while plotting
                    {
                        string before = goa == selected ? PanelSignature(goa.mobileMapObject) : null;
                        UpdateMobile(goa, mj, glide: true);
                        if (before != null && before != PanelSignature(goa.mobileMapObject)) refreshPanel = true;
                    }
                }
            }
            var gone = new List<int>();
            foreach (var kv in clientMobiles) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (int id in gone)
            {
                GameObjectAnimator g = clientMobiles[id];
                clientMobiles.Remove(id);
                if (g != null) DestroyMobile(g);
            }
            // Creating a mobile changes the selection (vanilla sets currentMapObject to it): restore it, also when the
            // selection is a base (no mobileMapObject) — otherwise "New Sea" at that base loses its port.
            ci.currentMapObject = selected != null ? selected : null; // (a destroyed selection becomes a real null)
            if (newlyCreatedOwn != null && !ci.enablePlotCourse && !ci.IsCreatingNewMobileGroup())
            {
                // P2 just formed a task force / launched planes: select it, as vanilla does on the player's PC
                expectNewType = -1;
                ci.SelectMapObject(newlyCreatedOwn);
                selected = ci.currentMapObject;
            }
            applySelected = null;
            if (selected != null && selected.mobileMapObject != null)
            {
                // The open task-force/air panel only redraws when asked: do it when an order changed what it shows
                // (ships, names, cargo). Not every snapshot: a redraw clears the ship highlights.
                if (refreshPanel && ci.mobileMapText.transform.parent.gameObject.activeSelf && !ci.isSelectingAircraftType && !ci.IsCreatingNewMobileGroup())
                    ci.UpdateMobileForceDisplay(selected.mobileMapObject);
                // Own air group: the range left (endurance) only redraws in vanilla Check5Min, which never runs on P2's copy,
                // so it stood still until P2 re-selected the group (user report, 2026-11-25). Same call, every snapshot.
                MobileMapObject sm = selected.mobileMapObject;
                if (sm.type == 1 && sm.currentFaction == 0 && sm.unitPrefabs != null && sm.unitPrefabs.Count > 0 &&
                    ci.airGroupDataText != null && ci.airGroupDataText.gameObject.activeInHierarchy)
                {
                    try { cm.RefreshAirData(sm); }
                    catch (Exception e) { Plugin.Log.LogWarning("MP client: air group range refresh failed: " + e.Message); }
                }
            }

            // 2. Bases
            JArray locs = root.Get("mapLocationSaveData") as JArray;
            if (locs != null)
            {
                var byId = new Dictionary<string, MapLocation>();
                foreach (MapLocation ml in cm.landMapLocations) byId[ml.locationID] = ml;
                foreach (JNode n in locs.Items)
                {
                    JObject l = MiniJson.ParseObject(((JString)n).Value);
                    JString lid = l.Get("locationID") as JString;
                    MapLocation ml;
                    if (lid == null || !byId.TryGetValue(lid.Value, out ml)) continue;
                    SnapshotCodec.StripObjectRefs(l);
                    JsonUtility.FromJsonOverwrite(MiniJson.Write(l), ml);
                    SpriteRenderer sr = ml.GetComponentInChildren<SpriteRenderer>();
                    if (sr != null && ml.currentFaction >= 0 && ml.currentFaction < ci.factionColours.Length) sr.color = ci.factionColours[ml.currentFaction];
                    ci.SetFireToLocation(ml, ml.troops[0] > 0 && ml.troops[1] > 0);
                }
            }

            // 3. Weather (every 10th snapshot)
            JArray weather = root.Get("weatherCellData") as JArray;
            if (weather != null && SnapshotsApplied % 10 == 0)
            {
                for (int i = 0; i < weather.Items.Count && i < WeatherManager.instance.weatherCells.Count; i++)
                    JsonUtility.FromJsonOverwrite(((JString)weather.Items[i]).Value, WeatherManager.instance.weatherCells[i]);
                WeatherManager.instance.SetWeatherIcons(display: true);
            }

            // 4. Campaign data (economy, dates, ownership lists...) — without the bulky save lists and the neutral presets
            foreach (string k in new[] { "mapLocationSaveData", "playerMobileObjectSaveData", "enemyMobileObjectSaveData",
                                         "neutralMobileObjectSaveData", "mapMarkersSaveData", "weatherCellData", "playerAirScoutData",
                                         "campDifficultyPreset0", "campDifficultyPreset1", "campDifficultyPreset2",
                                         "campDifficultyPreset3", "campDifficultyPreset4",
                                         // the encounter panel state is per side (host indexes don't fit the client's lists): MpEncounters
                                         "playerObjectEngaged", "otherObjectEngaged", "canIgnore", "canAutoResolve" })
                Remove(root, k);
            SnapshotCodec.StripObjectRefs(root);
            JsonUtility.FromJsonOverwrite(MiniJson.Write(root), cm.campaignData);
            List<int> dt = cm.campaignData.currentDateTime;
            if (dt != null && dt.Count >= 6) ci.dateTime = new DateTime(dt[5], dt[4], dt[3], dt[2], dt[1], dt[0]);
            ci.SetDateTimeText();
            ci.SetCommandTotal();

            if (SnapshotsApplied % 30 == 0)
            {
                int spotted = 0;
                foreach (GameObjectAnimator g in cm.otherMobile) if (g != null && g.mobileMapObject != null && g.mobileMapObject.spottedTimer > 0f) spotted++;
                string where = "";
                foreach (GameObjectAnimator g in cm.otherMobile) if (g != null) where += $" [{g.mobileMapObject.mobileName} @ {g.currentMap2DLocation}]";
                Plugin.Log.LogInfo($"MP client: snapshot #{SnapshotsApplied}, {ci.dateTime:dd MMM HH:mm}, own mobiles {cm.playerMobile.Count}, " +
                                   $"enemy mobiles {cm.otherMobile.Count} ({spotted} spotted){where}");
            }
        }

        /// <summary>What the mobile panel displays that orders can change (not formation: that is edited live in the panel).</summary>
        private static string PanelSignature(MobileMapObject m)
        {
            if (m == null) return "";
            return m.mobileName + "#" + string.Join("|", m.unitDisplayNames.ToArray()) + "#" + string.Join("|", m.cargo.ToArray())
                   + "#" + m.unitPrefabs.Count + "#" + m.isCAP;
        }

        private static GameObjectAnimator CreateMobile(string json)
        {
            CampaignInterface ci = CampaignInterface.instance;
            CampaignManager cm = CampaignManager.instance;
            ci.ClearNewGroupData(ci.newTaskForce);
            JsonUtility.FromJsonOverwrite(json, ci.newTaskForce);
            ci.newTaskForce.isMoving = false; // a "moving" mobile would start AI navigation on creation
            var type = (CampaignObjectType)Enum.Parse(typeof(CampaignObjectType), ci.newTaskForce.type.ToString());
            GameObjectAnimator goa = ci.CreateNewMobileObject(type, ci.newTaskForce, ci.newTaskForce.currentMap2DLocation, isLoading: true, null, string.Empty);
            UpdateMobile(goa, json);
            return goa;
        }

        private static void UpdateMobile(GameObjectAnimator goa, string json, bool glide = false)
        {
            CampaignManager cm = CampaignManager.instance;
            CampaignInterface ci = CampaignInterface.instance;
            MobileMapObject mmo = goa.mobileMapObject;
            JsonUtility.FromJsonOverwrite(json, mmo);
            mmo.gameObjectAnimator = goa;
            mmo.type = cm.SetMMOType(mmo);
            Vector2 target = mmo.currentMap2DLocation;
            Vector2 shown = goa.currentMap2DLocation;
            if (glide && (target - shown).magnitude <= SnapDistance)
            {
                if (goa.isMoving) goa.Stop(); // WMSK's own movement stays off (map time is frozen here); this also disarms it
                glides[goa] = new Glide { From = shown, To = target, Start = Time.realtimeSinceStartup, Duration = Mathf.Max(0.2f, snapshotGap) };
            }
            else
            {
                glides.Remove(goa);
                Teleport(goa, target);
            }
            // The selected fleet's yellow course line: vanilla CampaignInterface.Update redraws it every frame while the
            // fleet "isMoving", from its position through the current leg's end and the remaining waypoints. Stop() above
            // also cleared WMSK's own move switch, so these flags only feed the line (WMSK doesn't move the icon itself).
            goa.isMoving = mmo.isMoving;
            if (mmo.isMoving) goa.endingMap2DLocation = mmo.endingMap2DLocation;

            SpriteRenderer sr = goa.GetComponentInChildren<SpriteRenderer>();
            if (mmo.currentFaction == 0)
            {
                // own mobile: the selected one stays highlighted yellow like vanilla (ApplyHighlightColor → factionColours[3])
                GameObjectAnimator sel = applySelected != null ? applySelected : ci.currentMapObject;
                if (sr != null) sr.color = ci.factionColours[goa == sel && ci.factionColours.Length > 3 ? 3 : 0];
            }
            else
            {
                // Fog of war: the host computes what this side has spotted (spottedTimer on the mirrored enemy mobiles).
                // Debug.RevealAllMobiles (testing only) shows every enemy mobile so the sync itself can be watched.
                if (mmo.spottedTimer > 0f)
                {
                    if (!cm.spottedContacts.Contains(mmo)) cm.spottedContacts.Add(mmo);
                    ci.SetContactColor(mmo);
                }
                else if (Plugin.DebugRevealAll.Value)
                {
                    // SetContactColor fades with time since spotted (invisible at 0), so use the plain enemy colour.
                    if (sr != null) sr.color = ci.factionColours[1];
                }
                else
                {
                    cm.spottedContacts.Remove(mmo);
                    if (sr != null) sr.color = ci.noContactColor;
                }
            }
        }

        /// <summary>Client, every frame: move each gliding icon along its glide (see Glide).</summary>
        private static void ClientGlide()
        {
            if (glides.Count == 0) return;
            if (currentLocRef == null) currentLocRef = AccessTools.FieldRefAccess<GameObjectAnimator, Vector2>("_currentMap2DLocation");
            float now = Time.realtimeSinceStartup;
            List<GameObjectAnimator> done = null;
            foreach (var kv in glides)
            {
                GameObjectAnimator goa = kv.Key;
                Glide g = kv.Value;
                if (goa == null || goa.mobileMapObject == null) { (done ?? (done = new List<GameObjectAnimator>())).Add(goa); continue; }
                float t = Mathf.Clamp01((now - g.Start) / g.Duration);
                currentLocRef(goa) = Vector2.Lerp(g.From, g.To, t);
                goa.UpdateTransformAndVisibility();
                if (t >= 1f) (done ?? (done = new List<GameObjectAnimator>())).Add(goa);
            }
            if (done != null) foreach (GameObjectAnimator goa in done) glides.Remove(goa);
        }

        private static void Teleport(GameObjectAnimator goa, Vector2 pos)
        {
            if (currentLocRef == null) currentLocRef = AccessTools.FieldRefAccess<GameObjectAnimator, Vector2>("_currentMap2DLocation");
            if (goa.isMoving) goa.Stop();
            currentLocRef(goa) = pos;
            goa.UpdateTransformAndVisibility();
        }

        private static void DestroyMobile(GameObjectAnimator goa)
        {
            glides.Remove(goa);
            try { CampaignInterface.instance.DestroyMapMobileObject(goa); }
            catch (Exception e) { Plugin.Log.LogWarning("MP: destroy mobile failed: " + e.Message); }
        }

        private static void Remove(JObject o, string key)
        {
            for (int i = o.Members.Count - 1; i >= 0; i--) if (o.Members[i].Key == key) o.Members.RemoveAt(i);
        }
    }

    /// <summary>Client puppet: its own campaign simulation (timers → encounters, spotting, AI, economy) is off.</summary>
    [HarmonyPatch(typeof(CampaignManager), "FixedUpdate")]
    internal static class ClientSimOffPatch
    {
        private static bool Prefix() => !CampaignSync.IsClientPuppet;
    }
}
