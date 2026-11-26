using System.IO;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// The MP campaign setup (decision D18). Values are per side, indexed by <see cref="Side"/> (0 = Allies, 1 = Japan),
    /// not by faction: the host picks which side it plays (D20). Only campaign economy/rules: no unit stats (D1).
    /// Pure data + binary serialization (no Unity refs).
    /// </summary>
    internal sealed class MpSettings
    {
        public static class Side { public const int Allies = 0, Japan = 1; }

        public string Theatre = Theatres.Solomons.Id;
        public int HostSide = Side.Allies;

        // per side [Allies, Japan]
        public int[] StartCP = { 200, 200 };
        public int[] TroopsPct = { 100, 100 };
        public int[] SuppliesPct = { 100, 100 };
        public int[] FuelPct = { 100, 100 };
        public int[] EngineeringPct = { 100, 100 };
        public int[] CpPerWeek = { 25, 30 };
        public int[] CpPerLocation = { 0, 0 };
        public int[] CpPerPortRank = { 3, 3 };
        public int[] CpPerAirfieldRank = { 0, 0 };
        public int[] UpgradeCostPct = { 100, 100 };
        public int[] MaxPort = { 5, 5 };
        public int[] MaxAirfield = { 5, 5 };
        public int[] ReplenishPct = { 100, 100 };
        public int[] AirReplenishPct = { 100, 100 };
        public int[] RepairDays = { 2, 2 };
        public int[] RepairPerComp = { 1, 1 };

        // shared (single values in the game's data)
        public int ReleaseRefundPct = 100;
        public int SunkYieldPct = 100;

        public int ClientSide => 1 - HostSide;

        /// <summary>Row definitions for the setup screen: label, array, min, max, step.</summary>
        public sealed class Row
        {
            public string Label, Group;
            public int[] Values;
            public int Min, Max, Step;
            public string Suffix;
        }

        public Row[] Rows() => new[]
        {
            R("Starting", "Command points", StartCP, 0, 5000, 25, ""),
            R("Starting", "Troops at bases", TroopsPct, 0, 500, 10, "%"),
            R("Starting", "Supplies at bases", SuppliesPct, 0, 500, 10, "%"),
            R("Starting", "Fuel at bases", FuelPct, 0, 500, 10, "%"),
            R("Starting", "Engineering at bases", EngineeringPct, 0, 500, 10, "%"),
            R("Income", "CP per week", CpPerWeek, 0, 500, 5, ""),
            R("Income", "CP per location held", CpPerLocation, 0, 50, 1, ""),
            R("Income", "CP per port rank", CpPerPortRank, 0, 50, 1, ""),
            R("Income", "CP per airfield rank", CpPerAirfieldRank, 0, 50, 1, ""),
            R("Upgrades & bases", "Upgrade cost", UpgradeCostPct, 10, 500, 10, "%"),
            R("Upgrades & bases", "Max port level", MaxPort, 1, 5, 1, ""),
            R("Upgrades & bases", "Max airfield level", MaxAirfield, 1, 5, 1, ""),
            R("Replenishment", "Resource replenishment", ReplenishPct, 0, 500, 10, "%"),
            R("Replenishment", "Aircraft replenishment", AirReplenishPct, 0, 500, 10, "%"),
            R("Repair", "Base repair days", RepairDays, 0, 30, 1, ""),
            R("Repair", "Repair per compartment", RepairPerComp, 0, 10, 1, ""),
        };

        private static Row R(string g, string l, int[] v, int min, int max, int step, string suffix) =>
            new Row { Group = g, Label = l, Values = v, Min = min, Max = max, Step = step, Suffix = suffix };

        // ---------- wire format ----------

        public void Write(BinaryWriter w)
        {
            w.Write(Theatre ?? "");
            w.Write(HostSide);
            foreach (Row r in Rows()) { w.Write(r.Values[0]); w.Write(r.Values[1]); }
            w.Write(ReleaseRefundPct);
            w.Write(SunkYieldPct);
        }

        public static MpSettings Read(BinaryReader r)
        {
            MpSettings s = new MpSettings { Theatre = r.ReadString(), HostSide = r.ReadInt32() };
            foreach (Row row in s.Rows()) { row.Values[0] = r.ReadInt32(); row.Values[1] = r.ReadInt32(); }
            s.ReleaseRefundPct = r.ReadInt32();
            s.SunkYieldPct = r.ReadInt32();
            return s;
        }

        public MpSettings Clone()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ms);
                Write(w);
                w.Flush();
                ms.Position = 0;
                return Read(new BinaryReader(ms));
            }
        }
    }

    /// <summary>Theatres with a mirrored campaign pair (one campaign folder per human side).</summary>
    internal sealed class Theatres
    {
        public string Id, Name;
        public string[] CampaignBySide; // [Allies, Japan]

        public static readonly Theatres Solomons = new Theatres
        {
            Id = "solomons", Name = "Solomon Islands, August 1942", CampaignBySide = new[] { "campaign001", "campaign002" },
        };

        public static readonly Theatres[] All = { Solomons };

        public static Theatres Find(string id)
        {
            foreach (Theatres t in All) if (t.Id == id) return t;
            return Solomons;
        }
    }
}
