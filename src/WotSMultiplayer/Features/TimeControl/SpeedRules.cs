using System;

namespace WotSMultiplayer.Features.TimeControl
{
    internal enum ProposalKind { SpeedUp, Pause, Resume }

    internal sealed class Proposal
    {
        public int From;
        public ProposalKind Kind;
        public int TargetIndex;   // for SpeedUp
        public float ExpiresAt;   // real time
    }

    /// <summary>
    /// Pure rules for shared game speed between two players (no Unity calls; time is passed in).
    /// Decisions: D15 (defaults), D16 (slow instant, speed-up by vote, pause by agreement),
    /// D17 (force tokens skip the vote; forced speed locked 60 s; 3 per battle / 3 per in-game week on the map).
    /// Player indices: 0 and 1. In MP, 0 = host, 1 = client; the network layer will feed remote actions in.
    /// The host owns the authoritative instance; the client only mirrors the resulting state.
    /// </summary>
    internal sealed class SpeedRules
    {
        public const float ProposalTimeoutSeconds = 20f;
        public const float ForceLockSeconds = 60f;
        public const int TokensPerPeriod = 3;

        /// <summary>Running speeds only (no 0). Campaign: 1,60,300,600,1800. Battle: 1,5.</summary>
        public float[] Steps { get; private set; } = { 1f };
        public int CurrentIndex { get; private set; }
        /// <summary>Phase default (D15). The game drops back to it after an interruption (battle/encounter), see C6.</summary>
        public int DefaultIndex { get; private set; }
        public bool Paused { get; private set; }
        public Proposal Pending { get; private set; }
        public float LockedUntil { get; private set; }
        public int LockedBy { get; private set; } = -1;
        public readonly int[] TokensLeft = { TokensPerPeriod, TokensPerPeriod };

        /// <summary>Human-readable event for toasts / log. Args: (message).</summary>
        public event Action<string> Notify;
        /// <summary>Raised whenever CurrentIndex or Paused changes (the driver applies it to the game).</summary>
        public event Action Changed;

        public float CurrentSpeed => Steps[CurrentIndex];

        public static string PlayerName(int p) => p == 0 ? "Player 1" : "Player 2";

        public void Reset(float[] steps, int defaultIndex, bool startPaused)
        {
            Steps = steps;
            CurrentIndex = Clamp(defaultIndex);
            DefaultIndex = CurrentIndex;
            Paused = startPaused;
            Pending = null;
            LockedUntil = 0f;
            LockedBy = -1;
            ResetTokens();
            Changed?.Invoke();
        }

        public void ResetTokens()
        {
            TokensLeft[0] = TokensPerPeriod;
            TokensLeft[1] = TokensPerPeriod;
        }

        public bool IsLocked(float now) => now < LockedUntil;

        public void Tick(float now)
        {
            if (Pending != null && now >= Pending.ExpiresAt)
            {
                Say($"{PlayerName(Pending.From)}'s proposal ({Describe(Pending)}) expired.");
                Pending = null;
            }
            if (LockedBy >= 0 && !IsLocked(now))
            {
                LockedBy = -1;
                Say("Forced speed lock ended.");
            }
        }

        // ---------- player actions ----------

        /// <summary>Propose the next faster step (or the fastest step when toMax).</summary>
        public void RequestFaster(int player, float now, bool toMax = false)
        {
            if (Paused) { Say("The game is paused. Propose Resume first."); return; }
            int target = toMax ? Steps.Length - 1 : CurrentIndex + 1;
            if (target >= Steps.Length || target <= CurrentIndex) { Say("Already at the fastest speed."); return; }
            Propose(player, ProposalKind.SpeedUp, target, now);
        }

        /// <summary>Slowing down is instant (D16), except during another player's forced-speed lock (D17).</summary>
        public void RequestSlower(int player, float now)
        {
            if (Paused) { Say("The game is paused."); return; }
            if (CurrentIndex == 0) { Say("Already at the slowest speed. Pausing needs both players to agree."); return; }
            if (IsLocked(now) && LockedBy != player)
            {
                Say($"Speed is locked by {PlayerName(LockedBy)}'s force for {Math.Ceiling(LockedUntil - now)} more s.");
                return;
            }
            if (Pending != null && Pending.Kind == ProposalKind.SpeedUp) Pending = null;
            SetIndex(CurrentIndex - 1);
            Say($"{PlayerName(player)} slowed the game to {Format(CurrentSpeed)}.");
        }

        public void RequestPauseToggle(int player, float now)
        {
            Propose(player, Paused ? ProposalKind.Resume : ProposalKind.Pause, CurrentIndex, now);
        }

        /// <summary>Accept or decline the other player's pending proposal.</summary>
        public void Respond(int player, bool accept, float now)
        {
            if (Pending == null) return;
            if (Pending.From == player) { Say("You can't answer your own proposal."); return; }
            Proposal p = Pending;
            Pending = null;
            if (!accept) { Say($"{PlayerName(player)} declined: {Describe(p)}."); return; }
            Apply(p, now, forced: false);
            Say($"{PlayerName(player)} accepted: {Describe(p)}.");
        }

