using System;
using System.IO;
using System.Linq;
using WotSMultiplayer.Core;

/// <summary>Side-swap tests against real campaign saves (the session-1 backup) and the game's setup files.</summary>
internal static class SwapTests
{
    private static string FindModRoot()
    {
        DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && d.Name != "_MultiplayerMod") d = d.Parent;
        return d?.FullName;
    }

    private static JObject Obj(string json) => MiniJson.ParseObject(json);
    private static string W(JNode n) => n == null ? "null" : MiniJson.Write(n);

    public static void Run(Action<bool, string> check)
    {
        string mod = FindModRoot();
        string saves = mod == null ? null : Path.Combine(mod, "save_backup_2026-11-23", "campaign");
        string campaigns = mod == null ? null : Path.Combine(Path.GetDirectoryName(mod), "WarOnTheSea_Data", "StreamingAssets", "default", "campaign");
        if (saves == null || !Directory.Exists(saves)) { check(false, "swap: save backup folder not found (" + saves + ")"); return; }
        string setup001 = File.ReadAllLines(Path.Combine(campaigns, "campaign001", "setup.txt"))[0];

        foreach (string file in Directory.GetFiles(saves, "*.txt"))
        {
            string name = Path.GetFileName(file);
            string save = File.ReadAllLines(file)[0];
            JObject orig = Obj(save);

            // Exact round trip, outer and nested.
            check(MiniJson.Write(orig) == save, $"swap[{name}]: JSON round trip is byte-exact");
            bool nestedOk = true;
            foreach (string key in new[] { "mapLocationSaveData", "playerMobileObjectSaveData", "enemyMobileObjectSaveData" })
                foreach (JNode n in ((JArray)orig.Get(key)).Items)
                {
                    string s = ((JString)n).Value;
                    if (MiniJson.Write(MiniJson.Parse(s)) != s) nestedOk = false;
                }
            check(nestedOk, $"swap[{name}]: nested location/mobile JSON round trip is byte-exact");

            // One swap (these saves are campaign002 = Japan human; the mirror is campaign001).
            string swapped = SideSwap.Swap(save, "campaign001", setup001);
            JObject s1 = Obj(swapped);
            check(((JString)s1.Get("campaignID")).Value == "campaign001", $"swap[{name}]: campaignID → mirror");
            check(W(s1.Get("nations0")) == W(orig.Get("nations1")) && W(s1.Get("nations1")) == W(orig.Get("nations0")), $"swap[{name}]: nations swapped");

            JArray cpO = (JArray)orig.Get("commandPoints"), cpS = (JArray)s1.Get("commandPoints");
            check(W(cpS.Items[0]) == W(cpO.Items[1]) && W(cpS.Items[1]) == W(cpO.Items[0]), $"swap[{name}]: commandPoints [0]↔[1]");

            int pO = ((JArray)orig.Get("playerMobileObjectSaveData")).Items.Count, eO = ((JArray)orig.Get("enemyMobileObjectSaveData")).Items.Count;
            int pS = ((JArray)s1.Get("playerMobileObjectSaveData")).Items.Count, eS = ((JArray)s1.Get("enemyMobileObjectSaveData")).Items.Count;
            check(pS == eO && eS == pO, $"swap[{name}]: mobile lists exchanged (player {pO}→{pS}, enemy {eO}→{eS})");

            var fP = SideSwap.CountFactions(swapped, "playerMobileObjectSaveData");
            var fE = SideSwap.CountFactions(swapped, "enemyMobileObjectSaveData");
            check(fP.Keys.All(k => k == 0) && fE.Keys.All(k => k != 0), $"swap[{name}]: player list all faction 0, enemy list none faction 0");

            var locO = SideSwap.CountFactions(save, "mapLocationSaveData");
            var locS = SideSwap.CountFactions(swapped, "mapLocationSaveData");
            int c0 = locO.ContainsKey(0) ? locO[0] : 0, c1 = locO.ContainsKey(1) ? locO[1] : 0;
            int s0 = locS.ContainsKey(0) ? locS[0] : 0, s1c = locS.ContainsKey(1) ? locS[1] : 0;
            check(s0 == c1 && s1c == c0, $"swap[{name}]: location ownership flipped ({c0}/{c1} → {s0}/{s1c})");

            JObject l0 = Obj(((JString)((JArray)orig.Get("mapLocationSaveData")).Items[0]).Value);
            JObject l0s = Obj(((JString)((JArray)s1.Get("mapLocationSaveData")).Items[0]).Value);
            JArray tO = (JArray)l0.Get("troops"), tS = (JArray)l0s.Get("troops");
            check(W(tS.Items[0]) == W(tO.Items[1]) && W(tS.Items[1]) == W(tO.Items[0]), $"swap[{name}]: location troops [0]↔[1]");

            check(W(s1.Get("mustControl")) == W(Obj(setup001).Get("mustControl")), $"swap[{name}]: objectives taken from mirror setup");
            check(((JArray)s1.Get("mapMarkersSaveData")).Items.Count == 0, $"swap[{name}]: host map markers not leaked");

            JArray pr = (JArray)s1.Get("campDifficultyPreset0"), po = (JArray)orig.Get("campDifficultyPreset0");
            check(W(pr.Items[0]) == W(po.Items[1]) && W(pr.Items[1]) == W(po.Items[0]), $"swap[{name}]: difficulty pairs swapped");

            // Snapshot IDs survive the swap and follow their mobiles into the other list.
            var pIds = Enumerable.Range(1, pO).ToList();
            var eIds = Enumerable.Range(1001, eO).ToList();
            string withIds = SnapshotCodec.InjectIds(save, pIds, eIds);
            JObject sw = Obj(SideSwap.Swap(withIds, "campaign001", setup001));
            var idsInClientEnemy = ((JArray)sw.Get("enemyMobileObjectSaveData")).Items.Select(n => SnapshotCodec.GetId(Obj(((JString)n).Value))).ToList();
            var idsInClientPlayer = ((JArray)sw.Get("playerMobileObjectSaveData")).Items.Select(n => SnapshotCodec.GetId(Obj(((JString)n).Value))).ToList();
            check(idsInClientEnemy.SequenceEqual(pIds) && idsInClientPlayer.SequenceEqual(eIds), $"swap[{name}]: mobile IDs survive the swap (host player → client enemy)");

            // Object references ({"instanceID":N}) are stripped before applying on another machine.
            JObject loc = Obj(((JString)((JArray)orig.Get("mapLocationSaveData")).Items[0]).Value);
            bool hadRef = MiniJson.Write(loc).Contains("instanceID");
            SnapshotCodec.StripObjectRefs(loc);
            check(!MiniJson.Write(loc).Contains("instanceID") && loc.Has("locationID"), $"swap[{name}]: object refs stripped (had refs: {hadRef})");

            // Compression round trip + uncompressed fallback.
            byte[] packed = SnapshotCodec.Pack(save);
            check(SnapshotCodec.Unpack(packed) == save && packed[0] == 1, $"swap[{name}]: gzip round trip ({save.Length / 1024} KB → {packed.Length / 1024} KB)");
            check(SnapshotCodec.Unpack(SnapshotCodec.Pack(save, tryCompress: false)) == save, $"swap[{name}]: uncompressed fallback round trip");

            // Swap back: everything that was mirrored returns exactly.
            JObject s2 = Obj(SideSwap.Swap(swapped, "campaign002", null));
            bool back = W(s2.Get("nations0")) == W(orig.Get("nations0")) && W(s2.Get("commandPoints")) == W(orig.Get("commandPoints"))
                        && W(s2.Get("mapLocationSaveData")) == W(orig.Get("mapLocationSaveData"))
                        && W(s2.Get("factionSunk")) == W(orig.Get("factionSunk"))
                        && W(s2.Get("replenish0")) == W(orig.Get("replenish0"))
                        && ((JArray)s2.Get("playerMobileObjectSaveData")).Items.Count == pO;
            check(back, $"swap[{name}]: double swap restores mirrored fields");
        }
    }
}
