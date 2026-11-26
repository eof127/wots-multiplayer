using System;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// The after-action report on both PCs (user request 2026-11-25; D53).
    ///   54 CombatOver host→client {int P2's command points, int host's command points}
    /// Every MP battle ends on the host (vanilla Retreat / Leave, or EndBattleForBoth): EngagementAI.combatOver turns true
    /// and the host's ActionReport opens. The host then sends 54 (right after a fresh snapshot, so sunk ships are marked);
    /// P2's copy sets combatOver and opens its own vanilla report (its ships are the "player" side there).
    /// Command points: vanilla only pays the player (faction 0) for enemy ships sunk. In MP faction 1 is Player 2, so the
    /// host also works out P2's points with the same formula (host ships sunk × the campaign's sunk yield) and adds them
    /// to faction 1 when the host closes its report. Each report shows that player's own points (also 0) and a toast
    /// tells each player what the other earned. P2's copy never adds points itself (the campaign data comes from the host).
    /// </summary>
    internal static partial class MpBattle
    {
        private const byte MsgCombatOver = 54;

        // host
        private static bool hostReportSent;
        private static int hostGuestPoints; // still to add to faction 1 when the host closes its report
        // client
        private static bool clientReportOpen, clientHostLeft;
        private static int clientOwnPoints = -1, clientHostPoints;

        /// <summary>
        /// Host: the battle has really begun — EngagementAI's combat-over flag is down and both sides have units in its
        /// lists. Until then the flag can still be up from the previous battle (P2 got a blank action report before the
        /// battle started) and the lists empty ("Player 2 has nothing left" at the start) — user test 2026-11-26.
        /// </summary>
        private static bool hostBattleLive;

        private static bool HostBattleLive()
        {
            if (hostBattleLive) return true;
            EngagementAI ai = EngagementAI.instance;
            if (ai == null || ai.combatOver) return false;
            bool host = (ai.playerSea != null && ai.playerSea.Count > 0) || (ai.playerAir != null && ai.playerAir.Count > 0);
            bool guest = (ai.otherSea != null && ai.otherSea.Count > 0) || (ai.otherAir != null && ai.otherAir.Count > 0);
            hostBattleLive = host && guest;
            return hostBattleLive;
        }

        internal static void ReportReset()
        {
            hostBattleLive = false;
            hostReportSent = false;
            hostGuestPoints = 0;
            clientReportOpen = clientHostLeft = false;
            clientOwnPoints = -1;
            clientHostPoints = 0;
        }

        /// <summary>Host (MpBattle.Update): the battle has just ended → tell P2 and send the points.</summary>
        private static void HostCheckCombatOver()
        {
            if (hostReportSent || !HostBattleLive()) return;
            EngagementAI ai = EngagementAI.instance;
            if (ai == null || !ai.combatOver) return;
            hostReportSent = true;
            bool campaign = EngagementManager.instance != null && EngagementManager.instance.engagementData != null &&
                            EngagementManager.instance.engagementData.isCampaignEngagement;
            int guest = campaign ? GuestBattlePoints() : 0;
            int host = campaign && ActionReport.instance != null ? ActionReport.instance.pointsGained : 0;
            hostGuestPoints = guest;
            Plugin.Log.LogInfo($"MP battle: combat over — command points: host {host}, Player 2 {guest}");
            if (campaign) Toasts.Show($"{PeerLabel("Player 2")} earned {guest} command points in this battle.", 10f);
            if (!AnyoneInBattle) return;
            try { SendSnapshot(); } catch (Exception e) { Plugin.Log.LogWarning("MP battle: final snapshot failed: " + e.Message); }
            // each team's copy reads {own points, other side's points}
            SendBattleTeam1(MsgCombatOver, Protocol.Build(w => { w.Write(guest); w.Write(host); }));
            if (HostTeamHasSubordinates) SendBattleTeam0(MsgCombatOver, Protocol.Build(w => { w.Write(host); w.Write(guest); }));
        }

        /// <summary>Host: P2's points = the host's ships sunk or scuttled, valued as vanilla ActionReport values enemy ships.</summary>
        private static int GuestBattlePoints()
        {
            EngagementManager em = EngagementManager.instance;
            CampaignData cd = CampaignManager.instance != null ? CampaignManager.instance.campaignData : null;
            if (em == null || cd == null || em.playerUnits == null) return 0;
            int pts = 0;
            foreach (Unit u in em.playerUnits)
            {
                if (u == null || u.unitSea == null || u.isParked) continue;
                if (!u.isDestroyed && (u.damageableSea == null || !u.damageableSea.scuttled)) continue;
                UnitListData d;
                if (!em.unitSeaDictionary.TryGetValue(u.name, out d) || d == null) continue;
                if (cd.useSunkYieldPoints)
                {
                    int diff = LanguageManager.instance != null && LanguageManager.instance.campaignDifficultyDropdown != null
                        ? LanguageManager.instance.campaignDifficultyDropdown.value : 0;
                    if (d.sunkYield != null && d.sunkYield.Length > 0) pts += d.sunkYield[Mathf.Clamp(diff, 0, d.sunkYield.Length - 1)];
                }
                else pts += Mathf.FloorToInt(d.cost * cd.enemySunkYield);
            }
            return pts;
        }

        /// <summary>Client, message 54: open the vanilla action report on P2's copy.</summary>
        private static void ClientCombatOver(byte[] payload)
        {
            using (var r = Protocol.Reader(payload))
            {
                clientOwnPoints = r.ReadInt32();
                clientHostPoints = r.ReadInt32();
            }
            EngagementAI ai = EngagementAI.instance;
            if (ai == null || ActionReport.instance == null || clientReportOpen) return;
            try { ClientApply(); } catch { } // the final snapshot (sunk / scuttled ships) arrived just before this message
            clientReportOpen = true;
            if (CombatInterface.instance != null) CombatInterface.instance.ForceOffBinoculars();
            ai.combatOver = true;
            ActionReport.instance.CreateActionReport(); // → ReportCreated (postfix) shows P2's points
            Plugin.Log.LogInfo($"MP battle: client action report (command points: own {clientOwnPoints}, host {clientHostPoints})");
            if (Multiplayer.Session.IsSubordinate)
                Toasts.Show($"The battle is over. Your side earned {clientOwnPoints} command points, the other side {clientHostPoints}. " +
                            "Press Continue on your report to return to the map.", 10f);
            else Toasts.Show($"The battle is over. {PeerLabel("The host")} earned {clientHostPoints} command points. Press Continue on your report to return to the map.", 10f);
        }

        private static string PeerLabel(string fallback)
        {
            string n = Multiplayer.Session.PeerName;
            return string.IsNullOrEmpty(n) ? fallback : n;
        }

        /// <summary>Postfix of ActionReport.CreateActionReport: in MP each report always shows that player's own points.</summary>
        internal static void ReportCreated(ActionReport rep)
        {
            if (!ModState.MultiplayerActive || rep == null || rep.commandPointsText == null || rep.commandPointsText.Length < 2) return;
            if (!HostActive && !ClientActive) return;
            EngagementAI ai = EngagementAI.instance;
            EngagementManager em = EngagementManager.instance;
            if (ai == null || !ai.combatOver || em == null || em.engagementData == null || !em.engagementData.isCampaignEngagement) return;
            if (ClientActive)
            {
                if (clientOwnPoints < 0) return; // not from message 54
                rep.pointsGained = clientOwnPoints; // display only: CloseActionReport never pays it on P2's copy (below)
            }
            rep.commandPointsText[0].gameObject.SetActive(true);
            rep.commandPointsText[1].text = rep.pointsGained.ToString();
        }

        /// <summary>Prefix of ActionReport.CloseActionReport (only when the battle is over).</summary>
        internal static void ReportClosing(ActionReport rep)
        {
            if (!ModState.MultiplayerActive || rep == null) return;
            EngagementAI ai = EngagementAI.instance;
            if (ai == null || !ai.combatOver) return;
            if (ClientActive)
            {
                rep.pointsGained = 0; // the host pays P2 (faction 1); its campaign data reaches this copy in the next update
                if (!clientHostLeft) Toasts.Show("Back to the map. The campaign carries on when the host closes their report.", 8f);
                return;
            }
            if (!HostActive || !Multiplayer.Session.IsHost) return;
            EngagementManager em = EngagementManager.instance;
            CampaignManager cm = CampaignManager.instance;
            if (hostGuestPoints <= 0 || em == null || !em.engagementData.isCampaignEngagement || cm == null) return;
            cm.campaignData.commandBonusPoints[1] += hostGuestPoints;
            Plugin.Log.LogInfo($"MP battle: Player 2 (faction 1) gets {hostGuestPoints} command points for the battle (D53)");
            hostGuestPoints = 0;
        }

        /// <summary>Client: the host closed the battle while P2's report is still open.</summary>
        private static void ClientHostLeftDuringReport() => clientHostLeft = true;
    }

    [HarmonyPatch(typeof(ActionReport), nameof(ActionReport.CreateActionReport))]
    internal static class MpReportCreatePatch
    {
        private static void Postfix(ActionReport __instance) => MpBattle.ReportCreated(__instance);
    }

    [HarmonyPatch(typeof(ActionReport), nameof(ActionReport.CloseActionReport))]
    internal static class MpReportClosePatch
    {
        private static void Prefix(ActionReport __instance) => MpBattle.ReportClosing(__instance);
    }
}
