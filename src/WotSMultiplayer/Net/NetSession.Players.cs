using System;
using System.Collections.Generic;
using System.IO;

namespace WotSMultiplayer.Net
{
    internal enum PlayerRole : byte { Commander = 0, Subordinate = 1 }

    /// <summary>One player of the session, as everyone sees it (MsgType.Players).</summary>
    internal sealed class PlayerInfo
    {
        public int Index;     // 0 host (P1), 1 P2, 2+ subordinates (P3, P4 ...)
        public int Team;      // 0 the host's side, 1 Player 2's side
        public PlayerRole Role;
        public string Name;
    }

    /// <summary>
    /// Additional players, stage 1 (D57, docs/ADDITIONAL_PLAYERS.md): the host lets up to MaxPlayers − 2 subordinates join
    /// besides Player 2. Player 2 stays the session's main peer (IsConnected, PeerName, SendGame, OnConnected and
    /// OnGameMessage mean host ↔ Player 2 exactly as before), so a two-player game behaves byte-for-byte the same.
    /// Subordinates: a team (wished in Hello: −1 auto, 0 host's team, 1 Player 2's team), a player index (2, 3 …), their
    /// own keep-alive and timeout, chat (the host relays every chat line to everyone, with the sender's name) and the
    /// player list (MsgType.Players, sent to everyone whenever someone joins or leaves). Their game messages are not
    /// handed to the game yet (stage 2+: OnSubordinateGameMessage). On a subordinate's own PC the session is Connected,
    /// IsSubordinate is true and OnSubordinateConnected fires instead of OnConnected, so Player 2's game flows stay off.
    /// </summary>
    internal sealed partial class NetSession
    {
        /// <summary>Host: most players in the session, the host included (2 = the classic two-player game).</summary>
        public int MaxPlayers = 2;
        /// <summary>Client: the team to ask for when joining (−1 = whatever the host decides).</summary>
        public int JoinTeam = -1;

        /// <summary>This PC joined as a subordinate (client only).</summary>
        public bool IsSubordinate { get; private set; }
        /// <summary>This PC's team: 0 the host's side, 1 Player 2's side.</summary>
        public int LocalTeam => IsHost ? 0 : clientTeam;
        /// <summary>Everyone in the session (host first), as last announced by the host.</summary>
        public readonly List<PlayerInfo> Players = new List<PlayerInfo>();

        public event Action PlayersChanged;
        /// <summary>Client: joined the host's session as a subordinate (instead of OnConnected).</summary>
        public event Action OnSubordinateConnected;
        /// <summary>Host: a subordinate's game message (player index, type, payload) — for later stages.</summary>
        public event Action<int, byte, byte[]> OnSubordinateGameMessage;

        private int clientIndex = 1, clientTeam = 1;

        // ---------- stage 5: rejoining keeps your place ----------

        /// <summary>
        /// Host (set by the game while a multiplayer campaign is open): players keep their places. Player 2's slot is kept
        /// for Player 2's name (anyone else joins as a subordinate) until the host releases it, and a subordinate who comes
        /// back gets their old team whatever they ask for.
        /// </summary>
        public bool KeepSlots;
        /// <summary>Host: the name Player 2's slot belongs to (the last Player 2), or null.</summary>
        public string Player2SlotName { get; private set; }
        /// <summary>Host: each subordinate's team by name, for rejoins (kept until the session closes).</summary>
        private readonly Dictionary<string, int> knownTeams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static string NameKey(string n) => (n ?? "").Trim();

        /// <summary>Host: is Player 2's slot being kept for someone who isn't connected?</summary>
        public bool Player2SlotReserved => IsHost && KeepSlots && Player2SlotName != null && peer == null;

        /// <summary>Host: let anyone take Player 2's slot (e.g. a new player replaces one who can't come back).</summary>
        public void ReleasePlayer2Slot()
        {
            if (!IsHost || Player2SlotName == null) return;
            Say($"Player 2's slot is open: the next player to join takes {Player2SlotName}'s place.");
            Player2SlotName = null;
        }

        /// <summary>Host: who gets Player 2's slot when it is free (stage 5 rules; see KeepSlots).</summary>
        private bool TakesPlayer2Slot(string name, int wantedTeam)
        {
            if (peer != null) return false;
            string key = NameKey(name);
            if (KeepSlots && Player2SlotName != null) return string.Equals(key, NameKey(Player2SlotName), StringComparison.OrdinalIgnoreCase);
            if (knownTeams.ContainsKey(key) && !string.Equals(key, NameKey(Player2SlotName), StringComparison.OrdinalIgnoreCase))
                return false; // a known subordinate comes back as a subordinate
            return wantedTeam != 0;
        }

        /// <summary>Host: Player 2 (re)joined: the slot is theirs.</summary>
        private void RememberPlayer2(string name)
        {
            Player2SlotName = NameKey(name);
            knownTeams.Remove(Player2SlotName);
        }

