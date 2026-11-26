using System;
using System.Collections.Generic;
using WotSMultiplayer.Features.TimeControl;

// Minimal test runner (no NuGet test framework needed). Exit code = number of failures.
internal static class Program
{
    private static int failures;
    private static readonly List<string> log = new List<string>();

    private static void Check(bool cond, string name)
    {
        Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name);
        if (!cond) { failures++; foreach (string l in log) Console.WriteLine("        | " + l); }
    }

    private static SpeedRules Campaign()
    {
        log.Clear();
        var r = new SpeedRules();
        r.Notify += m => log.Add(m);
        r.Reset(new[] { 1f, 60f, 300f, 600f, 1800f }, 1, startPaused: false);
        return r;
    }

    private static int Main()
    {
        // D15 default
        var r = Campaign();
        Check(r.CurrentSpeed == 60f && !r.Paused, "campaign default is 60x running");

        // D16 speed-up needs acceptance
        r = Campaign();
        r.RequestFaster(0, 0f);
        Check(r.CurrentSpeed == 60f && r.Pending != null && r.Pending.TargetIndex == 2, "faster creates proposal, no change yet");
        r.Respond(0, true, 1f);
        Check(r.CurrentSpeed == 60f && r.Pending != null, "proposer cannot accept own proposal");
        r.Respond(1, true, 1f);
        Check(r.CurrentSpeed == 300f && r.Pending == null, "other player accepts -> 300x");

        r = Campaign();
        r.RequestFaster(1, 0f);
        r.Respond(0, false, 1f);
        Check(r.CurrentSpeed == 60f && r.Pending == null, "decline keeps speed");

        r = Campaign();
        r.RequestFaster(0, 0f);
        r.Tick(SpeedRules.ProposalTimeoutSeconds + 0.1f);
        Check(r.Pending == null && r.CurrentSpeed == 60f, "proposal expires");

        r = Campaign();
        r.RequestFaster(0, 0f);
        r.RequestFaster(1, 1f);
        Check(r.CurrentSpeed == 300f && r.Pending == null, "matching counter-proposal counts as acceptance");

        r = Campaign();
        r.RequestFaster(0, 0f, toMax: true);
        r.Respond(1, true, 1f);
        Check(r.CurrentSpeed == 1800f, "toMax proposal goes to fastest step");

        // D16 slowing is instant
        r = Campaign();
        r.RequestSlower(1, 0f);
        Check(r.CurrentSpeed == 1f && r.Pending == null, "slower is instant (60 -> 1)");
        r.RequestSlower(1, 0f);
        Check(r.CurrentSpeed == 1f && !r.Paused, "slower at minimum does not pause");

        r = Campaign();
        r.RequestFaster(0, 0f);
        r.RequestSlower(1, 1f);
        Check(r.CurrentSpeed == 1f && r.Pending == null, "slowing cancels a pending speed-up");

        // D16 pause by agreement
        r = Campaign();
        r.RequestPauseToggle(0, 0f);
        Check(!r.Paused && r.Pending != null && r.Pending.Kind == ProposalKind.Pause, "pause is a proposal");
        r.Respond(1, true, 1f);
        Check(r.Paused, "accepted pause pauses");
        r.RequestFaster(0, 2f);
        Check(r.Pending == null, "no speed proposals while paused");
        r.RequestPauseToggle(1, 3f);
        Check(r.Pending != null && r.Pending.Kind == ProposalKind.Resume, "resume is a proposal");
        r.Respond(0, true, 4f);
        Check(!r.Paused && r.CurrentSpeed == 60f, "resume returns to previous speed");

        // D17 force tokens + 60 s lock
        r = Campaign();
        r.Force(0, 100f);
        Check(r.CurrentSpeed == 300f && r.TokensLeft[0] == 2 && r.IsLocked(100f), "force speeds up, costs token, locks");
        r.RequestSlower(1, 130f);
        Check(r.CurrentSpeed == 300f, "other player cannot slow during lock");
        r.RequestSlower(0, 131f);
        Check(r.CurrentSpeed == 60f, "forcer may undo their own force");

        r = Campaign();
        r.Force(0, 100f);
        r.Tick(100f + SpeedRules.ForceLockSeconds + 0.1f);
        r.RequestSlower(1, 100f + SpeedRules.ForceLockSeconds + 0.2f);
        Check(r.CurrentSpeed == 60f && !r.IsLocked(161f), "after 60 s the other player can slow down");

        r = Campaign();
        r.RequestFaster(1, 0f, toMax: true);
        r.Force(1, 1f);
        Check(r.CurrentSpeed == 1800f && r.Pending == null, "force uses own pending proposal target");

        r = Campaign();
        r.Force(0, 0f); r.Force(0, 1f); r.Force(0, 2f);
        Check(r.TokensLeft[0] == 0 && r.CurrentSpeed == 1800f, "three forces use all tokens");
        r.RequestSlower(0, 3f);
        r.Force(0, 4f);
        Check(r.CurrentSpeed == 600f && r.TokensLeft[0] == 0, "no fourth force");
        r.ResetTokens();
        Check(r.TokensLeft[0] == 3 && r.TokensLeft[1] == 3, "token reset (weekly / per battle)");

        r = Campaign();
        r.Force(0, 0f);
        r.SystemSetIndex(0, "Aircraft attacking");
        Check(r.CurrentSpeed == 1f && !r.IsLocked(1f), "system slow-down overrides force lock");

        // C6 drop to default after an interruption (battle / encounter)
        r = Campaign();
        r.Force(0, 0f); r.Force(0, 1f);
        r.SlowToDefaultAfterInterruption("Back on the campaign map");
        Check(r.CurrentSpeed == 60f && !r.IsLocked(2f), "after interruption: 1800x drops to default 60x, lock cleared");
        r.RequestSlower(0, 3f);
        r.SlowToDefaultAfterInterruption("Encounter resolved");
        Check(r.CurrentSpeed == 1f, "after interruption: slower-than-default speed is kept");

        // Network mirror: state written by the host is reproduced on the client (with relative timers)
        r = Campaign();
        r.Force(0, 100f);
        r.RequestFaster(1, 101f);
        var ms = new System.IO.MemoryStream();
        var bw = new System.IO.BinaryWriter(ms);
        r.WriteState(bw, 102f);
        bw.Flush(); ms.Position = 0;
        var mirror = new SpeedRules();
        mirror.ReadState(new System.IO.BinaryReader(ms), 5000f);
        Check(mirror.CurrentSpeed == r.CurrentSpeed && mirror.TokensLeft[0] == 2 && mirror.LockedBy == 0
              && Math.Abs((mirror.LockedUntil - 5000f) - (r.LockedUntil - 102f)) < 0.01f
              && mirror.Pending != null && mirror.Pending.From == 1 && mirror.Pending.TargetIndex == r.Pending.TargetIndex
              && Math.Abs((mirror.Pending.ExpiresAt - 5000f) - (r.Pending.ExpiresAt - 102f)) < 0.01f,
              "state mirror: speed, tokens, lock and pending proposal survive with relative timers");

        // Battle steps 1x/5x
        log.Clear();
        var b = new SpeedRules();
        b.Reset(new[] { 1f, 5f }, 0, startPaused: false);
        b.RequestFaster(0, 0f); b.Respond(1, true, 1f);
        Check(b.CurrentSpeed == 5f, "battle 1x -> 5x by vote");
        b.RequestFaster(1, 2f);
        Check(b.Pending == null && b.CurrentSpeed == 5f, "no step above 5x");

        // Side swap on real saves
        log.Clear();
        SwapTests.Run(Check);

        // Networking (loopback)
        log.Clear();
        NetTests.Run(Check);

        // Player 2's remote orders (validation helpers)
        log.Clear();
        OrderTests.Run(Check);

        // Two-player encounters (D19, D37, D39)
        log.Clear();
        EncounterTests.Run(Check);

        // Room codes (D46)
        log.Clear();
        RoomCodeTests.Run(Check);

        // Retreat rule (D48)
        log.Clear();
        RetreatTests.Run(Check);

        // Additional players: team locks (D57 stage 3)
        log.Clear();
        LockTests.Run(Check);

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }
}
