using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using WotSMultiplayer.Core;
using WotSMultiplayer.Net;
using WotSMultiplayer.Ui;

namespace WotSMultiplayer.Features.MpBattle
{
    /// <summary>
    /// Retreating from a live battle, same rules for both players (D47 + D48):
    ///   * Disengage = vanilla EngagementAI.Disengage(player: true) on each copy (P2's is forwarded as BattleCmd.Disengage;
    ///     on the host P2's ships cease fire, `enemyDisengage` is set, and the host times P2's disengage with the player's
    ///     rules mirrored). Firing or attacking cancels it (vanilla CancelPlayerDisengage), so the side stays in full retreat.
    ///   * Agreement ends the battle at once: both sides disengaging, or the other player pressing Allow on the bar
    ///     (BattleCmd.AllowRetreat from P2).
    ///   * Otherwise: the vanilla countdown, then 30 s of game time without a new hit from the opponent (RetreatRules;
    ///     hits = DamageableSea.ApplyDamage / ApplySmallCalibreDamage / ApplySimpleDamageToShip, DamageableAir.ApplyDamageToAIR /
    ///     ApplySmallCalibreDamageToAir on the host; fire spread and flooding don't go through these), then the retreating
    ///     player may force the retreat (Retreat / Leave). The host checks both its own and P2's attempts.
    ///   * 52 BattleDisengage host→client (0.5 s while either side disengages): both sides' countdown and quiet time, so
    ///     both copies draw the same bars and P2's vanilla countdown follows the host.
    /// The battle then ends like the host's own retreat (combat over + action report; closing it ends it on both copies).
    /// </summary>
    internal static partial class MpBattle
    {
        private const byte MsgDisengage = 52;
        // flags in message 52
        private const byte DisGuest = 1, DisRefused = 2, DisHost = 4, DisGuestReady = 8, DisHostReady = 16, DisEnded = 32, DisGuestHit = 64;

        // host
        private static bool guestDisengaging, hostWasDisengaging;
        private static float guestDisengageTime, lastDisengageSend;
        private static float hostCountdownDoneAt = -1f, guestCountdownDoneAt = -1f, hostLastHit = -1f, guestLastHit = -1f;
        private static bool hostAllowedByGuest, guestAllowedByHost, lastSentActive;
        // client
        private static bool clientRetreatSent, clientEndedToast;
        private static float clientDisengageToggledAt = -10f;
        private struct BarSide { public bool Active, Ready; public float Elapsed, Needed, QuietLeft; }
        private static BarSide clientHostBar, clientOwnBar;

        internal static void RetreatReset()
        {
            guestDisengaging = hostWasDisengaging = false;
            guestDisengageTime = lastDisengageSend = 0f;
            hostCountdownDoneAt = guestCountdownDoneAt = hostLastHit = guestLastHit = -1f;
            hostAllowedByGuest = guestAllowedByHost = lastSentActive = guestWipeToast = false;
            clientRetreatSent = clientEndedToast = false;
            clientDisengageToggledAt = -10f;
            clientHostBar = clientOwnBar = new BarSide();
        }

        private static float BattleNow() => EngagementManager.instance != null ? EngagementManager.instance.engagementTime : 0f;

        // ---------- host: state ----------

        private static RetreatSide HostSide()
        {
            EngagementData ed = EngagementManager.instance != null ? EngagementManager.instance.engagementData : null;
            return new RetreatSide
            {
                Disengaging = ed != null && ed.playerDisengage,
                Elapsed = ed != null ? ed.disengageTimers.x : 0f,
                Needed = ed != null ? ed.currentDisengageTimes.x : 0f,
                CountdownDoneAt = hostCountdownDoneAt, LastHitAt = hostLastHit, AllowedByOther = hostAllowedByGuest,
                NoUnits = SideEmpty(true),
            };
        }

        private static RetreatSide GuestSide() => new RetreatSide
        {
            Disengaging = guestDisengaging, Elapsed = guestDisengageTime, Needed = GuestDisengageTime(),
            CountdownDoneAt = guestCountdownDoneAt, LastHitAt = guestLastHit, AllowedByOther = guestAllowedByHost,
            NoUnits = SideEmpty(false),
        };