        /// <summary>Force a speed-up without a vote: costs a token, locks the speed for 60 s.</summary>
        public void Force(int player, float now)
        {
            if (Paused) { Say("The game is paused. Forcing only applies to speed-ups."); return; }
            if (TokensLeft[player] <= 0) { Say($"{PlayerName(player)} has no force tokens left."); return; }
            int target = (Pending != null && Pending.From == player && Pending.Kind == ProposalKind.SpeedUp)
                ? Pending.TargetIndex : CurrentIndex + 1;
            if (target >= Steps.Length) { Say("Already at the fastest speed."); return; }
            TokensLeft[player]--;
            Pending = null;
            Apply(new Proposal { From = player, Kind = ProposalKind.SpeedUp, TargetIndex = target }, now, forced: true);
            Say($"{PlayerName(player)} FORCED {Format(CurrentSpeed)} (locked {ForceLockSeconds:0} s, {TokensLeft[player]} tokens left).");
        }

        /// <summary>After a battle or encounter: if running faster than the default, drop to the default (C6).</summary>
        public void SlowToDefaultAfterInterruption(string reason)
        {
            if (CurrentIndex > DefaultIndex) SystemSetIndex(DefaultIndex, reason);
        }

        /// <summary>Game-driven speed change (e.g. vanilla drop to 1x when aircraft attack). Overrides locks.</summary>
        public void SystemSetIndex(int index, string reason)
        {
            index = Clamp(index);
            if (index == CurrentIndex) return;
            LockedUntil = 0f;
            LockedBy = -1;
            if (Pending != null && Pending.Kind == ProposalKind.SpeedUp) Pending = null;
            SetIndex(index);
            Say($"{reason}: speed set to {Format(CurrentSpeed)}.");
        }

        // ---------- internals ----------

        private void Propose(int player, ProposalKind kind, int target, float now)
        {
            // The other player already proposed the same thing → treat as acceptance.
            if (Pending != null && Pending.From != player && Pending.Kind == kind &&
                (kind != ProposalKind.SpeedUp || Pending.TargetIndex == target))
            {
                Respond(player, true, now);
                return;
            }
            Pending = new Proposal { From = player, Kind = kind, TargetIndex = target, ExpiresAt = now + ProposalTimeoutSeconds };
            Say($"{PlayerName(player)} proposes: {Describe(Pending)}.");
        }

        private void Apply(Proposal p, float now, bool forced)
        {
            switch (p.Kind)
            {
                case ProposalKind.SpeedUp:
                    SetIndex(p.TargetIndex);
                    if (forced) { LockedUntil = now + ForceLockSeconds; LockedBy = p.From; }
                    break;
                case ProposalKind.Pause:
                    Paused = true;
                    Changed?.Invoke();
                    break;
                case ProposalKind.Resume:
                    Paused = false;
                    Changed?.Invoke();
                    break;
            }
        }

        private void SetIndex(int index)
        {
            CurrentIndex = Clamp(index);
            Changed?.Invoke();
        }

        private int Clamp(int i) => Math.Max(0, Math.Min(Steps.Length - 1, i));

        public string Describe(Proposal p)
        {
            switch (p.Kind)
            {
                case ProposalKind.SpeedUp: return "speed " + Format(Steps[Math.Min(p.TargetIndex, Steps.Length - 1)]);
                case ProposalKind.Pause: return "pause";
                default: return "resume";
            }
        }

        public static string Format(float speed) => speed.ToString("0.#") + "x";

        private void Say(string msg) => Notify?.Invoke(msg);

        // ---------- network state (host → client mirror) ----------
        // Times are sent as "seconds remaining" because the two PCs' clocks differ.

        public void WriteState(System.IO.BinaryWriter w, float now)
        {
            w.Write(Steps.Length);
            foreach (float s in Steps) w.Write(s);
            w.Write(CurrentIndex);
            w.Write(DefaultIndex);
            w.Write(Paused);
            w.Write(Pending != null);
            if (Pending != null)
            {
                w.Write(Pending.From);
                w.Write((byte)Pending.Kind);
                w.Write(Pending.TargetIndex);
                w.Write(Math.Max(0f, Pending.ExpiresAt - now));
            }
            w.Write(LockedBy);
            w.Write(Math.Max(0f, LockedUntil - now));
            w.Write(TokensLeft[0]);
            w.Write(TokensLeft[1]);
        }

        /// <summary>Overwrite this instance with the host's state (client display mirror; raises Changed).</summary>
        public void ReadState(System.IO.BinaryReader r, float now)
        {
            int n = r.ReadInt32();
            if (n < 1 || n > 64) throw new ArgumentException("bad speed step count " + n); // from the other PC: sanity check
            float[] steps = new float[n];
            for (int i = 0; i < n; i++) steps[i] = r.ReadSingle();
            Steps = steps;
            CurrentIndex = Clamp(r.ReadInt32());
            DefaultIndex = Clamp(r.ReadInt32());
            Paused = r.ReadBoolean();
            if (r.ReadBoolean())
            {
                Pending = new Proposal
                {
                    From = r.ReadInt32(), Kind = (ProposalKind)r.ReadByte(), TargetIndex = r.ReadInt32(),
                };
                Pending.ExpiresAt = now + r.ReadSingle();
            }
            else Pending = null;
            LockedBy = r.ReadInt32();
            LockedUntil = now + r.ReadSingle();
            TokensLeft[0] = r.ReadInt32();
            TokensLeft[1] = r.ReadInt32();
            Changed?.Invoke();
        }
    }
}
