using System;
using UnityEngine;
using UnityEngine.UI;
using WotSMultiplayer.Core;

namespace WotSMultiplayer.Features
{
    /// <summary>
    /// Day/night bar on the campaign map (bottom left), replacing vanilla's scrolling light meter (user requests
    /// 2026-11-25). The bar shows the current phase from its start to its end:
    ///   day:   light yellow (morning) → yellow → evening orange, with a sun crossing from left to right;
    ///   night: dusk orange → black → lightening towards dawn, with a moon crossing from left to right.
    /// The phase's start and end times sit at the bar's ends, so the start and end of night are always readable.
    /// Night = the hours the game itself treats as dark in battle (Utilities.GetIsDark: Config.visibilityByHour below
    /// 0.75; 18:00–07:00 with the standard data), so the bar matches when battles are fought in the dark.
    /// Vanilla's LightMeter image (material CampaignInterface.lightMeter) is hidden while this bar is shown and comes
    /// back when the option is off. UI only. [General] ClearDayNightBar (MP, on) / [SinglePlayer] ClearDayNightBar (off).
    /// </summary>
    internal static class DayNightBar
    {
        private const float DarkBelow = 0.75f; // Utilities.GetIsDark

        private static bool applied, failed, loggedLayout;
        private static Image vanillaBar;
        private static GameObject root;
        private static RectTransform rootRect, iconRect;
        private static RawImage barImage, icon;
        private static Text leftLabel, rightLabel;
        private static Texture2D dayTex, nightTex, sunTex, moonTex;
        private static int rise = 7, set = 18;
        private static int shownPhase = -1; // 0 day, 1 night
        private static float nextCheck, nextHours;

        internal static void Update()
        {
            if (failed) return;
            CampaignInterface ci = CampaignInterface.instance;
            if (ci == null || ci.lightMeter == null) return;
            try
            {
                if (Time.realtimeSinceStartup >= nextCheck)
                {
                    nextCheck = Time.realtimeSinceStartup + 1f;
                    bool want = ModState.MultiplayerActive ? Plugin.ClearDayNightBar.Value : Plugin.ClearDayNightBarSP.Value;
                    bool alive = applied && root != null && vanillaBar != null;
                    if (want && !alive) { Remove(); Build(ci); }
                    else if (!want && applied) Remove();
                }
                if (applied && root != null && vanillaBar != null) Tick(ci);
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning("Day/night bar failed, the game's own bar is back: " + e);
                try { Remove(); } catch { }
            }
        }

        // ---------- build / remove ----------

        private static void Build(CampaignInterface ci)
        {
            vanillaBar = null;
            foreach (Image im in Resources.FindObjectsOfTypeAll<Image>())
                if (im != null && im.gameObject.scene.IsValid() && im.material == ci.lightMeter) { vanillaBar = im; break; }
            if (vanillaBar == null) { nextCheck = Time.realtimeSinceStartup + 5f; return; }
            if (!loggedLayout)
            {
                loggedLayout = true;
                RectTransform vr = vanillaBar.rectTransform;
                string kids = "";
                foreach (Transform c in vanillaBar.transform) kids += " " + c.name;
                Plugin.Log.LogInfo($"Day/night bar: vanilla '{vanillaBar.name}' {vr.rect.width:0}x{vr.rect.height:0} under " +
                                   $"'{(vr.parent != null ? vr.parent.name : "-")}', children:{(kids.Length > 0 ? kids : " none")}");
            }
            EnsureTextures();

            root = new GameObject("WotSMP_DayNightBar", typeof(RectTransform));
            root.layer = vanillaBar.gameObject.layer;
            rootRect = root.GetComponent<RectTransform>();
            rootRect.SetParent(vanillaBar.transform, false);
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;
            barImage = root.AddComponent<RawImage>();
            barImage.raycastTarget = false;

            Font font = ci.dateTimeText != null && ci.dateTimeText.Length > 1 && ci.dateTimeText[1] != null ? ci.dateTimeText[1].font : null;
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            leftLabel = MakeLabel("Start", font, TextAnchor.MiddleLeft, new Vector2(0f, 0f), new Vector2(0.5f, 1f), new Vector2(3f, 0f));
            rightLabel = MakeLabel("End", font, TextAnchor.MiddleRight, new Vector2(0.5f, 0f), new Vector2(1f, 1f), new Vector2(-3f, 0f));

            GameObject ig = new GameObject("Icon", typeof(RectTransform));
            ig.layer = root.layer;
            iconRect = ig.GetComponent<RectTransform>();
            iconRect.SetParent(rootRect, false);
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            icon = ig.AddComponent<RawImage>();
            icon.raycastTarget = false;

            vanillaBar.enabled = false;
            shownPhase = -1;
            nextHours = 0f;
            applied = true;
            Plugin.Log.LogInfo("Day/night bar: sun/moon bar shown");
        }