        /// <summary>Host: nothing left on that side — vanilla's own test (EngagementAI's lists drop sunk/departed units).</summary>
        private static bool SideEmpty(bool hostSide)
        {
            EngagementAI ai = EngagementAI.instance;
            if (ai == null) return false;
            List<Unit> sea = hostSide ? ai.playerSea : ai.otherSea, air = hostSide ? ai.playerAir : ai.otherAir;
            return (sea == null || sea.Count == 0) && (air == null || air.Count == 0);
        }

        /// <summary>Host (damage prefixes): a new hit on one of this side's units resets that side's 30 s check.</summary>
        internal static void HostRecordHit(Unit u)
        {
            if (!HostActive || u == null) return;
            if (u.faction == Faction.Player) hostLastHit = BattleNow();
            else guestLastHit = BattleNow();
        }

        /// <summary>
        /// Host (MpBattle.Update): P2's disengage clock (game time), when each side's countdown finished, agreement → end,
        /// and the state for P2's copy while either side disengages (plus one message after both stopped).
        /// </summary>
        private static void HostUpdateDisengage()
        {
            EngagementAI ai = EngagementAI.instance;
            if (ai == null || ai.combatOver || !HostBattleLive()) return;
            float now = BattleNow();
            if (guestDisengaging) guestDisengageTime += Time.deltaTime;
            RetreatSide h = HostSide();
            if (h.Disengaging != hostWasDisengaging)
            {
                hostWasDisengaging = h.Disengaging;
                hostCountdownDoneAt = -1f;
                hostAllowedByGuest = false;
                h = HostSide();
            }
            RetreatSide g = GuestSide();
            if (h.Disengaging && hostCountdownDoneAt < 0f && RetreatRules.CountdownDone(h)) { hostCountdownDoneAt = now; h = HostSide(); }
            if (g.Disengaging && guestCountdownDoneAt < 0f && RetreatRules.CountdownDone(g)) { guestCountdownDoneAt = now; g = GuestSide(); }

            if (RetreatRules.Agreement(h, g) || RetreatRules.Agreement(g, h))
            {
                string why = h.Disengaging && g.Disengaging ? "both players are retreating"
                           : h.Disengaging ? "Player 2 allowed the host's retreat" : "the host allowed Player 2's retreat";
                EndBattleForBoth(why);
                return;
            }
            if (g.NoUnits && !guestWipeToast)
            {
                guestWipeToast = true;
                Toasts.Show("Player 2 has nothing left in this battle: you can leave now (RETREAT, then LEAVE).", 10f);
            }
            bool active = h.Disengaging || g.Disengaging;
            if ((active || lastSentActive) && Time.realtimeSinceStartup - lastDisengageSend >= 0.5f) SendDisengageState(false, false);
        }

        private static bool guestWipeToast;

        private static void HostGuestDisengage(bool on)
        {
            EngagementManager em = EngagementManager.instance;
            EngagementAI ai = EngagementAI.instance;
            if (em == null || ai == null || ai.combatOver) return;
            if (em.startCombatButton != null && em.startCombatButton.activeSelf) return; // deployment: vanilla says "no retreat" too
            if (guestDisengaging == on) { SendDisengageState(false, false); return; }
            guestDisengaging = on;
            guestDisengageTime = 0f;
            guestCountdownDoneAt = -1f;
            guestAllowedByHost = false;
            em.engagementData.enemyDisengage = on; // vanilla's flag for the other side breaking off
            if (on)
                foreach (List<Unit> list in new[] { ai.otherSea, ai.otherAir })
                    if (list != null)
                        foreach (Unit u in list)
                            if (u != null && !u.isDestroyed && u.unitAI != null)
                            {
                                u.unitAI.CeaseFiringAllWeapons(); // as vanilla does for the player's units
                                ai.ApplyFireAtWillToUnit(u, false);
                            }
            Plugin.Log.LogInfo($"MP battle: P2 {(on ? "disengaging (countdown " + GuestDisengageTime().ToString("0") + " s)" : "cancelled the disengage")}");
            Toasts.Show(on ? "Player 2 is disengaging: their ships cease fire. Disengage too, or press Allow on the bar, to end the battle now."
                           : "Player 2 cancelled the disengage.");
            SendDisengageState(false, false);
        }

