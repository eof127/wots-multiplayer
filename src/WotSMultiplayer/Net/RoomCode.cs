using System;
using System.Text;

namespace WotSMultiplayer.Net
{
    /// <summary>
    /// D46 room codes: the host's IPv4 address + port + a 20-bit password + a 12-bit checksum = 80 bits, written as 16
    /// Crockford base32 characters in groups of four (e.g. "K7Q2-M9XA-3FD1-W8PE"). There is no server: the joiner decodes the
    /// address from the code and connects directly; the password goes into the Hello handshake, so only someone who has the code
    /// gets in. The address/port bytes are mixed with a keystream from the password so codes don't visibly repeat the IP (not
    /// security — anyone with the code can decode it). Pure code: also compiled into the logic tests.
    /// </summary>
    internal static class RoomCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32: no I, L, O, U
        public const int MaxPassword = (1 << 20) - 1;
        public const int CodeChars = 16;

        public static string Encode(byte[] ipv4, int port, int password)
        {
            if (ipv4 == null || ipv4.Length != 4) throw new ArgumentException("an IPv4 address (4 bytes) is needed");
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (password < 0 || password > MaxPassword) throw new ArgumentOutOfRangeException(nameof(password));

            var b = new byte[10];
            Array.Copy(ipv4, 0, b, 0, 4);
            b[4] = (byte)(port >> 8);
            b[5] = (byte)port;
            uint tail = ((uint)password << 12) | (uint)(Checksum(ipv4, port, password) & 0xFFF);
            b[6] = (byte)(tail >> 24); b[7] = (byte)(tail >> 16); b[8] = (byte)(tail >> 8); b[9] = (byte)tail;
            Mix(b, password);

            var sb = new StringBuilder(CodeChars + 3);
            for (int group = 0; group < 2; group++) // 2 × 5 bytes = 2 × 8 characters
            {
                ulong v = 0;
                for (int i = 0; i < 5; i++) v = (v << 8) | b[group * 5 + i];
                for (int i = 7; i >= 0; i--) sb.Append(Alphabet[(int)((v >> (i * 5)) & 31)]);
            }
            string s = sb.ToString();
            return s.Substring(0, 4) + "-" + s.Substring(4, 4) + "-" + s.Substring(8, 4) + "-" + s.Substring(12, 4);
        }

        /// <summary>Lenient: case, spaces and dashes don't matter; O reads as 0, I/L as 1, U as V.</summary>
        public static bool TryDecode(string code, out byte[] ipv4, out int port, out int password, out string error)
        {
            ipv4 = null; port = 0; password = 0; error = null;
            var chars = new StringBuilder(CodeChars);
            foreach (char raw in code ?? "")
            {
                char c = char.ToUpperInvariant(raw);
                if (c == '-' || c == ' ' || c == '\t') continue;
                if (c == 'O') c = '0';
                else if (c == 'I' || c == 'L') c = '1';
                else if (c == 'U') c = 'V';
                if (Alphabet.IndexOf(c) < 0) { error = $"'{raw}' can't be part of a room code."; return false; }
                chars.Append(c);
            }
            if (chars.Length != CodeChars) { error = $"A room code has {CodeChars} letters/digits (you typed {chars.Length})."; return false; }

            var b = new byte[10];
            for (int group = 0; group < 2; group++)
            {
                ulong v = 0;
                for (int i = 0; i < 8; i++) v = (v << 5) | (uint)Alphabet.IndexOf(chars[group * 8 + i]);
                for (int i = 0; i < 5; i++) b[group * 5 + i] = (byte)(v >> ((4 - i) * 8));
            }
            uint tail = ((uint)b[6] << 24) | ((uint)b[7] << 16) | ((uint)b[8] << 8) | b[9]; // not mixed
            int pw = (int)(tail >> 12);
            Mix(b, pw); // undo (XOR) for the address and port bytes
            var ip = new[] { b[0], b[1], b[2], b[3] };
            int p = (b[4] << 8) | b[5];
            if ((tail & 0xFFF) != (uint)(Checksum(ip, p, pw) & 0xFFF)) { error = "That room code isn't valid (typo?). Check it with the host."; return false; }
            if (p == 0) { error = "That room code isn't valid (port 0)."; return false; }
            ipv4 = ip; port = p; password = pw;
            return true;
        }

        /// <summary>A new random password for a room.</summary>
        public static int NewPassword(Random rng) => rng.Next(0, MaxPassword + 1);

        private static int Checksum(byte[] ip, int port, int password)
        {
            uint h = 2166136261; // FNV-1a over the 9 data bytes
            foreach (byte x in new[] { ip[0], ip[1], ip[2], ip[3], (byte)(port >> 8), (byte)port,
                                       (byte)(password >> 16), (byte)(password >> 8), (byte)password })
            {
                h ^= x;
                h *= 16777619;
            }
            return (int)(h ^ (h >> 12) ^ (h >> 24));
        }

        /// <summary>XOR the six address/port bytes with a keystream derived from the password (its own inverse).</summary>
        private static void Mix(byte[] b, int password)
        {
            uint s = (uint)password * 2654435761u ^ 0x9E3779B9u;
            for (int i = 0; i < 6; i++)
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5; // xorshift32
                b[i] ^= (byte)s;
            }
        }
    }
}
