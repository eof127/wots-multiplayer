using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WorldMapStrategyKit;
using WotSMultiplayer.Core;
using WotSMultiplayer.Features.TimeControl;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// Additional players, stage 3 (D57): what a team's subordinates may not order on the campaign map.
    ///  - the force the team's commander has selected (Player 1 for the host's team, Player 2 for Player 2's), automatically;
    ///  - the forces the commander has locked: select one and press K ([Multiplayer] LockKey) or the speed window's button.
    /// The host keeps both teams' state (LockRules.TeamLockState) and refuses a subordinate's order on a blocked force
    /// (RemoteOrders); clients only display it and refuse locally first. Messages:
    ///   55 Select  Player 2 → host {mpId or −1}: Player 2's current own selection
    ///   56 Toggle  Player 2 → host {mpId}: lock / unlock one of Player 2's forces
    ///   57 State   host → the team's players {selected, n, ids}: the team's locks (Player 2 gets team 1's)
    /// </summary>
    internal static partial class TeamLocks
    {
        private const byte MsgSelect = 55, MsgToggle = 56, MsgState = 57;

        private static readonly TeamLockState[] host = { new TeamLockState(), new TeamLockState() };
        private static readonly bool[] dirty = { true, true };
        private static float lastSent, lastPrune, lastRefresh, lastRefusedToast = -10f;
        private static readonly TeamLockState mine = new TeamLockState(); // client: its team's state, from the host
        private static int sentSelection = int.MinValue;
        private static float selectionSentAt;

        private static NetSession Net => Multiplayer.Session;

        internal static TeamLockState HostTeam(int team) => host[team == 0 ? 0 : 1];

        internal static void Init()
        {
            Net.OnGameMessage += OnMessage;
            Net.OnSubordinateJoined += index => dirty[0] = dirty[1] = true;
        }

        /// <summary>A new MP campaign (mpIds start again): no locks.</summary>
        internal static void Reset()
        {
            host[0].Clear();
            host[1].Clear();
            mine.Clear();
            dirty[0] = dirty[1] = true;
            sentSelection = int.MinValue;
        }

        private static string KeyName => Plugin.LockKey.Value.ToString();

        private static bool OnMap => ModState.MultiplayerActive && TimeControlDriver.CampaignMapActive();

        /// <summary>This PC commands a team (the host or Player 2), as opposed to a subordinate.</summary>
        private static bool IsCommander => ModState.MultiplayerActive && !Net.IsSubordinate;

        private static int MyTeam => Net.LocalTeam;

        private static bool TeamHasSubordinates(int team)
        {
            foreach (PlayerInfo p in Net.Players) if (p.Role == PlayerRole.Subordinate && p.Team == team) return true;
            return false;
        }

        /// <summary>This PC's view of its team's locks (the host's own state or the copy the host sent).</summary>
        private static TeamLockState MyState => Net.IsHost ? host[0] : mine;

        // ---------- per frame ----------

        internal static void Update()
        {
            if (!ModState.MultiplayerActive) return;
            if (Net.IsHost) HostUpdate();
            else if (!Net.IsSubordinate) Player2Update();
        }

        private static void HostUpdate()
        {
            if (!CampaignSync.IsHostRunning) return;
            float now = Time.realtimeSinceStartup;
            int sel = OwnSelected(host: true);
            if (sel != host[0].Selected) { host[0].Selected = sel; dirty[0] = true; }
            if (LockKeyPressed()) ToggleSelected();
            if (now - lastPrune > 2f)
            {
                lastPrune = now;
                for (int t = 0; t < 2; t++) { int team = t; if (host[t].Prune(id => Exists(id, team))) dirty[t] = true; }
            }
            if (now - lastRefresh > 5f) { lastRefresh = now; dirty[0] = dirty[1] = true; } // cheap; covers late joiners
            if ((dirty[0] || dirty[1]) && now - lastSent > 0.15f)
            {
                lastSent = now;
                for (int t = 0; t < 2; t++) if (dirty[t]) { dirty[t] = false; Broadcast(t); }
            }
            if (MpBattle.MpBattle.HostActive) HostBattleUpdate(now);
        }

        private static void Player2Update()
        {
            if (!CampaignSync.IsClientPuppet || MpCampaign.Phase != MpPhase.Running) return;
            float now = Time.realtimeSinceStartup;
            int sel = OwnSelected(host: false);
            if (sel != sentSelection && now - selectionSentAt > 0.15f)
            {
                sentSelection = sel;
                selectionSentAt = now;
                Net.SendGame(MsgSelect, Protocol.Build(w => w.Write(sel)));
            }
            if (LockKeyPressed()) ToggleSelected();
            if (MpBattle.MpBattle.ClientActive) Player2BattleUpdate(now);
        }

        /// <summary>mpId of the selected own force (task force or air group), else −1.</summary>
        private static int OwnSelected(bool host)
        {
            CampaignInterface ci = CampaignInterface.instance;
            GameObjectAnimator g = ci != null ? ci.currentMapObject : null;
            if (g == null || g.mobileMapObject == null || g.mobileMapObject.currentFaction != 0) return -1;
            return host ? CampaignSync.HostIdOf(g) : CampaignSync.ClientIdOf(g);
        }

        private static bool Exists(int id, int team)
        {
            GameObjectAnimator g = CampaignSync.HostFind(id);
            return g != null && g.mobileMapObject != null && g.mobileMapObject.currentFaction == team;
        }

        private static void Broadcast(int team)
        {
            byte[] p = Protocol.Build(w => host[team].Write(w));
            if (team == 1 && Net.IsConnected) Net.SendGame(MsgState, p);
            Net.SendGameToSubordinates(MsgState, p, team);
        }

        private static bool LockKeyPressed()
        {
            if (!Plugin.LockKey.Value.IsDown() || !OnMap) return false;
            if (TimeControlDriver.ChatFocused || GUIUtility.keyboardControl != 0) return false; // typing in a mod window
            GameObject sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            InputField field = sel != null ? sel.GetComponent<InputField>() : null;
            return field == null || !field.isFocused; // typing in the game's rename box
        }

        /// <summary>Commander: lock or unlock the selected own force for the team's subordinates.</summary>
        internal static void ToggleSelected()
        {
            if (!IsCommander) return;
            CampaignInterface ci = CampaignInterface.instance;
            GameObjectAnimator g = ci != null ? ci.currentMapObject : null;
            if (g == null || g.mobileMapObject == null || g.mobileMapObject.currentFaction != 0)
            {
                Toasts.Show($"Select one of your task forces or air groups, then press {KeyName} to lock it for your subordinates.");
                return;
            }
            string name = g.mobileMapObject.mobileName;
            bool locked;
            if (Net.IsHost)
            {
                int id = CampaignSync.HostIdOf(g);
                bool was = host[0].Locked.Contains(id);
                locked = host[0].Toggle(id);
                if (!was && !locked) { Toasts.Show($"Too many locked forces ({TeamLockState.MaxLocks})."); return; }
                dirty[0] = true;
            }
            else
            {
                int id = CampaignSync.ClientIdOf(g);
                if (id < 0) return;
                locked = !mine.Locked.Contains(id);
                Net.SendGame(MsgToggle, Protocol.Build(w => w.Write(id)));
            }
            Toasts.Show(locked ? $"{name} locked: your subordinates can't give it orders ({KeyName} again unlocks it)."
                               : $"{name} unlocked: your subordinates may give it orders again.");
            Plugin.Log.LogInfo($"MP locks: {(locked ? "locked" : "unlocked")} '{name}'");
        }

        private static void OnMessage(byte type, byte[] payload)
        {
            if (type < MsgSelect || type > MsgBattleState) return;
            try
            {
                using (var r = Protocol.Reader(payload))
                {
                    if (type >= MsgBattleSelect) OnBattleMessage(type, r);
                    else if (Net.IsHost)
                    {
                        if (!CampaignSync.IsHostRunning) return;
                        int id = r.ReadInt32();
                        if (type == MsgSelect) { host[1].Selected = Exists(id, 1) ? id : -1; dirty[1] = true; }
                        else if (type == MsgToggle && Exists(id, 1)) { host[1].Toggle(id); dirty[1] = true; }
                    }
                    else if (type == MsgState) mine.Read(r);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("MP locks: message " + type + " failed: " + e.Message); }
        }

        // ---------- queries ----------

        /// <summary>Why a subordinate may not order a force, as a phrase ("TASK FORCE 1 is locked by your commander Halsey").</summary>
        internal static string Describe(BlockReason why, int team, string name)
        {
            string cmdr = "your commander " + RemoteOrders.PlayerName(team == 0 ? 0 : 1);
            return why == BlockReason.CommanderSelected ? $"{name} is selected by {cmdr}" : $"{name} is locked by {cmdr}";
        }

        /// <summary>Subordinate PC: true (with a toast) if its commander has this own force selected or locked.</summary>
        internal static bool ClientRefuses(int id, string name)
        {
            if (!ModState.MultiplayerActive || !Net.IsSubordinate) return false;
            BlockReason why = mine.Blocked(id);
            if (why == BlockReason.None) return false;
            if (Time.realtimeSinceStartup - lastRefusedToast > 2f)
            {
                lastRefusedToast = Time.realtimeSinceStartup;
                Toasts.Show(Describe(why, MyTeam, name) + ": it takes no orders from you.");
            }
            return true;
        }

        // ---------- UI ----------

        /// <summary>Speed window rows: the commander's lock button, or what a subordinate may not touch.</summary>
        internal static void DrawWindowRow(GUIStyle label)
        {
            if (!ModState.MultiplayerActive) return;
            if (TimeControlDriver.Phase == GamePhase.Battle && InBattle) { DrawBattleRow(label); return; }
            if (TimeControlDriver.Phase != GamePhase.Campaign) return;
            TeamLockState st = MyState;
            if (Net.IsSubordinate)
            {
                GUILayout.Label($"Your commander: {RemoteOrders.PlayerName(MyTeam == 0 ? 0 : 1)}. Their selected force and the " +
                                $"{st.Locked.Count} locked one(s) take no orders from you.", label);
                return;
            }
            if (!TeamHasSubordinates(MyTeam) && st.Locked.Count == 0) return;
            CampaignInterface ci = CampaignInterface.instance;
            GameObjectAnimator g = ci != null ? ci.currentMapObject : null;
            bool own = g != null && g.mobileMapObject != null && g.mobileMapObject.currentFaction == 0;
            int id = own ? (Net.IsHost ? CampaignSync.HostIdOf(g) : CampaignSync.ClientIdOf(g)) : -1;
            bool isLocked = id >= 0 && st.Locked.Contains(id);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Locked for subordinates: {st.Locked.Count}", label);
            GUI.enabled = own;
            if (Styles.DownButton(isLocked ? $"Unlock selected ({KeyName})" : $"Lock selected ({KeyName})", GUILayout.Width(150))) ToggleSelected();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private static GUIStyle badgeStyle;

        /// <summary>Map badges: "LOCKED" on locked forces (commander and subordinates), the commander's selection (subordinates).</summary>
        internal static void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (InBattle)
            {
                try { DrawBattleBanner(); }
                catch (Exception e) { Plugin.Log.LogWarning("MP locks: battle banner failed: " + e.Message); }
                return;
            }
            if (!OnMap) return;
            TeamLockState st = MyState;
            bool sub = Net.IsSubordinate;
            if (st.Locked.Count == 0 && !(sub && st.Selected >= 0)) return;
            CampaignInterface ci = CampaignInterface.instance;
            // the campaign map is drawn through WMSK's render viewport: map position → viewport world position → main camera
            Camera cam = ci != null && ci.map != null ? ci.map.cameraMain : null;
            if (cam == null) return;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.identity; // screen pixels
            try
            {
                float s = Mathf.Max(0.7f, Screen.height / 1080f);
                if (badgeStyle == null) badgeStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                badgeStyle.fontSize = Mathf.RoundToInt(10f * s);
                foreach (int id in st.Locked) Badge(ci.map, cam, id, "LOCKED", new Color(0.55f, 0.12f, 0.10f, 0.9f), s);
                if (sub && st.Selected >= 0 && !st.Locked.Contains(st.Selected))
                    Badge(ci.map, cam, st.Selected, (MyTeam == 0 ? "P1" : "P2") + " SELECTED", new Color(0.62f, 0.42f, 0.05f, 0.9f), s);
            }
            catch (Exception e) { Plugin.Log.LogWarning("MP locks: badges failed: " + e.Message); }
            finally { GUI.matrix = old; }
        }

        private static void Badge(WMSK map, Camera cam, int id, string text, Color back, float s)
        {
            GameObjectAnimator g = Net.IsHost ? CampaignSync.HostFind(id) : CampaignSync.ClientFind(id);
            if (g == null || !g.gameObject.activeInHierarchy) return;
            Vector3 sp = cam.WorldToScreenPoint(map.Map2DToWorldPosition(g.currentMap2DLocation, 0f));
            if (sp.z < 0f || sp.x < 0f || sp.x > Screen.width || sp.y < 0f || sp.y > Screen.height) return;
            float w = badgeStyle.CalcSize(new GUIContent(text)).x + 8f * s, h = 15f * s;
            Rect r = new Rect(sp.x - w / 2f, Screen.height - sp.y + 11f * s, w, h);
            Color oldColor = GUI.color;
            GUI.color = back;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = oldColor;
            badgeStyle.normal.textColor = Color.white;
            GUI.Label(r, text, badgeStyle);
        }
    }
}
