using System;
using System.Collections.Generic;
using System.IO;
using WotSMultiplayer.Features.MpCampaign;

/// <summary>Tests for the game-independent parts of Player 2's remote orders (OrderRules).</summary>
internal static class OrderTests
{
    public static void Run(Action<bool, string> check)
    {
        // launch counts: never more than asked, available or the per-group cap
        check(OrderRules.LaunchCount(12, 30, 12) == 12, "orders: launch capped by per-group limit");
        check(OrderRules.LaunchCount(20, 8, 12) == 8, "orders: launch capped by aircraft available");
        check(OrderRules.LaunchCount(5, 30, 0) == 5, "orders: per-group cap 0 = no cap");
        check(OrderRules.LaunchCount(4, -3, 12) == 0, "orders: negative availability launches nothing");

        // payload: vanilla's last dropdown entry = no payload; "a|b" index strings
        var idx = new List<string> { "0", "1|2", "3" };
        check(OrderRules.Payload(1, idx, 4).Count == 2 && OrderRules.Payload(1, idx, 4)[1] == 2, "orders: payload index string parsed");
        check(OrderRules.Payload(3, idx, 4).Count == 0, "orders: last entry = no payload");
        check(OrderRules.Payload(2, idx, 3).Count == 0, "orders: last entry = no payload (short list)");
        check(OrderRules.Payload(9, idx, 4).Count == 0 && OrderRules.Payload(-1, idx, 4).Count == 0, "orders: out-of-range payload = none");

        // ship indexes from the wire
        List<int> clean = OrderRules.CleanIndexes(new[] { 2, 0, 2, 7, -1, 1 }, 3);
        check(clean.Count == 3 && clean[0] == 2 && clean[1] == 1 && clean[2] == 0, "orders: indexes de-duplicated, in range, highest first");
        check(OrderRules.CleanIndexes(null, 3).Count == 0, "orders: null index list is empty");

        // split / merge rules
        check(OrderRules.CanSplit(1, 3) && OrderRules.CanSplit(2, 3), "orders: split some ships");
        check(!OrderRules.CanSplit(0, 3) && !OrderRules.CanSplit(3, 3), "orders: split needs some but not all ships");
        check(OrderRules.MergeFits(6, 3, 8) == 2, "orders: merge stops at the task-force size limit");
        check(OrderRules.MergeFits(8, 3, 8) == 0 && OrderRules.MergeFits(9, 3, 8) == 0, "orders: full target takes nothing");
        check(OrderRules.MergeFits(1, 3, 8) == 3, "orders: merge moves all when they fit");

        // swap
        var l = new List<string> { "a", "b", "c" };
        OrderRules.SwapAt(l, 0, 2);
        check(l[0] == "c" && l[2] == "a" && l[1] == "b", "orders: swap two ships");
        OrderRules.SwapAt(l, 0, 5);
        OrderRules.SwapAt<string>(null, 0, 1);
        check(l[0] == "c", "orders: swap out of range is a no-op");

        // wire round trip and limits
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            OrderRules.WriteIndexes(w, new List<int> { 4, 1, 3 });
            w.Flush();
            bytes = ms.ToArray();
        }
        using (var r = new BinaryReader(new MemoryStream(bytes)))
        {
            List<int> back = OrderRules.ReadIndexes(r);
            check(back.Count == 3 && back[0] == 4 && back[2] == 3, "orders: index list round trip");
        }
        bool threw = false;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(1000000);
            w.Flush();
            try { OrderRules.ReadIndexes(new BinaryReader(new MemoryStream(ms.ToArray()))); }
            catch (ArgumentException) { threw = true; }
        }
        check(threw, "orders: oversized index list rejected");

        // names
        check(OrderRules.CleanName("  Kido Butai\n ") == "Kido Butai", "orders: name trimmed, control chars removed");
        check(OrderRules.CleanName(new string('x', 100)).Length == 40, "orders: name capped at 40 chars");
        check(OrderRules.CleanName(null) == "", "orders: null name is empty");
    }
}
