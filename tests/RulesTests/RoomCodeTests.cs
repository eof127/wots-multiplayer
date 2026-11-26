using System;
using WotSMultiplayer.Net;

/// <summary>Tests for the D46 room codes (address + port + password in one short code).</summary>
internal static class RoomCodeTests
{
    private static bool RoundTrip(byte[] ip, int port, int pw)
    {
        string code = RoomCode.Encode(ip, port, pw);
        byte[] ip2; int port2, pw2; string err;
        return RoomCode.TryDecode(code, out ip2, out port2, out pw2, out err) &&
               ip2[0] == ip[0] && ip2[1] == ip[1] && ip2[2] == ip[2] && ip2[3] == ip[3] && port2 == port && pw2 == pw;
    }

    public static void Run(Action<bool, string> check)
    {
        byte[] home = { 203, 0, 113, 45 };
        string code = RoomCode.Encode(home, 7777, 123456);
        check(code.Length == 19 && code[4] == '-' && code[9] == '-' && code[14] == '-', "room code: 16 characters in groups of four (" + code + ")");
        check(RoundTrip(home, 7777, 123456), "room code: round trip (internet address)");
        check(RoundTrip(new byte[] { 192, 168, 1, 20 }, 7777, 0), "room code: round trip (LAN, password 0)");
        check(RoundTrip(new byte[] { 100, 64, 0, 1 }, 65535, RoomCode.MaxPassword), "room code: round trip (VPN, max port and password)");
        check(RoundTrip(new byte[] { 127, 0, 0, 1 }, 1, 42), "room code: round trip (loopback, port 1)");

        var rng = new Random(7);
        bool allOk = true;
        for (int i = 0; i < 2000; i++)
        {
            var ip = new byte[4];
            rng.NextBytes(ip);
            if (!RoundTrip(ip, rng.Next(1, 65536), RoomCode.NewPassword(rng))) allOk = false;
        }
        check(allOk, "room code: 2000 random round trips");

        byte[] d; int p, w; string err;
        check(RoomCode.TryDecode(code.ToLowerInvariant().Replace("-", " "), out d, out p, out w, out err) && w == 123456,
              "room code: lower case and spaces accepted");
        string confusable = code.Replace('0', 'O').Replace('1', 'I');
        check(RoomCode.TryDecode(confusable, out d, out p, out w, out err) && w == 123456, "room code: O/0 and I/1 read the same");
        check(!RoomCode.TryDecode(code.Substring(0, 18), out d, out p, out w, out err) && err.Contains("16"), "room code: too short is refused");
        check(!RoomCode.TryDecode(code + "!", out d, out p, out w, out err) && err.Contains("!"), "room code: invalid character is refused");

        // a single wrong character is almost always caught by the 12-bit checksum
        int caught = 0, tries = 0;
        string clean = code.Replace("-", "");
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        for (int pos = 0; pos < clean.Length; pos++)
            foreach (char c in alphabet)
            {
                if (c == clean[pos]) continue;
                tries++;
                string typo = clean.Substring(0, pos) + c + clean.Substring(pos + 1);
                if (!RoomCode.TryDecode(typo, out d, out p, out w, out err)) caught++;
            }
        check(caught >= tries - 2, $"room code: typos caught ({caught}/{tries})");

        string other = RoomCode.Encode(home, 7777, 654321);
        check(other.Substring(0, 9) != code.Substring(0, 9), "room code: same address, new password → different-looking code");
    }
}
