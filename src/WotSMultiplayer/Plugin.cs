using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Features.TimeControl;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer
{
    /// <summary>
    /// Entry point. Loaded by BepInEx from BepInEx/plugins/WotSMultiplayer/.
    /// Rule: every patch must no-op in single-player unless a config toggle enables it.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "community.wots.multiplayer";
        public const string Name = "WotS Multiplayer";
        public const string Version = Core.BuildInfo.Version;

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        /// <summary>SHA256 of the game's Assembly-CSharp.dll. Both players must match (handshake, M2).</summary>
        internal static string GameAssemblyHash = "unknown";

        internal static ConfigEntry<bool> ShowOverlay;
        internal static ConfigEntry<bool> AutoResolveInSinglePlayer;
        internal static ConfigEntry<bool> SpeedVoteTestMode;
        internal static ConfigEntry<KeyboardShortcut> PanelKey;
        internal static ConfigEntry<float> UiScaleSetting;
        internal static ConfigEntry<bool> DebugInput;
        internal static ConfigEntry<bool> DebugRevealAll;
        internal static ConfigEntry<int> MaxAircraftPerSide;
        internal static ConfigEntry<int> MaxAircraftPerGroup;
        internal static ConfigEntry<float> MinEncounterDistanceKm;
        internal static ConfigEntry<float> MpSpawnDistanceFactor;
        internal static ConfigEntry<float> MpReinforcementRadiusKm;
        internal static ConfigEntry<int> MpMaxTaskForcesPerSide;
        internal static ConfigEntry<bool> ClearDayNightBar;
        internal static ConfigEntry<bool> ClearDayNightBarSP;
        internal static ConfigEntry<bool> SpeedProposalSound;
        internal static ConfigEntry<bool> DamagedShipsSlowTaskForces;
        internal static ConfigEntry<bool> DamagedShipsSlowTaskForcesSP;
        internal static ConfigEntry<bool> LiveBattles;
        internal static ConfigEntry<string> PlayerName;
        internal static ConfigEntry<int> HostPort;
        internal static ConfigEntry<bool> HostLocalOnly;
        internal static ConfigEntry<string> JoinAddress;
        internal static ConfigEntry<int> MaxPlayers;
        internal static ConfigEntry<int> JoinTeam;
        internal static ConfigEntry<KeyboardShortcut> LockKey;

        private Harmony harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ShowOverlay = Config.Bind("General", "ShowVersionOverlay", true,
                "Show the small 'WotS MP' version label in the corner of the screen.");
            PanelKey = Config.Bind("General", "PanelKey", new KeyboardShortcut(KeyCode.F11),
                "Opens the mod settings panel (F5-F10 are used by the game).");
            DebugInput = Config.Bind("Debug", "LogMouseEvents", false,
                "Log every mouse down/up the mod's UI receives (for diagnosing click problems).");
            ClearDayNightBar = Config.Bind("General", "ClearDayNightBar", true,
                "Multiplayer: replace the campaign map's day/night bar (bottom left) with a sun/moon bar: the sun crosses it " +
                "by day (light yellow to evening orange), the moon by night (orange to black to first light), with the " +
                "start and end times of the day or night at its ends.");
            ClearDayNightBarSP = Config.Bind("SinglePlayer", "ClearDayNightBar", false,
                "The same sun/moon day/night bar in single-player.");
            DamagedShipsSlowTaskForces = Config.Bind("Campaign", "DamagedShipsSlowTaskForces", true,
                "Multiplayer: a task force moves only as fast as its slowest ship including battle damage (lost propellers, " +
                "engines, flooding); the fleet panel names the ship holding it back. The host's setting decides.");
            DamagedShipsSlowTaskForcesSP = Config.Bind("SinglePlayer", "DamagedShipsSlowTaskForces", false,
                "The same damaged-ship speed rule in single-player.");
            SpeedProposalSound = Config.Bind("General", "SpeedProposalSound", true,
                "Play a soft chime when the other player proposes a speed change (uses the game's campaign message volume).");
            MaxAircraftPerSide = Config.Bind("Battle", "MaxAircraftPerSide", 60,
                "Most planes per side that a campaign battle pulls in (vanilla 24). 0 = leave the game's value.");
            MaxAircraftPerGroup = Config.Bind("Battle", "MaxAircraftPerGroup", 12,
                "Most planes in one launched air group (vanilla 12). 0 = leave the game's value.");
            MinEncounterDistanceKm = Config.Bind("Campaign", "MinEncounterDistanceKm", 1f,
                "Closest distance (km) at which forces still meet on the campaign map (vanilla 6). Lower lets close encounters " +
                "happen, especially at night when lookouts see less than 6 km. 0 = keep the game's value.");
            MpSpawnDistanceFactor = Config.Bind("Battle", "MpSpawnDistanceFactor", 0.75f,
                "Multiplayer battles: the groups start this fraction of their campaign-map distance apart (1 = the game's own " +
                "spacing), never closer than 5 km unless they already were. The host's setting decides.");
            MpReinforcementRadiusKm = Config.Bind("Battle", "MpReinforcementRadiusKm", 20f,
                "Multiplayer battles: other ship task forces within this distance (km) of their side's fleet in the encounter " +
                "join the battle at their map positions (0 = the original game: one ship group per side). The host's setting decides.");
            MpMaxTaskForcesPerSide = Config.Bind("Battle", "MpMaxTaskForcesPerSide", 3,
                "Multiplayer battles: most ship task forces per side in one battle (1-8; more ships = more load on the host's PC).");
            LiveBattles = Config.Bind("Multiplayer", "LiveBattles", true,
                "A fought encounter between the two players becomes a live battle on both PCs (each player commands their own " +
                "side). Off = such encounters are announced and skipped. The host's setting decides (D44).");
            DebugRevealAll = Config.Bind("Debug", "RevealAllMobiles", false,
                "TESTING ONLY: on the MP client, show every enemy task force/air group even if not spotted (breaks fog of war).");
            UiScaleSetting = Config.Bind("General", "UiScale", 1.0f,
                "Extra size multiplier for mod windows (they already scale with screen height relative to 1080p).");
            AutoResolveInSinglePlayer = Config.Bind("SinglePlayer", "AutoResolveEncounters", false,
                "Automatically auto-resolve fighter-vs-scout encounters in single-player (always on in multiplayer).");
            SpeedVoteTestMode = Config.Bind("SinglePlayer", "SpeedVoteTestMode", false,
                "Test the multiplayer speed rules solo, with on-screen controls for a simulated Player 2.");

            PlayerName = Config.Bind("Multiplayer", "PlayerName", "Player", "Name shown to the other player.");
            HostPort = Config.Bind("Multiplayer", "HostPort", Net.Protocol.DefaultPort, "TCP port used when hosting.");
            HostLocalOnly = Config.Bind("Multiplayer", "HostLocalOnly", false,
                "Host on 127.0.0.1 only (testing on one PC; no firewall prompt). Off for real games.");
            JoinAddress = Config.Bind("Multiplayer", "JoinAddress", "", "Last address joined (host IP or IP:port).");
            MaxPlayers = Config.Bind("Multiplayer", "MaxPlayers", 2,
                "Host: most players in a room, you included (2-6). 2 = the classic two-player game; players 3+ join as subordinates " +
                "of a team: they see their team's map, give campaign orders and help in battle, except to what their commander has " +
                "selected or locked; no votes, no Start or retreat.");
            JoinTeam = Config.Bind("Multiplayer", "JoinTeam", -1,
                "When joining: -1 = whatever the host decides (the first player becomes Player 2), 0 = the host's team, 1 = Player 2's team.");
            LockKey = Config.Bind("Multiplayer", "LockKey", new KeyboardShortcut(KeyCode.K),
                "Commanders (Player 1 and 2) with subordinates: lock / unlock the selected task force or air group, so your " +
                "subordinates can't give it orders (also a button in the speed window).");

            Core.LaunchOptions.Parse();
            GameAssemblyHash = ComputeGameAssemblyHash();
            Core.Multiplayer.Init();
            Features.MpCampaign.MpCampaign.Init();
            TimeControlDriver.Init();
            Features.MpCampaign.CampaignSync.Init();
            Features.MpCampaign.RemoteOrders.Init();
            Features.MpCampaign.TeamLocks.Init();
            Features.MpCampaign.MpEncounters.Init();
            Features.MpBattle.MpBattle.Init();
            Log.LogInfo($"{Name} {Version} loading. Unity {Application.unityVersion}. Assembly-CSharp SHA256 {GameAssemblyHash}");

            harmony = new Harmony(Guid);
            try
            {
                harmony.PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception e)
            {
                Log.LogError("Harmony patching failed: " + e);
            }
            foreach (var m in harmony.GetPatchedMethods())
                Log.LogInfo($"  patched {m.DeclaringType?.Name}.{m.Name}");
            Log.LogInfo($"{Name} loaded.");
        }

        private static string ComputeGameAssemblyHash()
        {
            try
            {
                string path = Path.Combine(Path.Combine(Application.dataPath, "Managed"), "Assembly-CSharp.dll");
                using (FileStream fs = File.OpenRead(path))
                using (SHA256 sha = SHA256.Create())
                {
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("Could not hash Assembly-CSharp.dll: " + e.Message);
                return "unknown";
            }
        }

        private void Update()
        {
            try
            {
                if (DebugInput.Value && Input.GetMouseButtonDown(0))
                    Log.LogInfo($"[input] Update mouse0 down raw={Input.mousePosition} eventSystem={(UnityEngine.EventSystems.EventSystem.current != null ? UnityEngine.EventSystems.EventSystem.current.enabled.ToString() : "null")}");
                Core.LaunchOptions.Update();
                Features.AirLimits.Apply(); // cheap; covers a Config loaded before our patch was in place
                Core.Multiplayer.Update();
                Features.MpCampaign.MpCampaign.Update();
                Features.MpCampaign.CampaignSync.Update();
                Features.MpCampaign.TeamLocks.Update();
                Features.MpCampaign.MpEncounters.Update();
                Features.MpBattle.MpBattle.Update();
                ModPanel.Update();
                MultiplayerMenuButton.Update();
                RoomWindow.Update();
                Features.MpCampaign.MpTacticalOff.Update();
                Features.DayNightBar.Update();
                TimeControlDriver.Update();
                UiBlock.Update();
            }
            catch (Exception e)
            {
                LogThrottled("Update", e);
            }
        }

        private GUIStyle overlayStyle;

        private void OnGUI()
        {
            try
            {
                UiScale.Apply();
                if (DebugInput.Value)
                {
                    Event ev = Event.current;
                    if (ev.type == EventType.MouseDown || ev.type == EventType.MouseUp || ev.type == EventType.Used)
                        Log.LogInfo($"[input] OnGUI {ev.type} pos={ev.mousePosition} raw={Input.mousePosition} hot={GUIUtility.hotControl}");
                }
                UiBlock.BeginFrame();
                if (ShowOverlay.Value)
                {
                    if (overlayStyle == null)
                    {
                        overlayStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
                        overlayStyle.normal.textColor = new Color(1f, 1f, 1f, 0.6f);
                    }
                    string mp = Core.Multiplayer.Session.State == Net.SessionState.Idle ? "" : "   |   " + Core.Multiplayer.StatusLine();
                    // Top-left: the bottom-left corner holds the campaign clock and Command Points readout.
                    GUI.Label(new Rect(6, 2, 900, 20), $"WotS MP v{Version}  ({PanelKey.Value}){mp}", overlayStyle);
                }
                Features.MpCampaign.TeamLocks.OnGUI(); // map badges, under the windows
                TimeControlDriver.OnGUI();
                Features.MpCampaign.MpSetupWindow.OnGUI();
                ModPanel.OnGUI();
                RoomWindow.OnGUI();
                Features.MpCampaign.MpEncounters.OnGUI();
                Features.MpBattle.MpBattle.DrawRetreatBars();
                Features.MpCampaign.ShipSpeedLimit.OnGUI();
                Toasts.Draw();
            }
            catch (Exception e)
            {
                LogThrottled("OnGUI", e);
            }
        }

        private void OnApplicationQuit()
        {
            // Tell the other player we left (Bye) instead of letting them time out.
            try { Core.Multiplayer.Leave(); } catch { }
        }

        private float lastErrorLog;

        /// <summary>Avoids flooding the log with a per-frame exception.</summary>
        private void LogThrottled(string where, Exception e)
        {
            if (Time.realtimeSinceStartup - lastErrorLog < 5f) return;
            lastErrorLog = Time.realtimeSinceStartup;
            Log.LogError($"{where}: {e}");
        }
    }
}
