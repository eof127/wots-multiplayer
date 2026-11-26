using System;
using System.Collections.Generic;
using UnityEngine;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>Base values read from a campaign's setup.txt (JsonUtility; missing fields stay null).</summary>
    [Serializable]
    public class SetupBase
    {
        public int[] commandPoints;
        public int[] commandPointsPerWeek;
        public int[] commandPointsPerLocation;
        public int[] commandPointsPerPortRank;
        public int[] commandPointsPerAirfieldRank;
        public List<int> maxPort;
        public List<int> maxAirfield;
        public List<int> suppliesPerUpgrade0;
        public List<int> fuelPerUpgrade0;
        public List<int> engineeringPerUpgrade0;
        public List<int> replenish0;
        public int[] replenishDailyAircraft0;
        public List<int> baseRepairTime;
        public List<int> repairPerComp;
        public float releaseRefund = 1f;
        public float enemySunkYield = 1f;
    }

    /// <summary>One line of a campaign's mapLandLocations.txt.</summary>
    [Serializable]
    public class LocationBase
    {
        public string locationID;
        public int currentFaction;
        public int[] troops;
        public int[] supplies;
        public int[] fuel;
        public int[] engineering;
    }

    /// <summary>Reads campaign base data through the game's AssetLoader (respects data mods).</summary>
    internal static class CampaignFiles
    {
        public static SetupBase Setup(string campaignId) =>
            JsonUtility.FromJson<SetupBase>(AssetLoader.instance.ImportText("campaign/" + campaignId + "/setup.txt")[0]);

        public static Dictionary<string, LocationBase> Locations(string campaignId)
        {
            var d = new Dictionary<string, LocationBase>();
            foreach (string line in AssetLoader.instance.ImportText("campaign/" + campaignId + "/mapLandLocations.txt"))
            {
                if (string.IsNullOrEmpty(line) || line.Trim().Length == 0) continue;
                LocationBase l = JsonUtility.FromJson<LocationBase>(line);
                if (l != null && !string.IsNullOrEmpty(l.locationID)) d[l.locationID] = l;
            }
            return d;
        }

        public static int At(int[] a, int i, int fallback) => a != null && a.Length > i ? a[i] : fallback;
        public static int At(List<int> a, int i, int fallback) => a != null && a.Count > i ? a[i] : fallback;
    }

    /// <summary>Default setup values: each side's values from the campaign version where that side is the human player.</summary>
    internal static class MpDefaults
    {
        public static MpSettings Build(string theatreId, int hostSide)
        {
            Theatres t = Theatres.Find(theatreId);
            MpSettings s = new MpSettings { Theatre = t.Id, HostSide = hostSide };
            for (int side = 0; side < 2; side++)
            {
                SetupBase b = CampaignFiles.Setup(t.CampaignBySide[side]);
                s.StartCP[side] = CampaignFiles.At(b.commandPoints, 0, 200);
                s.CpPerWeek[side] = CampaignFiles.At(b.commandPointsPerWeek, 0, 25);
                s.CpPerLocation[side] = CampaignFiles.At(b.commandPointsPerLocation, 0, 0);
                s.CpPerPortRank[side] = CampaignFiles.At(b.commandPointsPerPortRank, 0, 3);
                s.CpPerAirfieldRank[side] = CampaignFiles.At(b.commandPointsPerAirfieldRank, 0, 0);
                s.MaxPort[side] = CampaignFiles.At(b.maxPort, 0, 5);
                s.MaxAirfield[side] = CampaignFiles.At(b.maxAirfield, 0, 5);
                s.RepairDays[side] = CampaignFiles.At(b.baseRepairTime, 0, 2);
                s.RepairPerComp[side] = CampaignFiles.At(b.repairPerComp, 0, 1);
                if (side == 0)
                {
                    s.ReleaseRefundPct = Mathf.RoundToInt(b.releaseRefund * 100f);
                    s.SunkYieldPct = Mathf.RoundToInt(b.enemySunkYield * 100f);
                }
            }
            return s;
        }
    }

    /// <summary>Applies MpSettings to the freshly started host campaign (faction 0 = host side, 1 = client side).</summary>
    internal static class MpApply
    {
        public static void ApplyToHostCampaign(MpSettings s)
        {
            CampaignManager cm = CampaignManager.instance;
            CampaignData cd = cm.campaignData;
            Theatres t = Theatres.Find(s.Theatre);
            int[] sideOfFaction = { s.HostSide, s.ClientSide };

            for (int f = 0; f < 2; f++)
            {
                int side = sideOfFaction[f];
                SetupBase b = CampaignFiles.Setup(t.CampaignBySide[side]); // that side's "human" balance values

                cd.commandPoints[f] = s.StartCP[side];
                cd.commandBonusPoints[f] = 0;
                cd.commandPointsPerWeek[f] = s.CpPerWeek[side];
                cd.commandPointsPerLocation[f] = s.CpPerLocation[side];
                cd.commandPointsPerPortRank[f] = s.CpPerPortRank[side];
                cd.commandPointsPerAirfieldRank[f] = s.CpPerAirfieldRank[side];
                cd.maxPort[f] = s.MaxPort[side];
                cd.maxAirfield[f] = s.MaxAirfield[side];
                cd.baseRepairTime[f] = s.RepairDays[side];
                cd.repairPerComp[f] = s.RepairPerComp[side];

                int up = s.UpgradeCostPct[side];
                SetList(f == 0 ? cd.suppliesPerUpgrade0 : cd.suppliesPerUpgrade1, Scale(b.suppliesPerUpgrade0, up));
                SetList(f == 0 ? cd.fuelPerUpgrade0 : cd.fuelPerUpgrade1, Scale(b.fuelPerUpgrade0, up));
                SetList(f == 0 ? cd.engineeringPerUpgrade0 : cd.engineeringPerUpgrade1, Scale(b.engineeringPerUpgrade0, up));
                SetList(f == 0 ? cd.replenish0 : cd.replenish1, Scale(b.replenish0, s.ReplenishPct[side]));
                int[] air = ScaleArr(b.replenishDailyAircraft0, s.AirReplenishPct[side]);
                if (air != null)
                {
                    if (f == 0) cd.replenishDailyAircraft0 = air; else cd.replenishDailyAircraft1 = air;
                }
            }
            cd.releaseRefund = s.ReleaseRefundPct / 100f;
            cd.enemySunkYield = s.SunkYieldPct / 100f;

            // Starting stocks at bases: each side gets the stocks from its own "human" campaign file, then the %.
            var baseLocs = new[] { CampaignFiles.Locations(t.CampaignBySide[s.HostSide]), CampaignFiles.Locations(t.CampaignBySide[s.ClientSide]) };
            foreach (MapLocation ml in cm.landMapLocations)
            {
                int f = ml.currentFaction;
                if (f != 0 && f != 1) continue;
                int side = sideOfFaction[f];
                LocationBase b;
                bool haveBase = baseLocs[f].TryGetValue(ml.locationID, out b) && b.currentFaction == 0;
                ml.troops[f] = Pct(haveBase ? CampaignFiles.At(b.troops, 0, ml.troops[f]) : ml.troops[f], s.TroopsPct[side]);
                ml.supplies[f] = Pct(haveBase ? CampaignFiles.At(b.supplies, 0, ml.supplies[f]) : ml.supplies[f], s.SuppliesPct[side]);
                ml.fuel[f] = Pct(haveBase ? CampaignFiles.At(b.fuel, 0, ml.fuel[f]) : ml.fuel[f], s.FuelPct[side]);
                ml.engineering[f] = Pct(haveBase ? CampaignFiles.At(b.engineering, 0, ml.engineering[f]) : ml.engineering[f], s.EngineeringPct[side]);
            }

            SetNeutralDifficulty(cd);
            CampaignInterface.instance.SetCommandTotal();
            Plugin.Log.LogInfo($"MP: applied setup (host {MpCampaign.SideName(s.HostSide)}: CP {cd.commandPoints[0]}, client CP {cd.commandPoints[1]}).");
        }

        /// <summary>
        /// Single-player campaign difficulty gives the human a bonus over the AI (Utilities.GetCampaignDifficultyModifier).
        /// In MP the setup screen defines each side explicitly, so all multipliers are neutral (1 for factors, 0 for additions).
        /// </summary>
        public static void SetNeutralDifficulty(CampaignData cd)
        {
            float[] neutral = Neutral();
            OptionsManager.instance.campaignDifficultyMultipliers = neutral;
            cd.campDifficultyPreset0 = (float[])neutral.Clone();
            cd.campDifficultyPreset1 = (float[])neutral.Clone();
            cd.campDifficultyPreset2 = (float[])neutral.Clone();
            cd.campDifficultyPreset3 = (float[])neutral.Clone();
            cd.campDifficultyPreset4 = (float[])neutral.Clone();
        }

        public static float[] Neutral()
        {
            float[] a = new float[30];
            // multiplicative categories: StartPoints(0), ReleaseRefund(1), SunkPoints(2), ReplenishRate(5), UpgradeCost(6)
            foreach (int cat in new[] { 0, 1, 2, 5, 6 }) { a[cat * 2] = 1f; a[cat * 2 + 1] = 1f; }
            return a;
        }

        private static int Pct(int v, int pct) => Mathf.FloorToInt(v * (pct / 100f));

        private static List<int> Scale(List<int> src, int pct)
        {
            if (src == null) return null;
            var r = new List<int>(src.Count);
            foreach (int v in src) r.Add(Pct(v, pct));
            return r;
        }

        private static int[] ScaleArr(int[] src, int pct)
        {
            if (src == null) return null;
            int[] r = new int[src.Length];
            for (int i = 0; i < src.Length; i++) r[i] = Pct(src[i], pct);
            return r;
        }

        private static void SetList(List<int> target, List<int> values)
        {
            if (target == null || values == null) return;
            target.Clear();
            target.AddRange(values);
        }
    }
}