        /// <summary>Host: P2 pressed Retreat (leave = false) or Leave (leave = true).</summary>
        private static void HostRetreat(bool leave)
        {
            EngagementAI ai = EngagementAI.instance;
            if (ai == null || ai.combatOver) return;
            float now = BattleNow();
            RetreatSide g = GuestSide(), h = HostSide();
            if (!RetreatRules.CanRetreat(g, h, now))
            {
                Plugin.Log.LogInfo($"MP battle: P2 retreat refused (disengaging {g.Disengaging}, countdown {g.Elapsed:0}/{g.Needed:0} s, quiet left {RetreatRules.QuietLeft(g, now):0} s)");
                SendDisengageState(true, false);
                return;
            }
            EndBattleForBoth(leave ? "Player 2 left the battle" : "Player 2 retreated");
        }

        /// <summary>Host: its own Retreat / Leave (after vanilla's confirm). False = refused (toast says why).</summary>
        internal static bool HostMayRetreat()
        {
            float now = BattleNow();
            RetreatSide h = HostSide(), g = GuestSide();
            if (RetreatRules.CanRetreat(h, g, now))
            {
                Plugin.Log.LogInfo($"MP battle: host retreat allowed ({(g.NoUnits ? "Player 2 has nothing left" : RetreatRules.Agreement(h, g) ? "agreement" : "countdown + 30 s quiet")})");
                return true;
            }
            Toasts.Show(RefusalText(h, now, mine: true));
            return false;
        }

        private static string RefusalText(RetreatSide s, float now, bool mine)
        {
            if (!s.Disengaging) return "Disengage first.";
            if (!RetreatRules.CountdownDone(s)) return $"Not yet: {Mathf.CeilToInt(s.Needed - s.Elapsed)} s of disengage countdown left (or the other player agrees).";
            return (RetreatRules.HitRestarted(s) ? "A new hit restarted the timer: " : "Not yet: ") +
                   $"{Mathf.CeilToInt(RetreatRules.QuietLeft(s, now))} s more without new hits (or the other player agrees).";
        }

        /// <summary>Host: the host pressed Allow on P2's bar / P2's AllowRetreat order.</summary>
        internal static void HostAllowGuest() { if (guestDisengaging) guestAllowedByHost = true; }
        private static void HostAllowedByGuest() { if (HostSide().Disengaging) hostAllowedByGuest = true; }

        private static void EndBattleForBoth(string why)
        {
            EngagementAI ai = EngagementAI.instance;
            if (ai == null || ai.combatOver) return;
            Plugin.Log.LogInfo($"MP battle: retreat — {why}: the battle ends for both (D47/D48)");
            SendDisengageState(false, true);
            if (CombatInterface.instance != null) CombatInterface.instance.ForceOffBinoculars();
            ai.combatOver = true;
            ActionReport.instance.CreateActionReport();
            EngagementManager.instance.engagementData.playerRetreated = false;
            guestDisengaging = false;
            Toasts.Show($"Retreat: {why}. The battle is over for both — close the action report to return to the map.", 10f);
        }

        private static void SendDisengageState(bool refused, bool ended)
        {
            lastDisengageSend = Time.realtimeSinceStartup;
            if (!AnyoneInBattle) return;
            float now = BattleNow();
            RetreatSide h = HostSide(), g = GuestSide();
            lastSentActive = h.Disengaging || g.Disengaging;
            byte flags = (byte)((g.Disengaging ? DisGuest : 0) | (refused ? DisRefused : 0) | (h.Disengaging ? DisHost : 0)
                              | (RetreatRules.CanForce(g, now) ? DisGuestReady : 0) | (RetreatRules.CanForce(h, now) ? DisHostReady : 0)
                              | (ended ? DisEnded : 0) | (RetreatRules.HitRestarted(g) ? DisGuestHit : 0));
            float gq = RetreatRules.QuietLeft(g, now), hq = RetreatRules.QuietLeft(h, now);
            SendBattleTeam1(MsgDisengage, Protocol.Build(w =>
            {
                w.Write(flags);
                w.Write(g.Elapsed); w.Write(g.Needed); w.Write(gq);
                w.Write(h.Elapsed); w.Write(h.Needed); w.Write(hq);
            }));
            if (!HostTeamHasSubordinates) return;
            // the host's team sees the host's side as its own ("guest" in the message = the receiver's side)
            byte flags0 = (byte)((h.Disengaging ? DisGuest : 0) | (g.Disengaging ? DisHost : 0)
                               | (RetreatRules.CanForce(h, now) ? DisGuestReady : 0) | (RetreatRules.CanForce(g, now) ? DisHostReady : 0)
                               | (ended ? DisEnded : 0) | (RetreatRules.HitRestarted(h) ? DisGuestHit : 0));
            SendBattleTeam0(MsgDisengage, Protocol.Build(w =>
            {
                w.Write(flags0);
                w.Write(h.Elapsed); w.Write(h.Needed); w.Write(hq);
                w.Write(g.Elapsed); w.Write(g.Needed); w.Write(gq);
            }));
        }

