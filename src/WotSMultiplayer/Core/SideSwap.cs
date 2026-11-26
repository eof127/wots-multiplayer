using System.Collections.Generic;

namespace WotSMultiplayer.Core
{
    /// <summary>
    /// Mirrors a campaign save (CampaignManager.GetCampaignDataString JSON) so the other side becomes "Player"
    /// (Faction 0). This is what the MP client loads (PLAN.md §2): the game's UI only controls Faction 0, so the
    /// client sees its own forces as the player's. The host's state is unchanged; only the client copy is mirrored.
    ///
    /// The field lists come from comparing campaign001 (Allies human) with campaign002 (Japan human) and from reading
    /// how each field is used in the decompiled game (docs/GAME_INTERNALS.md). Pure .NET, unit-tested on real saves.
    /// </summary>
    internal static class SideSwap
    {
        /// <summary>Top-level fields that exist as a 0/1 pair of separate keys: swap their values.</summary>
        public static readonly string[][] KeyPairs =
        {
            new[] { "nations0", "nations1" },
            new[] { "playerAircraftDowned", "otherAircraftDowned" },
            new[] { "playerOwnedShipClasses", "enemyOwnedShipClasses" },
            new[] { "playerOwnedShipInstances", "enemyOwnedShipInstances" },
            new[] { "playerOwnedRepairDays", "enemyOwnedRepairDays" },
            new[] { "endIfNoPlayerHomePort", "endIfNoEnemyHomePort" },
            new[] { "aircraft0", "aircraft1" },
            new[] { "aircraft0Numbers", "aircraft1Numbers" },
            new[] { "suppliesPerUpgrade0", "suppliesPerUpgrade1" },
            new[] { "fuelPerUpgrade0", "fuelPerUpgrade1" },
            new[] { "engineeringPerUpgrade0", "engineeringPerUpgrade1" },
            new[] { "replenish0", "replenish1" },
            new[] { "replenish0Max", "replenish1Max" },
            new[] { "replenishDailyAircraft0", "replenishDailyAircraft1" },
            new[] { "playerObjectEngaged", "otherObjectEngaged" },
            new[] { "playerMobileObjectSaveData", "enemyMobileObjectSaveData" },
        };

        /// <summary>Top-level arrays indexed by faction ([0] player, [1] enemy): swap elements 0 and 1.</summary>
        public static readonly string[] IndexPairs =
        {
            "commandPoints", "commandBonusPoints", "commandPointSpent", "commandPointsPerWeek", "commandPointsPerLocation",
            "commandPointsPerPortRank", "commandPointsPerAirfieldRank", "maxPort", "maxAirfield", "baseRepairTime",
            "repairPerComp", "repairHourPerIntegrity", "factionColors",
        };

        /// <summary>Difficulty arrays laid out as [cat0 player, cat0 enemy, cat1 player, ...] (Utilities.GetCampaignDifficultyModifier).</summary>
        public static readonly string[] InterleavedPairs =
        {
            "campDifficultyPreset0", "campDifficultyPreset1", "campDifficultyPreset2", "campDifficultyPreset3", "campDifficultyPreset4",
        };

        /// <summary>Objective fields taken from the mirror campaign's setup.txt (each side has its own goals).</summary>
        public static readonly string[] ObjectiveKeys = { "mustControl", "maxAirAt", "maxPortAt" };

        /// <summary>Per-location (MapLocation JSON) fields.</summary>
        private static readonly string[] LocationIndexPairs = { "troops", "supplies", "engineering", "fuel" };
        private static readonly string[][] LocationKeyPairs =
        {
            new[] { "aircraft0", "aircraft1" },
            new[] { "aircraft0Numbers", "aircraft1Numbers" },
            new[] { "replenishDailyAircraft0", "replenishDailyAircraft1" },
        };

