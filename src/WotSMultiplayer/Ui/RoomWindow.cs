using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;

namespace WotSMultiplayer.Ui
{
    /// <summary>
    /// M10: the main menu's MULTIPLAYER popup (MultiplayerMenuButton opens it). "Create a Room" hosts and shows a room code
    /// (D46: the code carries the chosen address + port + password); "Join a Room" connects with a code. Once the other player
    /// is connected the popup closes and the multiplayer campaign setup opens (MpCampaign), as with the F11 panel.
    /// </summary>
    internal static class RoomWindow
    {
        internal static bool Visible;
        internal static bool TextFieldFocused;

        private enum Page { Choose, Create, Join }
        private const string NameField = "wotsmp_room_name", CodeField = "wotsmp_room_code", AddrField = "wotsmp_room_addr", PortField = "wotsmp_room_port";

        private struct AddressChoice
        {
            public string Label;
            public IPAddress Address; // null = "internet address" typed by the host
        }

        private static Page page;
        private static Rect rect;
        private static string codeInput = "", portInput, publicInput = "", joinError, createError;
        private static List<AddressChoice> choices;
        private static int choice;
        private static GUIStyle textStyle, headerStyle, codeStyle, bigButton;
        private static bool wasConnected;

        internal static void Open()
        {
            Visible = true;
            page = Multiplayer.CurrentRoomCode != null ? Page.Create : Page.Choose;
            joinError = createError = null;
            choices = null;
            rect = new Rect(0, 0, 0, 0);
        }

        internal static void Update()
        {
            if (!Visible) { TextFieldFocused = false; wasConnected = false; return; }
            bool connected = Multiplayer.Session.IsConnected;
            if (connected && !wasConnected) Visible = false; // the campaign setup takes over (MpCampaign opens it)
            wasConnected = connected;
        }

