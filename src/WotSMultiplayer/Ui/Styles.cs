using UnityEngine;

namespace WotSMultiplayer.Ui
{
    /// <summary>Shared IMGUI styles. The default window skin is translucent, which is hard to read over the menus.</summary>
    internal static class Styles
    {
        /// <summary>
        /// Button that fires on mouse-DOWN. In the M4 playtest, GUILayout.Button clicks in the speed widget were lost
        /// when the widget sat on the right side of the campaign screen (hover and window drag still worked, so the
        /// mouse-up was being lost there; cause not found). Acting on mouse-down avoids it. Draw-only otherwise, so
        /// it never double-fires.
        /// </summary>
        internal static bool DownButton(string text, params GUILayoutOption[] options)
        {
            GUIContent content = new GUIContent(text);
            Rect r = GUILayoutUtility.GetRect(content, GUI.skin.button, options);
            Event e = Event.current;
            bool hover = r.Contains(e.mousePosition);
            if (GUI.enabled && e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }
            if (e.type == EventType.Repaint)
            {
                Color old = GUI.color;
                if (!GUI.enabled) GUI.color = new Color(old.r, old.g, old.b, old.a * 0.45f);
                GUI.skin.button.Draw(r, content, GUI.enabled && hover, false, false, false);
                GUI.color = old;
            }
            return false;
        }

        private static GUIStyle window;

        internal static GUIStyle Window
        {
            get
            {
                if (window == null)
                {
                    Texture2D bg = new Texture2D(1, 1);
                    bg.SetPixel(0, 0, new Color(0.08f, 0.10f, 0.13f, 0.96f));
                    bg.Apply();
                    bg.hideFlags = HideFlags.HideAndDontSave;
                    window = new GUIStyle(GUI.skin.window);
                    window.normal.background = bg;
                    window.onNormal.background = bg;
                    window.normal.textColor = window.onNormal.textColor = new Color(0.95f, 0.85f, 0.55f);
                    window.padding = new RectOffset(10, 10, 22, 10);
                }
                return window;
            }
        }
    }
}
