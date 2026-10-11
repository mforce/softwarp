namespace Factory;

// The state machine is ONE pure function: Plan(Observed) -> Decision. There is no stored cursor and no transition
// table. The caller cannot name a target, so it cannot ask for an illegal one. "Refusal" is Plan returning a wait or halt.

public abstract record Step;
public sealed record Launch(Station Station, int Attempt, string? Feedback) : Step;
public sealed record Await(ChildRef Child) : Step;
public sealed record Ingest(ChildRef Child) : Step;
/// <summary>DESIGN's bounce(station, reason). Appends a rejected ledger entry. The next Plan relaunches or escalates.
/// Idempotent on (Station, CauseKey): "comment:4413", "gate:Build:9b2e..." or "ingest:Spec:1".</summary>
public sealed record Bounce(Station Station, Cause Cause, string Feedback, string CauseKey) : Step;
public sealed record RunGate(GateKind Gate) : Step;
public sealed record PostSpec(Digest Digest) : Step;
public sealed record AwaitMaintainer(string Instruction) : Step;
public sealed record Push : Step;
public sealed record OpenPr(string Title, string Body) : Step;
public sealed record Halt(HaltReason Reason, string Why) : Step;
public sealed record Done(PrInfo Pr) : Step;

public readonly record struct Decision(Phase Phase, Step Step);

/// <summary>The child for the station's NEXT attempt: Attempt = ledger entries + 1. Plan consults it only when the
/// station has no current artifact. Once Ingest appends its entry the attempt number moves on, so Ingest cannot repeat.</summary>
public sealed record AgentSlot(int Attempt, ChildRef? Child, ChildState? State);

/// <summary>Every fact Plan may use, read in one place (Engine.Observe) and passed in by value. Plan does no I/O.
/// Each fact has one home: ledger, tracker, git or the pins file. Labels are not here because they are output.</summary>
public sealed record Observed(
    IssueSnapshot Issue, RunPins Pins, WorkflowSettings Workflow, IReadOnlySet<string> Maintainers,
    Ledger Ledger, IReadOnlyDictionary<Station, AgentSlot> Slots,
    WorkspaceState? Branch, IReadOnlyList<GateReceipt> Receipts, PrInfo? Pr);

public abstract record MaintainerSignal(Comment Source);
public sealed record Approve(Digest Digest, Comment Source) : MaintainerSignal(Source);
public sealed record Revise(string Note, Comment Source) : MaintainerSignal(Source);

public static class Signals
{
    /// <summary>The newest maintainer signal posted after the spec comment (Id greater than afterCommentId).
    /// A comment counts only if ALL hold: Kind == User; Login (case-insensitive) is in the machine's maintainer
    /// set, which comes from bindings.yaml and never from the repo; the first line is "/approve &lt;12 hex&gt;" or "/revise &lt;note&gt;".
    /// An /approve whose hex differs from the current digest is ignored. Forge-proof: GitHub sets the author, and the
    /// agents' App token cannot write as the maintainer. decisions.tsv can be written by any process
    /// running as this Unix user, so it never counts as an approval (DESIGN §12 deviation).</summary>
    public static MaintainerSignal? Latest(IssueSnapshot issue, IReadOnlySet<string> maintainers, Digest current, long afterCommentId)
        => throw new NotImplementedException();
}

public static class Machine
{
    public const int MaxMachineRejections = 2;

    public static Decision Plan(Observed o)
    {
        // TODO first match wins. Each rule reads only `o`.
        //
        // 0. Issue labels or ClaimedByRun belong to another run -> Halt(ClaimedElsewhere).
        // 1. TRIAGE   t = Ledger.Current<TriageReport>(); null -> Slot(o, Triage).
        //    t.Verdict != ReadyForAgent -> Halt(NeedsInfo | WontFix | ReadyForHuman, t.Reason).
        // 2. SPEC     s = Ledger.Current<SpecBundle>(); null -> Slot(o, Spec).
        //    d = SpecReview.DigestOf(s); posted = Issue.Comments.FirstOrDefault(c => c.Key == "spec:" + d.Short).
        //    posted is null -> (Approval, PostSpec(d)).
        //    sig = Signals.Latest(Issue, Maintainers, d, posted.Id):
        //      Revise -> Bounce(Spec, Maintainer, note, "comment:" + id);  null -> (Approval, AwaitMaintainer("/approve " + d.Short)).
        //    Reached on every later advance too, so a ledger edited after approval changes d and the run falls back here.
        // 3. IMPLEMENT i = Ledger.Current<ImplementReport>(); null -> Slot(o, Implement).
        //    Ledger.Attempts(Publish) == 0 -> Gates(o, owner: Implement), then Slot(o, Publish).
        //    (Once the publisher has run, the head is the cleaned head and a failure belongs to Publish, not Implement.)
        // 4. PUBLISH  p = Ledger.Current<PublishDraft>(); null -> Slot(o, Publish).
        //    Gates(o, owner: Publish): cleaning changed the code, so the receipts from step 3 are stale by SHA.
        //    Branch.RemoteHeadSha != Branch.HeadSha -> Push; Pr is null -> OpenPr(p.PrTitle, p.PrBody); else Done(Pr).
        //
        // Gates(o, owner): foreach gate in [Build, TestTargeted]: r = receipt for (gate, Branch.HeadSha);
        //   none -> RunGate(gate); r.ExitCode != 0 -> Bounce(owner, Gate, tail(r.LogPath), "gate:" + gate + ":" + head).
        //   Head SHAs in reports are checked once, at Ingest, against the live workspace.
        throw new NotImplementedException();
    }

    /// <summary>Shared by stations 1 to 4, so retry and escalation policy has one home.</summary>
    static Decision Slot(Observed o, Station station)
    {
        // TODO slot = o.Slots[station]; last = Ledger.Last(station).
        //   Ledger.MachineRejections(station) >= MaxMachineRejections -> Halt(Escalated, questionnaire from the problems).
        //   slot.Child is null -> Launch(station, slot.Attempt, feedback: last?.Problems), with a maintainer note first when present.
        //   slot.State: Running -> Await(child); Finished -> Ingest(child); Died -> Bounce(station, AgentDied, reason, "died:" + handle).
        throw new NotImplementedException();
    }
}

/// <summary>Label is the output of Plan, written after every advance. A hand-edited label self-heals on the next advance.</summary>
public static class Projection
{
    /// <summary>Triage..Publish -> Active; Approval -> NeedsHuman; Published -> Review; Halted -> by reason.
    /// Ready is only for the unattended claimer (build step 5). A manual run sits at Active.</summary>
    public static FactoryLabel Label(Decision d) => throw new NotImplementedException();
}