        private static Text MakeLabel(string name, Font font, TextAnchor align, Vector2 aMin, Vector2 aMax, Vector2 offset)
        {
            GameObject g = new GameObject(name, typeof(RectTransform));
            g.layer = root.layer;
            RectTransform r = g.GetComponent<RectTransform>();
            r.SetParent(rootRect, false);
            r.anchorMin = aMin;
            r.anchorMax = aMax;
            r.offsetMin = r.offsetMax = offset;
            Text t = g.AddComponent<Text>();
            t.font = font;
            t.alignment = align;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.fontStyle = FontStyle.Bold;
            t.raycastTarget = false;
            return t;
        }

        private static void Remove()
        {
            if (vanillaBar != null) vanillaBar.enabled = true;
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            vanillaBar = null;
            applied = false;
            shownPhase = -1;
        }

        // ---------- every frame ----------

        private static void Tick(CampaignInterface ci)
        {
            if (Time.realtimeSinceStartup >= nextHours) { nextHours = Time.realtimeSinceStartup + 10f; ReadHours(); }
            DateTime now = ci.dateTime;
            double h = now.Hour + now.Minute / 60.0 + now.Second / 3600.0;
            double dayLen = (set - rise + 24) % 24;
            if (dayLen <= 0) dayLen = 12;
            double sinceRise = (h - rise + 24) % 24;
            bool night = sinceRise >= dayLen;
            double f = night ? (sinceRise - dayLen) / (24 - dayLen) : sinceRise / dayLen;
            int phase = night ? 1 : 0;

            if (phase != shownPhase)
            {
                shownPhase = phase;
                barImage.texture = night ? nightTex : dayTex;
                icon.texture = night ? moonTex : sunTex;
                leftLabel.text = Clock(night ? set : rise);
                rightLabel.text = Clock(night ? rise : set);
                Color c = night ? new Color(0.88f, 0.88f, 0.95f) : new Color(0.30f, 0.17f, 0.04f);
                leftLabel.color = rightLabel.color = c;
            }

            Rect r = rootRect.rect;
            float size = Mathf.Max(12f, r.height * 1.45f);
            int fs = Mathf.Clamp(Mathf.RoundToInt(r.height * 0.72f), 8, 40);
            if (leftLabel.fontSize != fs) leftLabel.fontSize = rightLabel.fontSize = fs;
            iconRect.sizeDelta = new Vector2(size, size);
            float pad = size * 0.5f;
            iconRect.anchoredPosition = new Vector2(pad + (float)f * Mathf.Max(0f, r.width - 2f * pad), 0f);
            // the icon passes over the end labels: fade a label while the icon is on it
            SetAlpha(leftLabel, f < 0.22 ? 0.25f : 1f);
            SetAlpha(rightLabel, f > 0.78 ? 0.25f : 1f);
        }

        private static void SetAlpha(Text t, float a)
        {
            Color c = t.color;
            if (!Mathf.Approximately(c.a, a)) { c.a = a; t.color = c; }
        }

        private static string Clock(int hour) => (hour % 24).ToString("00") + ":00";

        /// <summary>Sunrise / sunset hours from the game's hourly visibility (first light hour after a dark one and back).</summary>
        private static void ReadHours()
        {
            float[] v = Config.instance != null ? Config.instance.visibilityByHour : null;
            if (v == null || v.Length != 24) return;
            int r = -1, s = -1;
            for (int i = 0; i < 24; i++)
            {
                bool dark = v[i] < DarkBelow, prevDark = v[(i + 23) % 24] < DarkBelow;
                if (prevDark && !dark && r < 0) r = i;
                if (!prevDark && dark && s < 0) s = i;
            }
            if (r < 0 || s < 0) { r = 6; s = 18; } // no change in the data (always light or always dark)
            if (r != rise || s != set)
            {
                rise = r; set = s;
                shownPhase = -1;
                Plugin.Log.LogInfo($"Day/night bar: day {Clock(rise)}–{Clock(set)}, night {Clock(set)}–{Clock(rise)} (game visibility < {DarkBelow})");
            }
        }

        // ---------- textures (made once) ----------

