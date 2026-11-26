using System;
using System.IO;
using System.Text;

namespace WotSMultiplayer.Net
{
    /// <summary>
    /// Wire format: every frame is [int32 length][byte type][payload], length = 1 + payload length, little-endian.
    /// Payloads are written with BinaryWriter (strings = length-prefixed UTF-8).
    /// No Unity references: this file is also compiled into the test tools.
    /// </summary>
    internal enum MsgType : byte
    {
        Hello = 1,      // client → host: handshake request
        Welcome = 2,    // host → client: handshake result
        Ping = 3,
        Pong = 4,
        Chat = 5,
        Bye = 6,        // graceful disconnect with reason
        Players = 7,    // host → everyone: the player list (additional players, protocol 20)
        // 20+ reserved for game sync (speed votes, campaign state, commands...)
        GameBase = 20,
    }

    internal static class Protocol
    {
        /// <summary>Bump when the wire format or message semantics change. Must match exactly on both sides.</summary>
        public const int Version = 23; // 23: additional players stage 4 (battle messages to subordinates by team, subordinate battle orders 44, 58 BattleSelect, 59 BattleToggle, 60 BattleLocks); 22: additional players stage 3 (subordinates send orders 28-35; 55 Select, 56 Toggle, 57 TeamLocks); 21: additional players stage 2 (39 subordinate campaign state; host sends 25/26/27/36/38 to subordinates; subordinates send no game messages); 20: additional players stage 1 (Hello team, Welcome team/role, 7 Players, chat sender); 19: 53 carries per-mount firing state, notice 27 may carry a vanilla message key (sound), AirCmd 5/6 + FleetCmd 7 (scout patterns, repeat course); 18: FleetCmd.Rearm (P2 resupply), first frame ≤ 16 KB (security); 17: 54 CombatOver (action report + command points for P2), BattleCmd.Classify; 16: 52 carries both sides' quiet time + ready/ended flags, BattleCmd 26 (D48); 15: snapshot air units carry damage smoke trails; 14: snapshot sea units carry smoke + close-AA target, 52 carries the host's disengage, 53 ammo; 13: 50 battle environment, 51 control feedback, 52 disengage + BattleCmd 24/25 (D47); 12: Hello carries the room password (D46);11: control state is a ushort (sub periscope + scope view);10: snapshot carries wanted speed/height, BattleCmd.Height, State without height;2: messages 32-35 (P2 orders); 3: 36-38 (encounters); 4: 40-43 (battle); 5: 44-45 (battle orders); 6: 46 shots + weapon orders; 7: 47 turrets, 48 torpedoes + battle controls; 8: battle snapshot carries actual speed; 9: 49 damage control + DC orders

        /// <summary>Largest frame after the handshake (the biggest real ones, campaign state and battle start, are well under 1 MB packed).</summary>
        public const int MaxFrameBytes = 16 * 1024 * 1024;
        /// <summary>Largest first frame on a connection (Hello / Welcome are a few hundred bytes).</summary>
        public const int MaxHandshakeFrameBytes = 16 * 1024;
        public const int DefaultPort = 7777;

        internal static byte[] Frame(byte type, byte[] payload)
        {
            int payloadLen = payload != null ? payload.Length : 0;
            byte[] frame = new byte[4 + 1 + payloadLen];
            byte[] len = BitConverter.GetBytes(1 + payloadLen);
            Buffer.BlockCopy(len, 0, frame, 0, 4);
            frame[4] = type;
            if (payloadLen > 0) Buffer.BlockCopy(payload, 0, frame, 5, payloadLen);
            return frame;
        }

        internal static byte[] Build(Action<BinaryWriter> write)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8))
            {
                write(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        internal static BinaryReader Reader(byte[] payload) =>
            new BinaryReader(new MemoryStream(payload ?? new byte[0]), Encoding.UTF8);
    }

    internal sealed class HelloMsg
    {
        public int ProtocolVersion;
        public string ModVersion;
        public string GameHash;
        public string PlayerName;
        public int RoomPassword = -1; // protocol 12: the password from a room code (D46); -1 = joined by address
        public int WantedTeam = -1;   // protocol 20: additional players — -1 auto, 0 host's team, 1 Player 2's team

        public byte[] Encode() => Protocol.Build(w =>
        {
            w.Write(ProtocolVersion); w.Write(ModVersion ?? ""); w.Write(GameHash ?? ""); w.Write(PlayerName ?? "");
            w.Write(RoomPassword);
            w.Write(WantedTeam);
        });

        public static HelloMsg Decode(byte[] p)
        {
            using (BinaryReader r = Protocol.Reader(p))
            {
                var h = new HelloMsg { ProtocolVersion = r.ReadInt32(), ModVersion = r.ReadString(), GameHash = r.ReadString(), PlayerName = r.ReadString() };
                // older clients end here: they still get the clear "protocol mismatch" answer instead of a decode error
                if (r.BaseStream.Position + 4 <= r.BaseStream.Length) h.RoomPassword = r.ReadInt32();
                if (r.BaseStream.Position + 4 <= r.BaseStream.Length) h.WantedTeam = r.ReadInt32();
                return h;
            }
        }
    }

    internal sealed class WelcomeMsg
    {
        public bool Accepted;
        public string Reason;
        public string HostName;
        public int YourPlayerIndex;
        public int Team = 1;                                // protocol 20
        public PlayerRole Role = PlayerRole.Commander;      // protocol 20

        public byte[] Encode() => Protocol.Build(w =>
        {
            w.Write(Accepted); w.Write(Reason ?? ""); w.Write(HostName ?? ""); w.Write(YourPlayerIndex);
            w.Write((byte)Team); w.Write((byte)Role);
        });

        public static WelcomeMsg Decode(byte[] p)
        {
            using (BinaryReader r = Protocol.Reader(p))
            {
                var m = new WelcomeMsg { Accepted = r.ReadBoolean(), Reason = r.ReadString(), HostName = r.ReadString(), YourPlayerIndex = r.ReadInt32() };
                if (r.BaseStream.Position + 2 <= r.BaseStream.Length) { m.Team = r.ReadByte(); m.Role = (PlayerRole)r.ReadByte(); }
                return m;
            }
        }
    }
}