        // ---------- client ----------

        private static Unit FirstOwnUnit()
        {
            EngagementManager em = EngagementManager.instance;
            if (em == null) return null;
            foreach (List<Unit> list in new[] { em.allSeaUnits, em.allAirUnits })
                if (list != null)
                    foreach (Unit u in list)
                        if (u != null && u.faction == Faction.Player && !u.isDestroyed) return u;
            return null;
        }

        /// <summary>Client: vanilla just toggled P2's disengage (button, or vanilla cancelling it when P2 attacks).</summary>
        internal static void ClientDisengageToggled(bool on)
        {
            Unit carrier = FirstOwnUnit(); // orders travel with a unit reference; the host ignores which one
            if (carrier == null) return;
            clientDisengageToggledAt = Time.realtimeSinceStartup;
            SendControl(carrier, BattleCmd.Disengage, w => w.Write(on));
        }

        /// <summary>Client: P2 confirmed Retreat (leave = false) or Leave (leave = true). False = skip vanilla here.</summary>
        internal static bool ClientRetreat(bool leave)
        {
            if (clientRetreatSent) return false;
            Unit carrier = FirstOwnUnit();
            if (carrier == null)
            {
                Toasts.Show("All your ships and planes in this battle are gone: the battle ends when the host leaves (they can leave at once).");
                return false;
            }
            clientRetreatSent = true;
            SendControl(carrier, BattleCmd.Retreat, w => w.Write(leave));
            Toasts.Show("Retreat sent to the host...");
            return false;
        }

        private static void ClientAllowHost()
        {
            if (Multiplayer.Session.IsSubordinate) return; // the commanders decide
            Unit carrier = FirstOwnUnit();
            if (carrier == null) return;
            SendControl(carrier, BattleCmd.AllowRetreat);
            Toasts.Show("You allowed the host's retreat: the battle ends.");
        }

        private static void ClientDisengageState(byte[] payload)
        {
            byte flags;
            float gElapsed, gNeeded, gQuiet;
            using (var r = Protocol.Reader(payload))
            {
                flags = r.ReadByte();
                gElapsed = r.ReadSingle(); gNeeded = r.ReadSingle(); gQuiet = r.ReadSingle();
                clientHostBar = new BarSide
                {
                    Active = (flags & DisHost) != 0, Ready = (flags & DisHostReady) != 0,
                    Elapsed = r.ReadSingle(), Needed = r.ReadSingle(), QuietLeft = r.ReadSingle(),
                };
            }
            clientOwnBar = new BarSide { Active = (flags & DisGuest) != 0, Ready = (flags & DisGuestReady) != 0, Elapsed = gElapsed, Needed = gNeeded, QuietLeft = gQuiet };
            if ((flags & DisEnded) != 0 && !clientEndedToast)
            {
                clientEndedToast = true;
                Toasts.Show("Retreat: the battle is over for both. Your action report opens now; press Continue on it to return to the map.", 10f);
            }
            EngagementManager em = EngagementManager.instance;
            if (em == null || em.engagementData == null) return;
            EngagementData ed = em.engagementData;
            bool active = clientOwnBar.Active;
            if ((flags & DisRefused) != 0)
            {
                clientRetreatSent = false;
                Toasts.Show(gElapsed < gNeeded
                    ? $"Not yet: {Mathf.CeilToInt(gNeeded - gElapsed)} s of disengage countdown left (or the host agrees)."
                    : ((flags & DisGuestHit) != 0 ? "A new hit restarted the timer: " : "Not yet: ") +
                      $"{Mathf.CeilToInt(gQuiet)} s more without new hits (or the host agrees).");
            }
            // P2's own toggle may still be on its way to the host: don't flip it back from an older state
            if (ed.playerDisengage != active && Time.realtimeSinceStartup - clientDisengageToggledAt > 1.5f)
            {
                ed.playerDisengage = active;
                if (CombatInterface.instance != null) CombatInterface.instance.SetPlayerDisengage(active);
            }
            if (active && ed.playerDisengage)
            {
                ed.disengageTimers.x = gElapsed;
                ed.currentDisengageTimes.x = gNeeded;
            }
        }