        /// <summary>Host, in the lobby: move a subordinate to the other team. False if there is no such subordinate.</summary>
        public bool MoveSubordinate(int index, int team)
        {
            if (!IsHost || (team != 0 && team != 1)) return false;
            Member m = members.Find(x => x.Index == index);
            if (m == null || m.Team == team) return false;
            m.Team = team;
            knownTeams[NameKey(m.Name)] = team;
            Say($"{m.Name} (Player {m.Index + 1}) moved to {TeamName(team)}.");
            BroadcastPlayers();
            return true;
        }

        /// <summary>Host: remove a subordinate from the session (they may join again with the room code).</summary>
        public bool RemoveSubordinate(int index)
        {
            if (!IsHost) return false;
            Member m = members.Find(x => x.Index == index);
            if (m == null) return false;
            try { m.Conn.Send((byte)MsgType.Bye, Protocol.Build(w => w.Write("The host removed you from the session."))); } catch { }
            DropMember(m, "removed by the host");
            return true;
        }

        /// <summary>Client: the host moved this PC to the other team (from the player list).</summary>
        public event Action<int> LocalTeamChanged;

        private sealed class Member
        {
            public Connection Conn;
            public int Index, Team;
            public string Name;
            public float LastReceived;
        }

        private readonly List<Member> members = new List<Member>();
        private volatile Connection[] keepAliveMembers = new Connection[0];

        public int SubordinateCount => members.Count;

        private int PlayerCount => 1 + (peer != null && IsConnected ? 1 : 0) + members.Count;
        private bool HasFreeSlot() => IsHost && PlayerCount < Math.Max(2, MaxPlayers);

        private Member FindMember(Connection c)
        {
            foreach (Member m in members) if (m.Conn == c) return m;
            return null;
        }

        /// <summary>Host: let a checked-in connection join as a subordinate.</summary>
        private void AcceptMember(Pending p, HelloMsg h)
        {
            int known;
            bool isKnown = knownTeams.TryGetValue(NameKey(h.PlayerName), out known);
            int team = isKnown && (KeepSlots || (h.WantedTeam != 0 && h.WantedTeam != 1)) ? known // a rejoin keeps its team
                     : h.WantedTeam == 0 || h.WantedTeam == 1 ? h.WantedTeam : AutoTeam();
            int index = 2;
            while (members.Exists(m => m.Index == index)) index++;
            var mem = new Member { Conn = p.Conn, Index = index, Team = team, Name = Safe(h.PlayerName), LastReceived = Now };
            members.Add(mem);
            knownTeams[NameKey(mem.Name)] = team;
            RefreshKeepAliveMembers();
            mem.Conn.Send((byte)MsgType.Welcome, new WelcomeMsg
            {
                Accepted = true, HostName = PlayerName, YourPlayerIndex = index, Team = team, Role = PlayerRole.Subordinate
            }.Encode());
            StartKeepAlive();
            Say($"{mem.Name} joined as Player {index + 1}, a subordinate on {TeamName(team)}.");
            if (Player2SlotReserved)
                Say($"Player 2's slot is kept for {Player2SlotName}. To give it to {mem.Name}, use \"Let anyone take it\" in the player list (F11) and have them join again.");
            BroadcastPlayers();
            UpdateHostWaitingState();
            RaiseSubordinateJoined(index);
        }

        /// <summary>The team with fewer players (host and Player 2 count for their teams); ties go to Player 2's team.</summary>
        private int AutoTeam()
        {
            int t0 = 1, t1 = peer != null && IsConnected ? 1 : 0;
            foreach (Member m in members) if (m.Team == 0) t0++; else t1++;
            return t0 < t1 ? 0 : 1;
        }

        internal static string TeamName(int team) => team == 0 ? "the host's team" : "Player 2's team";

        /// <summary>Host: a message from a subordinate.</summary>
        private void OnMemberReceived(Member m, byte type, byte[] payload)
        {
            m.LastReceived = Now;
            try
            {
                switch ((MsgType)type)
                {
                    case MsgType.Ping: m.Conn.Send((byte)MsgType.Pong, payload); break;
                    case MsgType.Pong: break;
                    case MsgType.Chat:
                        using (BinaryReader r = Protocol.Reader(payload))
                        {
                            string text = CleanText(r.ReadString(), MaxChatChars);
                            if (text.Length == 0) return;
                            OnChat?.Invoke(m.Name, text);
                            RelayChat(m.Name, text, except: m.Conn);
                        }
                        break;
                    case MsgType.Bye:
                        using (BinaryReader r = Protocol.Reader(payload)) DropMember(m, CleanText(r.ReadString(), 200));
                        break;
                    default:
                        if (type >= (byte)MsgType.GameBase) OnSubordinateGameMessage?.Invoke(m.Index, type, payload);
                        break;
                }
            }
            catch (Exception ex) { Trace("bad message type " + type + " from " + m.Name + ": " + ex.Message); }
        }

        private void DropMember(Member m, string reason)
        {
            if (!members.Remove(m)) return;
            RefreshKeepAliveMembers();
            m.Conn.Close(graceful: true);
            Say($"{m.Name} (Player {m.Index + 1}) left ({reason}).");
            BroadcastPlayers();
        }

