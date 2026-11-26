using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;

namespace WotSMultiplayer.Ui
{
    /// <summary>
    /// The mod window (default F11): multiplayer host/join + chat, and single-player options.
    /// While one of its text fields has keyboard focus, game hotkeys are blocked (InputBlockPatch).
    /// </summary>
    internal static class ModPanel
    {
        internal static bool Visible;

        /// <summary>True while a mod text field has keyboard focus (read by InputBlockPatch).</summary>
        internal static bool TextFieldFocused;

        private const string ChatField = "wotsmp_chat", NameField = "wotsmp_name", AddrField = "wotsmp_addr", PortField = "wotsmp_port";
        private static Rect rect = new Rect(20, 120, 420, 10);
        private static string chatInput = "", portInput;
        private static Vector2 chatScroll;
        private static int lastChatCount;
        private static GUIStyle chatStyle, headerStyle;

        internal static void Update()
        {
            if (Plugin.PanelKey.Value.IsDown() && !TextFieldFocused) Visible = !Visible;
            if (!Visible) TextFieldFocused = false;
        }

        internal static void OnGUI()
        {
            if (!Visible) return;
            // Clicking anywhere outside the window releases text focus so the game gets its keys back.
            Event e = Event.current;
            if (e.type == EventType.MouseDown && !rect.Contains(e.mousePosition) && TextFieldFocused)
            {
                GUIUtility.keyboardControl = 0;
                TextFieldFocused = false;
            }
            rect = GUILayout.Window(0x57A8, rect, Draw, $"WotS Multiplayer v{Plugin.Version}", Styles.Window, GUILayout.Width(420));
            UiBlock.Register(rect);
        }

        private static void Draw(int id)
        {
            if (chatStyle == null)
            {
                chatStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 12 };
                headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            }
            if (portInput == null) portInput = Plugin.HostPort.Value.ToString();
            NetSession s = Multiplayer.Session;

            // Enter sends chat (handled before the TextField sees the key).
            Event e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) &&
                GUI.GetNameOfFocusedControl() == ChatField)
            {
                Multiplayer.SendChat(chatInput);
                chatInput = "";
                e.Use();
            }

            GUILayout.Label("Multiplayer", headerStyle);
            GUILayout.Label(Multiplayer.StatusLine(), chatStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Your name", GUILayout.Width(80));
            GUI.SetNextControlName(NameField);
            string name = GUILayout.TextField(Plugin.PlayerName.Value, 24);
            if (name != Plugin.PlayerName.Value) Plugin.PlayerName.Value = name;
            GUILayout.EndHorizontal();

            if (s.State == SessionState.Idle)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", GUILayout.Width(80));
                GUI.SetNextControlName(PortField);
                portInput = GUILayout.TextField(portInput, 5, GUILayout.Width(60));
                bool local = GUILayout.Toggle(Plugin.HostLocalOnly.Value, " this PC only (testing)");
                if (local != Plugin.HostLocalOnly.Value) Plugin.HostLocalOnly.Value = local;
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Host a game"))
                {
                    int port;
                    if (int.TryParse(portInput, out port) && port > 0 && port < 65536) Plugin.HostPort.Value = port;
                    else portInput = Plugin.HostPort.Value.ToString();
                    Multiplayer.Host();
                }

                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Host address", GUILayout.Width(80));
                GUI.SetNextControlName(AddrField);
                string addr = GUILayout.TextField(Plugin.JoinAddress.Value, 64);
                if (addr != Plugin.JoinAddress.Value) Plugin.JoinAddress.Value = addr;
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Join game")) Multiplayer.Join(Plugin.JoinAddress.Value);
                GUILayout.Label("Internet play: the host forwards TCP port " + Plugin.HostPort.Value +
                                " or both of you use a VPN such as Tailscale/ZeroTier. Windows may ask the host to allow the game through the firewall.", chatStyle);
            }
            else
            {
                if (s.IsConnected && GUILayout.Button("Multiplayer campaign setup..."))
                    Features.MpCampaign.MpSetupWindow.Visible = !Features.MpCampaign.MpSetupWindow.Visible;
                if (GUILayout.Button(s.IsHost ? "Stop hosting" : (s.IsConnected ? "Leave game" : "Cancel"))) Multiplayer.Leave();
            }

            if (s.Players.Count > 2 || s.Player2SlotReserved) { GUILayout.Space(4); RoomWindow.DrawPlayers(s); }

            // Chat
            GUILayout.Space(6);
            GUILayout.Label("Chat", headerStyle);
            if (Multiplayer.ChatLog.Count != lastChatCount) { lastChatCount = Multiplayer.ChatLog.Count; chatScroll.y = float.MaxValue; }
            chatScroll = GUILayout.BeginScrollView(chatScroll, GUILayout.Height(140));
            foreach (string line in Multiplayer.ChatLog) GUILayout.Label(line, chatStyle);
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            GUI.enabled = s.CanChat;
            GUI.SetNextControlName(ChatField);
            chatInput = GUILayout.TextField(chatInput, 300);
            if (GUILayout.Button("Send", GUILayout.Width(60))) { Multiplayer.SendChat(chatInput); chatInput = ""; }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Single-player options
            GUILayout.Space(6);
            GUILayout.Label("Single-player options", headerStyle);
            bool ar = GUILayout.Toggle(Plugin.AutoResolveInSinglePlayer.Value, " Auto-resolve fighter/scout encounters in single-player");
            if (ar != Plugin.AutoResolveInSinglePlayer.Value) Plugin.AutoResolveInSinglePlayer.Value = ar;
            bool sv = GUILayout.Toggle(Plugin.SpeedVoteTestMode.Value, " Speed-vote test mode (simulated Player 2)");
            if (sv != Plugin.SpeedVoteTestMode.Value) Plugin.SpeedVoteTestMode.Value = sv;
            bool ov = GUILayout.Toggle(Plugin.ShowOverlay.Value, " Show version/status label");
            if (ov != Plugin.ShowOverlay.Value) Plugin.ShowOverlay.Value = ov;

            GUILayout.Space(4);
            if (GUILayout.Button($"Close ({Plugin.PanelKey.Value})")) Visible = false;

            string focused = GUI.GetNameOfFocusedControl();
            TextFieldFocused = focused == ChatField || focused == NameField || focused == AddrField || focused == PortField;
            GUI.DragWindow();
        }
    }

    /// <summary>Stops game hotkeys (Space = pause, T, WASD...) from firing while typing in a mod text field.</summary>
    [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetButtonDown))]
    internal static class InputBlockDownPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!ModPanel.TextFieldFocused && !RoomWindow.TextFieldFocused && !Features.TimeControl.TimeControlDriver.ChatFocused) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.GetButtonHeld))]
    internal static class InputBlockHeldPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!ModPanel.TextFieldFocused && !RoomWindow.TextFieldFocused && !Features.TimeControl.TimeControlDriver.ChatFocused) return true;
            __result = false;
            return false;
        }
    }
}
