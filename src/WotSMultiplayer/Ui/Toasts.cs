using System.Collections.Generic;
using UnityEngine;

namespace WotSMultiplayer.Ui
{
    /// <summary>
    /// Short on-screen notifications drawn with IMGUI (top-centre). Independent of the game's own
    /// message log, which only exists in battle and needs pre-registered message keys.
    /// Uses unscaled real time so toasts behave the same when the game is paused or sped up.
    /// </summary>
    internal static class Toasts
    {
        private sealed class Toast
        {
            public string Text;
            public float Until;
        }

        private const int MaxVisible = 5;
        private static readonly List<Toast> items = new List<Toast>();
        private static GUIStyle style;

        internal static void Show(string text, float seconds = 6f)
        {
            // toasts can carry text from the other PC (chat, notices): one line in the log, a sane length on screen
            text = text ?? "";
            if (text.Length > 600) text = text.Substring(0, 600) + "…";
            Plugin.Log.LogInfo("[toast] " + text.Replace('\r', ' ').Replace('\n', ' '));
            items.Add(new Toast { Text = text, Until = Time.realtimeSinceStartup + seconds });
            while (items.Count > MaxVisible) items.RemoveAt(0);
        }

        internal static void Draw()
        {
            float now = Time.realtimeSinceStartup;
            items.RemoveAll(t => t.Until < now);
            if (items.Count == 0) return;

            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box) { fontSize = 14, wordWrap = true, alignment = TextAnchor.MiddleCenter, richText = false };
                style.normal.textColor = Color.white;
                style.padding = new RectOffset(10, 10, 6, 6);
            }

            float screenW = UiScale.Width;
            float width = Mathf.Min(560f, screenW - 40f);
            float y = 60f;
            foreach (Toast t in items)
            {
                float h = style.CalcHeight(new GUIContent(t.Text), width);
                GUI.Box(new Rect((screenW - width) / 2f, y, width, h), t.Text, style);
                y += h + 4f;
            }
        }
    }
}