        private static void EnsureTextures()
        {
            if (dayTex != null) return;
            dayTex = Gradient(new[] { 0f, 0.45f, 0.80f, 1f }, new[]
            {
                new Color(1.00f, 0.95f, 0.70f), // light morning yellow
                new Color(1.00f, 0.87f, 0.42f), // midday yellow
                new Color(1.00f, 0.66f, 0.28f), // afternoon orange
                new Color(0.93f, 0.48f, 0.17f), // evening orange
            });
            nightTex = Gradient(new[] { 0f, 0.14f, 0.32f, 0.68f, 0.88f, 1f }, new[]
            {
                new Color(0.86f, 0.42f, 0.16f), // dusk orange
                new Color(0.36f, 0.15f, 0.10f), // fading
                new Color(0.03f, 0.03f, 0.06f), // black
                new Color(0.04f, 0.05f, 0.11f), // deep night
                new Color(0.22f, 0.25f, 0.40f), // lightening
                new Color(0.58f, 0.56f, 0.66f), // first light
            });
            sunTex = MakeSun(48);
            moonTex = MakeMoon(48);
        }

        private static Texture2D Gradient(float[] stops, Color[] colors)
        {
            const int w = 256;
            var t = new Texture2D(w, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            t.hideFlags = HideFlags.HideAndDontSave;
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)(w - 1);
                Color c = colors[colors.Length - 1];
                for (int i = 1; i < stops.Length; i++)
                    if (u <= stops[i]) { c = Color.Lerp(colors[i - 1], colors[i], (u - stops[i - 1]) / (stops[i] - stops[i - 1])); break; }
                t.SetPixel(x, 0, c);
            }
            t.Apply(false);
            return t;
        }

        private static Texture2D NewIcon(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private static Texture2D MakeSun(int n)
        {
            Texture2D t = NewIcon(n);
            float c = (n - 1) / 2f, R = n * 0.24f, ring = n * 0.03f, r0 = n * 0.31f, r1 = n * 0.46f, rayHalf = n * 0.035f;
            var core = new Color(1f, 0.95f, 0.55f);
            var edge = new Color(1f, 0.78f, 0.18f);
            var line = new Color(0.78f, 0.36f, 0.04f);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color px = new Color(0, 0, 0, 0);
                    // rays: 8 spokes
                    float ang = Mathf.Atan2(dy, dx);
                    float k = Mathf.Round(ang / (Mathf.PI / 4f)) * (Mathf.PI / 4f);
                    float off = Mathf.Abs(Mathf.Sin(ang - k)) * d; // distance from the spoke's line
                    float rayA = Mathf.Clamp01(rayHalf + 0.5f - off) * Mathf.Clamp01(d - r0 + 0.5f) * Mathf.Clamp01(r1 - d + 0.5f);
                    if (rayA > 0f) px = new Color(0.98f, 0.60f, 0.10f, rayA);
                    // disc with an outline
                    float discA = Mathf.Clamp01(R + ring - d + 0.5f);
                    if (discA > 0f)
                    {
                        Color fill = d <= R ? Color.Lerp(core, edge, d / R) : line;
                        fill = Color.Lerp(fill, line, Mathf.Clamp01(d - R + 0.5f));
                        px = Color.Lerp(px, new Color(fill.r, fill.g, fill.b, 1f), discA);
                        px.a = Mathf.Max(px.a, discA);
                    }
                    t.SetPixel(x, y, px);
                }
            t.Apply(false);
            return t;
        }

        private static Texture2D MakeMoon(int n)
        {
            Texture2D t = NewIcon(n);
            float c = (n - 1) / 2f, R = n * 0.27f, glow = n * 0.40f;
            float bx = c + n * 0.13f, by = c + n * 0.09f, bR = n * 0.23f; // the dark disc that cuts the crescent
            var lit = new Color(0.95f, 0.95f, 1f);
            var rim = new Color(0.78f, 0.80f, 0.92f);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x - c, dy = y - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                    float ex = x - bx, ey = y - by, e = Mathf.Sqrt(ex * ex + ey * ey);
                    float inA = Mathf.Clamp01(R - d + 0.5f);
                    float outB = Mathf.Clamp01(e - bR + 0.5f);
                    float moon = inA * outB;
                    float halo = Mathf.Clamp01((glow - d) / (glow - R)) * 0.22f * (1f - moon);
                    Color fill = Color.Lerp(lit, rim, Mathf.Clamp01(d / R));
                    Color px = new Color(0.85f, 0.88f, 1f, halo);
                    if (moon > 0f) px = Color.Lerp(px, new Color(fill.r, fill.g, fill.b, 1f), moon);
                    t.SetPixel(x, y, px);
                }
            t.Apply(false);
            return t;
        }
    }
}
