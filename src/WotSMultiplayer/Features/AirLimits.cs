using HarmonyLib;

namespace WotSMultiplayer.Features
{
    /// <summary>
    /// User request (D32): allow bigger air battles. Vanilla config.txt has maxAirPerSide = 24 (planes per side that can be
    /// pulled into one campaign battle, CampaignManager.GetValidNeighbouringMobiles) and Config.maxInFlight = 12 (planes per
    /// launched air group). These were performance limits, not balance values. The mod overrides them at runtime from
    /// its own config. config.txt is never edited. Applies in single-player and multiplayer (the host's values decide battles).
    /// </summary>
    internal static class AirLimits
    {
        internal static void Apply()
        {
            Config c = Config.instance;
            if (c == null) return;
            int perSide = Plugin.MaxAircraftPerSide.Value;
            int perGroup = Plugin.MaxAircraftPerGroup.Value;
            if (perSide > 0 && c.maxAirPerSide != perSide)
            {
                Plugin.Log.LogInfo($"Air limits: planes per side in a battle {c.maxAirPerSide} → {perSide}");
                c.maxAirPerSide = perSide;
            }
            if (perGroup > 0 && c.maxInFlight != perGroup)
            {
                Plugin.Log.LogInfo($"Air limits: planes per launched air group {c.maxInFlight} → {perGroup}");
                c.maxInFlight = perGroup;
            }
        }
    }

    /// <summary>Re-apply after the game (re)reads config.txt.</summary>
    [HarmonyPatch(typeof(Config), "ReadConfig")]
    internal static class AirLimitsConfigPatch
    {
        private static void Postfix() => AirLimits.Apply();
    }
}
