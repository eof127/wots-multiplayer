using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using WotSMultiplayer.Net;

/// <summary>Loopback tests: host and client sessions in one process talking over 127.0.0.1.</summary>
internal static class NetTests
{
    private static int FreePort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static NetSession Make(string name, string version = "1.0", string hash = "ABC")
    {
        var s = new NetSession { PlayerName = name, ModVersion = version, GameHash = hash };
        return s;
    }

    private static bool Pump(Func<bool> done, float seconds, params NetSession[] sessions)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            foreach (NetSession s in sessions) s.Poll();
            if (done()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    public static void Run(Action<bool, string> check)
    {
        int port = FreePort();
        var host = Make("Halsey");
        var client = Make("Yamamoto");
        var hostChat = new List<string>();
        var clientChat = new List<string>();
        var hostGame = new List<byte>();
        string hostDisconnect = null;
        host.OnChat += (n, t) => hostChat.Add(n + ":" + t);
        client.OnChat += (n, t) => clientChat.Add(n + ":" + t);
        host.OnGameMessage += (type, p) => hostGame.Add(type);
        host.OnDisconnected += r => hostDisconnect = r;

        host.StartHost(port, loopbackOnly: true);
        check(host.State == SessionState.Hosting, "net: host listens");

        client.StartJoin("127.0.0.1", port);
        bool ok = Pump(() => host.IsConnected && client.IsConnected, 5, host, client);
        check(ok, "net: handshake connects both sides");
        check(host.PeerName == "Yamamoto" && client.PeerName == "Halsey", "net: names exchanged");
        check(host.LocalPlayerIndex == 0 && client.LocalPlayerIndex == 1, "net: host is player 0, client is player 1");

        client.SendChat("hello from client");
        host.SendChat("hello from host");
        ok = Pump(() => hostChat.Count == 1 && clientChat.Count == 1, 3, host, client);
        check(ok && hostChat[0] == "Yamamoto:hello from client" && clientChat[0] == "Halsey:hello from host", "net: chat both directions");

        client.SendGame((byte)MsgType.GameBase, new byte[] { 1, 2, 3 });
        ok = Pump(() => hostGame.Count == 1, 3, host, client);
        check(ok && hostGame[0] == (byte)MsgType.GameBase, "net: game message delivered");

        ok = Pump(() => host.RttMs >= 0 && client.RttMs >= 0, 5, host, client);
        check(ok, "net: ping RTT measured on both sides (host " + host.RttMs + " ms, client " + client.RttMs + " ms)");

        // A third party is turned away while the session is full.
        var intruder = Make("Intruder");
        intruder.StartJoin("127.0.0.1", port);
        ok = Pump(() => intruder.State == SessionState.Idle, 5, host, client, intruder);
        check(ok && intruder.LastDisconnectReason.Contains("full") && host.IsConnected && client.IsConnected,
            "net: extra player rejected (session full), existing session unaffected");

        // Client leaves; host goes back to waiting and can accept a rejoin.
        client.Stop("Left the session");
        ok = Pump(() => host.State == SessionState.Hosting, 5, host, client);
        check(ok && hostDisconnect == "Left the session", "net: client leave → host back to Hosting with reason");

        client.StartJoin("127.0.0.1", port);
        ok = Pump(() => host.IsConnected && client.IsConnected, 5, host, client);
        check(ok, "net: client can rejoin");

        // Host closes the session.
        host.Stop("Host closed the session");
        ok = Pump(() => client.State == SessionState.Idle, 5, host, client);
        check(ok && client.LastDisconnectReason == "Host closed the session", "net: host stop → client disconnected with reason");

        // Version / game mismatches are rejected with a clear reason.
        port = FreePort();
        host = Make("Host", "1.0", "ABC");
        host.StartHost(port, true);
        var oldMod = Make("Old", "0.9", "ABC");
        oldMod.StartJoin("127.0.0.1", port);
        ok = Pump(() => oldMod.State == SessionState.Idle && oldMod.LastDisconnectReason != "", 5, host, oldMod);
        check(ok && oldMod.LastDisconnectReason.Contains("Mod version mismatch") && host.State == SessionState.Hosting,
            "net: mod version mismatch rejected, host keeps waiting");

        var otherGame = Make("Other", "1.0", "XYZ");
        otherGame.StartJoin("127.0.0.1", port);
        ok = Pump(() => otherGame.State == SessionState.Idle && otherGame.LastDisconnectReason != "", 5, host, otherGame);
        check(ok && otherGame.LastDisconnectReason.Contains("Game version mismatch"), "net: game build mismatch rejected");

        // Port already in use → clear error, no crash.
        var second = Make("Second");
        second.StartHost(port, true);
        check(second.State == SessionState.Idle && second.LastDisconnectReason.Contains("Could not host"), "net: port in use reported");
        host.Stop();

        // Rooms (D46): the password from the code must match; joining by address (-1) is refused by a room.
        port = FreePort();
        var roomHost = Make("RoomHost");
        roomHost.RoomPassword = 4242;
        roomHost.StartHost(port, true);
        var wrong = Make("Wrong");
        wrong.JoinPassword = 4241;
        wrong.StartJoin("127.0.0.1", port);
        ok = Pump(() => wrong.State == SessionState.Idle && wrong.LastDisconnectReason != "", 5, roomHost, wrong);
        check(ok && wrong.LastDisconnectReason.Contains("Wrong room code") && roomHost.State == SessionState.Hosting,
            "net: wrong room code refused, room stays open");
        var noCode = Make("NoCode");
        noCode.StartJoin("127.0.0.1", port);
        ok = Pump(() => noCode.State == SessionState.Idle && noCode.LastDisconnectReason != "", 5, roomHost, noCode);
        check(ok && noCode.LastDisconnectReason.Contains("Wrong room code"), "net: joining a room without its code refused");
        var right = Make("Right");
        right.JoinPassword = 4242;
        right.StartJoin("127.0.0.1", port);
        ok = Pump(() => roomHost.IsConnected && right.IsConnected, 5, roomHost, right);
        check(ok && roomHost.PeerName == "Right", "net: right room code connects");
        roomHost.Stop();
        Pump(() => right.State == SessionState.Idle, 3, roomHost, right);

        // Nobody listening → join fails cleanly.
        var lonely = Make("Lonely");
        lonely.StartJoin("127.0.0.1", FreePort());
        ok = Pump(() => lonely.State == SessionState.Idle, 10, lonely);
        check(ok && lonely.LastDisconnectReason.Contains("connect"), "net: join with no host fails with a reason");

        SecurityTests(check);
        AdditionalPlayerTests(check);
        RejoinTests(check);
    }

    /// <summary>Additional players, stages 1–2 (D57): subordinates beside Player 2, chat relay, player list, routing.</summary>
    private static void AdditionalPlayerTests(Action<bool, string> check)
    {
        int port = FreePort();
        var host = Make("Host");
        host.MaxPlayers = 4;
        host.StartHost(port, true);
        var hostChat = new List<string>();
        var hostGame = new List<byte>();
        var subGame = new List<int>();
        host.OnChat += (n, t) => hostChat.Add(n + ":" + t);
        host.OnGameMessage += (type, p) => hostGame.Add(type);
        host.OnSubordinateGameMessage += (idx, type, p) => subGame.Add(idx);
        var joined = new List<int>();
        host.OnSubordinateJoined += idx => joined.Add(idx);

        // a joiner asking for the host's team doesn't take Player 2's slot
        var p3 = Make("Third");
        p3.JoinTeam = 0;
        bool p3Sub = false, p3Conn = false, p3Gone = false;
        var p3Game = new List<byte>();
        var p3Dropped = new List<byte>();
        p3.OnSubordinateConnected += () => p3Sub = true;
        p3.OnConnected += () => p3Conn = true;
        p3.OnDisconnected += reason => p3Gone = true;
        p3.OnGameMessage += (type, p) => p3Game.Add(type);
        p3.SubordinateSendDropped += type => p3Dropped.Add(type);
        p3.StartJoin("127.0.0.1", port);
        bool ok = Pump(() => p3.IsConnected, 5, host, p3);
        check(ok && p3Sub && !p3Conn && p3.IsSubordinate && p3.LocalTeam == 0 && p3.LocalPlayerIndex == 2 && !host.IsConnected,
            "players: a joiner asking for the host's team becomes Player 3, a subordinate (Player 2's slot stays free)");

        var p2 = Make("Second");
        var p2Chat = new List<string>();
        var p2Game = new List<byte>();
        p2.OnChat += (n, t) => p2Chat.Add(n + ":" + t);
        p2.OnGameMessage += (type, p) => p2Game.Add(type);
        p2.StartJoin("127.0.0.1", port);
        ok = Pump(() => host.IsConnected && p2.IsConnected, 5, host, p2, p3);
        check(ok && !p2.IsSubordinate && p2.LocalPlayerIndex == 1 && p2.LocalTeam == 1 && host.PeerName == "Second",
            "players: the next joiner (no team wish) becomes Player 2 as before");

        var p4 = Make("Fourth");
        var p4Chat = new List<string>();
        var p4Game = new List<byte>();
        p4.OnChat += (n, t) => p4Chat.Add(n + ":" + t);
        p4.OnGameMessage += (type, p) => p4Game.Add(type);
        p4.StartJoin("127.0.0.1", port);
        ok = Pump(() => p4.IsConnected && p4.Players.Count == 4 && p2.Players.Count == 4 && p3.Players.Count == 4, 5, host, p2, p3, p4);
        check(ok && p4.IsSubordinate && p4.LocalPlayerIndex == 3 && p4.LocalTeam == 1 && host.SubordinateCount == 2,
            "players: auto team balances (Player 4 joins Player 2's team); everyone gets the list of 4 players");

        // full: a fifth player is turned away, nobody else is affected
        var p5 = Make("Fifth");
        p5.StartJoin("127.0.0.1", port);
        ok = Pump(() => p5.State == SessionState.Idle, 5, host, p2, p3, p4, p5);
        check(ok && p5.LastDisconnectReason.Contains("full") && host.IsConnected && host.SubordinateCount == 2,
            "players: a fifth player is refused when MaxPlayers is 4");

        // chat: everyone's line reaches everyone, with the sender's name
        p3.SendChat("from three");
        p2.SendChat("from two");
        host.SendChat("from host");
        ok = Pump(() => hostChat.Count >= 2 && p2Chat.Count >= 2 && p4Chat.Count >= 3, 5, host, p2, p3, p4);
        check(ok && hostChat.Contains("Third:from three") && p2Chat.Contains("Third:from three") && p2Chat.Contains("Host:from host")
              && p4Chat.Contains("Second:from two") && p4Chat.Contains("Third:from three") && p4Chat.Contains("Host:from host"),
            "players: chat lines are relayed to everyone with the sender's name");

        // game messages: host ↔ Player 2 exactly as before; a subordinate's own game messages are dropped at the source
        // (stage 2: subordinates watch)
        host.SendGame((byte)MsgType.GameBase, new byte[] { 7 });
        p3.SendGame((byte)MsgType.GameBase + 1, new byte[] { 1 });
        p2.SendGame((byte)MsgType.GameBase + 2, new byte[] { 2 });
        ok = Pump(() => p2Game.Count == 1 && hostGame.Count == 1, 5, host, p2, p3, p4);
        Pump(() => false, 0.3f, host, p2, p3, p4);
        check(ok && p3Game.Count == 0 && p4Game.Count == 0 && hostGame[0] == (byte)MsgType.GameBase + 2 && subGame.Count == 0
              && p3Dropped.Count == 1 && p3Dropped[0] == (byte)MsgType.GameBase + 1,
            "players: game messages stay host ↔ Player 2; a subordinate's own are dropped before sending (they watch)");

        // stage 3: a subordinate sends what the game allows (campaign orders); the host hears it with the sender's index
        p3.SubordinateMaySend = t => t == (byte)MsgType.GameBase + 8;
        p3.SendGame((byte)MsgType.GameBase + 8, new byte[] { 8 });
        p3.SendGame((byte)MsgType.GameBase + 9, new byte[] { 9 });
        var subTypes = new List<byte>();
        host.OnSubordinateGameMessage += (idx, type, p) => { if (idx == 2) subTypes.Add(type); };
        ok = Pump(() => subGame.Count == 1, 5, host, p2, p3, p4);
        Pump(() => false, 0.3f, host, p2, p3, p4);
        check(ok && subGame.Count == 1 && subGame[0] == 2 && subTypes.Count == 1 && subTypes[0] == (byte)MsgType.GameBase + 8
              && p3Dropped.Count == 2 && p3Dropped[1] == (byte)MsgType.GameBase + 9 && hostGame.Count == 1,
            "players: a subordinate's allowed messages reach the host with its player index; others are dropped");

        // stage 2 routing: to all subordinates, to one team, to one player; never to Player 2
        check(joined.Count == 2 && joined.Contains(2) && joined.Contains(3), "players: the host hears of each subordinate joining (index)");
        check(host.HasSubordinates() && host.HasSubordinates(0) && host.HasSubordinates(1) && host.SubordinateTeam(2) == 0
              && host.SubordinateTeam(3) == 1 && host.SubordinateTeam(1) == -1, "players: the host knows each subordinate's team");
        host.SendGameToSubordinates((byte)MsgType.GameBase + 3, new byte[] { 3 });
        host.SendGameToSubordinates((byte)MsgType.GameBase + 4, new byte[] { 4 }, team: 0);
        host.SendGameToSubordinates((byte)MsgType.GameBase + 5, new byte[] { 5 }, team: 1);
        host.SendGameToPlayer(3, (byte)MsgType.GameBase + 6, new byte[] { 6 });
        host.SendGameToSubordinates((byte)MsgType.Chat, new byte[] { 0 }); // below GameBase: refused
        ok = Pump(() => p3Game.Count == 2 && p4Game.Count == 3, 5, host, p2, p3, p4);
        Pump(() => false, 0.3f, host, p2, p3, p4);
        check(ok && p3Game[0] == (byte)MsgType.GameBase + 3 && p3Game[1] == (byte)MsgType.GameBase + 4
              && p4Game[0] == (byte)MsgType.GameBase + 3 && p4Game[1] == (byte)MsgType.GameBase + 5 && p4Game[2] == (byte)MsgType.GameBase + 6
              && p2Game.Count == 1 && p3Game.Count == 2 && p4Game.Count == 3,
            "players: host sends reach all subordinates, one team or one player, and never Player 2");

        // a subordinate leaves: the game goes on, the list shrinks
        p4.Stop("Left the session");
        ok = Pump(() => host.SubordinateCount == 1 && p2.Players.Count == 3, 5, host, p2, p3, p4);
        check(ok && host.IsConnected, "players: a subordinate leaving doesn't disturb host and Player 2");

        // Player 2 leaves: the host waits for Player 2 again, subordinates stay; the next joiner becomes Player 2
        p2.Stop("Left the session");
        ok = Pump(() => host.State == SessionState.Hosting && p3.Players.Count == 2, 5, host, p2, p3);
        check(ok && p3.IsConnected && host.SubordinateCount == 1, "players: when Player 2 leaves the subordinates stay connected");
        var p2b = Make("NewSecond");
        p2b.StartJoin("127.0.0.1", port);
        ok = Pump(() => host.IsConnected && p2b.IsConnected, 5, host, p2b, p3);
        check(ok && !p2b.IsSubordinate && p2b.LocalPlayerIndex == 1, "players: the next joiner takes the free Player 2 slot");

        host.Stop("Host closed the session");
        ok = Pump(() => p3.State == SessionState.Idle && p2b.State == SessionState.Idle, 5, host, p2b, p3);
        check(ok && p3Gone, "players: closing the session ends it for everyone (subordinates hear of it too)");
    }

    /// <summary>Additional players, stage 5: rejoins keep places, the kept Player 2 slot, moving and removing subordinates.</summary>
    private static void RejoinTests(Action<bool, string> check)
    {
        int port = FreePort();
        var host = Make("Host");
        host.MaxPlayers = 5;
        host.StartHost(port, true);
        var p2 = Make("Second");
        p2.StartJoin("127.0.0.1", port);
        bool ok = Pump(() => host.IsConnected && p2.IsConnected, 5, host, p2);
        var p3 = Make("Third");
        p3.JoinTeam = 1;
        p3.StartJoin("127.0.0.1", port);
        ok &= Pump(() => p3.IsConnected && host.SubordinateCount == 1, 5, host, p2, p3);
        check(ok && host.Player2SlotName == "Second" && p3.LocalTeam == 1, "rejoin: Player 2 and a subordinate on Player 2's team");

        // lobby: the host moves the subordinate to its own team; the subordinate's copy hears it
        int movedTo = -1;
        p3.LocalTeamChanged += t => movedTo = t;
        check(host.MoveSubordinate(2, 0), "rejoin: the host can move a subordinate");
        ok = Pump(() => p3.LocalTeam == 0, 5, host, p2, p3);
        check(ok && movedTo == 0 && host.SubordinateTeam(2) == 0 && p2.Players.Exists(x => x.Index == 2 && x.Team == 0),
            "rejoin: a moved subordinate's copy and everyone's list show the new team");

        // a campaign is open: Player 2 drops; a newcomer doesn't take the kept slot
        host.KeepSlots = true;
        p2.Stop("Left the session");
        ok = Pump(() => !host.IsConnected && host.Player2SlotReserved, 5, host, p2, p3);
        check(ok && host.Player2SlotName == "Second", "rejoin: during a campaign Player 2's slot is kept for Player 2");
        var newcomer = Make("Newcomer");
        newcomer.StartJoin("127.0.0.1", port);
        ok = Pump(() => newcomer.IsConnected, 5, host, p3, newcomer);
        check(ok && newcomer.IsSubordinate && !host.IsConnected, "rejoin: a newcomer joins as a subordinate while Player 2's slot is kept");

        // the subordinate drops and comes back asking for the other team: it keeps its team during the campaign
        p3.Stop("Left the session");
        Pump(() => host.SubordinateCount == 1, 5, host, p3, newcomer);
        var p3b = Make("Third");
        p3b.JoinTeam = 1;
        p3b.StartJoin("127.0.0.1", port);
        ok = Pump(() => p3b.IsConnected, 5, host, p3b, newcomer);
        check(ok && p3b.IsSubordinate && p3b.LocalTeam == 0, "rejoin: a returning subordinate gets its old team back");

        // Player 2 comes back (same name): its slot
        var p2b = Make("Second");
        p2b.JoinTeam = 0; // even asking for the host's team
        p2b.StartJoin("127.0.0.1", port);
        ok = Pump(() => host.IsConnected && p2b.IsConnected, 5, host, p2b, p3b, newcomer);
        check(ok && !p2b.IsSubordinate && p2b.LocalPlayerIndex == 1 && host.PeerName == "Second", "rejoin: Player 2 gets its kept slot back");

        // the host releases the slot after Player 2 leaves again: the next newcomer takes it
        p2b.Stop("Left the session");
        Pump(() => !host.IsConnected, 5, host, p2b, p3b, newcomer);
        host.ReleasePlayer2Slot();
        var other = Make("Other");
        other.StartJoin("127.0.0.1", port);
        ok = Pump(() => host.IsConnected && other.IsConnected, 5, host, other, p3b, newcomer);
        check(ok && !other.IsSubordinate && host.PeerName == "Other" && host.Player2SlotName == "Other",
            "rejoin: after the host releases Player 2's slot a new player takes it");

        // the host removes a subordinate: it is told why
        check(host.RemoveSubordinate(newcomer.LocalPlayerIndex), "rejoin: the host can remove a subordinate");
        ok = Pump(() => newcomer.State == SessionState.Idle, 5, host, other, p3b, newcomer);
        check(ok && newcomer.LastDisconnectReason.Contains("removed") && host.SubordinateCount == 1, "rejoin: a removed subordinate is told why");

        // after the campaign (lobby again): a known subordinate doesn't grab a free Player 2 slot
        host.KeepSlots = false;
        other.Stop("Left the session");
        Pump(() => !host.IsConnected, 5, host, other, p3b);
        p3b.Stop("Left the session");
        Pump(() => host.SubordinateCount == 0, 5, host, p3b);
        var p3c = Make("Third");
        p3c.StartJoin("127.0.0.1", port);
        ok = Pump(() => p3c.IsConnected, 5, host, p3c);
        check(ok && p3c.IsSubordinate && p3c.LocalTeam == 0, "rejoin: in the lobby a returning subordinate stays a subordinate on its team");

        host.Stop("Host closed the session");
        Pump(() => p3c.State == SessionState.Idle, 5, host, p3c);
        check(host.Player2SlotName == null && !host.KeepSlots, "rejoin: closing the session forgets the places");
    }

    /// <summary>Security review 2026-11-25: the protections in Connection / NetSession / MiniJson / SnapshotCodec.</summary>
    private static void SecurityTests(Action<bool, string> check)
    {
        // A connection that never says anything doesn't keep the real player out.
        int port = FreePort();
        var host = Make("Host");
        host.RoomPassword = 777;
        host.StartHost(port, true);
        var silent = new TcpClient();
        silent.Connect(IPAddress.Loopback, port);
        Pump(() => host.State == SessionState.Handshaking, 2, host);
        var friend = Make("Friend");
        friend.JoinPassword = 777;
        friend.StartJoin("127.0.0.1", port);
        bool ok = Pump(() => host.IsConnected && friend.IsConnected, 5, host, friend);
        check(ok && host.PeerName == "Friend", "security: a silent connection doesn't block the real player");
        host.Stop();
        Pump(() => friend.State == SessionState.Idle, 3, host, friend);
        try { silent.Close(); } catch { }

        // An oversized first frame (before the room code is checked) is refused at once.
        port = FreePort();
        host = Make("Host");
        host.RoomPassword = 777;
        host.StartHost(port, true);
        var big = new TcpClient();
        big.Connect(IPAddress.Loopback, port);
        big.GetStream().Write(BitConverter.GetBytes(10 * 1024 * 1024), 0, 4); // claims a 10 MB Hello
        ok = Pump(() => host.State == SessionState.Hosting && Closed(big), 5, host);
        check(ok, "security: an oversized handshake frame is refused and the room stays open");
        try { big.Close(); } catch { }

        // Anything but Hello before the handshake closes the connection.
        var chatty = new TcpClient();
        chatty.Connect(IPAddress.Loopback, port);
        byte[] chat = Protocol.Frame((byte)MsgType.GameBase, new byte[] { 1, 2, 3 });
        chatty.GetStream().Write(chat, 0, chat.Length);
        ok = Pump(() => host.State == SessionState.Hosting && Closed(chatty), 5, host);
        check(ok, "security: a game message before the handshake closes the connection");
        try { chatty.Close(); } catch { }

        // Wrong room codes: after 5 from one address it is locked out, even with the right code.
        host.ExemptLoopbackFromLockout = false;
        for (int i = 0; i < NetSession.LockoutAfterFailures; i++)
        {
            var guess = Make("Guess" + i);
            guess.JoinPassword = 1000 + i;
            guess.StartJoin("127.0.0.1", port);
            Pump(() => guess.State == SessionState.Idle, 5, host, guess);
        }
        var late = Make("Late");
        late.JoinPassword = 777;
        late.StartJoin("127.0.0.1", port);
        ok = Pump(() => late.State == SessionState.Idle, 12, host, late);
        check(ok && !host.IsConnected, "security: after " + NetSession.LockoutAfterFailures + " wrong codes the address is locked out");
        host.Stop();

        // Text from the other PC is cleaned.
        string cleaned = NetSession.CleanText("Bob<size=300>\n[Error] fake\u0007", 24);
        check(cleaned.IndexOf('<') < 0 && cleaned.IndexOf('\n') < 0 && cleaned.IndexOf('\u0007') < 0 && cleaned.Length <= 24,
            "security: names/chat lose markup and control characters (" + cleaned + ")");

        // Deeply nested JSON is refused instead of overflowing the stack.
        string deep = new string('[', 5000) + new string(']', 5000);
        bool threw = false;
        try { WotSMultiplayer.Core.MiniJson.Parse(deep); } catch (FormatException) { threw = true; }
        check(threw, "security: JSON nested 5000 deep is refused");

        // A small message that unpacks to more than the limit ("zip bomb") is refused.
        byte[] bomb = WotSMultiplayer.Core.SnapshotCodec.Pack(new string(' ', WotSMultiplayer.Core.SnapshotCodec.MaxUnpackedBytes + 1024));
        threw = false;
        try { WotSMultiplayer.Core.SnapshotCodec.Unpack(bomb); } catch (ArgumentException) { threw = true; }
        check(threw && bomb.Length < 1024 * 1024, "security: a " + bomb.Length / 1024 + " KB message that unpacks to 32 MB+ is refused");
    }

    /// <summary>The other side closed this raw socket (a read returns 0 or fails).</summary>
    private static bool Closed(TcpClient c)
    {
        try
        {
            if (!c.Client.Poll(0, SelectMode.SelectRead)) return false;
            byte[] b = new byte[1024];
            return c.Client.Receive(b) == 0;
        }
        catch { return true; }
    }
}