        /// <summary>Client (MpBattle.Update): the host's clocks run on between messages.</summary>
        internal static void ClientRetreatTick()
        {
            float dt = Time.deltaTime;
            Tick(ref clientHostBar, dt);
            Tick(ref clientOwnBar, dt);
        }

        private static void Tick(ref BarSide b, float dt)
        {
            if (!b.Active) return;
            if (b.Elapsed < b.Needed) b.Elapsed += dt;
            else if (b.QuietLeft > 0f) b.QuietLeft = Mathf.Max(0f, b.QuietLeft - dt);
        }

        // ---------- host: P2's disengage time (player rules, sides swapped) ----------

        /// <summary>
        /// P2's disengage time with the player's rules, sides swapped: vanilla EngagementAI.GetCurrentDisengageTimes().x /
        /// GetSeaSeaDisangageTime() computed for the "other" lists (P2's units on the host) against the player's.
        /// </summary>
        private static float GuestDisengageTime()
        {
            EngagementAI ai = EngagementAI.instance;
            EngagementManager em = EngagementManager.instance;
            Config cfg = Config.instance;
            if (ai == null || em == null || cfg == null) return 0f;
            List<Unit> mineSea = ai.otherSea ?? new List<Unit>(), mineAir = ai.otherAir ?? new List<Unit>();
            List<Unit> theirSea = ai.playerSea ?? new List<Unit>(), theirAir = ai.playerAir ?? new List<Unit>();
            if ((mineSea.Count == 0 && mineAir.Count == 0) || (theirSea.Count == 0 && theirAir.Count == 0) ||
                (guestDisengaging && em.engagementData.playerDisengage)) return 0f;
            if (!AnyDetected(mineSea, mineAir) && !AnyDetected(theirSea, theirAir)) return 0f;
            float t = cfg.disengageTimes.x;
            if (em.engagementData.playerDisengage) t = 0f; // the other side is breaking off too
            if (mineSea.Count > 0 && theirSea.Count > 0) return GuestSeaSeaTime(mineSea, mineAir, theirSea);
            if (mineSea.Count == 0 && theirSea.Count == 0) t *= cfg.disengageMultipliers.x;
            else if (mineAir.Count > 0 && theirSea.Count > 0) t *= cfg.disengageMultipliers.x;
            return t;
        }

        private static float GuestSeaSeaTime(List<Unit> mineSea, List<Unit> mineAir, List<Unit> theirSea)
        {
            Config cfg = Config.instance;
            float result = cfg.disengageTimes.x;
            if (!AnyDetected(mineSea, mineAir)) return result; // (vanilla: full time while the leaving side is undetected)
            float best = float.PositiveInfinity;
            foreach (Unit a in mineSea)
                foreach (Unit b in theirSea)
                    if (a != null && b != null) best = Mathf.Min(best, (a.transform.position - b.transform.position).sqrMagnitude);
            if (Mathf.Sqrt(best) > cfg.maxSeaVisibility * 100f) return 0f;
            float mine = AvgSpeed(mineSea), theirs = AvgSpeed(theirSea);
            if (theirs > 0f && mine / theirs < 0.5f) result *= cfg.disengageMultipliers.z;
            else if (theirs > mine) result *= cfg.disengageMultipliers.y;
            return result;
        }

        private static bool AnyDetected(List<Unit> a, List<Unit> b)
        {
            foreach (Unit u in a) if (u != null && u.detected) return true;
            foreach (Unit u in b) if (u != null && u.detected) return true;
            return false;
        }

        private static float AvgSpeed(List<Unit> sea)
        {
            float sum = 0f;
            int n = 0;
            foreach (Unit u in sea)
                if (u != null && u.damageableSea != null) { sum += u.unitData.maxSpeed * u.damageableSea.speedPenalty; n++; }
            return n > 0 ? sum / n : 0f;
        }

