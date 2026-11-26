using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// D56 (user, 2026-11-25): a task force moves only as fast as its slowest ship, battle damage included.
    /// Vanilla CampaignManager.GetMobileMapObjectSpeed already uses the slowest ship's design speed (maxKnots × the campaign's
    /// cruise factor) but ignores damage. Here:
    ///   - each ship's speed factor comes from its saved damage (MobileMapObject.cellVariables: the per-cell JSON the game
    ///     restores in the next battle) with the game's own battle formula (DamageableSea.GetSpeedPenalty and its Config
    ///     values), counting only DESTROYED propellers, funnels and engine rooms plus flooded compartments — machinery that is
    ///     only knocked out (offline) doesn't slow the force on the map (user, 2026-11-26: "Only destroyed ones"; before,
    ///     the battle-end GetSpeedPenalty value, offline parts included, was stored: a cruiser with its engines knocked out
    ///     held its force to 1 kn). A repaired ship (empty damage record) counts as undamaged;
    ///   - GetMobileMapObjectSpeed scales vanilla's result by (slowest damaged speed / slowest design speed);
    ///   - the fleet panel shows a note when damage is what limits the force.
    /// [Campaign] DamagedShipsSlowTaskForces (multiplayer, default on) / [SinglePlayer] DamagedShipsSlowTaskForces (off).
    /// No unit stats change: design speeds and battle damage are the game's own.
    /// </summary>
    internal static class ShipSpeedLimit
    {
        internal static bool Active => ModState.MultiplayerActive ? Plugin.DamagedShipsSlowTaskForces.Value : Plugin.DamagedShipsSlowTaskForcesSP.Value;

        /// <summary>Fraction of design speed ship i can make (1 = undamaged).</summary>
        internal static float DamageFactor(MobileMapObject m, int i)
        {
            if (m.cellVariables == null || i >= m.cellVariables.Count || m.unitPrefabs == null || i >= m.unitPrefabs.Count) return 1f;
            return FactorFromDamage(m.unitPrefabs[i], m.cellVariables[i]);
        }

        private static readonly Dictionary<string, float> factorCache = new Dictionary<string, float>();

        /// <summary>
        /// Speed fraction from a ship's saved damage: the game's GetSpeedPenalty formula, destroyed parts only (see the class
        /// notes). Never below 5 % (a ship that can still steam). Cached by damage record.
        /// </summary>
        internal static float FactorFromDamage(string prefab, string vars)
        {
            if (string.IsNullOrEmpty(vars) || vars == "DESTROYED" || string.IsNullOrEmpty(prefab)) return 1f;
            string key = prefab + "\n" + vars;
            float cached;
            if (factorCache.TryGetValue(key, out cached)) return cached;
            float f = 1f;
            try
            {
                UnitData d;
                Config c = Config.instance;
                if (c != null && EngagementManager.instance != null && EngagementManager.instance.unitDataDictionary.TryGetValue(prefab, out d) &&
                    d != null && d.cellDictionary != null && d.cellDeckList != null)
                {
                    int props = 0, engines = 0, funnels = 0, propsLost = 0, enginesLost = 0, funnelsLost = 0, flooded = 0;
                    foreach (KeyValuePair<string, Cell> kv in d.cellDictionary)
                    {
                        if (IsPropeller(kv.Value.cellType)) props++;
                        else if (kv.Value.cellType == CellType.Engineering) engines++;
                    }
                    foreach (Cell cell in d.cellDeckList) if (cell.cellType == CellType.Funnel) funnels++;
                    string[] a = vars.Split('|');
                    var cv = new CellVariable();
                    for (int i = 0; i + 1 < a.Length; i += 2)
                    {
                        Cell cell;
                        if (a[i].Contains(","))
                        {
                            if (!d.cellDictionary.TryGetValue(a[i], out cell)) continue;
                        }
                        else
                        {
                            int idx;
                            if (!int.TryParse(a[i], out idx) || idx < 0 || idx >= d.cellDeckList.Count) continue;
                            cell = d.cellDeckList[idx];
                        }
                        cv.isDestroyed = false;
                        cv.isFlooded = 0f;
                        JsonUtility.FromJsonOverwrite(a[i + 1], cv);
                        if (cv.isDestroyed)
                        {
                            if (IsPropeller(cell.cellType)) propsLost++;
                            else if (cell.cellType == CellType.Engineering) enginesLost++;
                            else if (cell.cellType == CellType.Funnel) funnelsLost++;
                        }
                        else if (cv.isFlooded != 0f) flooded++; // (the game unfloods destroyed cells when it loads them)
                    }
                    if (props > 0) f -= c.speedPenaltyNoPropellers * propsLost / props;
                    if (funnels > 0) f -= c.speedPenaltyNoFunnels * funnelsLost / funnels;
                    int hull = d.cellDictionary.Count - d.cellDeckList.Count; // the game's own denominator
                    if (hull > 0) f -= (float)flooded / hull * c.speedPenaltyFloodedHull;
                    if (engines > 0) f -= c.speedPenaltyNoEngineering * enginesLost / engines;
                    f = Mathf.Clamp(f, 0.05f, 1f);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Task force speed: reading the damage of " + prefab + " failed: " + e.Message);
                f = 1f;
            }
            if (factorCache.Count > 2000) factorCache.Clear();
            factorCache[key] = f;
            return f;
        }

        private static bool IsPropeller(CellType t) =>
            t == CellType.Propeller || t == CellType.DoublePropeller || t == CellType.PropellersRudder;

        internal struct ShipSpeed { public int Index; public float Knots; public float Factor; public string Name; }

        /// <summary>Design speed × damage factor of every ship (knots), slowest first.</summary>
        internal static List<ShipSpeed> Speeds(MobileMapObject m)
        {
            var list = new List<ShipSpeed>();
            if (m == null || m.type != 0 || m.unitPrefabs == null) return list;
            Dictionary<string, UnitData> dict = EngagementManager.instance != null ? EngagementManager.instance.unitDataDictionary : null;
            if (dict == null) return list;
            for (int i = 0; i < m.unitPrefabs.Count; i++)
            {
                UnitData d;
                if (!dict.TryGetValue(m.unitPrefabs[i], out d) || d == null) continue;
                float f = DamageFactor(m, i);
                string name = m.unitDisplayNames != null && i < m.unitDisplayNames.Count ? m.unitDisplayNames[i] : m.unitPrefabs[i];
                list.Add(new ShipSpeed { Index = i, Knots = d.maxKnots * f, Factor = f, Name = name });
            }
            list.Sort((a, b) => a.Knots.CompareTo(b.Knots));
            return list;
        }

        // ---------- the note above the fleet panel's speed ----------

        private static MobileMapObject noteFor;
        private static string note;
        private static GUIStyle noteStyle;
        private static readonly Vector3[] corners = new Vector3[4];

        internal static void PanelShown(MobileMapObject m)
        {
            note = null;
            noteFor = null;
            if (!Active || m == null || m.type != 0 || m.currentFaction != 0) return;
            List<ShipSpeed> s = Speeds(m);
            if (s.Count == 0) return;
            // only when battle damage slows the force (user test 2026-11-26: the note showed for every force whose ships
            // simply have different design speeds, which vanilla already handles)
            float design = float.MaxValue;
            foreach (ShipSpeed x in s) design = Mathf.Min(design, x.Factor > 0f ? x.Knots / x.Factor : x.Knots);
            if (s[0].Factor >= 0.999f || design - s[0].Knots < 0.25f) return;
            float cruise = CampaignManager.instance != null ? CampaignManager.instance.campaignData.taskForceCruiseSpeed : 1f;
            note = $"Speed limited to {s[0].Knots * cruise:0} kn: {s[0].Name} is damaged ({s[0].Factor * 100f:0}% of its speed). " +
                   $"Undamaged the force would make {design * cruise:0} kn" +
                   (s.Count > 1 ? $"; without {s[0].Name} it makes {s[1].Knots * cruise:0} kn." : ".");
            noteFor = m;
        }

        internal static void OnGUI()
        {
            if (note == null || noteFor == null) return;
            CampaignInterface ci = CampaignInterface.instance;
            if (ci == null || ci.currentMapObject == null || ci.currentMapObject.mobileMapObject != noteFor ||
                !ci.mobileMapText.transform.parent.gameObject.activeInHierarchy) return;
            // next to the panel's Rename box (top right of the task force panel; user request 2026-11-25), else beside the
            // panel title, else above the speed
            RectTransform anchor = null;
            bool beside = true;
            if (ci.renameField != null && ci.renameField.gameObject.activeInHierarchy) anchor = ci.renameField.GetComponent<RectTransform>();
            else if (ci.mobileMapText != null && ci.mobileMapText.gameObject.activeInHierarchy) anchor = ci.mobileMapText.rectTransform;
            else if (ci.speedDisplay != null && ci.speedDisplay.gameObject.activeInHierarchy) { anchor = ci.speedDisplay.rectTransform; beside = false; }
            Rect r;
            if (anchor == null || !ScreenRect(anchor, out r)) return;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.identity; // screen pixels
            try
            {
                float s = Mathf.Max(0.7f, Screen.height / 1080f);
                if (noteStyle == null) noteStyle = new GUIStyle(GUI.skin.box) { wordWrap = true, alignment = TextAnchor.MiddleLeft, richText = false };
                noteStyle.fontSize = Mathf.RoundToInt(13f * s);
                noteStyle.normal.textColor = new Color(1f, 0.85f, 0.45f);
                float w = 360f * s;
                float h = noteStyle.CalcHeight(new GUIContent(note), w);
                float x, y;
                if (beside && r.xMax + 6f * s + w <= Screen.width - 4f) { x = r.xMax + 6f * s; y = r.y; }   // to the right of Rename
                else if (beside) { x = Mathf.Clamp(r.x, 4f, Screen.width - w - 4f); y = r.yMax + 4f * s; } // below it
                else { x = Mathf.Clamp(r.x, 4f, Screen.width - w - 4f); y = r.y - h - 4f * s; if (y < 4f) y = r.yMax + 4f * s; }
                GUI.Box(new Rect(x, Mathf.Clamp(y, 4f, Screen.height - h - 4f), w, h), note, noteStyle);
            }
            finally { GUI.matrix = old; }
        }

        /// <summary>A UI element's rectangle in IMGUI screen coordinates (origin top-left).</summary>
        private static bool ScreenRect(RectTransform rt, out Rect r)
        {
            r = new Rect();
            Canvas canvas = rt.GetComponentInParent<Canvas>();
            if (canvas == null) return false;
            rt.GetWorldCorners(corners);
            Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
            r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Max(a.x, b.x), Screen.height - Mathf.Min(a.y, b.y));
            return true;
        }
    }

    [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.GetMobileMapObjectSpeed))]
    internal static class ShipSpeedLimitSpeedPatch
    {
        private static void Postfix(MobileMapObject mmo, ref float __result)
        {
            if (!ShipSpeedLimit.Active || mmo == null || mmo.type != 0 || mmo.unitPrefabs == null || mmo.unitPrefabs.Count == 0) return;
            try
            {
                Dictionary<string, UnitData> dict = EngagementManager.instance.unitDataDictionary;
                float design = float.MaxValue, damaged = float.MaxValue;
                for (int i = 0; i < mmo.unitPrefabs.Count; i++)
                {
                    UnitData d;
                    if (!dict.TryGetValue(mmo.unitPrefabs[i], out d) || d == null) continue;
                    design = Mathf.Min(design, d.maxKnots);
                    damaged = Mathf.Min(damaged, d.maxKnots * ShipSpeedLimit.DamageFactor(mmo, i));
                }
                if (design > 0f && design < float.MaxValue && damaged < design) __result *= damaged / design;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Task force speed (damage) failed: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(EngagementData), nameof(EngagementData.ProcessUnitsPostCombat))]
    internal static class ShipSpeedLimitRecordPatch
    {
        private static void Postfix(MobileMapObject mmo, List<Unit> unitsToCheck, int startIndex)
        {
            if (!ShipSpeedLimit.Active || mmo == null || mmo.type != 0 || unitsToCheck == null) return;
            if (ModState.MultiplayerActive && !Core.Multiplayer.Session.IsHost) return; // the host's campaign decides
            try
            {
                // log only: the factor is read from the saved damage when needed (FactorFromDamage); the battle-end
                // GetSpeedPenalty value isn't used any more (it counted knocked-out parts, and for the AI side it also
                // switches radar on and may order a retreat)
                string slowest = null;
                for (int i = 0; i < mmo.unitPrefabs.Count && mmo.cellVariables != null && i < mmo.cellVariables.Count; i++)
                {
                    float f = ShipSpeedLimit.FactorFromDamage(mmo.unitPrefabs[i], mmo.cellVariables[i]);
                    if (f < 0.999f) slowest = (slowest == null ? "" : slowest + ", ") + $"{mmo.unitPrefabs[i]} {f * 100f:0}%";
                }
                if (slowest != null) Plugin.Log.LogInfo($"Task force '{mmo.mobileName}': damaged ships' speed after the battle: {slowest} (D56, destroyed parts only)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Recording damaged speeds failed: " + e.Message); }
        }
    }

    /// <summary>After the battle's losses are applied: the force's speed with the damage (used from its next leg on).</summary>
    [HarmonyPatch(typeof(CampaignManager), "ProcessMobileObjectPostCombat")]
    internal static class ShipSpeedLimitAfterBattlePatch
    {
        private static void Postfix(CampaignManager __instance, MobileMapObject mmo)
        {
            if (!ShipSpeedLimit.Active || mmo == null || mmo.type != 0 || mmo.unitPrefabs == null || mmo.unitPrefabs.Count == 0) return;
            if (mmo.gameObjectAnimator == null || (!__instance.playerMobile.Contains(mmo.gameObjectAnimator) && !__instance.otherMobile.Contains(mmo.gameObjectAnimator))) return;
            try
            {
                float before = mmo.speed;
                mmo.speed = __instance.GetMobileMapObjectSpeed(mmo);
                if (mmo.speed < before - 0.01f)
                    Plugin.Log.LogInfo($"Task force '{mmo.mobileName}': {before * 1.943846f:0.0} → {mmo.speed * 1.943846f:0.0} kn after battle damage (D56)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Task force speed after battle failed: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(CampaignInterface), nameof(CampaignInterface.UpdateMobileForceDisplay))]
    internal static class ShipSpeedLimitPanelPatch
    {
        private static void Postfix(MobileMapObject mmo)
        {
            try { ShipSpeedLimit.PanelShown(mmo); }
            catch (Exception e) { Plugin.Log.LogWarning("Task force speed note failed: " + e.Message); }
        }
    }
}
