using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace WotSMultiplayer.Ui
{
    /// <summary>
    /// M10: a MULTIPLAYER button in the main menu, right after CAMPAIGN; it opens RoomWindow. The menu buttons are scene objects
    /// wired in the editor, so the CAMPAIGN button is found at runtime (its label, else a click target with "Campaign" in the
    /// name) and cloned: same look and hover behaviour, new label and click action. If the buttons aren't in a layout group, the
    /// ones below are moved down by one row. Nothing changes on disk.
    /// </summary>
    internal static class MultiplayerMenuButton
    {
        private const string Label = "MULTIPLAYER";
        private static GameObject button;
        private static Text[] labels; // the button has more than one text layer (seen: "CAMPAIGN" drawn over the new label)
        private static float nextTry;
        private static bool loggedFailure;

        internal static void Update()
        {
            MenuSystem ms = MenuSystem.instance;
            if (ms == null || ms.mainInterface == null || !ms.mainInterface.activeInHierarchy) return;
            if (button != null)
            {
                if (labels != null)
                    foreach (Text t in labels)
                        if (t != null && t.text != Label) t.text = Label; // a language refresh rewrites cloned labels
                return;
            }
            if (Time.realtimeSinceStartup < nextTry) return;
            nextTry = Time.realtimeSinceStartup + 2f;
            try { Create(ms.mainInterface); }
            catch (Exception e)
            {
                if (!loggedFailure) Plugin.Log.LogWarning("Multiplayer menu button: " + e.Message);
                loggedFailure = true;
            }
        }

        private static void Create(GameObject menu)
        {
            Button campaign = FindCampaignButton(menu);
            if (campaign == null)
            {
                if (!loggedFailure)
                {
                    var names = new List<string>();
                    foreach (Button b in menu.GetComponentsInChildren<Button>(true)) names.Add(b.name + "='" + LabelOf(b) + "'");
                    Plugin.Log.LogWarning("Multiplayer menu button: CAMPAIGN button not found; buttons: " + string.Join(", ", names.ToArray()));
                    loggedFailure = true;
                }
                return;
            }

            Transform parent = campaign.transform.parent;
            GameObject clone = UnityEngine.Object.Instantiate(campaign.gameObject, parent, false);
            clone.name = "WotSMP_MultiplayerButton";
            clone.transform.SetSiblingIndex(campaign.transform.GetSiblingIndex() + 1);
            Button b2 = clone.GetComponent<Button>();
            b2.onClick = new Button.ButtonClickedEvent(); // drop the copied CAMPAIGN action
            b2.onClick.AddListener(RoomWindow.Open);
            labels = clone.GetComponentsInChildren<Text>(true);
            foreach (Text t in labels) t.text = Label;

            if (parent.GetComponent<LayoutGroup>() == null) MakeRoom(campaign, clone, parent);
            button = clone;
            Plugin.Log.LogInfo($"Multiplayer menu button added after '{LabelOf(campaign)}' ({(parent.GetComponent<LayoutGroup>() != null ? "layout group" : "moved the buttons below")})");
        }

        private static Button FindCampaignButton(GameObject menu)
        {
            Button byTarget = null;
            foreach (Button b in menu.GetComponentsInChildren<Button>(true))
            {
                if (b.name == "WotSMP_MultiplayerButton") continue;
                if (LabelOf(b).Trim().ToUpperInvariant() == "CAMPAIGN") return b;
                for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                {
                    string m = b.onClick.GetPersistentMethodName(i) ?? "";
                    if (byTarget == null && m.IndexOf("Campaign", StringComparison.OrdinalIgnoreCase) >= 0) byTarget = b;
                }
            }
            return byTarget;
        }

        private static string LabelOf(Button b)
        {
            Text t = b.GetComponentInChildren<Text>(true);
            return t != null ? t.text ?? "" : "";
        }

        /// <summary>No layout group: put the clone one row below CAMPAIGN and move the buttons below it down one row.</summary>
        private static void MakeRoom(Button campaign, GameObject clone, Transform parent)
        {
            var campRt = (RectTransform)campaign.transform;
            float y = campRt.anchoredPosition.y;
            float step = 0f;
            var below = new List<RectTransform>();
            foreach (Transform t in parent)
            {
                if (t == campaign.transform || t.gameObject == clone || t.GetComponent<Button>() == null) continue;
                var rt = t as RectTransform;
                if (rt == null || rt.anchoredPosition.y >= y) continue;
                below.Add(rt);
                float gap = y - rt.anchoredPosition.y;
                if (step == 0f || gap < step) step = gap;
            }
            if (step <= 0f) step = campRt.rect.height * 1.25f;
            ((RectTransform)clone.transform).anchoredPosition = campRt.anchoredPosition - new Vector2(0f, step);
            foreach (RectTransform rt in below) rt.anchoredPosition -= new Vector2(0f, step);
        }
    }
}
