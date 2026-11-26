using HarmonyLib;
using WotSMultiplayer.Core;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// D45: in multiplayer every combat-difficulty multiplier is 1.0 for both players (like D28 for the campaign difficulty).
    /// Vanilla gives the "player" column of the chosen preset to Faction.Player and the "AI" column to everyone else
    /// (Utilities.GetDifficultyModifier: index difficulty*2, +1 when not the player), so on the host Player 2's units would
    /// get the AI's bonuses (on Elite: 1.5x shell/torpedo/bomb/ASW damage, 0.6x repair time, 1.2x solution, 2x sonar) and
    /// the host the player's (1.2x visual and radar). Both PCs return 1.0 while multiplayer is active (the client's UI uses
    /// the same values for reload and solution displays). Single-player is untouched.
    /// </summary>
    [HarmonyPatch]
    internal static class MpNeutralDifficultyPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Utilities), nameof(Utilities.GetDifficultyModifier), new[] { typeof(Difficulty), typeof(Faction) })]
        [HarmonyPatch(typeof(Utilities), nameof(Utilities.GetDifficultyModifier), new[] { typeof(Difficulty), typeof(int) })]
        private static bool Neutral(ref float __result)
        {
            if (!ModState.MultiplayerActive) return true;
            __result = 1f;
            return false;
        }
    }
}
