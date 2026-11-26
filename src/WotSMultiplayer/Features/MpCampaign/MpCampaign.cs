using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Features.TimeControl;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    internal enum MpPhase : byte
    {
        Lobby = 0,             // setup screen
        HostStarting = 1,      // host is loading the new campaign
        WaitingForClient = 2,  // host sent the state, client is loading it
        ClientLoading = 3,     // (client) loading the mirrored state
        Running = 4,           // both players are in the campaign
        Ended = 5,             // disconnected/aborted while in an MP campaign
    }

    /// <summary>
    /// M3: setup lobby → host starts the campaign with the settings → host sends the side-swapped state →
    /// client loads it. Until campaign sync exists (M4) both campaigns are frozen once running.
    /// </summary>
    internal static class MpCampaign
    {
        // message types (>= MsgType.GameBase)
        private const byte MsgSetupState = 20, MsgSetupReady = 21, MsgCampaignState = 22, MsgClientLoaded = 23;
        // additional players (stage 2): host → one subordinate {campaign id, team, theatre, host side, campaign json}
        private const byte MsgSubordinateState = 39;

        internal static MpSettings Settings = new MpSettings();
        internal static bool ClientReady;
        /// <summary>Subordinate PC: Player 2's ready flag as the host last sent it (setup window).</summary>
        internal static bool Player2ReadySeen;
        internal static MpPhase Phase = MpPhase.Lobby;
        internal static string StatusText = "";

        private static bool dirty;
        private static float lastSent;
        private static bool defaultsLoaded;

        private static NetSession Net => Multiplayer.Session;
        internal static bool IsHost => Net.IsHost;
        private static string StateDir => Path.Combine(Paths.ConfigPath, "WotSMultiplayer");

        internal static void Init()
        {
            Net.OnGameMessage += OnMessage;
            // additional players (stage 2): a subordinate who joins while the campaign runs gets it once the map shows
            Net.OnSubordinateJoined += index => { if (HostCampaignOpen()) subordinatesToSend.Add(index); MarkDirty(); };
            // stage 5: a subordinate at the main menu sees the setup (read-only) while the commanders get ready
            Net.OnSubordinateConnected += () => { if (AtMainMenu() && Phase == MpPhase.Lobby) MpSetupWindow.Visible = true; };
            Net.OnConnected += () =>
            {
                ClientReady = false;
                if (IsHost && awaitingRejoin && !AtMainMenu())
                {
                    // M8 rejoin: Player 2 is back (same room code); send the running campaign once the map is showing
                    rejoinPending = true;
                    dirty = true;
                    Toasts.Show(Net.PeerName + " rejoined. Sending them the campaign as it is now...", 8f);
                    return;
                }
                if (Phase == MpPhase.Ended) Phase = MpPhase.Lobby;
                if (IsHost) { EnsureDefaults(); dirty = true; }
                if (AtMainMenu()) MpSetupWindow.Visible = true;
            };
            Net.OnDisconnected += reason =>
            {
                ClientReady = false;
                pendingSubordinateLoad = null;
                if (Phase == MpPhase.Lobby) return;
                Phase = MpPhase.Ended;
                rejoinPending = false;
                if (IsHost && Net.State == SessionState.Hosting && !AtMainMenu())
                {
                    awaitingRejoin = true;
                    StatusText = "Player 2 disconnected (" + reason + "). The campaign is paused until they rejoin.";
                    Toasts.Show($"{Net.PeerName} disconnected ({reason}). The campaign is paused: they can rejoin with the same room code " +
                                "(MULTIPLAYER → Join from their main menu).", 12f);
                    return;
                }
                StatusText = "Disconnected: " + reason + ". Return to the main menu to start again.";
                Toasts.Show("Multiplayer campaign stopped: " + (Net.IsSubordinate ? "the connection to the host ended" : "the other player disconnected") +
                            " (" + reason + "). " + (IsHost ? "" : "To rejoin, return to the main menu and join the host's room again."), 10f);
            };
        }

        internal static void EnsureDefaults()
        {
            if (defaultsLoaded) return;
            try { Settings = MpDefaults.Build(Settings.Theatre, Settings.HostSide); defaultsLoaded = true; }
            catch (Exception e) { Plugin.Log.LogWarning("MP defaults: " + e.Message); }
        }

        internal static void ResetDefaults()
        {
            defaultsLoaded = false;
            int side = Settings.HostSide;
            Settings = new MpSettings { HostSide = side };
            EnsureDefaults();
            MarkDirty();
        }

        internal static void MarkDirty() => dirty = true;

        // ---------- per frame ----------

        internal static void Update()
        {
            if (IsHost) Net.KeepSlots = HostCampaignOpen(); // stage 5: rejoins keep Player 2's slot and subordinates' teams
            if (IsHost && (Net.IsConnected || Net.HasSubordinates()) && dirty && Time.realtimeSinceStartup - lastSent > 0.2f)
            {
                dirty = false;
                lastSent = Time.realtimeSinceStartup;
                byte[] setup = Protocol.Build(w => { w.Write((byte)Phase); w.Write(ClientReady); Settings.Write(w); });
                Net.SendGame(MsgSetupState, setup);
                Net.SendGameToSubordinates(MsgSetupState, setup); // stage 5: they see it read-only
            }

            // Back at the main menu after an MP campaign: leave MP mode.
            if ((Phase == MpPhase.Running || Phase == MpPhase.Ended || (awaitingRejoin && Phase == MpPhase.WaitingForClient)) && AtMainMenu())
            {
                Phase = MpPhase.Lobby;
                ModState.MultiplayerActive = false;
                awaitingRejoin = rejoinPending = false;
                StatusText = "";
                ClientReady = false;
                MarkDirty();
                subordinatesToSend.Clear();
                if (Net.IsConnected) MpSetupWindow.Visible = true;
            }

            // additional players (stage 2), subordinate: a campaign that arrived away from the main menu loads once back there
            if (pendingSubordinateLoad != null && Net.IsSubordinate && Phase == MpPhase.Lobby && AtMainMenu())
            {
                string[] l = pendingSubordinateLoad;
                pendingSubordinateLoad = null;
                ClientLoadState(l[0], l[1], l[2]);
            }

            // additional players (stage 2): send waiting subordinates the campaign once the host's map shows (not in a battle)
            if (IsHost && subordinatesToSend.Count > 0 && Time.realtimeSinceStartup - subordinateSendAt > 1f)
            {
                if (!HostCampaignOpen()) subordinatesToSend.Clear();
                else if (TimeControlDriver.CampaignMapActive() && !MpBattleActiveOnHost())
                {
                    subordinateSendAt = Time.realtimeSinceStartup;
                    try { SendSubordinateStates(); }
                    catch (Exception e)
                    {
                        subordinatesToSend.Clear();
                        Plugin.Log.LogError("MP: sending the campaign to subordinates failed: " + e);
                        Toasts.Show("Sending the campaign to the additional players failed: " + e.Message, 10f);
                    }
                }
            }

            // M8 rejoin (host): Player 2 reconnected; send the campaign once the map shows (not during a battle), and again
            // every 15 s until they have loaded it (they may still have been on their old campaign screen).
            // (not TimeControlDriver.Phase: it reads None while the MP phase isn't Running, i.e. exactly now)
            if (IsHost && Net.IsConnected && awaitingRejoin && TimeControlDriver.CampaignMapActive() && !MpBattleActiveOnHost())
            {
                if (rejoinPending || (Phase == MpPhase.WaitingForClient && Time.realtimeSinceStartup - rejoinSentAt > 15f))
                {
                    rejoinPending = false;
                    rejoinSentAt = Time.realtimeSinceStartup;
                    try { SendMirroredState(); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("MP rejoin: sending the campaign failed: " + e);
                        Toasts.Show("Sending the campaign to the rejoining player failed: " + e.Message, 10f);
                    }
                }
            }

            // The client is a puppet (its clock comes from host snapshots), so its own campaign time never runs.
            // The host's time is controlled by the networked speed rules once Running.
            if (ModState.MultiplayerActive && (!IsHost || Phase == MpPhase.WaitingForClient || Phase == MpPhase.Ended))
            {
                CampaignInterface ci = CampaignInterface.instance;
                if (ci != null && ci.map != null && ci.map.timeSpeed != 0f) ci.PauseCampaign();
            }
        }

        internal static bool AtMainMenu()
        {
            MenuSystem ms = MenuSystem.instance;
            return ms != null && ms.gameObject.activeInHierarchy && ms.mainInterface != null && ms.mainInterface.activeSelf;
        }

        // ---------- host actions ----------

        internal static string CanStart()
        {
            if (!Net.IsConnected) return "Waiting for the other player to connect.";
            if (!IsHost) return "Only the host starts the campaign.";
            if (!ClientReady) return "Waiting for the other player to press Ready.";
            if (!AtMainMenu()) return "Return to the main menu first.";
            if (Phase != MpPhase.Lobby) return "Already starting.";
            return null;
        }

        internal static void HostStart()
        {
            string why = CanStart();
            if (why != null) { Toasts.Show(why); return; }
            Theatres t = Theatres.Find(Settings.Theatre);
            string hostCampaign = t.CampaignBySide[Settings.HostSide];
            ModState.MultiplayerActive = true; // AI-off patches must be active before the campaign sets itself up
            CampaignSync.Reset();
            Phase = MpPhase.HostStarting;
            StatusText = "Starting the campaign...";
            MarkDirty();
            MpSetupWindow.Visible = false;
            try
            {
                // Same path as the Campaign button (MenuSystem.NewCampaign activates the selection menu, then builds the list).
                MenuSystem ms = MenuSystem.instance;
                ms.NewCampaign(false);
                int idx = ms.selectionFileNames.IndexOf(hostCampaign);
                if (idx < 0) throw new Exception("campaign '" + hostCampaign + "' not found in the campaign list");
                Invoke(ms, "SetSelectedIndex", idx);
                ms.StartSelection();
                Plugin.Log.LogInfo("MP: host starting " + hostCampaign);
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException ?? e;
                Plugin.Log.LogError("MP host start failed: " + inner);
                Abort("Could not start the campaign: " + inner.Message);
            }
        }

        // ---------- M8: continue a saved MP campaign ----------
        // The host's MP saves are "Multiplayer (Host) <name>.txt" (MpSaveNamePatch renames every save made in MP: the Esc menu
        // save and vanilla's autosave at battle start). Continuing loads one on the host through the vanilla Load list, then
        // sends Player 2 the side-swapped state exactly like a new campaign. The setup screen's economy values are already in
        // the save, so they are not applied again.

        internal const string HostSavePrefix = "Multiplayer (Host)";
        private static bool continuing;
        private static string continueName;
        private static List<KeyValuePair<string, DateTime>> saveList = new List<KeyValuePair<string, DateTime>>();
        private static float saveListAt = -10f;

        private static string CampaignSaveDir => Path.Combine(Application.persistentDataPath, Library.instance.campaignPath);

        /// <summary>The host's MP saves, newest first (file name with .txt, last write time). Re-read every 3 s at most.</summary>
        internal static List<KeyValuePair<string, DateTime>> HostSaves()
        {
            if (Time.realtimeSinceStartup - saveListAt < 3f) return saveList;
            saveListAt = Time.realtimeSinceStartup;
            var list = new List<KeyValuePair<string, DateTime>>();
            try
            {
                if (Library.instance != null && Directory.Exists(CampaignSaveDir))
                    foreach (string f in Directory.GetFiles(CampaignSaveDir, HostSavePrefix + "*.txt"))
                        list.Add(new KeyValuePair<string, DateTime>(Path.GetFileName(f), File.GetLastWriteTime(f)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("MP: listing saves failed: " + e.Message); }
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            saveList = list;
            return list;
        }

        /// <summary>Which campaign folder (side) a save belongs to: its CampaignData "campaignID".</summary>
        private static string ReadCampaignId(string fileName)
        {
            string text = File.ReadAllText(Path.Combine(CampaignSaveDir, fileName));
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"campaignID\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        internal static void HostContinue(string fileName)
        {
            string why = CanStart();
            if (why != null) { Toasts.Show(why); return; }
            try
            {
                string id = ReadCampaignId(fileName);
                Theatres theatre = null;
                int hostSide = -1;
                foreach (Theatres t in Theatres.All)
                    for (int s = 0; s < t.CampaignBySide.Length; s++)
                        if (t.CampaignBySide[s] == id) { theatre = t; hostSide = s; }
                if (theatre == null) throw new Exception($"'{fileName}' is not a campaign this mod can mirror (campaign '{id ?? "?"}')");
                Settings.Theatre = theatre.Id;
                Settings.HostSide = hostSide; // the save decides who plays which side
                ModState.MultiplayerActive = true;
                CampaignSync.Reset();
                continuing = true;
                continueName = fileName;
                Phase = MpPhase.HostStarting;
                StatusText = "Loading the saved campaign " + fileName + "...";
                MarkDirty();
                MpSetupWindow.Visible = false;
                MenuSystem ms = MenuSystem.instance;
                ms.NewCampaign(true); // the Campaign → Load path: builds the saved-campaign list
                int idx = ms.selectionFileNames.IndexOf(fileName);
                if (idx < 0) throw new Exception("'" + fileName + "' is not in the game's load list");
                Invoke(ms, "SetSelectedIndex", idx);
                ms.StartSelection();
                Plugin.Log.LogInfo($"MP: host continuing '{fileName}' ({id}, host plays {SideName(hostSide)})");
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException ?? e;
                Plugin.Log.LogError("MP host continue failed: " + inner);
                Abort("Could not load the saved campaign: " + inner.Message);
            }
        }

        /// <summary>Called when the host's campaign has finished loading (LoadScreen at 100%).</summary>
        internal static void OnHostCampaignReady()
        {
            try
            {
                Theatres t = Theatres.Find(Settings.Theatre);
                string clientCampaign = t.CampaignBySide[Settings.ClientSide];
                if (continuing)
                {
                    continuing = false;
                    MpApply.SetNeutralDifficulty(CampaignManager.instance.campaignData); // runtime multipliers aren't saved
                    Plugin.Log.LogInfo("MP: continued " + continueName + " (setup values come from the save)");
                }
                else MpApply.ApplyToHostCampaign(Settings);
                SendMirroredState();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("MP host start failed: " + e);
                Abort("Starting the multiplayer campaign failed: " + e.Message);
            }
        }

        /// <summary>Host: send Player 2 the side-swapped campaign as it is now (start, continue and rejoin).</summary>
        private static void SendMirroredState()
        {
            Theatres t = Theatres.Find(Settings.Theatre);
            string clientCampaign = t.CampaignBySide[Settings.ClientSide];
            string hostState = CampaignManager.instance.GetCampaignDataString();
            string mirrorSetup = AssetLoader.instance.ImportText("campaign/" + clientCampaign + "/setup.txt")[0];
            string clientState = SideSwap.Swap(hostState, clientCampaign, mirrorSetup);
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(Path.Combine(StateDir, "last_host_state.txt"), hostState);
            File.WriteAllText(Path.Combine(StateDir, "last_client_state.txt"), clientState);

            Net.SendGame(MsgCampaignState, Protocol.Build(w => { w.Write(clientCampaign); w.Write(clientState); }));
            if (!awaitingRejoin) foreach (PlayerInfo p in Net.Players) if (p.Index >= 2) subordinatesToSend.Add(p.Index);
            Phase = MpPhase.WaitingForClient;
            StatusText = $"Campaign ready. Sent it to {Net.PeerName} ({clientState.Length / 1024} KB), waiting for them to load...";
            MarkDirty();
            Toasts.Show(StatusText, 8f);
            Plugin.Log.LogInfo("MP: sent the campaign to " + Net.PeerName + (awaitingRejoin ? " (rejoin)" : ""));
        }

        // ---------- M8: rejoin a running campaign ----------
        // When Player 2 drops, the host's campaign pauses (Phase Ended) and the room stays open. When they join again from
        // their main menu (same room code), the host sends the campaign as it is now, exactly like at the start. A battle
        // in progress on the host finishes first. Leaving to the main menu on the host ends the wait.
        private static bool awaitingRejoin, rejoinPending;
        private static float rejoinSentAt;

        private static bool MpBattleActiveOnHost() => Features.MpBattle.MpBattle.HostActive;

        // ---------- additional players, stage 2: subordinates watch their team's map ----------
        // Team 1 (Player 2's side) gets Player 2's side-swapped campaign; team 0 (the host's side) the host's own campaign,
        // unmirrored ("own side = faction 0" already holds). After loading, the map follows the host's snapshots (CampaignSync)
        // and the speed window shows the votes. Subordinates give no orders yet (NetSession drops their game messages).
        private static readonly HashSet<int> subordinatesToSend = new HashSet<int>();
        private static string[] pendingSubordinateLoad; // subordinate: {campaign id, json, save name} waiting for the main menu
        private static float subordinateSendAt = -10f;

        /// <summary>Host: an MP campaign is loaded here (running, waiting for Player 2, or paused for a rejoin).</summary>
        private static bool HostCampaignOpen() =>
            IsHost && ModState.MultiplayerActive && (Phase == MpPhase.WaitingForClient || Phase == MpPhase.Running || Phase == MpPhase.Ended) && !AtMainMenu();

        private static void SendSubordinateStates()
        {
            Theatres t = Theatres.Find(Settings.Theatre);
            string hostCampaign = t.CampaignBySide[Settings.HostSide], clientCampaign = t.CampaignBySide[Settings.ClientSide];
            string hostState = CampaignManager.instance.GetCampaignDataString();
            string clientState = null;
            foreach (int index in new List<int>(subordinatesToSend))
            {
                int team = Net.SubordinateTeam(index);
                if (team < 0) continue; // left meanwhile
                if (team == 1 && clientState == null)
                {
                    string mirrorSetup = AssetLoader.instance.ImportText("campaign/" + clientCampaign + "/setup.txt")[0];
                    clientState = SideSwap.Swap(hostState, clientCampaign, mirrorSetup);
                }
                string campaignId = team == 0 ? hostCampaign : clientCampaign, json = team == 0 ? hostState : clientState;
                Net.SendGameToPlayer(index, MsgSubordinateState, Protocol.Build(w =>
                {
                    w.Write(campaignId); w.Write((byte)team); w.Write(Settings.Theatre ?? ""); w.Write((byte)Settings.HostSide); w.Write(json);
                }));
                Plugin.Log.LogInfo($"MP: sent the campaign to Player {index + 1} ({NetSession.TeamName(team)}, {json.Length / 1024} KB)");
            }
            subordinatesToSend.Clear();
        }

        // ---------- client actions ----------

        internal static void ClientSetReady(bool ready)
        {
            ClientReady = ready;
            Net.SendGame(MsgSetupReady, Protocol.Build(w => w.Write(ready)));
        }

        private static void ClientLoadState(string clientCampaign, string json, string fileName = "Multiplayer (Player 2).txt")
        {
            if (!AtMainMenu())
            {
                Toasts.Show("The host started the campaign, but you are not at the main menu. Return to the main menu and ask the host to start again.", 12f);
                return;
            }
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, Library.instance.campaignPath);
                File.WriteAllLines(Path.Combine(dir, fileName), new[] { json });
                try
                {
                    // debug copy only (never fails the load); one per player, since copies on one PC share the config folder
                    Directory.CreateDirectory(StateDir);
                    string debugName = Net.IsSubordinate ? $"last_received_state_p{Net.LocalPlayerIndex + 1}.txt" : "last_received_state.txt";
                    File.WriteAllText(Path.Combine(StateDir, debugName), json);
                }
                catch (Exception e) { Plugin.Log.LogWarning("MP: debug copy of the received campaign not written: " + e.Message); }

                ModState.MultiplayerActive = true;
                CampaignSync.Reset();
                Phase = MpPhase.ClientLoading;
                StatusText = "Loading the campaign from the host...";
                MpSetupWindow.Visible = false;

                MenuSystem ms = MenuSystem.instance;
                ms.NewCampaign(true); // the Campaign → Load path: builds the saved-campaign list
                int idx = ms.selectionFileNames.IndexOf(fileName);
                if (idx < 0) throw new Exception("saved state not found in the load list");
                Invoke(ms, "SetSelectedIndex", idx);
                ms.StartSelection();
                Plugin.Log.LogInfo($"MP: client loading {clientCampaign} ({json.Length} chars)");
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException ?? e;
                Plugin.Log.LogError("MP client load failed: " + inner);
                Abort("Loading the host's campaign failed: " + inner.Message);
            }
        }

        /// <summary>Called when the client's campaign has finished loading.</summary>
        internal static void OnClientCampaignReady()
        {
            MpApply.SetNeutralDifficulty(CampaignManager.instance.campaignData);
            Phase = MpPhase.Running;
            StatusText = "In the multiplayer campaign.";
            if (Net.IsSubordinate)
            {
                int side = Net.LocalTeam == 0 ? Settings.HostSide : Settings.ClientSide;
                Toasts.Show($"Multiplayer campaign loaded. You are Player {Net.LocalPlayerIndex + 1}, a subordinate for {SideName(side)} " +
                            $"({NetSession.TeamName(Net.LocalTeam)}). Your map follows the host's game. Give orders as usual; the force your commander " +
                            "has selected and the ones they locked take no orders from you.", 14f);
                Plugin.Log.LogInfo("MP: subordinate campaign loaded");
                return;
            }
            Net.SendGame(MsgClientLoaded, new byte[0]);
            Toasts.Show($"Multiplayer campaign loaded. You command {SideName(Settings.ClientSide)}. The host's game runs the campaign; " +
                        "your orders go to it and your map follows it.", 12f);
        }

        // ---------- messages ----------

        private static void OnMessage(byte type, byte[] payload)
        {
            try
            {
                using (BinaryReader r = Protocol.Reader(payload))
                {
                    switch (type)
                    {
                        case MsgSetupState when !IsHost:
                            MpPhase hostPhase = (MpPhase)r.ReadByte();
                            Player2ReadySeen = r.ReadBoolean(); // host's view of Player 2's ready flag (ours, on Player 2's PC)
                            Settings = MpSettings.Read(r);
                            if (hostPhase == MpPhase.HostStarting && Phase == MpPhase.Lobby) StatusText = "The host is starting the campaign...";
                            break;
                        case MsgSetupReady when IsHost:
                            ClientReady = r.ReadBoolean();
                            Toasts.Show(Net.PeerName + (ClientReady ? " is ready." : " is not ready."));
                            MarkDirty();
                            break;
                        case MsgCampaignState when !IsHost:
                            string campaignId = r.ReadString();
                            string json = r.ReadString();
                            if (Phase == MpPhase.ClientLoading || Phase == MpPhase.Running) break; // a rejoin re-send while already loading
                            ClientLoadState(campaignId, json);
                            break;
                        case MsgSubordinateState when !IsHost && Net.IsSubordinate:
                            string subCampaign = r.ReadString();
                            int team = r.ReadByte();
                            string theatre = r.ReadString();
                            int hostSide = r.ReadByte();
                            string subJson = r.ReadString();
                            if (Phase == MpPhase.ClientLoading || Phase == MpPhase.Running) break;
                            if (theatre.Length > 0) Settings.Theatre = theatre; // (Find falls back to the Solomons)
                            if (hostSide == MpSettings.Side.Allies || hostSide == MpSettings.Side.Japan) Settings.HostSide = hostSide;
                            string subFile = $"Multiplayer (Player {Net.LocalPlayerIndex + 1}).txt";
                            if (AtMainMenu()) ClientLoadState(subCampaign, subJson, subFile);
                            else
                            {
                                // e.g. in the options or still in a single-player game: load it once back at the main menu
                                pendingSubordinateLoad = new[] { subCampaign, subJson, subFile };
                                Toasts.Show("The host's campaign is ready for you: return to the main menu and it loads.", 12f);
                            }
                            break;
                        case MsgClientLoaded when IsHost:
                            if (awaitingRejoin)
                            {
                                awaitingRejoin = false;
                                Toasts.Show($"{Net.PeerName} is back in the campaign. It's paused: propose Resume in the speed window.", 10f);
                                Plugin.Log.LogInfo("MP: rejoin complete");
                            }
                            Phase = MpPhase.Running;
                            StatusText = "Both players are in the campaign.";
                            MarkDirty();
                            Toasts.Show($"{Net.PeerName} has loaded the campaign. You command {SideName(Settings.HostSide)}. " +
                                        "The campaign starts paused: propose Resume in the speed window.", 12f);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("MP message " + type + " failed: " + e);
            }
        }

        // ---------- helpers ----------

        internal static string SideName(int side) => side == MpSettings.Side.Allies ? "the Allies" : "Japan";

        private static void Abort(string why)
        {
            continuing = false;
            Phase = MpPhase.Lobby;
            ModState.MultiplayerActive = false;
            StatusText = why;
            MarkDirty();
            Toasts.Show(why, 10f);
            MpSetupWindow.Visible = true;
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            MethodInfo m = AccessTools.Method(target.GetType(), method);
            if (m == null) throw new MissingMethodException(target.GetType().Name, method);
            m.Invoke(target, args);
        }
    }

    /// <summary>LoadScreen reaches 100% at the very end of CampaignManager.SetupCampaign (new and loaded campaigns).</summary>
    [HarmonyPatch(typeof(LoadScreen), nameof(LoadScreen.SetLoadProgress))]
    internal static class LoadCompletePatch
    {
        private static void Postfix(float value)
        {
            if (value < 1f) return;
            if (MpCampaign.Phase == MpPhase.HostStarting) MpCampaign.OnHostCampaignReady();
            else if (MpCampaign.Phase == MpPhase.ClientLoading) MpCampaign.OnClientCampaignReady();
        }
    }
}