        internal static void OnGUI()
        {
            if (!Visible) return;
            if (textStyle == null)
            {
                textStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 13 };
                headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };
                codeStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 28, alignment = TextAnchor.MiddleCenter };
                bigButton = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 40 };
            }
            if (rect.width <= 0f) rect = new Rect(UiScale.Width / 2f - 230f, UiScale.Height / 2f - 200f, 460f, 10f);
            Event e = Event.current;
            if (e.type == EventType.MouseDown && !rect.Contains(e.mousePosition) && TextFieldFocused)
            {
                GUIUtility.keyboardControl = 0;
                TextFieldFocused = false;
            }
            rect = GUILayout.Window(0x57AA, rect, Draw, "Multiplayer", Styles.Window, GUILayout.Width(460));
            UiBlock.Register(rect);
        }

        private static void Draw(int id)
        {
            NetSession s = Multiplayer.Session;
            if (portInput == null) portInput = Plugin.HostPort.Value.ToString();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Your name", GUILayout.Width(80));
            GUI.SetNextControlName(NameField);
            // Show the name actually sent (a -wotsmp-name launch name wins over the config and isn't saved to it).
            string current = Multiplayer.EffectiveName;
            string name = GUILayout.TextField(current, 24);
            if (name != current)
            {
                if (LaunchOptions.Name != null) LaunchOptions.Name = name;
                else Plugin.PlayerName.Value = name;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            switch (page)
            {
                case Page.Choose: DrawChoose(s); break;
                case Page.Create: DrawCreate(s); break;
                case Page.Join: DrawJoin(s); break;
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (page != Page.Choose && s.State == SessionState.Idle && GUILayout.Button("Back", GUILayout.Width(80))) page = Page.Choose;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(80))) Visible = false;
            GUILayout.EndHorizontal();

            string focused = GUI.GetNameOfFocusedControl();
            TextFieldFocused = focused == NameField || focused == CodeField || focused == AddrField || focused == PortField;
            GUI.DragWindow();
        }

        private static void DrawChoose(NetSession s)
        {
            if (s.State != SessionState.Idle)
            {
                GUILayout.Label(Multiplayer.StatusLine(), textStyle);
                if (GUILayout.Button(s.IsHost ? "Close the room" : "Cancel")) Multiplayer.Leave();
                return;
            }
            GUILayout.Label("Play a campaign together: one player creates a room and sends the room code to the other.", textStyle);
            GUILayout.Space(6);
            if (GUILayout.Button("Create a Room", bigButton)) { page = Page.Create; createError = null; }
            GUILayout.Space(4);
            if (GUILayout.Button("Join a Room", bigButton)) { page = Page.Join; joinError = null; }
        }

        // ---------- create ----------

        private static void DrawCreate(NetSession s)
        {
            GUILayout.Label("Create a Room", headerStyle);
            if (s.IsHost && s.State != SessionState.Idle && Multiplayer.CurrentRoomCode != null)
            {
                GUILayout.Label("Room code — send it to the other player:", textStyle);
                GUILayout.Label(Multiplayer.CurrentRoomCode, codeStyle, GUILayout.Height(44));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Copy code"))
                {
                    GUIUtility.systemCopyBuffer = Multiplayer.CurrentRoomCode;
                    Toasts.Show("Room code copied.");
                }
                if (GUILayout.Button("Close the room")) Multiplayer.Leave();
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
                GUILayout.Label(s.IsConnected ? s.PeerName + " has joined." : "Waiting for the other player to join...", textStyle);
                if (s.MaxPlayers > 2) DrawPlayers(s);
                GUILayout.Label($"Over the internet, your router must forward TCP port {s.Port} to this PC, and Windows may ask you to allow " +
                                "the game through the firewall. On the same network or a VPN (Tailscale, ZeroTier) no forwarding is needed.", textStyle);
                return;
            }

            if (choices == null) choices = FindAddresses();
            GUILayout.Label("Which address goes into the code?", textStyle);
            for (int i = 0; i < choices.Count; i++)
            {
                bool on = GUILayout.Toggle(choice == i, " " + choices[i].Label);
                if (on && choice != i) choice = i;
            }
            if (choice >= choices.Count) choice = 0;
            if (choices[choice].Address == null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Internet IP", GUILayout.Width(80));
                GUI.SetNextControlName(AddrField);
                publicInput = GUILayout.TextField(publicInput, 15);
                GUILayout.EndHorizontal();
                GUILayout.Label("Your internet (public) IP: search \"what is my IP\" in a browser and type it here.", textStyle);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(80));
            GUI.SetNextControlName(PortField);
            portInput = GUILayout.TextField(portInput, 5, GUILayout.Width(60));
            GUILayout.EndHorizontal();
            // additional players (D57 stage 1): 2 = the classic game; more = subordinates who can chat for now
            GUILayout.BeginHorizontal();
            GUILayout.Label("Players", GUILayout.Width(80));
            int max = Mathf.Clamp(Plugin.MaxPlayers.Value, 2, 6);
            for (int n = 2; n <= 6; n++)
                if (GUILayout.Toggle(max == n, n.ToString(), GUI.skin.button, GUILayout.Width(32)) && max != n) Plugin.MaxPlayers.Value = n;
            GUILayout.EndHorizontal();
            if (Plugin.MaxPlayers.Value > 2)
                GUILayout.Label("Players 3+ join as subordinates of a team. In this version they can chat and see who is playing; " +
                                "map view and orders come in later updates.", textStyle);

            if (createError != null) GUILayout.Label(createError, textStyle);
            if (GUILayout.Button("Create Room", bigButton))
            {
                createError = null;
                IPAddress address = choices[choice].Address;
                if (address == null && !TryParseIPv4(publicInput, out address)) { createError = "Type your internet IP address (four numbers like 203.0.113.45)."; return; }
                int port;
                if (!int.TryParse(portInput, out port) || port < 1 || port > 65535) { createError = "The port must be a number from 1 to 65535."; return; }
                string code = Multiplayer.CreateRoom(address, port);
                if (code == null) createError = string.IsNullOrEmpty(s.LastDisconnectReason) ? "Could not create the room." : s.LastDisconnectReason;
                else
                {
                    GUIUtility.systemCopyBuffer = code;
                    Toasts.Show("Room created. Code " + code + " (copied to the clipboard).", 10f);
                }
            }
        }

        private static List<AddressChoice> FindAddresses()
        {
            var list = new List<AddressChoice> { new AddressChoice { Label = "Internet (type your public IP below)", Address = null } };
            var seen = new HashSet<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        Add(list, seen, ua.Address, ni.Description);
                }
            }
            catch (Exception)
            {
                try { foreach (IPAddress a in Dns.GetHostAddresses(Dns.GetHostName())) Add(list, seen, a, null); } catch (Exception) { }
            }
            list.Add(new AddressChoice { Label = "This PC only (testing with two copies)", Address = IPAddress.Loopback });
            return list;
        }

        private static void Add(List<AddressChoice> list, HashSet<string> seen, IPAddress a, string adapter)
        {
            if (a == null || a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) return;
            byte[] b = a.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return; // link-local
            if (!seen.Add(a.ToString())) return;
            string kind;
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)) kind = "Local network";
            else if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) kind = "VPN (e.g. Tailscale)";
            else kind = "This PC's public address";
            string label = kind + ": " + a + (string.IsNullOrEmpty(adapter) ? "" : "  (" + Shorten(adapter) + ")");
            list.Add(new AddressChoice { Label = label, Address = a });
        }

        private static string Shorten(string s) => s.Length > 32 ? s.Substring(0, 32) + "…" : s;

        private static bool TryParseIPv4(string text, out IPAddress address)
        {
            address = null;
            string[] parts = (text ?? "").Trim().Split('.');
            if (parts.Length != 4) return false;
            var b = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                int v;
                if (!int.TryParse(parts[i], out v) || v < 0 || v > 255) return false;
                b[i] = (byte)v;
            }
            address = new IPAddress(b);
            return true;
        }

        // ---------- join ----------

        private static void DrawJoin(NetSession s)
        {
            GUILayout.Label("Join a Room", headerStyle);
            if (s.State != SessionState.Idle)
            {
                GUILayout.Label(Multiplayer.StatusLine(), textStyle);
                if (s.Players.Count > 2) DrawPlayers(s);
                if (GUILayout.Button(s.IsConnected ? "Leave" : "Cancel")) Multiplayer.Leave();
                return;
            }
            GUILayout.Label("Room code from the host:", textStyle);
            GUI.SetNextControlName(CodeField);
            codeInput = GUILayout.TextField(codeInput, 24);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Paste", GUILayout.Width(80))) codeInput = (GUIUtility.systemCopyBuffer ?? "").Trim();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            // additional players: which team to ask for (the host's room decides whether there is room)
            GUILayout.BeginHorizontal();
            GUILayout.Label("Team", GUILayout.Width(80));
            int team = Mathf.Clamp(Plugin.JoinTeam.Value, -1, 1);
            string[] names = { "Auto", "Host's team", "Player 2's team" };
            for (int t = -1; t <= 1; t++)
                if (GUILayout.Toggle(team == t, names[t + 1], GUI.skin.button) && team != t) Plugin.JoinTeam.Value = t;
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(s.LastDisconnectReason)) GUILayout.Label("Last attempt: " + s.LastDisconnectReason, textStyle);
            if (joinError != null) GUILayout.Label(joinError, textStyle);
            if (GUILayout.Button("Join", bigButton)) joinError = Multiplayer.JoinRoom(codeInput);
        }

        /// <summary>Everyone in the session (additional players).</summary>
        private static GUIStyle playerStyle;

        /// <summary>
        /// Everyone in the session. The host can move a subordinate to the other team (before the campaign starts), remove
        /// one, and give Player 2's kept slot to someone else (stage 5).
        /// </summary>
        internal static void DrawPlayers(NetSession s)
        {
            if (s.Players.Count == 0 && !s.Player2SlotReserved) return;
            if (playerStyle == null) playerStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 13 };
            GUILayout.Label("Players:", playerStyle);
            bool lobby = Features.MpCampaign.MpCampaign.Phase == Features.MpCampaign.MpPhase.Lobby && !s.KeepSlots;
            bool anySub = false;
            foreach (PlayerInfo p in s.Players.ToArray())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"   P{p.Index + 1}  {p.Name}  —  {NetSession.TeamName(p.Team)}" +
                                (p.Role == PlayerRole.Subordinate ? ", subordinate" : ", commander") +
                                (p.Index == s.LocalPlayerIndex ? " (you)" : ""), playerStyle);
                if (s.IsHost && p.Role == PlayerRole.Subordinate)
                {
                    anySub = true;
                    GUI.enabled = lobby;
                    if (Styles.DownButton("Other team", GUILayout.Width(96))) s.MoveSubordinate(p.Index, 1 - p.Team);
                    GUI.enabled = true;
                    if (Styles.DownButton("Remove", GUILayout.Width(76))) s.RemoveSubordinate(p.Index);
                }
                GUILayout.EndHorizontal();
            }
            if (s.Player2SlotReserved)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"   Player 2's slot is kept for {s.Player2SlotName}: they rejoin with the room code.", playerStyle);
                if (Styles.DownButton("Let anyone take it", GUILayout.Width(150))) s.ReleasePlayer2Slot();
                GUILayout.EndHorizontal();
            }
            if (s.IsHost && anySub && !lobby) GUILayout.Label("   Teams can only be changed before the campaign starts.", playerStyle);
        }

        /// <summary>Speed window: who plays which side ("Allies: Halsey (P1), Kondo (P4)   Japan: …"); null for two players.</summary>
        internal static string TeamsLine(NetSession s)
        {
            if (s.Players.Count < 3) return null;
            var t0 = new System.Collections.Generic.List<string>();
            var t1 = new System.Collections.Generic.List<string>();
            foreach (PlayerInfo p in s.Players) (p.Team == 0 ? t0 : t1).Add($"{p.Name} (P{p.Index + 1})");
            int hostSide = Features.MpCampaign.MpCampaign.Settings.HostSide;
            string a = hostSide == 0 ? "Allies" : "Japan", b = hostSide == 0 ? "Japan" : "Allies";
            return $"{a}: {string.Join(", ", t0.ToArray())}    {b}: {string.Join(", ", t1.ToArray())}";
        }
    }
}
