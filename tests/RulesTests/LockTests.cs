using System;
using System.Collections.Generic;
using System.IO;
using WotSMultiplayer.Features.MpCampaign;

/// <summary>Additional players, stage 3: the team locks (TeamLockState) and which forces an order touches (LockRules).</summary>
internal static class LockTests
{
    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            write(w);
            w.Flush();
            return ms.ToArray();
        }
    }

    public static void Run(Action<bool, string> check)
    {
        // the commander's selection and locks
        var t = new TeamLockState();
        check(t.Blocked(5) == BlockReason.None && t.Blocked(-1) == BlockReason.None, "locks: nothing blocked at first");
        t.Selected = 5;
        check(t.Blocked(5) == BlockReason.CommanderSelected && t.Blocked(6) == BlockReason.None, "locks: the commander's selection is blocked");
        check(t.Toggle(6) && t.Blocked(6) == BlockReason.Locked, "locks: K locks a force");
        check(!t.Toggle(6) && t.Blocked(6) == BlockReason.None, "locks: K again unlocks it");
        check(!t.Toggle(-1) && t.Locked.Count == 0, "locks: no force selected = nothing locked");
        t.Toggle(6);
        t.Selected = 6;
        check(t.Blocked(6) == BlockReason.CommanderSelected, "locks: selected and locked reads as selected");

        // gone forces are forgotten
        t.Toggle(7);
        t.Selected = 8;
        bool changed = t.Prune(id => id == 7);
        check(changed && t.Selected == -1 && !t.Locked.Contains(6) && t.Locked.Contains(7), "locks: sunk or merged forces are dropped");
        check(!t.Prune(id => id == 7), "locks: nothing to drop = no change");

        // a full lock list refuses more
        var full = new TeamLockState();
        for (int i = 0; i < TeamLockState.MaxLocks; i++) full.Toggle(i);
        check(!full.Toggle(9999) && full.Locked.Count == TeamLockState.MaxLocks, "locks: at most 256 locks");

        // wire round trip and malformed input
        var a = new TeamLockState { Selected = 3 };
        a.Toggle(10); a.Toggle(11);
        byte[] wire = Bytes(a.Write);
        var b = new TeamLockState();
        b.Toggle(99);
        using (var r = new BinaryReader(new MemoryStream(wire))) b.Read(r);
        check(b.Selected == 3 && b.Locked.Count == 2 && b.Locked.Contains(10) && b.Locked.Contains(11) && !b.Locked.Contains(99),
            "locks: the state survives the wire and replaces the old one");
        bool refused = false;
        try { using (var r = new BinaryReader(new MemoryStream(Bytes(w => { w.Write(-1); w.Write(100000); })))) b.Read(r); }
        catch (FormatException) { refused = true; }
        check(refused && b.Locked.Count == 2, "locks: a huge lock count from the wire is refused (state kept)");

        // which forces an order touches
        check(LockRules.IsOrder(28) && LockRules.IsOrder(35) && !LockRules.IsOrder(27) && !LockRules.IsOrder(36) && !LockRules.IsOrder(24),
            "locks: only campaign orders (28-35) count as orders");
        List<int> ids = LockRules.OrderTargets(29, Bytes(w => { w.Write(42); w.Write(0.1f); w.Write(0.2f); w.Write(false); }));
        check(ids.Count == 1 && ids[0] == 42, "locks: a course order touches its force");
        check(LockRules.OrderTargets(28, Bytes(w => { w.Write("port1"); w.Write("{}"); w.Write(10); w.Write("TF"); })).Count == 0,
            "locks: forming a task force at a port touches no force");
        check(LockRules.OrderTargets(32, Bytes(w => { w.Write((byte)0); w.Write("base"); })).Count == 0,
            "locks: a launch from a base touches no force");
        ids = LockRules.OrderTargets(32, Bytes(w => { w.Write((byte)1); w.Write(17); w.Write(0); w.Write("plane"); }));
        check(ids.Count == 1 && ids[0] == 17, "locks: a launch from a carrier touches the carrier's task force");
        ids = LockRules.OrderTargets(34, Bytes(w => { w.Write(5); w.Write((byte)FleetCmd.Merge); w.Write(9); w.Write(1); w.Write(0); }));
        check(ids.Count == 2 && ids[0] == 5 && ids[1] == 9, "locks: a merge touches both task forces");
        ids = LockRules.OrderTargets(34, Bytes(w => { w.Write(5); w.Write((byte)FleetCmd.Rename); w.Write("X"); }));
        check(ids.Count == 1 && ids[0] == 5, "locks: other fleet orders touch their task force only");
        check(LockRules.OrderTargets(33, new byte[] { 1, 2 }).Count == 0 && LockRules.OrderTargets(33, null).Count == 0,
            "locks: a truncated order yields no target (and no crash)");

        // battles (stage 4): unit ids, the order head, commander-only commands
        byte k; int ix; byte c;
        int sid = LockRules.BattleUnitId(0, 17), aid = LockRules.BattleUnitId(1, 17);
        LockRules.SplitBattleUnitId(aid, out k, out ix);
        check(sid != aid && k == 1 && ix == 17, "locks: a sea and an air unit with the same index get different ids");
        bool head = LockRules.ReadBattleOrderHead(Bytes(w => { w.Write((byte)1); w.Write(70000); w.Write((byte)7); w.Write(3); }), out k, out ix, out c);
        check(head && k == 1 && ix == 70000 && c == 7, "locks: a battle order's unit and command are read from its head");
        check(!LockRules.ReadBattleOrderHead(new byte[] { 0, 1, 0 }, out k, out ix, out c) && !LockRules.ReadBattleOrderHead(null, out k, out ix, out c),
            "locks: a too-short battle order is not read");
        check(!LockRules.ReadBattleOrderHead(Bytes(w => { w.Write((byte)5); w.Write(1); w.Write((byte)0); }), out k, out ix, out c),
            "locks: an unknown unit kind is not read");
        check(LockRules.IsCommanderOnlyBattleCmd(24) && LockRules.IsCommanderOnlyBattleCmd(25) && LockRules.IsCommanderOnlyBattleCmd(26)
              && !LockRules.IsCommanderOnlyBattleCmd(0) && !LockRules.IsCommanderOnlyBattleCmd(22) && !LockRules.IsCommanderOnlyBattleCmd(27),
            "locks: disengage, retreat and allow-retreat are the commanders' only");
        var bt = new TeamLockState { Selected = sid };
        bt.Toggle(aid);
        check(bt.Blocked(sid) == BlockReason.CommanderSelected && bt.Blocked(aid) == BlockReason.Locked && bt.Blocked(LockRules.BattleUnitId(0, 18)) == BlockReason.None,
            "locks: battle units are blocked by selection or lock like task forces");

        var team = new TeamLockState { Selected = 9 };
        int blocked;
        check(LockRules.FirstBlocked(team, new List<int> { 5, 9 }, out blocked) == BlockReason.CommanderSelected && blocked == 9,
            "locks: merging into the commander's selected force is refused");
        check(LockRules.FirstBlocked(team, new List<int> { 5 }, out blocked) == BlockReason.None && blocked == -1,
            "locks: an order on a free force runs");
    }
}
