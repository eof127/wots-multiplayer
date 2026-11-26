using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpCampaign
{
    /// <summary>
    /// Multiplayer campaign setup screen (D18/D20). The host edits; the client sees the same values live and presses Ready.
    /// </summary>
    internal static class MpSetupWindow
    {
        internal static bool Visible;
        private static Rect rect = new Rect(-1, 70, 700, 10);
        private static Vector2 scroll;
        private static GUIStyle header, bold, small, valueStyle;

        internal static void OnGUI()
        {
            if (!Visible) return;
            if (rect.x < 0) rect.x = Mathf.Max(460f, (UiScale.Width - rect.width) / 2f + 120f);
            rect = GUILayout.Window(0x57A9, rect, Draw, "Multiplayer campaign setup", Styles.Window, GUILayout.Width(700));
            UiBlock.Register(rect);
        }

        private static void Draw(int id)
        {
            if (header == null)
            {
                header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
                header.normal.textColor = new Color(0.95f, 0.85f, 0.55f);
                bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
                valueStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            }
            var net = Multiplayer.Session;
            bool host = net.IsHost;
            bool sub = net.IsSubordinate; // stage 5: subordinates see the setup read-only
            bool editable = host && net.IsConnected && MpCampaign.Phase == MpPhase.Lobby;
            MpSettings s = MpCampaign.Settings;
            if (host) MpCampaign.EnsureDefaults();

            Theatres t = Theatres.Find(s.Theatre);
            GUILayout.Label("Theatre: " + t.Name, bold);

            // Side picker (D20)
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host plays:", GUILayout.Width(110));
            GUI.enabled = editable;
            int side = GUILayout.Toolbar(s.HostSide, new[] { "Allies", "Japan" }, GUILayout.Width(220));
            GUI.enabled = true;
            if (side != s.HostSide) { s.HostSide = side; MpCampaign.MarkDirty(); }
            if (sub)
                GUILayout.Label($"   {RemoteOrders.PlayerName(0)}: {MpCampaign.SideName(s.HostSide)}   {RemoteOrders.PlayerName(1)}: " +
                                $"{MpCampaign.SideName(s.ClientSide)}   You: {MpCampaign.SideName(net.LocalTeam == 0 ? s.HostSide : s.ClientSide)}");
            else GUILayout.Label(host ? $"   You: {MpCampaign.SideName(s.HostSide)}   {net.PeerName}: {MpCampaign.SideName(s.ClientSide)}"
                                      : $"   You: {MpCampaign.SideName(s.ClientSide)}   {net.PeerName}: {MpCampaign.SideName(s.HostSide)}");
            GUILayout.EndHorizontal();

            // Column headers
            string alliesWho = s.HostSide == 0 ? (host ? "you" : net.PeerName) : (host ? net.PeerName : "you");
            string japanWho = s.HostSide == 1 ? (host ? "you" : net.PeerName) : (host ? net.PeerName : "you");
            if (sub)
            {
                int alliesTeam = s.HostSide == 0 ? 0 : 1;
                alliesWho = RemoteOrders.PlayerName(alliesTeam) + (net.LocalTeam == alliesTeam ? ", your team" : "");
                japanWho = RemoteOrders.PlayerName(1 - alliesTeam) + (net.LocalTeam == 1 - alliesTeam ? ", your team" : "");
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("", GUILayout.Width(220));
            GUILayout.Label($"Allies ({alliesWho})", bold, GUILayout.Width(210));
            GUILayout.Label($"Japan ({japanWho})", bold, GUILayout.Width(210));
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Min(470f, UiScale.Height - 300f)));
            string group = null;
            foreach (MpSettings.Row row in s.Rows())
            {
                if (row.Group != group) { group = row.Group; GUILayout.Space(4); GUILayout.Label(group, header); }
                GUILayout.BeginHorizontal();
                GUILayout.Label(row.Label, GUILayout.Width(220));
                for (int i = 0; i < 2; i++)
                {
                    int v = Stepper(row.Values[i], row.Min, row.Max, row.Step, row.Suffix, editable);
                    if (v != row.Values[i]) { row.Values[i] = v; MpCampaign.MarkDirty(); }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(4);
            GUILayout.Label("Both sides", header);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Refund when releasing ships", GUILayout.Width(220));
            int rr = Stepper(s.ReleaseRefundPct, 0, 200, 10, "%", editable);
            if (rr != s.ReleaseRefundPct) { s.ReleaseRefundPct = rr; MpCampaign.MarkDirty(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Points for sinking enemy ships", GUILayout.Width(220));
            int sy = Stepper(s.SunkYieldPct, 0, 300, 10, "%", editable);
            if (sy != s.SunkYieldPct) { s.SunkYieldPct = sy; MpCampaign.MarkDirty(); }
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();

            GUILayout.Label("Percentages scale each side's normal values (what that side gets as the human player in single-player). " +
                            "Only the campaign economy changes; ships, aircraft and weapons are untouched.", small);

            // Status + actions
            GUILayout.Space(4);
            if (!net.IsConnected) GUILayout.Label("Not connected: host or join from the mod window (F11) first.", bold);
            else if (sub)
                GUILayout.Label($"You are Player {net.LocalPlayerIndex + 1}, a subordinate on {NetSession.TeamName(net.LocalTeam)}. " +
                                $"{RemoteOrders.PlayerName(1)} is {(MpCampaign.Player2ReadySeen ? "ready" : "not ready")}; Player 1 starts the " +
                                "campaign and your team's map then loads by itself.", bold);
            else
            {
                string other = net.PeerName;
                GUILayout.Label(host ? $"{other}: {(MpCampaign.ClientReady ? "READY" : "not ready")}"
                                     : $"You are {(MpCampaign.ClientReady ? "READY" : "not ready")}. The host starts the campaign.", bold);
            }
            if (!string.IsNullOrEmpty(MpCampaign.StatusText)) GUILayout.Label(MpCampaign.StatusText, small);
            if (net.Players.Count > 2 || net.Player2SlotReserved) RoomWindow.DrawPlayers(net);

            GUILayout.BeginHorizontal();
            if (host)
            {
                GUI.enabled = editable;
                if (GUILayout.Button("Reset to defaults", GUILayout.Width(160))) MpCampaign.ResetDefaults();
                string why = MpCampaign.CanStart();
                GUI.enabled = why == null;
                if (GUILayout.Button(why == null ? "Start campaign" : "Start campaign (" + why + ")")) MpCampaign.HostStart();
                GUI.enabled = true;
            }
            else if (!sub)
            {
                GUI.enabled = net.IsConnected && MpCampaign.Phase == MpPhase.Lobby;
                if (GUILayout.Button(MpCampaign.ClientReady ? "Not ready" : "Ready", GUILayout.Width(160))) MpCampaign.ClientSetReady(!MpCampaign.ClientReady);
                GUI.enabled = true;
            }
            if (GUILayout.Button("Close", GUILayout.Width(90))) Visible = false;
            GUILayout.EndHorizontal();

            // M8: continue a saved MP campaign (host only; the save decides the sides and the economy values)
            if (host)
            {
                var saves = MpCampaign.HostSaves();
                if (saves.Count > 0)
                {
                    GUILayout.Space(6);
                    GUILayout.Label("Or continue a saved multiplayer campaign (sides and values come from the save):", bold);
                    string why = MpCampaign.CanStart();
                    for (int i = 0; i < saves.Count && i < 5; i++)
                    {
                        string file = saves[i].Key;
                        string label = file.Substring(MpCampaign.HostSavePrefix.Length).Trim();
                        if (label.EndsWith(".txt")) label = label.Substring(0, label.Length - 4);
                        GUILayout.BeginHorizontal();
                        GUI.enabled = why == null;
                        if (GUILayout.Button("Continue", GUILayout.Width(100))) MpCampaign.HostContinue(file);
                        GUI.enabled = true;
                        GUILayout.Label($"{label}   ({saves[i].Value:d MMM HH:mm})");
                        GUILayout.EndHorizontal();
                    }
                    if (why != null) GUILayout.Label(why, small);
                }
            }
            GUI.DragWindow();
        }

        private static int Stepper(int value, int min, int max, int step, string suffix, bool editable)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(210));
            GUI.enabled = editable && value > min;
            if (GUILayout.Button("−", GUILayout.Width(36))) value = Mathf.Max(min, value - step);
            GUI.enabled = true;
            GUILayout.Label(value + suffix, valueStyle, GUILayout.Width(110));
            GUI.enabled = editable && value < max;
            if (GUILayout.Button("+", GUILayout.Width(36))) value = Mathf.Min(max, value + step);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            return value;
        }
    }
}
