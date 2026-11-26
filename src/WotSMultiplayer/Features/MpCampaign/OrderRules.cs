using System;
using System.Collections.Generic;
using System.IO;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>Player 2's air orders (message 33). Values are on the wire: append only.</summary>
    internal enum AirCmd : byte { ReturnToBase = 0, Land = 1, CapOn = 2, CapOff = 3, StrategicBomb = 4, ScoutPattern = 5, Repeat = 6 }

    /// <summary>Player 2's task-force orders (message 34). Values are on the wire: append only.</summary>
    internal enum FleetCmd : byte { Split = 0, Swap = 1, Merge = 2, Formation = 3, Rename = 4, Bombard = 5, Rearm = 6, Repeat = 7 }

    /// <summary>Player 2's cargo orders (message 35). Values are on the wire: append only.</summary>
    internal enum CargoCmd : byte { Transfer = 0, UnloadAll = 1 }

    /// <summary>
    /// Game-independent helpers for the remote orders (no Unity references, unit-tested in tests/RulesTests).
    /// The host never trusts the client's numbers: everything is clamped/validated again here.
    /// </summary>
    internal static class OrderRules
    {
        /// <summary>Largest ship-index list accepted from the wire (a task force holds far fewer ships).</summary>
        public const int MaxIndexes = 64;

        /// <summary>Planes actually launched: what was asked for, limited by what is available and the per-group cap.</summary>
        public static int LaunchCount(int requested, int available, int maxPerGroup)
        {
            int n = Math.Min(requested, available);
            if (maxPerGroup > 0) n = Math.Min(n, maxPerGroup);
            return Math.Max(0, n);
        }

        /// <summary>
        /// Payload list for a launch, like the vanilla launch panel: the last dropdown entry means "no payload";
        /// otherwise the entry's "a|b|c" index string. Out-of-range values also mean no payload.
        /// </summary>
        public static List<int> Payload(int dropdownValue, IList<string> payloadIndexes, int displayCount)
        {
            var result = new List<int>();
            if (payloadIndexes == null || dropdownValue < 0 || dropdownValue >= payloadIndexes.Count || dropdownValue == displayCount - 1)
                return result;
            foreach (string part in (payloadIndexes[dropdownValue] ?? "").Split('|'))
            {
                int v;
                if (int.TryParse(part.Trim(), out v)) result.Add(v);
            }
            return result;
        }

        /// <summary>Distinct in-range ship indexes, highest first (safe for RemoveAt in a loop).</summary>
        public static List<int> CleanIndexes(IEnumerable<int> indexes, int count)
        {
            var list = new List<int>(); // net35: no SortedSet
            if (indexes != null)
                foreach (int i in indexes)
                    if (i >= 0 && i < count && !list.Contains(i)) list.Add(i);
            list.Sort();
            list.Reverse();
            return list;
        }

        /// <summary>A split needs at least one picked ship and at least one left behind.</summary>
        public static bool CanSplit(int picked, int total) => picked > 0 && picked < total;

        /// <summary>How many picked ships fit into the target task force.</summary>
        public static int MergeFits(int targetCount, int picked, int maxShips) =>
            Math.Max(0, Math.Min(picked, maxShips - targetCount));

        /// <summary>Swaps two entries of a per-ship list (no-op if an index is out of range or the list is null).</summary>
        public static void SwapAt<T>(IList<T> list, int i, int j)
        {
            if (list == null || i == j || i < 0 || j < 0 || i >= list.Count || j >= list.Count) return;
            T t = list[i];
            list[i] = list[j];
            list[j] = t;
        }

        public static void WriteIndexes(BinaryWriter w, IList<int> indexes)
        {
            int n = Math.Min(indexes.Count, MaxIndexes);
            w.Write(n);
            for (int i = 0; i < n; i++) w.Write(indexes[i]);
        }

        public static List<int> ReadIndexes(BinaryReader r)
        {
            int n = r.ReadInt32();
            // ArgumentException (mscorlib): Unity's Mono has no System.IO.InvalidDataException at runtime
            if (n < 0 || n > MaxIndexes) throw new ArgumentException("bad index count " + n);
            var list = new List<int>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadInt32());
            return list;
        }

        /// <summary>Task-force names typed by Player 2: trimmed, no control characters, at most 40 characters.</summary>
        public static string CleanName(string name)
        {
            if (name == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in name.Trim())
            {
                if (char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= 40) break;
            }
            return sb.ToString();
        }
    }
}
