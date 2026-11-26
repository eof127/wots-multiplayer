using System.Collections.Generic;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Core
{
    /// <summary>
    /// In-game owner of the network session: wires NetSession to config, the BepInEx log, toasts and the chat log,
    /// and pumps it every frame. Game-sync features (M3+) subscribe to Session.OnGameMessage / OnConnected.
    /// </summary>
    internal static class Multiplayer
    {
        internal static readonly NetSession Session = new NetSession();
        internal static readonly List<string> ChatLog = new List<string>();
        private const int MaxChatLines = 60;
        private static float lastDroppedNote = -10f;

        internal static void Init()
        {
            Session.ModVersion = BuildInfo.Version;
            Session.GameHash = Plugin.GameAssemblyHash;
            Session.Log += msg => Plugin.Log.LogInfo(msg);
            Session.Notice += msg => { Toasts.Show(msg); AddChatLine("* " + msg); };
            Session.OnSubordinateConnected += () => Toasts.Show("You joined as Player " + (Session.LocalPlayerIndex + 1) + ", a subordinate on " +
                NetSession.TeamName(Session.LocalTeam) + ". Stay at the main menu: when the host's campaign runs, your team's map " +
                "loads by itself. You give campaign orders to your team's forces, except the one your commander has selected or " +
                "locked; Player 1 and Player 2 vote on speed and encounters.", 12f);
            // a subordinate sends only campaign orders (stage 3); anything else is dropped: say so, at most every 5 s
            Session.SubordinateSendDropped += type =>
            {
                if (type == 21 || type == 23 || type == 24 || type == 37) return; // setup/loaded (internal), speed and encounters say it themselves
                if (UnityEngine.Time.realtimeSinceStartup - lastDroppedNote < 5f) return;
                lastDroppedNote = UnityEngine.Time.realtimeSinceStartup;
                Toasts.Show("Only Player 1 and Player 2 can do that.");
            };
            Session.LocalTeamChanged += team => Toasts.Show("The host moved you to " + NetSession.TeamName(team) + ".", 8f);
            Session.PlayersChanged += () => Plugin.Log.LogInfo("[net] players: " + string.Join(", ", Session.Players.ConvertAll(p => "P" + (p.Index + 1) + " " + p.Name + " team " + p.Team + (p.Role == PlayerRole.Subordinate ? " sub" : "")).ToArray()));
            Session.OnChat += (name, text) =>
            {
                AddChatLine(name + ": " + text);
                if (!ModPanel.Visible) Toasts.Show(name + ": " + text, 8f);
            };
        }

        /// <summary>Launch option -wotsmp-name overrides the (shared) config name for this copy.</summary>
        internal static string EffectiveName => LaunchOptions.Name ?? Plugin.PlayerName.Value;

        internal static void Host()
        {
            Session.PlayerName = EffectiveName;
            Session.MaxPlayers = Plugin.MaxPlayers.Value;
            Session.RoomPassword = -1; // hosted by address (F11 panel / launch option): no room code needed
            CurrentRoomCode = null;
            Session.StartHost(Plugin.HostPort.Value, Plugin.HostLocalOnly.Value);
            if (!Plugin.HostLocalOnly.Value && Session.State == SessionState.Hosting)
                Toasts.Show("Hosting by address has no room code: anyone who can reach port " + Plugin.HostPort.Value +
                            " can join. For games over the Internet prefer MULTIPLAYER → Create a Room.", 10f);
        }

        /// <summary>The open room's code (D46), or null when not hosting a room.</summary>
        internal static string CurrentRoomCode { get; private set; }

        private static readonly System.Random rng = new System.Random();

        /// <summary>
        /// Create a Room (D46): host on the port and make a code from the chosen address + port + a new password. A loopback
        /// address hosts "this PC only" (no firewall prompt; two copies on one PC). Returns the code, or null on failure.
        /// </summary>
        internal static string CreateRoom(System.Net.IPAddress address, int port)
        {
            byte[] ip = address != null ? address.GetAddressBytes() : null;
            if (ip == null || ip.Length != 4) { Toasts.Show("Pick an IPv4 address for the room code."); return null; }
            int password = RoomCode.NewPassword(rng);
            Session.PlayerName = EffectiveName;
            Session.RoomPassword = password;
            Session.MaxPlayers = Plugin.MaxPlayers.Value;
            // Listen only where the friend will connect: a VPN or local-network address in the code → that adapter only
            // (the game isn't reachable through the PC's other networks, e.g. public Wi-Fi). "Internet" (the router's
            // public address, not on this PC) needs all adapters: the router forwards to the LAN address.
            System.Net.IPAddress bind = System.Net.IPAddress.IsLoopback(address) ? System.Net.IPAddress.Loopback
                                      : IsThisPcAddress(address) ? address : System.Net.IPAddress.Any;
            Session.StartHost(port, bind);
            if (Session.State != SessionState.Hosting) { CurrentRoomCode = null; return null; }
            Plugin.HostPort.Value = port;
            CurrentRoomCode = RoomCode.Encode(ip, port, password);
            Plugin.Log.LogInfo($"[net] room created for {address}:{port} (listening on {(System.Net.IPAddress.Any.Equals(bind) ? "all adapters" : bind.ToString())})");
            return CurrentRoomCode;
        }

        private static bool IsThisPcAddress(System.Net.IPAddress a)
        {
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.Equals(a)) return true;
            }
            catch (System.Exception) { }
            return false;
        }

        /// <summary>Join a room by its code (D46). Returns an error message, or null when connecting.</summary>
        internal static string JoinRoom(string code)
        {
            byte[] ip; int port, password; string error;
            if (!RoomCode.TryDecode(code, out ip, out port, out password, out error)) return error;
            Session.PlayerName = EffectiveName;
            Session.JoinPassword = password;
            Session.JoinTeam = Plugin.JoinTeam.Value;
            CurrentRoomCode = null;
            Session.StartJoin(new System.Net.IPAddress(ip).ToString(), port);
            return null;
        }

        /// <summary>Accepts "host", "host:port".</summary>
        internal static void Join(string address)
        {
            Session.PlayerName = EffectiveName;
            string host = (address ?? "").Trim();
            int port = Protocol.DefaultPort;
            int colon = host.LastIndexOf(':');
            if (colon > 0)
            {
                int p;
                if (int.TryParse(host.Substring(colon + 1), out p) && p > 0 && p < 65536) port = p;
                host = host.Substring(0, colon);
            }
            if (host.Length == 0) { Toasts.Show("Enter the host's IP address first."); return; }
            Plugin.JoinAddress.Value = address.Trim();
            Session.JoinPassword = -1; // by address (no room code)
            Session.JoinTeam = Plugin.JoinTeam.Value;
            Session.StartJoin(host, port);
        }

        internal static void Leave()
        {
            CurrentRoomCode = null;
            Session.Stop(Session.IsHost ? "Host closed the session" : "Left the session");
        }

        internal static void SendChat(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 300) text = text.Substring(0, 300);
            if (!Session.CanChat) { Toasts.Show("Not connected."); return; }
            Session.SendChat(text);
            AddChatLine(EffectiveName + ": " + text);
        }

        internal static void Update() => Session.Poll();

        internal static string StatusLine()
        {
            switch (Session.State)
            {
                case SessionState.Hosting: return $"Hosting on port {Session.Port}: waiting for the other player" + (Session.SubordinateCount > 0 ? $" ({Session.SubordinateCount} subordinate(s) connected)" : "");
                case SessionState.Connecting: return "Connecting...";
                case SessionState.Handshaking: return "Connected, checking versions...";
                case SessionState.Connected:
                    if (Session.IsSubordinate) return $"Connected to {Session.PeerName} as Player {Session.LocalPlayerIndex + 1} (subordinate, {NetSession.TeamName(Session.LocalTeam)})";
                    return $"Connected to {Session.PeerName} as Player {Session.LocalPlayerIndex + 1}" +
                           (Session.RttMs >= 0 ? $" (ping {Session.RttMs} ms)" : "");
                default:
                    return string.IsNullOrEmpty(Session.LastDisconnectReason) ? "Not connected" : "Not connected: " + Session.LastDisconnectReason;
            }
        }

        private static void AddChatLine(string line)
        {
            ChatLog.Add(line);
            while (ChatLog.Count > MaxChatLines) ChatLog.RemoveAt(0);
        }
    }
}