        private void DropAllMembers(string reason)
        {
            foreach (Member m in members.ToArray())
            {
                try { m.Conn.Send((byte)MsgType.Bye, Protocol.Build(w => w.Write(reason ?? ""))); } catch { }
                m.Conn.Close(graceful: true);
            }
            members.Clear();
            RefreshKeepAliveMembers();
        }

        private void CheckMemberTimeouts(float now)
        {
            foreach (Member m in members.ToArray())
                if (now - m.LastReceived > IdleTimeoutSeconds) DropMember(m, "connection timed out");
        }

        private void RefreshKeepAliveMembers()
        {
            var a = new Connection[members.Count];
            for (int i = 0; i < a.Length; i++) a[i] = members[i].Conn;
            keepAliveMembers = a;
        }

        /// <summary>Host: one chat line to everyone except its sender.</summary>
        private void RelayChat(string sender, string text, Connection except)
        {
            byte[] payload = Protocol.Build(w => { w.Write(text); w.Write(sender); });
            if (peer != null && IsConnected && peer != except) peer.Send((byte)MsgType.Chat, payload);
            foreach (Member m in members) if (m.Conn != except) m.Conn.Send((byte)MsgType.Chat, payload);
        }

        /// <summary>Host: everyone in the session, to everyone.</summary>
        private void BroadcastPlayers()
        {
            if (!IsHost) return;
            Players.Clear();
            Players.Add(new PlayerInfo { Index = 0, Team = 0, Role = PlayerRole.Commander, Name = Safe(PlayerName) });
            if (peer != null && IsConnected) Players.Add(new PlayerInfo { Index = 1, Team = 1, Role = PlayerRole.Commander, Name = PeerName });
            foreach (Member m in members) Players.Add(new PlayerInfo { Index = m.Index, Team = m.Team, Role = PlayerRole.Subordinate, Name = m.Name });
            byte[] payload = Protocol.Build(w =>
            {
                w.Write((byte)Players.Count);
                foreach (PlayerInfo p in Players) { w.Write((byte)p.Index); w.Write((byte)p.Team); w.Write((byte)p.Role); w.Write(p.Name ?? ""); }
            });
            if (peer != null && IsConnected) peer.Send((byte)MsgType.Players, payload);
            foreach (Member m in members) m.Conn.Send((byte)MsgType.Players, payload);
            PlayersChanged?.Invoke();
        }

        /// <summary>Client: the host's player list.</summary>
        private void ClientHandlePlayers(byte[] payload)
        {
            using (BinaryReader r = Protocol.Reader(payload))
            {
                int n = r.ReadByte();
                var list = new List<PlayerInfo>(n);
                for (int i = 0; i < n; i++)
                    list.Add(new PlayerInfo { Index = r.ReadByte(), Team = r.ReadByte(), Role = (PlayerRole)r.ReadByte(), Name = CleanText(r.ReadString(), 24) });
                Players.Clear();
                Players.AddRange(list);
            }
            // stage 5: the host can move a subordinate to the other team (in the lobby); the list carries it
            PlayerInfo me = Players.Find(x => x.Index == clientIndex);
            if (me != null && me.Team != clientTeam && (me.Team == 0 || me.Team == 1))
            {
                clientTeam = me.Team;
                LocalTeamChanged?.Invoke(clientTeam);
            }
            PlayersChanged?.Invoke();
        }

        /// <summary>Host: send a game message to every subordinate, or those of one team.</summary>
        public void SendGameToSubordinates(byte type, byte[] payload, int team = -1)
        {
            if (!IsHost || type < (byte)MsgType.GameBase) return;
            foreach (Member m in members) if (team < 0 || m.Team == team) m.Conn.Send(type, payload);
        }

        /// <summary>Host: send a game message to one subordinate (player index 2+).</summary>
        public void SendGameToPlayer(int index, byte type, byte[] payload)
        {
            if (!IsHost || type < (byte)MsgType.GameBase) return;
            foreach (Member m in members) if (m.Index == index) m.Conn.Send(type, payload);
        }

        /// <summary>Host: any subordinate (of a team, or of any team for −1) connected.</summary>
        public bool HasSubordinates(int team = -1)
        {
            foreach (Member m in members) if (team < 0 || m.Team == team) return true;
            return false;
        }

        /// <summary>Host: a subordinate's team, or −1.</summary>
        public int SubordinateTeam(int index)
        {
            foreach (Member m in members) if (m.Index == index) return m.Team;
            return -1;
        }

        /// <summary>Host: a subordinate has just joined (player index).</summary>
        public event Action<int> OnSubordinateJoined;
        /// <summary>Subordinate PC: a game message was not sent (not one <see cref="SubordinateMaySend"/> allows).</summary>
        public event Action<byte> SubordinateSendDropped;
        /// <summary>Subordinate PC: which game messages it may send to the host (the game sets it: stage 3 = campaign orders).</summary>
        public Func<byte, bool> SubordinateMaySend = type => false;

        private void RaiseSubordinateJoined(int index) => OnSubordinateJoined?.Invoke(index);
        private void RaiseSendDropped(byte type) => SubordinateSendDropped?.Invoke(type);
    }
}
