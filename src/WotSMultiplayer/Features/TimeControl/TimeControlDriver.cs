using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.TimeControl
{
    internal enum GamePhase { None, Campaign, Battle }

    /// <summary>
    /// Connects <see cref="SpeedRules"/> to the game:
    ///  - detects whether we are on the campaign map or in a battle,
    ///  - enforces the agreed speed (campaign: WMSK.timeSpeed, battle: Time.timeScale), which also undoes the
    ///    vanilla notification/menu auto-pauses that would otherwise freeze the shared clock,
    ///  - draws the speed/vote widget (and, in solo test mode, controls for a simulated Player 2),
    ///  - rewires the campaign pause button so it proposes a pause instead of pausing.
    /// Inputs arrive via TimeControlPatches. Everything no-ops unless ModState.SpeedRulesActive.
    /// </summary>
    internal static class TimeControlDriver
    {
        // ---------- multiplayer roles (M5 core) ----------
        // The host owns the SpeedRules and applies the speed. The client mirrors the host's state for display and sends
        // its actions as messages, which the host applies as player 1.
        private const byte MsgSpeedAction = 24, MsgSpeedState = 25, MsgNotice = 27;
        private enum SpeedAct : byte { Faster = 1, FasterMax = 2, Slower = 3, PauseToggle = 4, Accept = 5, Decline = 6, Force = 7 }

        internal static bool IsRemote => ModState.MultiplayerActive && !Multiplayer.Session.IsHost;
        internal static bool IsHostMp => ModState.MultiplayerActive && Multiplayer.Session.IsHost;
        /// <summary>Additional players (stage 2): a subordinate sees the speed state but has no vote.</summary>
        internal static bool IsWatcher => IsRemote && Multiplayer.Session.IsSubordinate;
        private static float lastWatcherNote = -10f;

        /// <summary>0 in single-player test mode and for the MP host; 1 for the MP client.</summary>
        internal static int LocalPlayer => IsRemote ? 1 : 0;

        private static GamePhase remotePhase = GamePhase.None;
        private static bool stateDirty;
        private static float lastStateSent;

        internal static GamePhase Phase { get; private set; } = GamePhase.None;

        internal static readonly SpeedRules CampaignRules = new SpeedRules();
        internal static readonly SpeedRules BattleRules = new SpeedRules();

        internal static SpeedRules Active =>
            Phase == GamePhase.Campaign ? CampaignRules : Phase == GamePhase.Battle ? BattleRules : null;

        /// <summary>Set by the UnitAIAir.AttackUnit patch so the vanilla air-attack slow-down is treated as a system change.</summary>
        internal static bool InAirAttackScope;

        /// <summary>True from CampaignManager.GenerateCampaignEngagement until the mission quits (see patches).</summary>
        internal static bool InCampaignBattle;

        private static bool battleFlag;
        private static string campaignKey;
        private static int lastCampaignWeek = -1;
        private static readonly HashSet<int> rewiredButtons = new HashSet<int>();
        private static bool campaignButtonsScanned;

        private static MethodInfo setCompressText;

        internal static float Now => Time.realtimeSinceStartup;

        static TimeControlDriver()
        {
            CampaignRules.Notify += OnRulesNotice;
            BattleRules.Notify += OnRulesNotice;
            CampaignRules.Changed += () => stateDirty = true;
            BattleRules.Changed += () => stateDirty = true;
        }

        internal static void Init() => Multiplayer.Session.OnGameMessage += OnMessage;

        private static void OnRulesNotice(string msg)
        {
            if (IsRemote) return; // the mirror never generates notices; the host forwards its own
            Toasts.Show(msg);
            if (IsHostMp && Multiplayer.Session.IsConnected)
                Multiplayer.Session.SendGame(MsgNotice, Protocol.Build(w => w.Write(msg)));
            stateDirty = true;
        }

        private static void OnMessage(byte type, byte[] payload)
        {
            using (System.IO.BinaryReader r = Protocol.Reader(payload))
            {
                if (type == MsgSpeedAction && IsHostMp)
                {
                    ApplyAction(1, (SpeedAct)r.ReadByte());
                }
                else if (type == MsgSpeedState && IsRemote)
                {
                    byte ph = r.ReadByte();
                    remotePhase = (GamePhase)ph;
                    SpeedRules target = remotePhase == GamePhase.Campaign ? CampaignRules : remotePhase == GamePhase.Battle ? BattleRules : null;
                    if (target != null) target.ReadState(r, Now);
                }
                else if (type == MsgNotice && IsRemote)
                {
                    Toasts.Show(NetSession.CleanText(r.ReadString(), 400));
                    // optional vanilla campaign-message key: play its sound as vanilla does for the player
                    if (r.BaseStream.Position < r.BaseStream.Length) PlayVanillaMessageSound(r.ReadString());
                }
            }
        }

        /// <summary>Host: apply an action from player p to the active rules.</summary>
        private static void ApplyAction(int p, SpeedAct a)
        {
            SpeedRules rules = Active;
            if (rules == null) return;
            float now = Now;
            switch (a)
            {
                case SpeedAct.Faster:
                case SpeedAct.FasterMax:
                    if (Phase == GamePhase.Battle && !BattleSpeedUpAllowed()) return;
                    rules.RequestFaster(p, now, a == SpeedAct.FasterMax);
                    break;
                case SpeedAct.Slower: rules.RequestSlower(p, now); break;
                case SpeedAct.PauseToggle: rules.RequestPauseToggle(p, now); break;
                case SpeedAct.Accept: rules.Respond(p, true, now); break;
                case SpeedAct.Decline: rules.Respond(p, false, now); break;
                case SpeedAct.Force:
                    if (Phase == GamePhase.Battle && !BattleSpeedUpAllowed()) return;
                    rules.Force(p, now);
                    break;
            }
            stateDirty = true;
        }

        private static void Act(SpeedAct a)
        {
            if (IsWatcher)
            {
                if (Now - lastWatcherNote > 4f) { lastWatcherNote = Now; Toasts.Show("Only Player 1 and Player 2 vote on the game speed."); }
                return;
            }
            if (IsRemote) Multiplayer.Session.SendGame(MsgSpeedAction, new[] { (byte)a });
            else ApplyAction(0, a);
        }

        private static void BroadcastState()
        {
            if (!Multiplayer.Session.IsConnected) return;
            if (!stateDirty && Time.realtimeSinceStartup - lastStateSent < 0.5f) return;
            stateDirty = false;
            lastStateSent = Time.realtimeSinceStartup;
            SpeedRules rules = Active;
            byte[] state = Protocol.Build(w =>
            {
                w.Write((byte)Phase);
                if (rules != null) rules.WriteState(w, Now);
            });
            Multiplayer.Session.SendGame(MsgSpeedState, state);
            Multiplayer.Session.SendGameToSubordinates(MsgSpeedState, state); // they see the votes (no vote of their own)
        }

        /// <summary>Host: a notice for the subordinates (e.g. a battle started).</summary>
        internal static void NoticeToSubordinates(string msg)
        {
            if (Multiplayer.Session.IsHost && Multiplayer.Session.HasSubordinates())
                Multiplayer.Session.SendGameToSubordinates(MsgNotice, Protocol.Build(w => w.Write(msg)));
        }

        // ---------- hooks from patches ----------

        internal static void OnBattleStarted()
        {
            battleFlag = true;
            float fast = Config.instance != null ? Config.instance.timeCompressionFactor : 5f;
            BattleRules.Reset(new[] { 1f, fast }, 0, startPaused: false);
            Plugin.Log.LogInfo("TimeControl: battle started.");
        }

        internal static void OnBattleEnded()
        {
            if (!battleFlag) return;
            battleFlag = false;
            Plugin.Log.LogInfo("TimeControl: battle ended.");
        }

        internal static void LocalFaster(bool toMax = false) => Act(toMax ? SpeedAct.FasterMax : SpeedAct.Faster);
        internal static void LocalSlower() => Act(SpeedAct.Slower);
        internal static void LocalPauseToggle() => Act(SpeedAct.PauseToggle);
        internal static void LocalRespond(bool accept) => Act(accept ? SpeedAct.Accept : SpeedAct.Decline);
        internal static void LocalForce() => Act(SpeedAct.Force);

        // ---------- per-frame ----------

        internal static void Update()
        {
            if (!ModState.SpeedRulesActive)
            {
                Phase = GamePhase.None;
                return;
            }

            if (IsRemote)
            {
                // Client: display the host's state only. Its own campaign clock is frozen by MpCampaign (puppet).
                // In a live MP battle it runs at the host's battle speed, so shells, clock and puppets stay in step (M6c).
                Phase = remotePhase;
                if (Phase == GamePhase.Battle && MpBattle.MpBattle.ClientActive) EnforceBattle(BattleRules);
                else if (Phase == GamePhase.Campaign) ShowRemoteCampaignSpeed(CampaignRules);
                return;
            }

            GamePhase newPhase = DetectPhase();
            if (newPhase != Phase)
            {
                GamePhase previous = Phase;
                Phase = newPhase;
                if (Phase == GamePhase.Campaign)
                {
                    bool sameCampaign = OnEnterCampaign();
                    // Back from a battle (or a menu): don't resume at a high speed, or post-battle cooldowns
                    // (e.g. 30 game-minutes) expire within a second and the same fleets re-engage at once.
                    if (sameCampaign && previous != GamePhase.Campaign)
                        CampaignRules.SlowToDefaultAfterInterruption("Back on the campaign map");
                }
            }

            SpeedRules rules = Active;
            if (rules != null)
            {
                rules.Tick(Now);
                if (Phase == GamePhase.Campaign)
                {
                    CheckWeeklyTokens();
                    EnforceCampaign(rules);
                }
                else
                {
                    EnforceBattle(rules);
                }
            }
            if (IsHostMp) BroadcastState();
        }

        private static GamePhase DetectPhase()
        {
            if (CampaignMapActive())
            {
                battleFlag = false; // back on the map: any battle is over
                return GamePhase.Campaign;
            }
            if (battleFlag && EngagementManager.instance != null && CombatInterface.instance != null)
                return GamePhase.Battle;
            return GamePhase.None;
        }

        internal static bool CampaignMapActive()
        {
            // Note: CampaignInterface stays enabled during campaign battles (verified in playtest), hence InCampaignBattle.
            CampaignInterface ci = CampaignInterface.instance;
            CampaignManager cm = CampaignManager.instance;
            return !InCampaignBattle && ci != null && ci.enabled && ci.gameObject.activeInHierarchy && LoadScreen.loadComplete &&
                   cm != null && cm.campaignData != null && cm.landMapLocations != null && cm.landMapLocations.Count > 0;
        }

        /// <summary>Returns true if this is the same campaign as before (re-entry), false if it was (re)initialised.</summary>
        private static bool OnEnterCampaign()
        {
            CampaignData cd = CampaignManager.instance.campaignData;
            CampaignInterface ci = CampaignInterface.instance;
            string key = cd.campaignID + "|" + ci.startDate.Ticks;
            bool same = key == campaignKey;
            if (!same)
            {
                campaignKey = key;
                // Running steps = vanilla timeCompression without the 0 (pause) entry.
                List<float> steps = new List<float>();
                foreach (float s in cd.timeCompression) if (s > 0f) steps.Add(s);
                if (steps.Count == 0) steps.Add(1f);
                int def = steps.IndexOf(60f); // D15: campaign default 60x
                if (def < 0) def = Mathf.Min(1, steps.Count - 1);
                CampaignRules.Reset(steps.ToArray(), def, startPaused: true);
                lastCampaignWeek = CampaignWeek();
                campaignButtonsScanned = false;
                Toasts.Show("Multiplayer speed rules active. The campaign starts paused: propose Resume (both players must agree).", 8f);
            }
            if (!campaignButtonsScanned)
            {
                RewireCampaignPauseButtons();
                campaignButtonsScanned = true;
            }
            return same;
        }

        private static int CampaignWeek()
        {
            CampaignInterface ci = CampaignInterface.instance;
            return (int)Math.Floor((ci.dateTime - ci.startDate).TotalDays / 7.0);
        }

        private static void CheckWeeklyTokens()
        {
            int week = CampaignWeek();
            if (week != lastCampaignWeek)
            {
                lastCampaignWeek = week;
                CampaignRules.ResetTokens();
                Toasts.Show("New in-game week: campaign force tokens reset (3 each).");
            }
        }

        private static bool encounterWasOpen;
        private static float lastAppliedCampaignSpeed = -1f;
        private static int suppressedPauses;

        private static void EnforceCampaign(SpeedRules rules)
        {
            // Encounter panel open = vanilla structural pause; leave it. When it closes, drop to the default speed
            // if faster (C6) and resume.
            bool encounterOpen = EngagementPanel.instance != null && EngagementPanel.instance.gameObject.activeSelf;
            if (encounterOpen) { encounterWasOpen = true; return; }
            if (encounterWasOpen)
            {
                encounterWasOpen = false;
                rules.SlowToDefaultAfterInterruption("Encounter resolved");
            }
            if (PauseMenu.instance != null && PauseMenu.instance.gameObject.activeSelf) return;

            CampaignInterface ci = CampaignInterface.instance;
            float target = rules.Paused ? 0f : rules.CurrentSpeed;
            if (Mathf.Approximately(ci.map.timeSpeed, target)) return;

            // The game changed the speed behind our back (vanilla notification/menu auto-pause): undo it (D22).
            if (ci.map.timeSpeed == 0f && target > 0f && Mathf.Approximately(lastAppliedCampaignSpeed, target))
            {
                suppressedPauses++;
                Plugin.Log.LogInfo($"TimeControl: suppressed a game auto-pause (#{suppressedPauses}) at {ci.dateTime:dd MMM HH:mm}.");
            }

            ci.map.timeSpeed = target;
            lastAppliedCampaignSpeed = target;
            if (ci.pauseButtonImage != null) ci.pauseButtonImage.color = target == 0f ? Color.gray : Color.white;
            try
            {
                if (setCompressText == null) setCompressText = AccessTools.Method(typeof(CampaignInterface), "SetCompressText");
                setCompressText?.Invoke(ci, null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("SetCompressText failed: " + e.Message); }
        }

        private static string shownRemoteSpeed;

        /// <summary>
        /// Client, campaign: the clock panel's "60x"-style text and pause icon show the host's speed. Display only — the local
        /// map.timeSpeed stays 0 (the client's campaign clock is the host's, from snapshots), which is why vanilla showed "0x".
        /// </summary>
        private static void ShowRemoteCampaignSpeed(SpeedRules rules)
        {
            CampaignInterface ci = CampaignInterface.instance;
            if (ci == null || ci.compressionText == null) return;
            float speed = rules.Paused ? 0f : rules.CurrentSpeed;
            string text = speed.ToString("F0") + LanguageManager.instance.GetDictionaryString(LanguageManager.generalDictionary, "Multiplied");
            if (text == shownRemoteSpeed && ci.compressionText.text == text) return;
            shownRemoteSpeed = text;
            ci.compressionText.text = text;
            if (ci.pauseButtonImage != null) ci.pauseButtonImage.color = speed == 0f ? Color.gray : Color.white;
        }

        private static void EnforceBattle(SpeedRules rules)
        {
            if (ActionReport.instance != null && ActionReport.instance.gameObject.activeSelf) return;
            if (PauseMenu.instance != null && PauseMenu.instance.gameObject.activeSelf) return;

            float target = rules.Paused ? 0f : rules.CurrentSpeed;
            if (Mathf.Approximately(Time.timeScale, target)) return;

            CombatInterface.instance.SetTimeRate(target); // sets timeScale + speed text + pause icon
            if (target > 0f) Time.fixedDeltaTime = target * 0.02f; // same physics step rule as vanilla compression
        }

        /// <summary>The speed the game is actually running at (debug readout in the widget).</summary>
        private static float AppliedSpeed()
        {
            if (IsRemote) { SpeedRules m = Active; return m == null || m.Paused ? 0f : m.CurrentSpeed; } // host's speed
            if (Phase == GamePhase.Campaign && CampaignInterface.instance != null && CampaignInterface.instance.map != null)
                return CampaignInterface.instance.map.timeSpeed;
            return Time.timeScale;
        }

        /// <summary>Vanilla forbids battle compression while an air attack is in progress (CombatInterface.AllowCompression).</summary>
        internal static bool BattleSpeedUpAllowed()
        {
            if (EngagementManager.instance != null && EngagementManager.instance.airAttack > 0)
            {
                Toasts.Show("Can't speed up during an air attack.");
                return false;
            }
            return true;
        }

        // ---------- pause button rewiring ----------

        /// <summary>
        /// The campaign pause button calls CampaignInterface.TogglePauseCampaign via a persistent (scene) listener.
        /// TogglePauseCampaign is also called by the game for menu auto-pauses, so instead of patching the method
        /// we wrap the button: in rules mode it proposes a pause; otherwise it behaves exactly as before.
        /// </summary>
        private static void RewireCampaignPauseButtons()
        {
            int found = 0;
            foreach (Button b in Resources.FindObjectsOfTypeAll<Button>())
            {
                if (b == null || b.hideFlags != HideFlags.None || rewiredButtons.Contains(b.GetInstanceID())) continue;
                Button.ButtonClickedEvent original = b.onClick;
                bool isPause = false;
                for (int i = 0; i < original.GetPersistentEventCount(); i++)
                {
                    if (original.GetPersistentMethodName(i) == "TogglePauseCampaign" && original.GetPersistentTarget(i) is CampaignInterface)
                        isPause = true;
                }
                if (!isPause) continue;

                Button.ButtonClickedEvent wrapped = new Button.ButtonClickedEvent();
                wrapped.AddListener(() =>
                {
                    if (ModState.SpeedRulesActive && Phase == GamePhase.Campaign) LocalPauseToggle();
                    else original.Invoke();
                });
                b.onClick = wrapped;
                rewiredButtons.Add(b.GetInstanceID());
                found++;
            }
            Plugin.Log.LogInfo($"TimeControl: rewired {found} campaign pause button(s).");
        }

        // ---------- UI ----------

        private static Rect windowRect = new Rect(-1, 90, 330, 10);
        private static GUIStyle labelStyle, boldStyle;

        internal static void OnGUI()
        {
            if (!ModState.SpeedRulesActive || Active == null) { ChatFocused = false; return; }
            if (windowRect.x < 0) windowRect.x = UiScale.Width - windowRect.width - 20;
            if (ChatFocused && Event.current.type == EventType.MouseDown && !windowRect.Contains(Event.current.mousePosition))
            {
                GUIUtility.keyboardControl = 0; // clicking the game leaves the chat field (and gives the keys back to the game)
                ChatFocused = false;
            }
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
                boldStyle = new GUIStyle(labelStyle) { fontStyle = FontStyle.Bold };
            }
            SpeedRules ar = Active;
            bool incoming = !IsWatcher && ar.Pending != null && ar.Pending.From != LocalPlayer;
            if (Event.current.type == EventType.Repaint) ProposalChime(incoming);
            string title = incoming ? "Game speed: YOUR ANSWER NEEDED"
                         : (Phase == GamePhase.Campaign ? "Game speed: campaign" : "Game speed: battle") + (IsWatcher ? " (watching)" : "");
            windowRect = GUILayout.Window(0x57A7, windowRect, DrawWindow, title, Styles.Window, GUILayout.Width(330));
            UiBlock.Register(windowRect);
            if (incoming && Event.current.type == EventType.Repaint)
            {
                // a slow amber glow round the whole window (about 1 pulse per second, alpha only: no flashing)
                float p = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * Mathf.PI * 2f * 0.9f);
                Color c = Color.Lerp(new Color(1f, 0.55f, 0.08f, 0.35f), new Color(1f, 0.80f, 0.25f, 0.95f), p);
                DrawBorder(new Rect(windowRect.x - 4f, windowRect.y - 4f, windowRect.width + 8f, windowRect.height + 8f), c, 3f);
            }
        }

        // ---------- chat (user request 2026-11-25: a basic text chat in the speed window) ----------

        private const string ChatFieldName = "WotSMP_SpeedChat";
        internal static bool ChatFocused;
        private static string chatInput = "";
        private static Vector2 chatScroll;
        private static int chatSeen = -1;
        private static GUIStyle chatLine;

        private static void DrawChat()
        {
            if (chatLine == null) { chatLine = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, richText = false }; }
            Event e = Event.current;
            // Enter sends (before the text field eats the key); Escape leaves the field
            if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == ChatFieldName)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    if (chatInput.Trim().Length > 0) Multiplayer.SendChat(chatInput);
                    chatInput = "";
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Escape) { GUIUtility.keyboardControl = 0; e.Use(); }
            }

            GUILayout.Space(4);
            GUILayout.Label("Chat", boldStyle);
            List<string> log = Multiplayer.ChatLog;
            if (log.Count != chatSeen) { chatSeen = log.Count; chatScroll.y = float.MaxValue; }
            chatScroll = GUILayout.BeginScrollView(chatScroll, GUILayout.Height(84));
            if (log.Count == 0) GUILayout.Label("No messages yet.", chatLine);
            else foreach (string line in log) GUILayout.Label(line, chatLine);
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUI.enabled = Multiplayer.Session.CanChat;
            GUI.SetNextControlName(ChatFieldName);
            chatInput = GUILayout.TextField(chatInput ?? "", 300);
            if (Styles.DownButton("Send", GUILayout.Width(52)))
            {
                if (chatInput.Trim().Length > 0) Multiplayer.SendChat(chatInput);
                chatInput = "";
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            ChatFocused = GUI.GetNameOfFocusedControl() == ChatFieldName;
        }

        // ---------- proposal panel (user request 2026-11-25: make pending accept/decline stand out) ----------
        // Design notes (short reading on game pop-up / button design): one clear decision with two actions; the answer
        // buttons big, filled and colour-coded (green accept, red decline) with high-contrast text; a visible countdown
        // so the time limit is obvious; an attention cue that is noticeable but calm (slow ~1 Hz pulse of the border, no
        // strobing, which also matters for photosensitive players); sound only as a supplement to the visual cue; a fixed
        // panel size so nothing jumps under the mouse when a proposal appears.

        private static GUIStyle panelAlert, panelInfo, panelIdle, btnText, btnTextOff;
        private static bool chimeWasIncoming;
        private static AudioClip chime;

        private static void EnsureProposalStyles()
        {
            if (panelAlert != null) return;
            panelInfo = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = true };
            panelInfo.normal.textColor = new Color(0.80f, 0.90f, 1f);
            panelAlert = new GUIStyle(panelInfo) { fontSize = 14 };
            panelAlert.normal.textColor = new Color(1f, 0.93f, 0.70f);
            panelIdle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            panelIdle.normal.textColor = new Color(0.62f, 0.64f, 0.68f);
            btnText = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            btnText.normal.textColor = Color.white;
            btnTextOff = new GUIStyle(btnText) { fontStyle = FontStyle.Normal, fontSize = 13 };
            btnTextOff.normal.textColor = new Color(0.50f, 0.52f, 0.55f);
        }

        private static void DrawProposalPanel(SpeedRules r, float now, int me, int other)
        {
            EnsureProposalStyles();
            bool watcher = IsWatcher;
            bool incoming = !watcher && r.Pending != null && r.Pending.From != me;
            bool outgoing = !watcher && r.Pending != null && r.Pending.From == me;
            float left = r.Pending != null ? Mathf.Max(0f, r.Pending.ExpiresAt - now) : 0f;
            float frac = Mathf.Clamp01(left / SpeedRules.ProposalTimeoutSeconds);
            int secs = Mathf.CeilToInt(left);
            bool repaint = Event.current.type == EventType.Repaint;

            Rect area = GUILayoutUtility.GetRect(100f, 310f, 104f, 104f, GUILayout.ExpandWidth(true)); // fixed height
            if (repaint)
            {
                FillRect(area, incoming ? new Color(0.32f, 0.21f, 0.04f, 1f) : outgoing ? new Color(0.07f, 0.15f, 0.27f, 1f) : new Color(0.12f, 0.13f, 0.15f, 1f));
                if (incoming)
                {
                    float p = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * Mathf.PI * 2f * 0.9f);
                    DrawBorder(area, Color.Lerp(new Color(1f, 0.60f, 0.10f, 0.6f), new Color(1f, 0.88f, 0.35f, 1f), p), 3f);
                }
                else if (outgoing) DrawBorder(area, new Color(0.35f, 0.60f, 0.90f, 0.8f), 1f);
            }

            string text = watcher && r.Pending != null
                            ? $"{SpeedRules.PlayerName(r.Pending.From)} proposes {r.Describe(r.Pending)} ({secs} s). " +
                              $"Waiting for {SpeedRules.PlayerName(1 - r.Pending.From)}."
                        : incoming ? $"{SpeedRules.PlayerName(r.Pending.From)} proposes {r.Describe(r.Pending)} ({secs} s)"
                        : outgoing ? $"Waiting for {SpeedRules.PlayerName(other)} to answer: {r.Describe(r.Pending)} ({secs} s)"
                        : "No pending proposal";
            GUI.Label(new Rect(area.x + 8f, area.y + 4f, area.width - 16f, 36f), text, incoming ? panelAlert : outgoing ? panelInfo : panelIdle);

            Rect bar = new Rect(area.x + 8f, area.y + 42f, area.width - 16f, 8f);
            if (repaint)
            {
                FillRect(bar, new Color(0f, 0f, 0f, 0.55f));
                if (r.Pending != null)
                {
                    Color bc = frac > 0.5f ? new Color(0.35f, 0.80f, 0.40f) : frac > 0.25f ? new Color(0.95f, 0.72f, 0.20f) : new Color(0.92f, 0.30f, 0.22f);
                    FillRect(new Rect(bar.x, bar.y, bar.width * frac, bar.height), bc);
                }
            }

            float bw = (area.width - 24f) / 2f;
            Rect accept = new Rect(area.x + 8f, area.y + 58f, bw, 38f);
            Rect decline = new Rect(accept.xMax + 8f, accept.y, bw, 38f);
            if (ColorButton(accept, "ACCEPT", incoming, new Color(0.16f, 0.58f, 0.26f), new Color(0.24f, 0.74f, 0.34f))) LocalRespond(true);
            if (ColorButton(decline, "DECLINE", incoming, new Color(0.66f, 0.18f, 0.16f), new Color(0.84f, 0.26f, 0.22f))) LocalRespond(false);
        }

        /// <summary>Filled, colour-coded button that acts on mouse-down (see Styles.DownButton for why).</summary>
        private static bool ColorButton(Rect r, string text, bool enabled, Color normal, Color hover)
        {
            Event e = Event.current;
            bool over = enabled && r.Contains(e.mousePosition);
            if (over && e.type == EventType.MouseDown && e.button == 0) { e.Use(); return true; }
            if (e.type == EventType.Repaint)
            {
                FillRect(r, enabled ? (over ? hover : normal) : new Color(0.20f, 0.21f, 0.23f, 1f));
                DrawBorder(r, enabled ? new Color(1f, 1f, 1f, over ? 0.95f : 0.45f) : new Color(1f, 1f, 1f, 0.08f), over ? 2f : 1f);
                GUI.Label(r, text, enabled ? btnText : btnTextOff);
            }
            return false;
        }

        private static void FillRect(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static void DrawBorder(Rect r, Color c, float t)
        {
            FillRect(new Rect(r.x, r.y, r.width, t), c);
            FillRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            FillRect(new Rect(r.x, r.y + t, t, r.height - 2f * t), c);
            FillRect(new Rect(r.xMax - t, r.y + t, t, r.height - 2f * t), c);
        }

        /// <summary>
        /// Player 2: the sound vanilla plays with a campaign message (ErrorPanel.SetErrorMessage → audioReferences →
        /// AudioManager.PlayMessageAudio, e.g. audio/ui/Click2 for "NewSea", "ShipsReleased", "Rearmed").
        /// </summary>
        internal static void PlayVanillaMessageSound(string dictRef)
        {
            if (string.IsNullOrEmpty(dictRef)) return;
            try
            {
                ErrorPanel ep = ErrorPanel.instance;
                if (ep == null || ep.audioReferences == null || AudioManager.instance == null || AssetLoader.instance == null) return;
                string clip;
                if (!ep.audioReferences.TryGetValue(dictRef, out clip)) return;
                AudioManager.instance.PlayMessageAudio(AssetLoader.instance.ImportAudioClip(clip), isMessageLog: false);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Message sound failed: " + e.Message); }
        }

        /// <summary>A soft two-note chime when the other player's proposal arrives ([General] SpeedProposalSound).</summary>
        private static void ProposalChime(bool incoming)
        {
            bool start = incoming && !chimeWasIncoming;
            chimeWasIncoming = incoming;
            if (!start || !Plugin.SpeedProposalSound.Value) return;
            try
            {
                AudioManager am = AudioManager.instance;
                if (am == null || am.messageSource == null) return;
                if (chime == null) chime = MakeChime();
                float vol = OptionsManager.instance != null && OptionsManager.instance.optionsData != null
                    ? OptionsManager.instance.optionsData.campMessageVolume : 0.6f;
                am.messageSource.PlayOneShot(chime, Mathf.Clamp01(vol));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Speed proposal chime failed: " + e.Message); }
        }

        private static AudioClip MakeChime()
        {
            const int rate = 44100;
            int n = (int)(rate * 0.36f);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float f = t < 0.15f ? 660f : 880f;          // E5 then A5
                float local = t < 0.15f ? t : t - 0.15f;
                float env = Mathf.Exp(-local * 14f) * Mathf.Clamp01(local * 400f); // soft attack, quick decay
                data[i] = 0.28f * env * Mathf.Sin(2f * Mathf.PI * f * t);
            }
            AudioClip c = AudioClip.Create("WotSMP_ProposalChime", n, 1, rate, false);
            c.SetData(data, 0);
            return c;
        }

        private static void DrawWindow(int id)
        {
            if (Plugin.DebugInput.Value && (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseUp))
                Plugin.Log.LogInfo($"[input] speed widget {Event.current.type} local={Event.current.mousePosition} rect={windowRect}");
            SpeedRules r = Active;
            if (r == null) return;
            float now = Now;
            int me = LocalPlayer, other = 1 - LocalPlayer;

            // Fixed set of rows so buttons never jump when a proposal or lock appears.
            GUILayout.Label((r.Paused ? "PAUSED (at " + SpeedRules.Format(r.CurrentSpeed) + ")" : "Speed: " + SpeedRules.Format(r.CurrentSpeed)) +
                            "    [game: " + SpeedRules.Format(AppliedSpeed()) + "]", boldStyle);
            GUILayout.Label(r.IsLocked(now)
                ? $"Locked by {SpeedRules.PlayerName(r.LockedBy)}'s force: {Mathf.CeilToInt(r.LockedUntil - now)} s"
                : "No force lock", labelStyle);
            GUILayout.Label($"Forces left: P1 {r.TokensLeft[0]} / P2 {r.TokensLeft[1]}" +
                            (Phase == GamePhase.Campaign ? " (reset weekly)" : " (this battle)"), labelStyle);

            GUILayout.Space(4);
            DrawProposalPanel(r, now, me, other);
            GUILayout.Space(4);

            if (IsWatcher)
                GUILayout.Label("You are a subordinate: Player 1 and Player 2 vote on the speed. You see their votes here.", labelStyle);
            else
            {
                GUILayout.BeginHorizontal();
                if (Styles.DownButton("Slower")) LocalSlower();
                if (Styles.DownButton("Faster")) LocalFaster();
                if (Styles.DownButton($"Force ({r.TokensLeft[me]})")) LocalForce();
                if (Styles.DownButton(r.Paused ? "Resume" : "Pause")) LocalPauseToggle();
                GUILayout.EndHorizontal();
            }

            string teams = ModState.MultiplayerActive ? RoomWindow.TeamsLine(Multiplayer.Session) : null;
            if (teams != null) GUILayout.Label(teams, labelStyle);
            if (ModState.MultiplayerActive) MpCampaign.TeamLocks.DrawWindowRow(labelStyle);
            if (ModState.MultiplayerActive) DrawChat();

            if (Plugin.SpeedVoteTestMode.Value && !ModState.MultiplayerActive)
            {
                GUILayout.Space(6);
                GUILayout.Label("Test partner (simulated " + SpeedRules.PlayerName(other) + "):", boldStyle);
                GUILayout.BeginHorizontal();
                if (Styles.DownButton("Accept")) r.Respond(other, true, now);
                if (Styles.DownButton("Decline")) r.Respond(other, false, now);
                if (Styles.DownButton("Slower")) r.RequestSlower(other, now);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (Styles.DownButton("Faster")) { if (Phase != GamePhase.Battle || BattleSpeedUpAllowed()) r.RequestFaster(other, now); }
                if (Styles.DownButton($"Force ({r.TokensLeft[other]})")) { if (Phase != GamePhase.Battle || BattleSpeedUpAllowed()) r.Force(other, now); }
                if (Styles.DownButton(r.Paused ? "Resume" : "Pause")) r.RequestPauseToggle(other, now);
                GUILayout.EndHorizontal();
            }
            GUI.DragWindow();
        }
    }
}
