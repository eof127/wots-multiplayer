using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace WotSMultiplayer.Net
{
    internal enum SessionState
    {
        Idle,          // not hosting, not connected
        Hosting,       // listening, waiting for the other player
        Connecting,    // client: TCP connect in progress
        Handshaking,   // TCP up, Hello/Welcome in progress
        Connected,     // handshake done: both players in the session
    }

    /// <summary>
    /// Two-player session over TCP (decision D13: one PC hosts, the friend joins by IP).
    /// Host = player 0, client = player 1. The host keeps listening after a disconnect so the friend can rejoin.
    /// All callbacks fire on the thread that calls Poll() (the Unity main thread in-game). No Unity references.
    /// Hardening (security review 2026-11-25):
    ///  - the host keeps up to MaxPendingHandshakes connections waiting for their Hello at once (each with its own 10 s
    ///    limit) and the first valid Hello wins, so a connection that never says anything can't keep the friend out;
    ///  - wrong room codes: an address with LockoutAfterFailures wrong codes within 10 minutes is refused for
    ///    LockoutMinutes (the 20-bit password can't be guessed by trying codes); the host is told;
    ///  - names and chat from the other side are cleaned (no control characters or markup, length limits);
    ///  - Poll handles at most a frame's budget of messages, so a flood can't freeze the game.
    /// </summary>
    internal sealed partial class NetSession
    {
        public const float PingIntervalSeconds = 2f;
        public const float HandshakeTimeoutSeconds = 10f;
        public const int MaxPendingHandshakes = 4;
        public const int LockoutAfterFailures = 5;
        public const float FailureWindowSeconds = 600f;
        public const float LockoutMinutes = 15f;
        public const int MaxChatChars = 300;
        // 30 s (was 15): the first two-PC game (2026-11-25) dropped right after a battle — the laptop loading back to the
        // map sent nothing for 15 s. Pings now come from a background timer (they don't stop while a PC loads), and the
        // longer limit rides out Wi-Fi / VPN hiccups. A crashed or closed game still drops at once (the socket closes).
        public const float IdleTimeoutSeconds = 30f;
        public const int ConnectTimeoutMs = 8000;

        // identity (set by the caller before starting)
        public string ModVersion = "0";
        public string GameHash = "";
        public string PlayerName = "Player";
        /// <summary>Host: the room's password (D46); -1 = no room (hosted by address: anyone with the address may join).</summary>
        public int RoomPassword = -1;
        /// <summary>Client: the password from the room code; -1 = joining by address.</summary>
        public int JoinPassword = -1;
        /// <summary>Wrong codes from this PC (127.0.0.1, two copies on one PC) never lock it out. Tests turn this off.</summary>
        public bool ExemptLoopbackFromLockout = true;

        public SessionState State { get; private set; } = SessionState.Idle;
        public bool IsHost { get; private set; }
        public string PeerName { get; private set; } = "";
        public int RttMs { get; private set; } = -1;
        public int Port { get; private set; }
        public string LastDisconnectReason { get; private set; } = "";
        public int LocalPlayerIndex => IsHost ? 0 : clientIndex;
        public bool IsConnected => State == SessionState.Connected;

        /// <summary>Diagnostics for the log.</summary>
        public event Action<string> Log;
        /// <summary>Player-facing notices (toasts).</summary>
        public event Action<string> Notice;
        public event Action OnConnected;
        public event Action<string> OnDisconnected;
        public event Action<string, string> OnChat;           // (sender name, text)
        public event Action<byte, byte[]> OnGameMessage;      // types >= MsgType.GameBase

        private readonly NetEventQueue events = new NetEventQueue();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private Listener listener;
        private Connection peer;

        // host: connections that haven't sent a valid Hello yet
        private sealed class Pending { public Connection Conn; public float Since; }
        private readonly List<Pending> pending = new List<Pending>();
        // host: wrong room codes per address (times), and addresses refused until a time
        private readonly Dictionary<string, List<float>> codeFailures = new Dictionary<string, List<float>>();
        private readonly Dictionary<string, float> lockedOutUntil = new Dictionary<string, float>();
        private readonly List<float> allFailures = new List<float>();
        private bool warnedManyFailures;
        private float stateSince, lastReceived;
        // keep-alive pings on a thread-pool timer: the game's main thread can stall for many seconds (scene loads), and
        // Connection.Send is thread-safe (locked queue + writer thread)
        private System.Threading.Timer keepAliveTimer;
        private volatile Connection keepAlivePeer;

        private float Now => (float)clock.Elapsed.TotalSeconds;

        // ---------- control ----------

        public void StartHost(int port, bool loopbackOnly) => StartHost(port, loopbackOnly ? IPAddress.Loopback : IPAddress.Any);

        /// <summary>Host on one local address only (e.g. the VPN or LAN address in the room code), or IPAddress.Any.</summary>
        public void StartHost(int port, IPAddress bind)
        {
            bool loopbackOnly = IPAddress.IsLoopback(bind);
            Stop("restarting");
            try
            {
                listener = new Listener(bind, port, events);
            }
            catch (Exception e)
            {
                LastDisconnectReason = "Could not host on port " + port + ": " + e.Message;
                Say(LastDisconnectReason);
                return;
            }
            IsHost = true;
            Port = port;
            SetState(SessionState.Hosting);
            Say((loopbackOnly ? "Hosting (this PC only) on port " : "Hosting on port ") + port + ". Waiting for the other player...");
            if (!loopbackOnly && !IPAddress.Any.Equals(bind)) Trace("listening on " + bind + " only");
        }

        public void StartJoin(string host, int port)
        {
            Stop("restarting");
            IsHost = false;
            Port = port;
            SetState(SessionState.Connecting);
            Say("Connecting to " + host + ":" + port + "...");
            Connector.ConnectAsync(host, port, ConnectTimeoutMs, events);
        }

        /// <summary>Leave the session (host: also stop listening).</summary>
        public void Stop(string reason = "Left the session")
        {
            bool wasActive = State != SessionState.Idle;
            DropPeer(reason, sendBye: true, notify: false);
            DropAllMembers(reason);
            Players.Clear();
            IsSubordinate = false;
            knownTeams.Clear();
            Player2SlotName = null;
            KeepSlots = false;
            foreach (Pending p in pending) p.Conn.Close(graceful: false);
            pending.Clear();
            if (listener != null) { listener.Stop(); listener = null; }
            if (wasActive && reason != "restarting") Trace("session stopped: " + reason);
            LastDisconnectReason = ""; // the player chose to stop; don't keep showing an old error
            SetState(SessionState.Idle);
        }

        public void SendChat(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (IsHost) { RelayChat(Safe(PlayerName), text, except: null); return; } // to Player 2 and every subordinate
            if (!IsConnected) return;
            peer.Send((byte)MsgType.Chat, Protocol.Build(w => w.Write(text)));
        }

        /// <summary>Someone to chat with: Player 2 or the host, or (host) any subordinate.</summary>
        public bool CanChat => IsConnected || (IsHost && members.Count > 0);

        /// <summary>Send a game-sync message (type must be >= MsgType.GameBase).</summary>
        public void SendGame(byte type, byte[] payload)
        {
            if (!IsConnected) return;
            if (type < (byte)MsgType.GameBase) throw new ArgumentException("game message types start at " + (byte)MsgType.GameBase);
            if (IsSubordinate && !SubordinateMaySend(type)) { RaiseSendDropped(type); return; } // additional players: only what the game allows (stage 3: orders)
            peer.Send(type, payload);
        }

        // ---------- pump (call every frame) ----------

        public void Poll()
        {
            // a frame's budget of messages (~40 ms); the rest waits for the next frame, in order
            long start = clock.ElapsedMilliseconds;
            NetEvent e;
            int handled = 0;
            while (events.TryDequeue(out e))
            {
                try { Handle(e); }
                finally { if (e.Kind == NetEventKind.Received && e.Conn != null) e.Conn.Handled(); }
                if (++handled >= 400 || clock.ElapsedMilliseconds - start > 40) break;
            }

            float now = Now;
            if (IsHost)
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                    if (now - pending[i].Since > HandshakeTimeoutSeconds)
                    {
                        Trace("handshake timed out: " + pending[i].Conn.RemoteEndPoint);
                        pending[i].Conn.Close(graceful: false);
                        pending.RemoveAt(i);
                    }
                UpdateHostWaitingState();
                CheckMemberTimeouts(now);
            }
            else if (State == SessionState.Handshaking && now - stateSince > HandshakeTimeoutSeconds)
                DropPeer("handshake timed out", sendBye: true, notify: true);
            if (State == SessionState.Connected && now - lastReceived > IdleTimeoutSeconds)
                DropPeer("connection timed out (no data for " + (int)IdleTimeoutSeconds + " s)", sendBye: false, notify: true);
            // (pings: KeepAliveTick on the timer thread)
        }

        /// <summary>Host without a player: Handshaking while someone is checking in, else Hosting.</summary>
        private void UpdateHostWaitingState()
        {
            if (!IsHost || peer != null || listener == null) return;
            SetState(pending.Count > 0 ? SessionState.Handshaking : SessionState.Hosting);
        }

        private void StartKeepAlive()
        {
            keepAlivePeer = peer;
            if (keepAliveTimer == null)
            {
                int ms = (int)(PingIntervalSeconds * 1000f);
                keepAliveTimer = new System.Threading.Timer(_ => KeepAliveTick(), null, ms, ms);
            }
        }

        /// <summary>Timer thread: ping the peer, so the connection stays alive while this PC's game is busy loading.</summary>
        private void KeepAliveTick()
        {
            byte[] ping = BitConverter.GetBytes(clock.Elapsed.Ticks);
            Connection p = keepAlivePeer;
            if (p != null) { try { p.Send((byte)MsgType.Ping, ping); } catch (Exception) { } } // closing; the main thread reports it
            foreach (Connection m in keepAliveMembers) { try { m.Send((byte)MsgType.Ping, ping); } catch (Exception) { } }
        }

        private void Handle(NetEvent e)
        {
            switch (e.Kind)
            {
                case NetEventKind.Accepted: OnAccepted(e); break;
                case NetEventKind.Connected: OnTcpConnected(e); break;
                case NetEventKind.ConnectFailed:
                    if (State == SessionState.Connecting) { LastDisconnectReason = e.Reason; Say("Join failed: " + e.Reason); SetState(SessionState.Idle); }
                    break;
                case NetEventKind.Closed:
                    if (e.Conn == peer) DropPeer(e.Reason, sendBye: false, notify: true);
                    else
                    {
                        Member m = FindMember(e.Conn);
                        if (m != null) DropMember(m, e.Reason);
                        else RemovePending(e.Conn);
                    }
                    break;
                case NetEventKind.Received:
                    if (e.Conn == peer) OnReceived(e.Type, e.Payload);
                    else
                    {
                        Member m = FindMember(e.Conn);
                        if (m != null) { OnMemberReceived(m, e.Type, e.Payload); break; }
                        Pending p = FindPending(e.Conn);
                        if (p != null) OnPendingReceived(p, e.Type, e.Payload);
                    }
                    break;
            }
        }

        private Pending FindPending(Connection c)
        {
            foreach (Pending p in pending) if (p.Conn == c) return p;
            return null;
        }

        private void RemovePending(Connection c)
        {
            Pending p = FindPending(c);
            if (p == null) return;
            pending.Remove(p);
            UpdateHostWaitingState();
        }

        private void OnAccepted(NetEvent e)
        {
            TcpClientInfo info = TcpClientInfo.Of(e.Client);
            if (listener == null || !IsHost)
            {
                try { e.Client.Close(); } catch { }
                return;
            }
            if (IsLockedOut(info.Address))
            {
                // refused without a word (and without a toast per attempt)
                try { e.Client.Close(); } catch { }
                return;
            }
            if (peer != null && !HasFreeSlot())
            {
                // The session is full (MaxPlayers; 2 = only one other player).
                Connection.RejectAndClose(e.Client, (byte)MsgType.Welcome,
                    new WelcomeMsg { Accepted = false, Reason = "The session is full.", HostName = PlayerName }.Encode());
                Trace("rejected extra connection from " + info.EndPoint);
                return;
            }
            if (pending.Count >= MaxPendingHandshakes)
            {
                try { e.Client.Close(); } catch { }
                Trace("too many connections checking in: closed " + info.EndPoint);
                return;
            }
            Connection c = new Connection(e.Client, events);
            pending.Add(new Pending { Conn = c, Since = Now });
            UpdateHostWaitingState();
            Trace("incoming connection from " + c.RemoteEndPoint);
        }

        /// <summary>Host: a connection that hasn't been let in yet may only say Hello.</summary>
        private void OnPendingReceived(Pending p, byte type, byte[] payload)
        {
            if (type == (byte)MsgType.Hello)
            {
                HelloMsg h;
                try { h = HelloMsg.Decode(payload); }
                catch (Exception ex) { Trace("bad Hello from " + p.Conn.RemoteEndPoint + ": " + ex.Message); DropPending(p); return; }
                HostHandleHello(p, h);
            }
            else if (type != (byte)MsgType.Ping && type != (byte)MsgType.Pong)
            {
                Trace("message " + type + " before the handshake from " + p.Conn.RemoteEndPoint + ": closed");
                DropPending(p);
            }
        }

        private void DropPending(Pending p)
        {
            p.Conn.Close(graceful: false);
            pending.Remove(p);
            UpdateHostWaitingState();
        }

        private void OnTcpConnected(NetEvent e)
        {
            if (State != SessionState.Connecting) { try { e.Client.Close(); } catch { } return; }
            peer = new Connection(e.Client, events);
            lastReceived = Now;
            SetState(SessionState.Handshaking);
            peer.Send((byte)MsgType.Hello, new HelloMsg
            {
                ProtocolVersion = Protocol.Version, ModVersion = ModVersion, GameHash = GameHash, PlayerName = PlayerName,
                RoomPassword = JoinPassword, WantedTeam = JoinTeam
            }.Encode());
        }

        private void OnReceived(byte type, byte[] payload)
        {
            lastReceived = Now;
            try
            {
                switch ((MsgType)type)
                {
                    case MsgType.Hello: break; // the host reads Hello only from connections still checking in (OnPendingReceived)
                    case MsgType.Welcome: ClientHandleWelcome(WelcomeMsg.Decode(payload)); break;
                    case MsgType.Ping: peer.Send((byte)MsgType.Pong, payload); break;
                    case MsgType.Pong:
                        long sent = BitConverter.ToInt64(payload, 0);
                        RttMs = (int)TimeSpan.FromTicks(clock.Elapsed.Ticks - sent).TotalMilliseconds;
                        break;
                    case MsgType.Chat:
                        if (IsConnected)
                            using (BinaryReader r = Protocol.Reader(payload))
                            {
                                string text = CleanText(r.ReadString(), MaxChatChars);
                                // client: the host relays everyone's lines with the sender's name (protocol 20)
                                string sender = !IsHost && r.BaseStream.Position < r.BaseStream.Length ? Safe(r.ReadString()) : PeerName;
                                OnChat?.Invoke(sender, text);
                                if (IsHost) RelayChat(PeerName, text, except: peer); // Player 2's line to the subordinates
                            }
                        break;
                    case MsgType.Players:
                        if (!IsHost) ClientHandlePlayers(payload);
                        break;
                    case MsgType.Bye:
                        using (BinaryReader r = Protocol.Reader(payload)) DropPeer(CleanText(r.ReadString(), 200), sendBye: false, notify: true);
                        break;
                    default:
                        if (IsConnected && type >= (byte)MsgType.GameBase) OnGameMessage?.Invoke(type, payload);
                        else Trace("ignored message type " + type + " in state " + State);
                        break;
                }
            }
            catch (Exception ex)
            {
                Trace("bad message type " + type + ": " + ex.Message);
            }
        }

        private void HostHandleHello(Pending p, HelloMsg h)
        {
            if (!IsHost) return;
            string reason = null;
            bool wrongCode = false;
            // Player 2's slot when free (unless the joiner asked for the host's team, is a known subordinate, or the slot is
            // kept for Player 2 during a campaign); otherwise a subordinate slot
            bool asPlayer2 = TakesPlayer2Slot(h.PlayerName, h.WantedTeam);
            if (!asPlayer2 && !HasFreeSlot())
                reason = "The session is full.";
            else if (h.ProtocolVersion != Protocol.Version)
                reason = $"Network protocol mismatch (host {Protocol.Version}, you {h.ProtocolVersion}). Install the same mod version.";
            else if (h.ModVersion != ModVersion)
                reason = $"Mod version mismatch: host has v{ModVersion}, you have v{CleanText(h.ModVersion, 20)}.";
            else if (!string.Equals(h.GameHash, GameHash, StringComparison.OrdinalIgnoreCase))
                reason = "Game version mismatch: both players need the same War on the Sea update (Assembly-CSharp.dll differs).";
            else if (RoomPassword >= 0 && h.RoomPassword != RoomPassword)
            {
                reason = "Wrong room code. Ask the host for the current code (a new room gets a new code).";
                wrongCode = true;
            }

            pending.Remove(p);
            if (reason != null)
            {
                p.Conn.Send((byte)MsgType.Welcome, new WelcomeMsg { Accepted = false, Reason = reason, HostName = PlayerName }.Encode());
                p.Conn.Close(graceful: true);
                if (wrongCode) RecordWrongCode(p.Conn.RemoteAddress, Safe(h.PlayerName));
                else Say("Rejected " + Safe(h.PlayerName) + ": " + reason);
                UpdateHostWaitingState();
                return;
            }
            if (!asPlayer2) { AcceptMember(p, h); return; }
            peer = p.Conn;
            lastReceived = Now;
            PeerName = Safe(h.PlayerName);
            RememberPlayer2(PeerName);
            peer.Send((byte)MsgType.Welcome, new WelcomeMsg { Accepted = true, HostName = PlayerName, YourPlayerIndex = 1, Team = 1, Role = PlayerRole.Commander }.Encode());
            if (Math.Max(2, MaxPlayers) - 1 - members.Count <= 1)
            {
                // no room for anyone else: those still checking in are turned away
                foreach (Pending o in pending)
                {
                    o.Conn.Send((byte)MsgType.Welcome, new WelcomeMsg { Accepted = false, Reason = "The session is full.", HostName = PlayerName }.Encode());
                    o.Conn.Close(graceful: true);
                }
                pending.Clear();
            }
            BecomeConnected();
            BroadcastPlayers();
        }

        // ---------- wrong room codes (host) ----------

        private bool IsLockedOut(IPAddress a)
        {
            if (a == null || (ExemptLoopbackFromLockout && IPAddress.IsLoopback(a))) return false;
            float until;
            if (!lockedOutUntil.TryGetValue(a.ToString(), out until)) return false;
            if (Now < until) return true;
            lockedOutUntil.Remove(a.ToString());
            return false;
        }

        private void RecordWrongCode(IPAddress a, string name)
        {
            float now = Now;
            string key = a != null ? a.ToString() : "?";
            allFailures.Add(now);
            allFailures.RemoveAll(t => now - t > FailureWindowSeconds);
            if (allFailures.Count >= 20 && !warnedManyFailures)
            {
                warnedManyFailures = true;
                Say("Many wrong room codes in the last 10 minutes: someone may be guessing codes. Close the room and make a new one, or use a VPN.");
            }
            if (a == null || (ExemptLoopbackFromLockout && IPAddress.IsLoopback(a)))
            {
                Say("Rejected " + name + ": wrong room code.");
                return;
            }
            List<float> list;
            if (!codeFailures.TryGetValue(key, out list)) codeFailures[key] = list = new List<float>();
            list.Add(now);
            list.RemoveAll(t => now - t > FailureWindowSeconds);
            if (list.Count >= LockoutAfterFailures)
            {
                lockedOutUntil[key] = now + LockoutMinutes * 60f;
                list.Clear();
                Say($"Blocked {key} for {LockoutMinutes:0} minutes after {LockoutAfterFailures} wrong room codes.");
            }
            else Say($"Rejected {name} ({key}): wrong room code.");
        }

        private void ClientHandleWelcome(WelcomeMsg w)
        {
            if (IsHost || State != SessionState.Handshaking) return;
            if (!w.Accepted)
            {
                string why = CleanText(w.Reason, 300);
                LastDisconnectReason = why;
                DropPeer("rejected by host: " + why, sendBye: false, notify: true, graceful: true);
                return;
            }
            PeerName = Safe(w.HostName);
            clientIndex = w.YourPlayerIndex > 0 ? w.YourPlayerIndex : 1;
            clientTeam = w.Team == 0 ? 0 : 1;
            IsSubordinate = w.Role == PlayerRole.Subordinate;
            BecomeConnected();
        }

        private void BecomeConnected()
        {
            RttMs = -1;
            SetState(SessionState.Connected);
            StartKeepAlive();
            if (IsSubordinate)
            {
                // additional player (stage 1): connected and chatting, but Player 2's game flows stay off on this PC
                Say($"Connected to {PeerName}'s game as Player {clientIndex + 1}, a subordinate on {TeamName(clientTeam)}.");
                OnSubordinateConnected?.Invoke();
                return;
            }
            Say(IsHost ? PeerName + " joined your game (you are Player 1, host)." : "Connected to " + PeerName + "'s game (you are Player 2).");
            OnConnected?.Invoke();
        }

        private void DropPeer(string reason, bool sendBye, bool notify, bool graceful = false)
        {
            if (peer == null) return;
            keepAlivePeer = null;
            bool wasConnected = State == SessionState.Connected;
            if (sendBye) peer.Send((byte)MsgType.Bye, Protocol.Build(w => w.Write(reason ?? "")));
            peer.Close(graceful: sendBye || graceful);
            peer = null;
            RttMs = -1;
            LastDisconnectReason = reason ?? "";
            if (IsHost && listener != null) SetState(pending.Count > 0 ? SessionState.Handshaking : SessionState.Hosting);
            else SetState(SessionState.Idle);
            if (notify) Say(wasConnected ? $"{PeerName} left ({reason})." + (IsHost && listener != null ? " The room stays open." : "")
                                         : "Connection ended: " + reason);
            if (!IsHost) Players.Clear();
            // (stage 2: a subordinate in a campaign stops it like Player 2 does; IsSubordinate is cleared by Stop / the next join)
            if (wasConnected) OnDisconnected?.Invoke(reason);
            if (IsHost && wasConnected) BroadcastPlayers(); // Player 2 left: the subordinates' list
        }

        private void SetState(SessionState s)
        {
            if (State == s) return;
            State = s;
            stateSince = Now;
            Trace("state → " + s);
        }

        private static string Safe(string name)
        {
            name = CleanText(name, 24);
            return name.Length == 0 ? "Player" : name;
        }

        /// <summary>
        /// Text from the other PC, made safe to show and log: no control characters (no fake log lines), no '&lt;' '&gt;'
        /// (no rich-text markup in the game's or the mod's UI), trimmed to a length.
        /// </summary>
        internal static string CleanText(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, max));
            foreach (char c in s)
            {
                if (sb.Length >= max) break;
                if (char.IsControl(c)) { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                sb.Append(c == '<' ? '(' : c == '>' ? ')' : c);
            }
            return sb.ToString().Trim();
        }

        private void Say(string msg) { Trace(msg); Notice?.Invoke(msg); }
        // wall-clock time, so the two players' logs can be lined up (BepInEx's log has no timestamps)
        private void Trace(string msg) => Log?.Invoke("[net " + DateTime.Now.ToString("HH:mm:ss") + "] " + msg);
    }
}