        // ---------- the retreat bars (both copies) ----------

        private static GUIStyle retreatBarStyle;

        /// <summary>Plugin.OnGUI: one bar per side that is disengaging, on both copies; the other side's bar has Allow.</summary>
        internal static void DrawRetreatBars()
        {
            if (!HostActive && !ClientActive) return;
            EngagementManager em = EngagementManager.instance;
            EngagementAI ai = EngagementAI.instance;
            if (em == null || em.engagementData == null || ai == null || ai.combatOver) return;
            string peer = string.IsNullOrEmpty(Multiplayer.Session.PeerName) ? (HostActive ? "Player 2" : "The host") : Multiplayer.Session.PeerName;
            BarSide mine, other;
            if (HostActive)
            {
                float now = BattleNow();
                RetreatSide h = HostSide(), g = GuestSide();
                mine = new BarSide { Active = h.Disengaging, Ready = RetreatRules.CanForce(h, now), Elapsed = h.Elapsed, Needed = h.Needed, QuietLeft = RetreatRules.QuietLeft(h, now) };
                other = new BarSide { Active = g.Disengaging, Ready = RetreatRules.CanForce(g, now), Elapsed = g.Elapsed, Needed = g.Needed, QuietLeft = RetreatRules.QuietLeft(g, now) };
            }
            else
            {
                mine = clientOwnBar;
                mine.Active = em.engagementData.playerDisengage && clientOwnBar.Active;
                other = clientHostBar;
            }
            int row = 0;
            if (mine.Active) DrawRetreatBar(row++, true, peer, mine);
            if (other.Active && DrawRetreatBar(row, false, peer, other))
            {
                if (HostActive) HostAllowGuest();
                else ClientAllowHost();
            }
        }

        /// <summary>Returns true when Allow was clicked (other side's bar only).</summary>
        private static bool DrawRetreatBar(int row, bool mine, string peer, BarSide b)
        {
            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            GUI.matrix = Matrix4x4.identity; // screen pixels
            bool clicked = false;
            try
            {
                float s = Mathf.Max(0.6f, Screen.height / 1080f);
                float w = 560f * s, h = 26f * s;
                Rect bar = new Rect((Screen.width - w) / 2f, (150f + row * 34f) * s, w, h); // below the toasts
                bool countdown = b.Elapsed < b.Needed;
                float fill = b.Ready ? 1f : countdown ? (b.Needed > 0f ? Mathf.Clamp01((b.Needed - b.Elapsed) / b.Needed) : 0f)
                                                       : Mathf.Clamp01(b.QuietLeft / RetreatRules.QuietSeconds);
                if (retreatBarStyle == null) retreatBarStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                retreatBarStyle.fontSize = Mathf.RoundToInt(14f * s);
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.8f);
                    GUI.DrawTexture(bar, Texture2D.whiteTexture);
                    GUI.color = b.Ready ? new Color(0.3f, 0.75f, 0.35f, 1f) : countdown ? new Color(0.86f, 0.66f, 0.22f, 1f) : new Color(0.35f, 0.6f, 0.9f, 1f);
                    GUI.DrawTexture(new Rect(bar.x + 2f, bar.y + 2f, (bar.width - 4f) * fill, bar.height - 4f), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
                string text;
                if (mine)
                    text = b.Ready ? "You can retreat now (Retreat): the battle ends for both"
                         : countdown ? $"You are disengaging: countdown {Mathf.CeilToInt(b.Needed - b.Elapsed)} s (ends now if {peer} agrees)"
                         : $"Hold fire: {Mathf.CeilToInt(b.QuietLeft)} s without new hits, then you can retreat";
                else
                    text = b.Ready ? $"{peer} can retreat now: the battle ends for both if they do"
                         : countdown ? $"{peer} is disengaging: countdown {Mathf.CeilToInt(b.Needed - b.Elapsed)} s"
                         : $"{peer} is retreating: {Mathf.CeilToInt(b.QuietLeft)} s without new hits, then they can leave";
                GUI.Label(bar, text, retreatBarStyle);
                if (!mine && !Multiplayer.Session.IsSubordinate)
                {
                    Rect btn = new Rect(bar.xMax + 8f * s, bar.y, 120f * s, h);
                    clicked = GUI.Button(btn, "Allow retreat");
                    float f = UiScale.Factor; // UiBlock works in the scaled (virtual) coordinates: keep the click off the game
                    UiBlock.Register(new Rect(btn.x / f, btn.y / f, btn.width / f, btn.height / f));
                }
            }
            finally
            {
                GUI.matrix = oldMatrix;
                GUI.color = oldColor;
            }
            return clicked;
        }
    }

