namespace Factory;

public enum Outcome { Progressed, WaitingOnAgent, WaitingOnMaintainer, Halted, Finished, Stale }

/// <summary>What the coordinator parses with --json. Exit codes: Progressed/Finished 0, WaitingOnAgent 10,
/// WaitingOnMaintainer 11, Halted 12, Stale 13. The coordinator never has to read prose.</summary>
public sealed record AdvanceResult(Outcome Outcome, Phase Phase, string Message, string Next, IReadOnlyList<ChildRef> Watch)
{
    public int ExitCode => throw new NotImplementedException();
}

/// <summary>Run dir ~/.factory/runs/&lt;repo&gt;/&lt;n&gt;/: pins.json, ledger/NNN-station-attempt.json, children.tsv, gates/,
/// holdout/, agents/ (raw outputs), progress.md, decisions.tsv (a log, never an authority). Every write is
/// temp-file then rename. Ledger and receipts are write-once.</summary>
public sealed class RunStore(string factoryHome)
{
    public string Home { get; } = factoryHome;
    public string Dir(IssueRef issue) => throw new NotImplementedException();
    public RunPins? ReadPins(IssueRef issue) => throw new NotImplementedException();
    public void WritePins(IssueRef issue, RunPins pins) => throw new NotImplementedException();
    public Ledger ReadLedger(IssueRef issue) => throw new NotImplementedException();
    /// <summary>No-op when an entry with the same (Station, CauseKey) exists. This is the crash-safety of Ingest and Bounce.</summary>
    public void Append(IssueRef issue, LedgerEntry entry) => throw new NotImplementedException();
    public ChildRef? ReadChild(AgentKey key) => throw new NotImplementedException();
    public void WriteChild(ChildRef child) => throw new NotImplementedException();
    public IReadOnlyList<GateReceipt> ReadReceipts(IssueRef issue) => throw new NotImplementedException();
    public void WriteReceipt(IssueRef issue, GateReceipt receipt) => throw new NotImplementedException();
    public void WriteHoldouts(IssueRef issue, IReadOnlyList<Holdout> holdouts) => throw new NotImplementedException();
}

/// <summary>The whole public surface of the orchestrator. Capabilities, store and Machine are hidden behind four methods.
/// Observe and Execute are private. The only sequence that exists is Observe -> Machine.Plan -> Execute -> SetLabel.</summary>
public sealed class Engine(Caps caps, RunStore store, MachineConfig config)
{
    public Caps Caps { get; } = caps;
    public RunStore Store { get; } = store;
    public MachineConfig Config { get; } = config;

    /// <summary>Station 0. Claim, pin (WORKFLOW commit from origin/main, factory version), label Active, make the run dir.
    /// Repeating it is harmless: AlreadyMine does nothing. LostToOther returns Halted(ClaimedElsewhere).</summary>
    public Task<AdvanceResult> Start(IssueRef issue, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Observe, Plan, Execute one Step, then reconcile the label. With expect set and a different Phase, return Stale
    /// and do nothing. With follow &gt; 0, keep going while Progressed, polling agents until the budget ends.</summary>
    public Task<AdvanceResult> Advance(IssueRef issue, Phase? expect, TimeSpan follow, CancellationToken ct)
        => throw new NotImplementedException();

    /// <summary>Observe and Plan with no effect. Same result type, so a script cannot tell status and advance apart.</summary>
    public Task<AdvanceResult> Status(IssueRef issue, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Appends a Halt marker, labels NeedsHuman, removes the worktrees. Does not kill running agents (build step 5 does).</summary>
    public Task<AdvanceResult> Stop(IssueRef issue, string reason, CancellationToken ct) => throw new NotImplementedException();

    Task<Observed> Observe(IssueRef issue, CancellationToken ct)
    {
        // TODO the only read boundary. tracker.Read; store.ReadPins; Workflow.Parse(work.ReadAtCommit(issue, pins.WorkflowCommit, "WORKFLOW.md"));
        // ledger; per station Slots[s] = (attempt = ledger.Attempts(s) + 1, child = store.ReadChild ?? spawn.Find, state = Poll(child));
        // work.Inspect(Branch); store.ReadReceipts; tracker.FindPr only once the branch has been pushed.
        throw new NotImplementedException();
    }

    Task<AdvanceResult> Execute(Observed o, Decision d, CancellationToken ct)
    {
        // TODO one switch over d.Step. Every arm is safe to run twice:
        //  Launch   Ensure workspace (Triage/Spec -> Trunk, others -> Branch); Briefs.Render; AgentCall with Artifacts.SchemaFor<T>();
        //           spawn.Start; store.WriteChild BEFORE returning. A crash in between is healed by spawn.Find on the next Observe.
        //  Ingest   Artifacts.Parse<T>; then checks that need the world: ImplementReport/PublishDraft.HeadSha == Branch.HeadSha,
        //           TriageReport.Repro.Command re-run on Trunk and the exit code compared. Spec: write holdouts to holdout/.
        //           store.Append(accepted or Rejected(Schema)), keyed "ingest:<station>:<attempt>".
        //  Bounce   store.Append(rejected entry). The Maintainer note and gate-log tail go to the next brief as Feedback.
        //  RunGate  gates.Run with the command from o.Workflow; TestTargeted fills {paths} from the approved Tickets.TestTargets.
        //           store.WriteReceipt (pass or fail). The failure shows up as a Bounce on the next Plan.
        //  PostSpec tracker.Upsert(key "spec:" + digest.Short, SpecReview.Render(bundle) + approve instructions).
        //  Push     work.Push. OpenPr: tracker.FindPr first, then OpenPr. SetLabel(Projection.Label(d)) at the end of every Execute.
        throw new NotImplementedException();
    }
}
