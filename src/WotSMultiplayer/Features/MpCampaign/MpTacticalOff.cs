using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using WotSMultiplayer.Core;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// D50 (user): no TACTICAL view in multiplayer. The fleet panel's TACTICAL button (scene-wired to
    /// CampaignInterface.MobileMapObjectEnterWorld → GenerateCampaignEngagement) opens a 3D scene for one fleet outside the
    /// two-player battle flow (seen in the v0.17.2 test: each copy entered its own scene; the other side's ships would be
    /// AI-run there). In MP the button is hidden and the call is refused, except when the mod itself starts a real MP
    /// battle (MpBattle.HostStart sets <see cref="AllowEnterWorld"/>). Single-player is untouched.
    /// </summary>
    internal static class MpTacticalOff
    {
        internal static bool AllowEnterWorld;
        private static readonly List<Button> buttons = new List<Button>();
        private static bool searched, hidden;
        private static float nextSearch;

        /// <summary>Plugin.Update: keep the TACTICAL buttons hidden while MP is active, restore them afterwards.</summary>
        internal static void Update()
        {
            if (ModState.MultiplayerActive)
            {
                if (searched && buttons.TrueForAll(b => b == null)) searched = false; // the campaign scene was rebuilt
                if (!searched && Time.realtimeSinceStartup >= nextSearch && CampaignInterface.instance != null)
                {
                    nextSearch = Time.realtimeSinceStartup + 3f;
                    Find();
                }
                foreach (Button b in buttons)
                    if (b != null && b.gameObject.activeSelf) b.gameObject.SetActive(false); // vanilla panel refreshes re-show it
                hidden = buttons.Count > 0;
            }
            else if (hidden)
            {
                foreach (Button b in buttons) if (b != null) b.gameObject.SetActive(true);
                hidden = false;
            }
        }

        private static void Find()
        {
            buttons.Clear();
            foreach (Button b in Resources.FindObjectsOfTypeAll<Button>())
            {
                if (b == null || !b.gameObject.scene.IsValid()) continue;
                for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                    if (b.onClick.GetPersistentMethodName(i) == "MobileMapObjectEnterWorld") { buttons.Add(b); break; }
            }
            searched = buttons.Count > 0;
            if (searched) Plugin.Log.LogInfo($"MP: TACTICAL view off in multiplayer ({buttons.Count} button(s) hidden)");
        }
    }

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.MobileMapObjectEnterWorld))]
    internal static class MpTacticalOffPatch
    {
        private static bool Prefix()
        {
            if (!ModState.MultiplayerActive || MpTacticalOff.AllowEnterWorld) return true;
            Toasts.Show("The TACTICAL view is off in multiplayer: battles start from encounters.");
            return false;
        }
    }
}
