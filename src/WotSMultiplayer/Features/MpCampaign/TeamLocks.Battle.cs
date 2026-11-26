using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WotSMultiplayer.Core;
using WotSMultiplayer.Features.TimeControl;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// Additional players, stage 4: the same rule in battle, per ship / plane group. The unit the team's commander has
    /// selected (CombatInterface.currentUnit) is off limits to the team's subordinates; K (or the speed window's button)
    /// locks / unlocks the selected own unit. Battle locks belong to one battle (unit ids = kind and index of that battle).
    ///   58 BattleSelect  Player 2 → host {unit id or −1}
    ///   59 BattleToggle  Player 2 → host {unit id}
    ///   60 BattleState   host → the team's players {selected, n, ids}
    /// </summary>
    internal static partial class TeamLocks
    {
        private const byte MsgBattleSelect = 58, MsgBattleToggle = 59, MsgBattleState = 60;

        private static readonly TeamLockState[] battleHost = { new TeamLockState(), new TeamLockState() };
        private static readonly bool[] battleDirty = { true, true };
        private static readonly TeamLockState battleMine = new TeamLockState();
        private static int battleSentSelection = int.MinValue;
        private static float battleSelectionSentAt, lastBattleSent, lastBattleRefresh;

        internal static TeamLockState BattleTeam(int team) => battleHost[team == 0 ? 0 : 1];
        private static TeamLockState MyBattleState => Net.IsHost ? battleHost[0] : battleMine;

        internal static bool InBattle => ModState.MultiplayerActive && (MpBattle.MpBattle.HostActive || MpBattle.MpBattle.ClientActive);

        /// <summary>Every PC, when a battle starts: no battle locks yet.</summary>
        internal static void BattleReset()
        {
            battleHost[0].Clear();
            battleHost[1].Clear();
            battleMine.Clear();
            battleDirty[0] = battleDirty[1] = true;
            battleSentSelection = int.MinValue;
        }

        internal static string UnitName(Unit u) =>
            u == null ? "that unit" : !string.IsNullOrEmpty(u.unitInstanceName) ? u.unitInstanceName : u.name;

        /// <summary>The selected unit if it is one of this PC's own (not sunk), with its battle id; else −1.</summary>
        private static int CurrentOwnBattleUnit(out Unit u)
        {
            u = CombatInterface.instance != null ? CombatInterface.instance.currentUnit : null;
            if (u == null || u.faction != Faction.Player || u.isDestroyed) { u = null; return -1; }
            byte kind;
            int index;
            return MpBattle.MpBattle.BattleUnitRef(u, out kind, out index) ? LockRules.BattleUnitId(kind, index) : -1;
        }

        private static bool BattleUnitOfTeam(int id, int team)
        {
            byte kind;
            int index;
            LockRules.SplitBattleUnitId(id, out kind, out index);
            Unit u = MpBattle.MpBattle.BattleUnitAt(kind, index);
            return u != null && !u.isDestroyed && (u.faction == Faction.Player) == (team == 0);
        }

        private static void HostBattleUpdate(float now)
        {
            Unit u;
            int sel = CurrentOwnBattleUnit(out u);
            if (sel != battleHost[0].Selected) { battleHost[0].Selected = sel; battleDirty[0] = true; }
            if (BattleLockKeyPressed()) ToggleBattleSelected();
            if (now - lastBattleRefresh > 5f) { lastBattleRefresh = now; battleDirty[0] = battleDirty[1] = true; }
            if ((battleDirty[0] || battleDirty[1]) && now - lastBattleSent > 0.15f)
            {
                lastBattleSent = now;
                for (int t = 0; t < 2; t++) if (battleDirty[t]) { battleDirty[t] = false; BroadcastBattle(t); }
            }
        }

        private static void Player2BattleUpdate(float now)
        {
            Unit u;
            int sel = CurrentOwnBattleUnit(out u);
            if (sel != battleSentSelection && now - battleSelectionSentAt > 0.15f)
            {
                battleSentSelection = sel;
                battleSelectionSentAt = now;
                Net.SendGame(MsgBattleSelect, Protocol.Build(w => w.Write(sel)));
            }
            if (BattleLockKeyPressed()) ToggleBattleSelected();
        }

        private static void BroadcastBattle(int team)
        {
            byte[] p = Protocol.Build(w => battleHost[team].Write(w));
            if (team == 1 && Net.IsConnected) Net.SendGame(MsgBattleState, p);
            Net.SendGameToSubordinates(MsgBattleState, p, team);
        }

        private static bool BattleLockKeyPressed()
        {
            if (!Plugin.LockKey.Value.IsDown() || !InBattle) return false;
            if (TimeControlDriver.ChatFocused || GUIUtility.keyboardControl != 0) return false;
            GameObject sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            InputField field = sel != null ? sel.GetComponent<InputField>() : null;
            return field == null || !field.isFocused;
        }

        /// <summary>Commander, in battle: lock or unlock the selected own ship / plane group for the team's subordinates.</summary>
        internal static void ToggleBattleSelected()
        {
            if (!IsCommander) return;
            Unit u;
            int id = CurrentOwnBattleUnit(out u);
            if (id < 0)
            {
                Toasts.Show($"Select one of your ships or plane groups, then press {KeyName} to lock it for your subordinates.");
                return;
            }
            bool locked;
            if (Net.IsHost)
            {
                bool was = battleHost[0].Locked.Contains(id);
                locked = battleHost[0].Toggle(id);
                if (!was && !locked) { Toasts.Show($"Too many locked units ({TeamLockState.MaxLocks})."); return; }
                battleDirty[0] = true;
            }
            else
            {
                locked = !battleMine.Locked.Contains(id);
                Net.SendGame(MsgBattleToggle, Protocol.Build(w => w.Write(id)));
            }
            string name = UnitName(u);
            Toasts.Show(locked ? $"{name} locked: your subordinates can't give it orders ({KeyName} again unlocks it)."
                               : $"{name} unlocked: your subordinates may give it orders again.");
            Plugin.Log.LogInfo($"MP locks: battle {(locked ? "locked" : "unlocked")} '{name}'");
        }

        /// <summary>Host / clients: battle lock messages (from TeamLocks.OnMessage).</summary>
        private static void OnBattleMessage(byte type, System.IO.BinaryReader r)
        {
            if (Net.IsHost)
            {
                if (!MpBattle.MpBattle.HostActive) return;
                int id = r.ReadInt32();
                if (type == MsgBattleSelect) { battleHost[1].Selected = id >= 0 && BattleUnitOfTeam(id, 1) ? id : -1; battleDirty[1] = true; }
                else if (type == MsgBattleToggle && BattleUnitOfTeam(id, 1)) { battleHost[1].Toggle(id); battleDirty[1] = true; }
            }
            else if (type == MsgBattleState) battleMine.Read(r);
        }

        /// <summary>Subordinate PC, in battle: true (with a toast) if its commander has this own unit selected or locked.</summary>
        internal static bool ClientRefusesBattle(byte kind, int index)
        {
            if (!ModState.MultiplayerActive || !Net.IsSubordinate) return false;
            BlockReason why = battleMine.Blocked(LockRules.BattleUnitId(kind, index));
            if (why == BlockReason.None) return false;
            if (Time.realtimeSinceStartup - lastRefusedToast > 2f)
            {
                lastRefusedToast = Time.realtimeSinceStartup;
                Toasts.Show(Describe(why, MyTeam, UnitName(MpBattle.MpBattle.BattleUnitAt(kind, index))) + ": it takes no orders from you.");
            }
            return true;
        }

        /// <summary>Speed window, in battle: the commander's lock button, or what a subordinate may not touch.</summary>
        private static void DrawBattleRow(GUIStyle label)
        {
            TeamLockState st = MyBattleState;
            if (Net.IsSubordinate)
            {
                GUILayout.Label($"Your commander: {RemoteOrders.PlayerName(MyTeam == 0 ? 0 : 1)}. The unit they have selected and the " +
                                $"{st.Locked.Count} locked one(s) take no orders from you. Retreat and Start are theirs.", label);
                return;
            }
            if (!TeamHasSubordinates(MyTeam) && st.Locked.Count == 0) return;
            Unit u;
            int id = CurrentOwnBattleUnit(out u);
            bool isLocked = id >= 0 && st.Locked.Contains(id);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Locked for subordinates: {st.Locked.Count}", label);
            GUI.enabled = id >= 0;
            if (Styles.DownButton(isLocked ? $"Unlock selected ({KeyName})" : $"Lock selected ({KeyName})", GUILayout.Width(150))) ToggleBattleSelected();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private static GUIStyle bannerStyle;

        /// <summary>In battle: a banner when the selected own unit is locked (commander) or off limits (subordinate).</summary>
        private static void DrawBattleBanner()
        {
            if (EngagementAI.instance != null && EngagementAI.instance.combatOver) return; // the action report is up
            Unit u;
            int id = CurrentOwnBattleUnit(out u);
            if (id < 0) return;
            string text = null;
            if (Net.IsSubordinate)
            {
                BlockReason why = battleMine.Blocked(id);
                if (why != BlockReason.None) text = Describe(why, MyTeam, UnitName(u)) + ": it takes no orders from you.";
            }
            else if (MyBattleState.Locked.Contains(id)) text = $"{UnitName(u)} is locked for your subordinates ({KeyName} unlocks it).";
            if (text == null) return;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;
            try
            {
                float s = Mathf.Max(0.7f, Screen.height / 1080f);
                if (bannerStyle == null) bannerStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, wordWrap = true, fontStyle = FontStyle.Bold };
                bannerStyle.fontSize = Mathf.RoundToInt(14f * s);
                bannerStyle.normal.textColor = new Color(1f, 0.82f, 0.55f);
                float w = Mathf.Min(620f * s, Screen.width - 20f), h = bannerStyle.CalcHeight(new GUIContent(text), w) + 6f * s;
                GUI.Box(new Rect((Screen.width - w) / 2f, Screen.height * 0.2f, w, h), text, bannerStyle); // below the toasts
            }
            finally { GUI.matrix = old; }
        }
    }
}