        /// <param name="saveJson">Host save JSON (first line of a campaign save file).</param>
        /// <param name="mirrorCampaignId">Campaign folder where the other side is the human side (e.g. campaign002).</param>
        /// <param name="mirrorSetupJson">Optional: that campaign's setup.txt, for the objective fields.</param>
        public static string Swap(string saveJson, string mirrorCampaignId, string mirrorSetupJson)
        {
            JObject root = MiniJson.ParseObject(saveJson);

            if (mirrorCampaignId != null) root.Set("campaignID", new JString(mirrorCampaignId));

            foreach (string[] p in KeyPairs) SwapKeys(root, p[0], p[1]);
            foreach (string k in IndexPairs) SwapIndex01(root.Get(k) as JArray);
            foreach (string k in InterleavedPairs) SwapInterleaved(root.Get(k) as JArray);

            // factionSunk: list of faction ids per sunk ship.
            FlipFactionValues(root.Get("factionSunk") as JArray);

            // Host-only / perspective-only data that must not leak to the other player.
            Clear(root, "mapMarkersSaveData");
            Clear(root, "playerAirScoutData");
            root.Set("currentlyInCombatIndex", JRaw.Int(-1));
            root.Set("canIgnore", new JRaw("false"));
            root.Set("canAutoResolve", new JRaw("false"));

            // Locations: owner and per-faction stocks.
            RewriteNested(root.Get("mapLocationSaveData") as JArray, SwapLocation);
            // Mobiles: the two lists were already exchanged above; now fix each mobile's faction and perspective fields.
            RewriteNested(root.Get("playerMobileObjectSaveData") as JArray, SwapMobile);
            RewriteNested(root.Get("enemyMobileObjectSaveData") as JArray, SwapMobile);

            if (!string.IsNullOrEmpty(mirrorSetupJson))
            {
                JObject setup = MiniJson.ParseObject(mirrorSetupJson);
                foreach (string k in ObjectiveKeys)
                {
                    JNode v = setup.Get(k);
                    if (v != null) root.Set(k, v);
                }
            }
            return MiniJson.Write(root);
        }

        private static void SwapLocation(JObject loc)
        {
            FlipFaction(loc, "currentFaction");
            foreach (string k in LocationIndexPairs) SwapIndex01(loc.Get(k) as JArray);
            foreach (string[] p in LocationKeyPairs) SwapKeys(loc, p[0], p[1]);
        }

        private static void SwapMobile(JObject m)
        {
            FlipFaction(m, "currentFaction");
            m.Set("ignoreByPlayer", new JRaw("false"));        // the host player's "ignore this contact" choice
            Clear(m, "intelData");                              // the host's intel picture
            // AI mission indexes point into the host campaign's mission list, which differs in the mirror campaign;
            // LoadCampaign indexes seaMissions[missionIndex], so keep them in range. (MP mobiles are player-created: 0.)
            if (m.Has("missionIndex")) m.Set("missionIndex", JRaw.Int(0));
            if (m.Has("originalMissionIndex")) m.Set("originalMissionIndex", JRaw.Int(0));
        }

        // ---------- helpers ----------

        private static void SwapKeys(JObject o, string a, string b)
        {
            JNode va = o.Get(a), vb = o.Get(b);
            if (va == null || vb == null) return;
            o.Set(a, vb);
            o.Set(b, va);
        }

        private static void SwapIndex01(JArray arr)
        {
            if (arr == null || arr.Items.Count < 2) return;
            JNode t = arr.Items[0];
            arr.Items[0] = arr.Items[1];
            arr.Items[1] = t;
        }

        private static void SwapInterleaved(JArray arr)
        {
            if (arr == null) return;
            for (int i = 0; i + 1 < arr.Items.Count; i += 2)
            {
                JNode t = arr.Items[i];
                arr.Items[i] = arr.Items[i + 1];
                arr.Items[i + 1] = t;
            }
        }

        /// <summary>0 ↔ 1; neutral (2) and others unchanged.</summary>
        internal static int Flip(int faction) => faction == 0 ? 1 : faction == 1 ? 0 : faction;

        private static void FlipFaction(JObject o, string key)
        {
            JRaw r = o.Get(key) as JRaw;
            if (r != null) o.Set(key, JRaw.Int(Flip(r.AsInt())));
        }

        private static void FlipFactionValues(JArray arr)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Items.Count; i++)
            {
                JRaw r = arr.Items[i] as JRaw;
                if (r != null) arr.Items[i] = JRaw.Int(Flip(r.AsInt()));
            }
        }

        private static void Clear(JObject o, string key)
        {
            if (o.Has(key)) o.Set(key, new JArray());
        }

        /// <summary>Each element is a JSON string holding a JSON object (JsonUtility of a component).</summary>
        private static void RewriteNested(JArray arr, System.Action<JObject> rewrite)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Items.Count; i++)
            {
                JString s = arr.Items[i] as JString;
                if (s == null) continue;
                JObject inner = MiniJson.ParseObject(s.Value);
                rewrite(inner);
                arr.Items[i] = new JString(MiniJson.Write(inner));
            }
        }

        /// <summary>Count of entries per faction value in a nested list (test/diagnostics helper).</summary>
        internal static Dictionary<int, int> CountFactions(string saveJson, string listKey)
        {
            var counts = new Dictionary<int, int>();
            JArray arr = MiniJson.ParseObject(saveJson).Get(listKey) as JArray;
            if (arr == null) return counts;
            foreach (JNode n in arr.Items)
            {
                JRaw f = MiniJson.ParseObject(((JString)n).Value).Get("currentFaction") as JRaw;
                int k = f != null ? f.AsInt() : -1;
                counts[k] = counts.ContainsKey(k) ? counts[k] + 1 : 1;
            }
            return counts;
        }
    }
}
