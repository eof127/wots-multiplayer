using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace WotSMultiplayer.Ui
{
    /// <summary>
    /// IMGUI windows don't stop clicks reaching the game's uGUI underneath (e.g. main-menu buttons such as Quit).
    /// While the mouse is over a mod window, the game's EventSystem is switched off; it is restored as soon as the
    /// mouse leaves. Windows call Register(rect) every OnGUI (virtual, pre-scale coordinates).
    /// </summary>
    internal static class UiBlock
    {
        private static readonly List<Rect> current = new List<Rect>();
        private static readonly List<Rect> last = new List<Rect>();
        private static EventSystem disabledByUs;

        internal static void Register(Rect r) { if (Event.current.type == EventType.Repaint) current.Add(r); }

        /// <summary>Call at the start of OnGUI.</summary>
        internal static void BeginFrame()
        {
            if (Event.current.type != EventType.Repaint) return;
            last.Clear();
            last.AddRange(current);
            current.Clear();
        }

        internal static void Update()
        {
            float f = UiScale.Factor;
            Vector2 m = new Vector2(Input.mousePosition.x / f, (Screen.height - Input.mousePosition.y) / f);
            bool over = false;
            foreach (Rect r in last) if (r.Contains(m)) { over = true; break; }

            EventSystem es = EventSystem.current;
            if (over && es != null && es.enabled) { es.enabled = false; disabledByUs = es; }
            else if (!over && disabledByUs != null)
            {
                if (disabledByUs) disabledByUs.enabled = true;
                disabledByUs = null;
            }
        }
    }
}