    /// <summary>Retreat / Leave on both copies, and the host's record of new hits (D47 + D48).</summary>
    [HarmonyPatch]
    internal static class MpBattleRetreatPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.Disengage))]
        private static void DisengageBefore(bool player, ref bool __state)
        {
            EngagementManager em = EngagementManager.instance;
            __state = em != null && em.engagementData != null && em.engagementData.playerDisengage;
        }

        /// <summary>A subordinate's Disengage button does nothing: retreating is the commanders' decision.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.Disengage))]
        private static bool SubordinateNoDisengage(bool player)
        {
            if (!MpBattle.ClientActive || !player || MpBattle.SuppressForward || !Multiplayer.Session.IsSubordinate) return true;
            Toasts.Show("Only your commander decides on disengaging and retreating.");
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EngagementAI), nameof(EngagementAI.Disengage))]
        private static void DisengageAfter(bool player, bool __state)
        {
            if (!MpBattle.ClientActive || !player || MpBattle.SuppressForward) return;
            bool now = EngagementManager.instance.engagementData.playerDisengage;
            if (now != __state) MpBattle.ClientDisengageToggled(now);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.RetreatCombat))]
        private static bool Retreat(CombatInterface __instance, bool confirm)
        {
            if (confirm) return true;
            if (MpBattle.HostActive && Multiplayer.Session.IsConnected)
            {
                if (!EngagementAI.instance.CanPlayerDisengage()) return true; // vanilla logs "can't leave yet"
                return MpBattle.HostMayRetreat();
            }
            if (!MpBattle.ClientActive) return true;
            if (Multiplayer.Session.IsSubordinate) { Toasts.Show("Only your commander decides on retreating."); return false; }
            if (!EngagementAI.instance.CanPlayerDisengage()) return true;
            if (__instance.disengageButtons != null && __instance.disengageButtons.Length > 1) __instance.disengageButtons[1].gameObject.SetActive(false);
            return MpBattle.ClientRetreat(false);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatInterface), nameof(CombatInterface.LeaveCombat))]
        private static bool Leave(CombatInterface __instance, bool confirm)
        {
            if (confirm) return true;
            if (MpBattle.HostActive && Multiplayer.Session.IsConnected) return MpBattle.HostMayRetreat();
            if (!MpBattle.ClientActive) return true;
            if (Multiplayer.Session.IsSubordinate) { Toasts.Show("Only your commander decides on leaving the battle."); return false; }
            if (__instance.disengageButtons != null && __instance.disengageButtons.Length > 2) __instance.disengageButtons[2].gameObject.SetActive(false);
            return MpBattle.ClientRetreat(true);
        }

        // new hits (host): these are the paths of shells, torpedoes, bombs, depth charges, AA/strafing and rams; fire spread
        // and flooding work on cells directly and don't pass here
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.ApplyDamage))]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.ApplySmallCalibreDamage))]
        [HarmonyPatch(typeof(DamageableSea), nameof(DamageableSea.ApplySimpleDamageToShip))]
        private static void SeaHit(DamageableSea __instance)
        {
            if (MpBattle.HostActive && __instance != null) MpBattle.HostRecordHit(__instance.parentUnit);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageableAir), nameof(DamageableAir.ApplyDamageToAIR))]
        private static void AirHit(DamageableAir __instance)
        {
            if (MpBattle.HostActive && __instance != null) MpBattle.HostRecordHit(__instance.parentUnit);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DamageableAir), nameof(DamageableAir.ApplySmallCalibreDamageToAir))]
        private static void AirSmallHit(DamageableAir __instance, float damage)
        {
            if (MpBattle.HostActive && damage > 0f && __instance != null) MpBattle.HostRecordHit(__instance.parentUnit);
        }
    }
}
