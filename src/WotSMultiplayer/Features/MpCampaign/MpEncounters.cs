using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WorldMapStrategyKit;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// M4d: encounters between the two players. In MP every vanilla encounter is "host force vs Player 2 force".
    /// The host detects it (vanilla CheckMobileEncounters); both players get the vanilla encounter panel plus a 15 s
    /// time bar (D37). Engage/Ignore follow D19/D39 (EncounterRules). Messages:
    ///   36 EncounterOpen   host→client {id, guest mpId, host mpId, ignorable, weather cell, host name, guest name}
    ///   37 EncounterChoice client→host {id, choice}
    ///   38 EncounterState  host→client {id, host choice, guest choice, outcome, text}
    /// A fought encounter would start a live battle (M6). Until that exists it is announced and skipped (D36).
    /// Ignored/skipped pairs don't meet again until they have separated (detection range), without blocking other
    /// encounters (the pair's distance is hidden from the encounter scan).
    /// </summary>
    internal static class MpEncounters
    {
        private const byte MsgOpen = 36, MsgChoice = 37, MsgState = 38;

        private sealed class Encounter
        {
            public int Id;
            public bool Ignorable;
            public EncounterChoice Host, Guest;
            public float Started;            // Time.realtimeSinceStartup when opened on this PC
            public GameObjectAnimator HostGoa, GuestGoa; // host only
            public string HostName = "", GuestName = "";
            public bool OtherDecided;        // client: the host has chosen
            public bool Watching;            // subordinate (stage 2): sees the encounter, the commanders decide
            public bool HostDecided, GuestDecided; // subordinate: who has chosen
            public bool OwnIsHost;           // subordinate: its team is the host's (team 0)
        }

        private static Encounter current;
        private static int nextId = 1;
        private static readonly List<GameObjectAnimator[]> ignoredPairs = new List<GameObjectAnimator[]>();
        private static float lastPrune;
        internal static bool InScan;

        private static bool IsHost => ModState.MultiplayerActive && Multiplayer.Session.IsHost;
        private static bool IsClient => ModState.MultiplayerActive && !Multiplayer.Session.IsHost;

        internal static void Init() => Multiplayer.Session.OnGameMessage += OnMessage;

        // ---------- host ----------

        /// <summary>Host, from the OpenEngagementPanel prefix. False = skip the panel (pair already ignored).</summary>
        internal static bool HostOnPanelOpening()
        {
            CampaignManager cm = CampaignManager.instance;
            CampaignData cd = cm.campaignData;
            if (cd.playerObjectEngaged < 0 || cd.playerObjectEngaged >= cm.playerMobile.Count ||
                cd.otherObjectEngaged < 0 || cd.otherObjectEngaged >= cm.otherMobile.Count) return true;
            GameObjectAnimator h = cm.playerMobile[cd.playerObjectEngaged];
            GameObjectAnimator g = cm.otherMobile[cd.otherObjectEngaged];
            if (IsIgnoredPair(h, g))
            {
                EngagementPanel.instance.ClearEncounterData();
                return false;
            }
            bool ignorable = EncounterRules.Ignorable(Side(h.mobileMapObject), Side(g.mobileMapObject));
            cd.canIgnore = ignorable;
            cd.canAutoResolve = false;
            current = new Encounter
            {
                Id = nextId++, Ignorable = ignorable, Started = Time.realtimeSinceStartup,
                HostGoa = h, GuestGoa = g, HostName = h.mobileMapObject.mobileName, GuestName = g.mobileMapObject.mobileName,
            };
            int guestId = CampaignSync.HostIdOf(g), hostId = CampaignSync.HostIdOf(h);
            Encounter e = current;
            byte[] open = Protocol.Build(w =>
            {
                w.Write(e.Id); w.Write(guestId); w.Write(hostId); w.Write(e.Ignorable); w.Write(cd.engagementWeatherIndex);
                w.Write(e.HostName ?? ""); w.Write(e.GuestName ?? "");
            });
            if (Multiplayer.Session.IsConnected) Multiplayer.Session.SendGame(MsgOpen, open);
            Multiplayer.Session.SendGameToSubordinates(MsgOpen, open); // stage 2: subordinates watch (each reads it for its team)
            bool vanilla = EncounterRules.VanillaIgnorable(Side(h.mobileMapObject), Side(g.mobileMapObject));
            Plugin.Log.LogInfo($"MP encounter #{e.Id}: {e.HostName} vs P2 {e.GuestName}, ignorable={ignorable} (D49; vanilla would allow ignoring: {vanilla})");
            return true;
        }

        private static EncounterSide Side(MobileMapObject m)
        {
            UnitData lead = m.unitPrefabs.Count > 0 && EngagementManager.instance.unitDataDictionary.ContainsKey(m.unitPrefabs[0])
                ? EngagementManager.instance.unitDataDictionary[m.unitPrefabs[0]] : null;
            return new EncounterSide
            {
                IsAir = m.type == 1,
                LeadIsScout = lead != null && lead.unitSubtype == UnitSubType.Scout,
                LeadIsAir = lead != null && lead.unitType == UnitType.Air,
                Count = m.unitPrefabs.Count,
                SubOnly = CampaignManager.instance.IsMobileSubOnly(m),
            };
        }

        private static void TryResolve()
        {
            Encounter e = current;
            if (e == null) return;
            // a force vanished (sunk/landed/merged) → nothing to decide
            if (e.HostGoa == null || e.GuestGoa == null || e.HostGoa.mobileMapObject == null || e.GuestGoa.mobileMapObject == null)
            {
                Finish(EncounterOutcome.Cancelled);
                return;
            }
            bool late = Time.realtimeSinceStartup - e.Started >= EncounterRules.DecisionSeconds;
            EncounterOutcome o = EncounterRules.Resolve(e.Ignorable, e.Host, e.Guest, late);
            if (o != EncounterOutcome.Pending) Finish(o);
        }

        private static void Finish(EncounterOutcome o)
        {
            Encounter e = current;
            current = null;
            CampaignManager cm = CampaignManager.instance;
            string hostText, guestText;
            bool startBattle = false;
            switch (o)
            {
                case EncounterOutcome.Ignored:
                    if (e.GuestGoa != null && e.GuestGoa.mobileMapObject != null) e.GuestGoa.mobileMapObject.ignoreByPlayer = true; // vanilla Ignore
                    AddIgnoredPair(e.HostGoa, e.GuestGoa);
                    hostText = $"Encounter ignored by both: {e.HostName} and the enemy {e.GuestName} pass each other.";
                    guestText = $"Encounter ignored by both: {e.GuestName} and the enemy {e.HostName} pass each other.";
                    break;
                case EncounterOutcome.Fight:
                    if (MpBattle.MpBattle.LiveBattlesEnabled)
                    {
                        // D44: a live two-player battle (MpBattle); [Multiplayer] LiveBattles = false falls back to D36 below.
                        hostText = $"Battle: {e.HostName} vs {e.GuestName}!";
                        guestText = $"Battle: {e.GuestName} vs {e.HostName}!";
                        startBattle = true;
                        break;
                    }
                    // D36 fallback (live battles switched off by the host): no AI-run battles, so announce and skip.
                    AddIgnoredPair(e.HostGoa, e.GuestGoa);
                    hostText = $"Battle: {e.HostName} vs {e.GuestName}. Live battles are switched off in the host's settings, so it is skipped.";
                    guestText = $"Battle: {e.GuestName} vs {e.HostName}. Live battles are switched off in the host's settings, so it is skipped.";
                    break;
                default:
                    hostText = guestText = "Encounter cancelled (a force is no longer there).";
                    break;
            }
            if (EngagementPanel.instance != null)
            {
                EngagementPanel.instance.ClearEncounterData();
                EngagementPanel.instance.gameObject.SetActive(false);
            }
            Toasts.Show(hostText);
            Plugin.Log.LogInfo($"MP encounter #{e.Id}: {o} (host {e.Host}, P2 {e.Guest})");
            SendState(e, o, guestText, hostText);
            if (startBattle) MpBattle.MpBattle.HostStart(e.HostGoa, e.GuestGoa);
        }

        /// <summary>The encounter's state to Player 2 and its team (guest text), and to the host's team (host text).</summary>
        private static void SendState(Encounter e, EncounterOutcome o, string guestText, string hostText = null)
        {
            Func<string, byte[]> build = text => Protocol.Build(w =>
            {
                w.Write(e.Id); w.Write((byte)e.Host); w.Write((byte)e.Guest); w.Write((byte)o); w.Write(text ?? "");
            });
            byte[] guest = build(guestText);
            if (Multiplayer.Session.IsConnected) Multiplayer.Session.SendGame(MsgState, guest);
            Multiplayer.Session.SendGameToSubordinates(MsgState, guest, team: 1);
            if (Multiplayer.Session.HasSubordinates(0))
                Multiplayer.Session.SendGameToSubordinates(MsgState, hostText == null || hostText == guestText ? guest : build(hostText), team: 0);
        }

        private static void AddIgnoredPair(GameObjectAnimator h, GameObjectAnimator g)
        {
            if (h != null && g != null && !IsIgnoredPair(h, g)) ignoredPairs.Add(new[] { h, g });
        }

        private static bool IsIgnoredPair(GameObjectAnimator a, GameObjectAnimator b)
        {
            foreach (GameObjectAnimator[] p in ignoredPairs)
                if ((p[0] == a && p[1] == b) || (p[0] == b && p[1] == a)) return true;
            return false;
        }

        /// <summary>Encounter-scan helper: are these the positions of an ignored pair?</summary>
        internal static bool IsIgnoredPositions(Vector2 a, Vector2 b)
        {
            for (int i = 0; i < ignoredPairs.Count; i++)
            {
                GameObjectAnimator h = ignoredPairs[i][0], g = ignoredPairs[i][1];
                if (h == null || g == null) continue;
                Vector2 p = h.currentMap2DLocation, q = g.currentMap2DLocation;
                if ((a == p && b == q) || (a == q && b == p)) return true;
            }
            return false;
        }

        internal static bool HasIgnoredPairs => ignoredPairs.Count > 0;

        /// <summary>A pair may meet again once it has separated beyond the encounter detection range (or a side is gone).</summary>
        private static void PruneIgnoredPairs()
        {
            CampaignManager cm = CampaignManager.instance;
            if (cm == null) return;
            float maxKm = cm.campaignData.maxCampaignDetectionRange;
            for (int i = ignoredPairs.Count - 1; i >= 0; i--)
            {
                GameObjectAnimator h = ignoredPairs[i][0], g = ignoredPairs[i][1];
                if (h == null || g == null || h.mobileMapObject == null || g.mobileMapObject == null ||
                    cm.GetDistanceInMetresBetweenMapObjects(h.currentMap2DLocation, g.currentMap2DLocation) / 1000f > maxKm)
                    ignoredPairs.RemoveAt(i);
            }
        }

        // ---------- both ----------

        /// <summary>The panel's Engage (StartCampaignEngagement) / Ignore buttons in MP.</summary>
        internal static void Choose(EncounterChoice c)
        {
            Encounter e = current;
            if (e == null)
            {
                // a stale panel (e.g. reopened by a load): close it, never start an AI-run battle (D36)
                if (EngagementPanel.instance != null)
                {
                    EngagementPanel.instance.ClearEncounterData();
                    EngagementPanel.instance.gameObject.SetActive(false);
                }
                return;
            }
            if (e.Watching)
            {
                Toasts.Show("Only Player 1 and Player 2 decide encounters. You see it as it happens.");
                return;
            }
            if (IsHost)
            {
                if (e.Host != EncounterChoice.None) return;
                e.Host = c;
                SendState(e, EncounterOutcome.Pending, "");
                TryResolve();
            }
            else
            {
                if (e.Guest != EncounterChoice.None) return;
                e.Guest = c;
                int id = e.Id;
                Multiplayer.Session.SendGame(MsgChoice, Protocol.Build(w => { w.Write(id); w.Write((byte)c); }));
            }
        }

        internal static void Update()
        {
            if (!ModState.MultiplayerActive)
            {
                current = null;
                ignoredPairs.Clear();
                return;
            }
            if (!IsHost) return;
            if (current != null) TryResolve();
            if (ignoredPairs.Count > 0 && Time.realtimeSinceStartup - lastPrune > 1f)
            {
                lastPrune = Time.realtimeSinceStartup;
                PruneIgnoredPairs();
            }
        }

        private static void OnMessage(byte type, byte[] payload)
        {
            if (type < MsgOpen || type > MsgState || !ModState.MultiplayerActive) return;
            try
            {
                using (var r = Protocol.Reader(payload))
                {
                    if (type == MsgChoice && IsHost)
                    {
                        int id = r.ReadInt32();
                        var c = (EncounterChoice)r.ReadByte();
                        Encounter e = current;
                        if (e == null || e.Id != id || e.Guest != EncounterChoice.None) return;
                        if (c != EncounterChoice.Engage && c != EncounterChoice.Ignore) return;
                        e.Guest = c;
                        SendState(e, EncounterOutcome.Pending, "");
                        TryResolve();
                    }
                    else if (type == MsgOpen && IsClient) ClientOpen(r);
                    else if (type == MsgState && IsClient) ClientState(r);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("MP encounter message " + type + " failed: " + ex);
            }
        }

        // ---------- client ----------

        private static void ClientOpen(System.IO.BinaryReader r)
        {
            int id = r.ReadInt32(), guestId = r.ReadInt32(), hostId = r.ReadInt32();
            bool ignorable = r.ReadBoolean();
            int weather = r.ReadInt32();
            string hostName = r.ReadString(), guestName = r.ReadString();
            // a subordinate of the host's team sees the encounter from the host's side (its map is the host's, unmirrored)
            bool watching = Multiplayer.Session.IsSubordinate, ownIsHost = watching && Multiplayer.Session.LocalTeam == 0;
            current = new Encounter
            {
                Id = id, Ignorable = ignorable, Started = Time.realtimeSinceStartup, HostName = hostName, GuestName = guestName,
                Watching = watching, OwnIsHost = ownIsHost,
            };
            string ownName = ownIsHost ? hostName : guestName, enemyName = ownIsHost ? guestName : hostName;

            CampaignManager cm = CampaignManager.instance;
            GameObjectAnimator own = CampaignSync.ClientFind(ownIsHost ? hostId : guestId);
            GameObjectAnimator enemy = CampaignSync.ClientFind(ownIsHost ? guestId : hostId);
            int ownIdx = own != null ? cm.playerMobile.IndexOf(own) : -1;
            if (watching) Toasts.Show($"Encounter: your team's {ownName} meets the enemy {enemyName}. The commanders decide.");
            if (ownIdx < 0)
            {
                Plugin.Log.LogWarning($"MP encounter #{id}: own force {ownName} not found on this side yet");
                if (!watching) Toasts.Show($"Encounter: your {ownName} meets the enemy {enemyName}.");
                return; // no panel to click: the deadline makes it an Ignore for this side
            }
            CampaignData cd = cm.campaignData;
            cd.playerObjectEngaged = ownIdx;
            cd.otherObjectEngaged = enemy != null ? cm.otherMobile.IndexOf(enemy) : -1;
            cd.canIgnore = ignorable;
            cd.canAutoResolve = false;
            if (weather >= 0 && weather < WeatherManager.instance.weatherCells.Count) cd.engagementWeatherIndex = weather;
            EngagementPanel.instance.OpenEngagementPanel(ownIdx);
        }

        private static void ClientState(System.IO.BinaryReader r)
        {
            int id = r.ReadInt32();
            var hostChoice = (EncounterChoice)r.ReadByte();
            var guestChoice = (EncounterChoice)r.ReadByte(); // ours (Player 2's)
            var o = (EncounterOutcome)r.ReadByte();
            string text = r.ReadString();
            Encounter e = current;
            if (e == null || e.Id != id) return;
            if (o == EncounterOutcome.Pending)
            {
                e.OtherDecided = hostChoice != EncounterChoice.None;
                e.HostDecided = hostChoice != EncounterChoice.None;
                e.GuestDecided = guestChoice != EncounterChoice.None;
                return;
            }
            current = null;
            if (EngagementPanel.instance != null)
            {
                EngagementPanel.instance.ClearEncounterData();
                EngagementPanel.instance.gameObject.SetActive(false);
            }
            if (!string.IsNullOrEmpty(text)) Toasts.Show(text);
        }

        // ---------- time bar (both) ----------

        private static GUIStyle barStyle, hintStyle;

        internal static void OnGUI()
        {
            Encounter e = current;
            if (e == null || Event.current.type != EventType.Repaint) return;
            Rect panel = PanelScreenRect();
            Matrix4x4 old = GUI.matrix;
            Color oldColor = GUI.color;
            GUI.matrix = Matrix4x4.identity; // the bar sits in screen pixels next to the game's panel
            try
            {
                float s = Mathf.Max(0.6f, Screen.height / 1080f);
                float h = 26f * s;
                Rect bar = new Rect(panel.x, panel.yMax + 6f * s, panel.width, h);
                float elapsed = Time.realtimeSinceStartup - e.Started;
                float frac = EncounterRules.TimeLeftFraction(elapsed);
                GUI.color = new Color(0f, 0f, 0f, 0.8f);
                GUI.DrawTexture(bar, Texture2D.whiteTexture);
                GUI.color = frac > 0.34f ? new Color(0.86f, 0.66f, 0.22f, 1f) : new Color(0.86f, 0.26f, 0.2f, 1f);
                GUI.DrawTexture(new Rect(bar.x + 2f, bar.y + 2f, (bar.width - 4f) * frac, bar.height - 4f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                if (barStyle == null)
                {
                    barStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                    hintStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, wordWrap = true };
                }
                barStyle.fontSize = Mathf.RoundToInt(14f * s);
                hintStyle.fontSize = Mathf.RoundToInt(12f * s);
                EncounterChoice mine = IsHost ? e.Host : e.Guest;
                bool otherDecided = IsHost ? e.Guest != EncounterChoice.None : e.OtherDecided;
                int left = Mathf.CeilToInt(Mathf.Max(0f, EncounterRules.DecisionSeconds - elapsed));
                string you = mine == EncounterChoice.None ? "choose" : mine == EncounterChoice.Engage ? "Engage" : "Ignore";
                if (e.Watching)
                {
                    bool ownDecided = e.OwnIsHost ? e.HostDecided : e.GuestDecided, enemyDecided = e.OwnIsHost ? e.GuestDecided : e.HostDecided;
                    GUI.Label(bar, $"{left} s   |   Your commander: {(ownDecided ? "decided" : "deciding…")}   |   " +
                                   $"Enemy commander: {(enemyDecided ? "decided" : "deciding…")}", barStyle);
                }
                else GUI.Label(bar, $"{left} s   |   You: {you}   |   Opponent: {(otherDecided ? "decided" : "deciding…")}", barStyle);
                string hint = e.Watching ? "You are watching: Player 1 and Player 2 decide this encounter."
                    : e.Ignorable
                    ? "Skipped only if both choose Ignore. No answer counts as Ignore."
                    : "This encounter can't be avoided: the battle starts when both are ready or the time runs out.";
                GUI.Label(new Rect(bar.x, bar.yMax + 2f * s, bar.width, 40f * s), hint, hintStyle);
            }
            finally
            {
                GUI.matrix = old;
                GUI.color = oldColor;
            }
        }

        /// <summary>The vanilla encounter panel's rectangle in IMGUI screen coordinates (fallback: top centre).</summary>
        private static Rect PanelScreenRect()
        {
            try
            {
                if (EngagementPanel.instance != null && EngagementPanel.instance.gameObject.activeInHierarchy)
                {
                    RectTransform rt = EngagementPanel.instance.GetComponent<RectTransform>();
                    var corners = new Vector3[4];
                    rt.GetWorldCorners(corners);
                    Canvas canvas = rt.GetComponentInParent<Canvas>();
                    if (canvas != null) canvas = canvas.rootCanvas;
                    Camera cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                    Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                    if (tr.x - bl.x > 50f && tr.y - bl.y > 20f)
                        return new Rect(bl.x, Screen.height - tr.y, tr.x - bl.x, tr.y - bl.y);
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Encounter bar: panel rect failed: " + ex.Message); }
            float w = Screen.width * 0.3f;
            return new Rect((Screen.width - w) / 2f, Screen.height * 0.15f, w, 0f);
        }
    }

    // ---------- patches ----------

    /// <summary>Host: every MP encounter goes through the two-player flow. Runs after AutoResolvePatch (D6 first).</summary>
    [HarmonyPatch(typeof(EngagementPanel), nameof(EngagementPanel.OpenEngagementPanel))]
    [HarmonyPriority(Priority.Low)]
    internal static class MpEncounterOpenPatch
    {
        private static bool Prefix(bool __runOriginal)
        {
            if (!__runOriginal) return false; // auto-resolved already
            if (!ModState.MultiplayerActive || !Multiplayer.Session.IsHost) return true;
            return MpEncounters.HostOnPanelOpening();
        }
    }

    [HarmonyPatch(typeof(EngagementPanel), nameof(EngagementPanel.StartCampaignEngagement))]
    internal static class MpEncounterEngagePatch
    {
        private static bool Prefix()
        {
            if (!ModState.MultiplayerActive) return true;
            MpEncounters.Choose(EncounterChoice.Engage);
            return false;
        }
    }

    [HarmonyPatch(typeof(EngagementPanel), nameof(EngagementPanel.Ignore))]
    internal static class MpEncounterIgnorePatch
    {
        private static bool Prefix()
        {
            if (!ModState.MultiplayerActive) return true;
            MpEncounters.Choose(EncounterChoice.Ignore);
            return false;
        }
    }

    /// <summary>
    /// Around the encounter scan:
    ///  - MP host: hide ignored pairs by making their mutual distance huge for its duration, so they neither re-trigger
    ///    nor block other encounters later in the scan (vanilla stops at the first encounter found).
    ///  - SP and MP (D40, user request): the minimum encounter distance (vanilla 6 km) is lowered to
    ///    [Campaign] MinEncounterDistanceKm for the scan only, so close and night encounters happen. The campaign's own
    ///    value (also saved in campaign files) is restored right after. Battle spawn distance is unaffected: vanilla
    ///    EngagementData.SetGroupStartPositionsIfTooClose pushes groups apart by visibility.
    /// </summary>
    [HarmonyPatch(typeof(CampaignManager), "CheckMobileEncounters")]
    internal static class MpEncounterScanPatch
    {
        private static float savedAutoScout = float.NaN;

        private static void Prefix(CampaignManager __instance, ref float __state)
        {
            MpEncounters.InScan = ModState.MultiplayerActive && Multiplayer.Session.IsHost && MpEncounters.HasIgnoredPairs;
            __state = -1f;
            CampaignData cd = __instance.campaignData;
            // MP: the scan's AI auto-scout (an "enemy" ship near a contact launches one scout plane at random,
            // CheckMobileEncounters, faction 1 only) flew AI scouts from Player 2's ships (user test 2026-11-26). Faction 1 is
            // a human in MP: the threshold is set below any distance for the scan, so it never fires.
            if (ModState.MultiplayerActive && cd != null)
            {
                savedAutoScout = cd.autoScoutThreshold;
                cd.autoScoutThreshold = -1f;
            }
            float km = Plugin.MinEncounterDistanceKm.Value;
            if (cd != null && km > 0f && km < cd.minCampaignDetectionRange)
            {
                __state = cd.minCampaignDetectionRange;
                cd.minCampaignDetectionRange = km;
            }
        }

        private static void Finalizer(CampaignManager __instance, float __state)
        {
            MpEncounters.InScan = false;
            if (!float.IsNaN(savedAutoScout) && __instance.campaignData != null) __instance.campaignData.autoScoutThreshold = savedAutoScout;
            savedAutoScout = float.NaN;
            if (__state >= 0f && __instance.campaignData != null) __instance.campaignData.minCampaignDetectionRange = __state;
        }
    }

    [HarmonyPatch(typeof(CampaignManager), nameof(CampaignManager.GetDistanceInMetresBetweenMapObjects))]
    internal static class MpEncounterDistancePatch
    {
        private static bool Prefix(Vector2 a, Vector2 b, ref float __result)
        {
            if (!MpEncounters.InScan || !MpEncounters.IsIgnoredPositions(a, b)) return true;
            __result = 1e9f;
            return false;
        }
    }
}
