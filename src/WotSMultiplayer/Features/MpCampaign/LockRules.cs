using System;
using System.Collections.Generic;
using System.IO;

namespace WotSMultiplayer.Features.MpCampaign
{
    internal enum BlockReason : byte { None = 0, CommanderSelected = 1, Locked = 2 }

    /// <summary>
    /// Additional players, stage 3 (D57): one team's locks as the host keeps them and the team's players see them.
    /// The team's commander (Player 1 for team 0, Player 2 for team 1) blocks a mobile for the team's subordinates by
    /// having it selected (automatic) or by locking it (K or the speed window's button). Mobiles are host mpIds.
    /// Game-independent (no Unity references), unit-tested in tests/RulesTests.
    /// </summary>
    internal sealed class TeamLockState
    {
        /// <summary>Most locks a team can hold (also the most read from the wire).</summary>
        public const int MaxLocks = 256;

        public int Selected = -1;
        public readonly HashSet<int> Locked = new HashSet<int>();

        /// <summary>Lock or unlock a mobile. Returns true if it is locked afterwards.</summary>
        public bool Toggle(int id)
        {
            if (id < 0) return false;
            if (Locked.Remove(id)) return false;
            if (Locked.Count >= MaxLocks) return false;
            Locked.Add(id);
            return true;
        }

        /// <summary>Why a subordinate may not order this mobile (None = allowed).</summary>
        public BlockReason Blocked(int id)
        {
            if (id < 0) return BlockReason.None;
            if (id == Selected) return BlockReason.CommanderSelected;
            return Locked.Contains(id) ? BlockReason.Locked : BlockReason.None;
        }

        /// <summary>Forget mobiles that are gone. Returns true if anything changed.</summary>
        public bool Prune(Func<int, bool> exists)
        {
            bool changed = false;
            if (Selected >= 0 && !exists(Selected)) { Selected = -1; changed = true; }
            var gone = new List<int>();
            foreach (int id in Locked) if (!exists(id)) gone.Add(id);
            foreach (int id in gone) Locked.Remove(id);
            return changed || gone.Count > 0;
        }

        public void Clear()
        {
            Selected = -1;
            Locked.Clear();
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Selected);
            w.Write(Locked.Count);
            foreach (int id in Locked) w.Write(id);
        }

        /// <summary>Replace this state with one read from the wire (malformed counts are refused).</summary>
        public void Read(BinaryReader r)
        {
            int sel = r.ReadInt32();
            int n = r.ReadInt32();
            if (n < 0 || n > MaxLocks) throw new FormatException("bad lock count " + n) /* (InvalidDataException is missing from Unity's Mono) */;
            var ids = new List<int>(n);
            for (int i = 0; i < n; i++) ids.Add(r.ReadInt32());
            Selected = sel;
            Locked.Clear();
            foreach (int id in ids) if (id >= 0) Locked.Add(id);
        }
    }

    internal static class LockRules
    {
        // the order messages (RemoteOrders): 28 CreateTaskForce, 29 SetCourse, 30 Upgrade, 31 Disband, 32 LaunchAir,
        // 33 AirOrder, 34 FleetOrder, 35 CargoOrder
        public const byte FirstOrder = 28, LastOrder = 35;

        public static bool IsOrder(byte type) => type >= FirstOrder && type <= LastOrder;

        /// <summary>
        /// The mobiles (host mpIds) an order message acts on, read from its payload: the ordered task force or air group,
        /// the carrier a group launches from, and a merge's target. Orders at a base (form a task force, upgrade, launch
        /// from a base) act on no mobile. A payload too short to hold its ids yields what could be read.
        /// </summary>
        public static List<int> OrderTargets(byte type, byte[] payload)
        {
            var ids = new List<int>(2);
            if (payload == null) return ids;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(payload, false)))
                {
                    switch (type)
                    {
                        case 29: case 31: case 33: case 35: // SetCourse, Disband, AirOrder, CargoOrder: {mpId, ...}
                            ids.Add(r.ReadInt32());
                            break;
                        case 32: // LaunchAir {kind 0: locationId | kind 1: mpId + ship ...}
                            if (r.ReadByte() == 1) ids.Add(r.ReadInt32());
                            break;
                        case 34: // FleetOrder {mpId, FleetCmd, args}; Merge's args start with the target mpId
                            ids.Add(r.ReadInt32());
                            if ((FleetCmd)r.ReadByte() == FleetCmd.Merge) ids.Add(r.ReadInt32());
                            break;
                    }
                }
            }
            catch (EndOfStreamException) { }
            return ids;
        }

        // ---------- battles (stage 4) ----------

        /// <summary>Battle order message (MpBattle): {unit kind 0 sea / 1 air, index, BattleCmd, values}.</summary>
        public const byte BattleOrder = 44;

        /// <summary>One id per battle unit: kind (0 sea, 1 air) and index in allSeaUnits / allAirUnits.</summary>
        public static int BattleUnitId(byte kind, int index) => kind * 100000 + index;

        public static void SplitBattleUnitId(int id, out byte kind, out int index)
        {
            kind = (byte)(id / 100000);
            index = id % 100000;
        }

        /// <summary>Retreat decisions stay with the commanders: 24 Disengage, 25 Retreat, 26 AllowRetreat.</summary>
        public static bool IsCommanderOnlyBattleCmd(byte cmd) => cmd >= 24 && cmd <= 26;

        /// <summary>The head of a battle order: unit kind, index and command. False if the payload is too short.</summary>
        public static bool ReadBattleOrderHead(byte[] payload, out byte kind, out int index, out byte cmd)
        {
            kind = 0; index = -1; cmd = 0;
            if (payload == null || payload.Length < 6) return false;
            kind = payload[0];
            index = payload[1] | (payload[2] << 8) | (payload[3] << 16) | (payload[4] << 24); // BinaryWriter: little-endian
            cmd = payload[5];
            return kind <= 1 && index >= 0;
        }

        /// <summary>First blocked target of an order for this team (None = the order may run).</summary>
        public static BlockReason FirstBlocked(TeamLockState team, List<int> targets, out int blockedId)
        {
            blockedId = -1;
            foreach (int id in targets)
            {
                BlockReason b = team.Blocked(id);
                if (b != BlockReason.None) { blockedId = id; return b; }
            }
            return BlockReason.None;
        }
    }
}
