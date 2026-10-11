namespace Factory;

/// <summary>What `status` prints and `progress.md` holds. Rendered from an Observation and the move it implies.</summary>
public sealed record Status(RunId Id, Station Station, Move Next, string Headline, string? Watch, IReadOnlyList<Decision> Recent)
{
    public string ToText() => throw new NotImplementedException();
    public string ToJson() => throw new NotImplementedException();
}

/// <summary>Result of one `run`/`advance` call. The exit code is the coordinator's branch point.</summary>
public sealed record Outcome(Status Status)
{
    public int ExitCode => Status.Next switch { Done => 0, Wait => 2, Blocked or Stopped or Escalate => 3, _ => 1 };
}

/// <summary>
/// The shell. Observe (read everything) -> Planner.Next (pure) -> Execute (one side effect) -> repeat, until the move is
/// Wait, Blocked, Stopped, Escalate or Done. Every executed move appends to decisions.tsv, and progress.md is rewritten
/// at the end of every call. Each Execute arm is idempotent against a re-observe, so re-running `advance` after a crash
/// repeats nothing that already happened.
/// </summary>
public sealed class Driver(MachineConfig config, ITracker tracker, IReadOnlyDictionary<string, ISpawner> spawners, IGit git,
    IGateRunner gates, TimeProvider clock, TextWriter console)
{
    /// <summary>`factory run`: refuse if the issue carries `factory:active` with a foreign run comment; else Claim and continue as Advance.</summary>
    public Task<Outcome> Run(RunId id, bool singleStep, TimeSpan? waitFor) => throw new NotImplementedException();

    /// <summary>
    /// `factory advance`. With `waitFor`, a Wait move polls Status every 30 s until the child finishes or the budget ends,
    /// then continues. `singleStep` executes one move and returns (trust rung 0).
    /// </summary>
    public Task<Outcome> Advance(RunId id, bool singleStep, TimeSpan? waitFor)
    {
        // TODO: loop { o = Observe; m = Planner.Next(o);
        //              if m is Blocked|Stopped|Escalate|Done, or (m is Wait && waitFor exhausted) -> break;
        //              if m is Wait -> sleep 30 s, continue;  Execute(o, m); if singleStep -> break; }
        //       status = Describe(o, m); runDir.WriteProgress(status.ToText()); console.WriteLine(status.ToText()); return new Outcome(status)
        throw new NotImplementedException();
    }

    /// <summary>`factory status`: Observe + Describe, no Execute.</summary>
    public Task<Status> Peek(RunId id) => throw new NotImplementedException();

    /// <summary>
    /// Read-only. Tracker (issue, comments), run dir (run.json, artifacts, tsv/jsonl logs), spawner status of the newest child,
    /// git inspect of the worktree, PR lookup by branch. Approval is matched against the digest of spec.json on disk.
    /// </summary>
    public Task<Observation> Observe(RunId id) => throw new NotImplementedException();

    /// <summary>One side effect per move. The switch has no default arm: a new Move record fails to compile until handled here.</summary>
    public Task Execute(RunId id, Observation o, Move move)
    {
        // TODO, per move (each guarded so a repeat after a crash is a no-op):
        //  Claim      workflow = Workflow.Parse(git.FileAtTrunk("WORKFLOW.md")), pin its commit; branch = `factory/<issue>-<kebab title>`;
        //             WriteRun; EnsureWorktree; SetLabel(Active); Comment(run id, worktree, watch) unless Issue.RunCommentId != null
        //  Spawn      b = config.Binding(station); req = (Briefs.Render(..., feedback), Artifacts.Schema(type), config.Models.For(b.Tier),
        //             cwd = worktree, b.ReadOnly, b.Expected, ChildLog); child = spawners[b.Spawner].Spawn(req); AppendChild;
        //             print what / log path / watch command / expected duration (dev-rules background visibility)
        //  Replace    spawners[..].Stop(stalled); then as Spawn (the stopped row stays, so Attempts counts it)
        //  Collect    raw = Result(child) ?? {}; WriteArtifact(station, raw)   (validity is judged on the next Observe)
        //  PostSpec   WriteSpecAndHoldouts; Comment(spec.md + "reply `approve <digest>` or `revise <digest>: <feedback>`")
        //  RunGates   for gate in [build, test_targeted(paths from `git diff --name-only base..head`)]: AppendGate(gates.Run(...)); stop at first red
        //  Bounce     Send(child, failing gate + tail of its log)   (Send adds the coordinator tag)
        //  Publish    git.Push(worktree); pr = OpenPullRequest(report.PrTitle, Briefs.PullRequestBody); SetLabel(Review)
        //  Stopped    SetLabel(ReadyForHuman -> NeedsHuman | NeedsInfo -> NeedsInfo | Wontfix -> Wontfix); Comment(reason)
        //  Escalate   SetLabel(NeedsHuman); Comment(Briefs.Questionnaire)
        //  Wait | Blocked | Done   nothing
        //  always     AppendDecision(clock.now, StationOf(move), move type name, detail)
        throw new NotImplementedException();
    }

    public static Status Describe(Observation o, Move next) => throw new NotImplementedException();

    /// <summary>Station implied by the next move, for labels and the status line. Derived, never stored.</summary>
    public static Station StationOf(Move next) => next switch
    {
        Claim => Station.Intake,
        Spawn s => s.Station,
        Wait w => w.Child.Station,
        Replace r => r.Stalled.Station,
        Collect c => c.Child.Station,
        PostSpec or Blocked => Station.Spec,
        RunGates or Bounce => Station.Implement,
        Publish or Done => Station.Publish,
        Stopped => Station.Triage,
        Escalate e => e.Station,
        _ => throw new ArgumentOutOfRangeException(nameof(next)),
    };
}
