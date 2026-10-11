namespace Softwarp;

/// <summary>Pure transition policy. The caller cannot select a station or certify a gate.</summary>
public static class Policy
{
    public static NextAction Next(Evidence evidence, Workflow workflow)
    {
        // TODO: Reject another claim, corrupt pins, unexpected branch, or inconsistent remote facts.
        // Triage absent -> Agent(Triage). Human/info/wontfix -> Stop with matching disposition.
        // Agent-ready requires reproduction on pinned trunk, redundancy and rejection searches.
        // Spec absent -> Agent(Spec). Approval absent -> AwaitApproval, even if local TSV says yes.
        // Implementation absent for approved package -> Agent(Implement).
        // Clean head must descend from pinned base and belong to the run branch.
        // Missing successful build/targeted-test receipts for this HEAD + commands -> Gates.
        // Failed gates -> implementation repair with logs; 2 failed revision cycles -> NeedsHuman.
        // Cleanup absent for this implementation -> Agent(Cleanup).
        // Cleanup may add commits; require gates on its final clean HEAD before Publish.
        // Matching existing PR -> Complete; unexpected PR head -> Stop, never overwrite it.
        // One invalid-artifact retry per action revision; second invalid output -> NeedsHuman.
        // Bounce invalidates target and downstream receipts; after 2 revision cycles -> NeedsHuman.
        // An interrupted operation reconciles its external id before either completing or retrying.
        throw new NotImplementedException();
    }

    public static ApprovedSpec VerifyApproval(RunManifest run, SpecPackage package, ApprovalEvidence evidence)
    {
        // TODO: Match immutable configured account id, issue/repository, exact run+digest text.
        // Re-fetch edits/deletions before implementation and publication; reject unknown authors.
        // Local text, supplied URLs and cached evidence grant no authority by themselves.
        throw new NotImplementedException();
    }

    /// <summary>Pure validation in addition to generated schema checks at each agent boundary.</summary>
    public static void Validate(SpecArtifact spec)
    {
        // TODO: Require unique criteria/ticket ids, acyclic edges referencing existing tickets,
        // nonempty acceptance and holdouts referring to criteria; reject empty required strings.
        // Ticket splitting stays local; multiple independently publishable items need human scope.
        throw new NotImplementedException();
    }
}

/// <summary>Owns durable effects and recovery; dependencies can be replaced by in-memory fakes.</summary>
public sealed class Factory
{
    public Factory(IRunStore runs, ITracker tracker, IIsolation isolation, ISpawn spawn, IGates gates)
        => throw new NotImplementedException();

    public Task<RunId> StartAsync(IntakeRequest request, CancellationToken ct)
    {
        // TODO: Under item lock, snapshot issue/trunk/workflow and resolve input versions once.
        // Persist manifest before claim; reconcile existing matching claim on retry.
        // Existing foreign claim or configuration without required commands fails closed.
        throw new NotImplementedException();
    }

    public Task<AdvanceResult> AdvanceAsync(RunId run, CancellationToken ct)
    {
        // TODO: Lock run; load local receipts; refresh claim, approval, branch, PR observations.
        // Hydrate eligible evidence by content prerequisites; Policy.Next chooses one action.
        // Persist intent; ensure or reconcile effect; atomically import receipt; refresh labels/views.
        // Print role, log path, watch command and expected duration before waiting for a worker.
        // Validate schema and domain content; only accepted output becomes a receipt.
        // Recheck branch/head and approval immediately before push; create/find PR by branch/run.
        // Successful push without PR resumes PR creation, never a second implementation.
        // Gate failures record command exits, return 3, and consume bounded implementation revisions.
        // Sanitized errors and a short questionnaire accompany needs-human transitions.
        throw new NotImplementedException();
    }

    public Task<AdvanceResult> StatusAsync(RunId run, CancellationToken ct) => throw new NotImplementedException();
    public Task<SpecView> SpecAsync(RunId run, CancellationToken ct) => throw new NotImplementedException();
    public Task<ApprovalCheck> RecordApprovalAsync(RunId run, Uri receipt, CancellationToken ct)
    {
        // TODO: Fetch through tracker, verify with policy, save URL, render decisions mirror.
        // Never post as maintainer or accept a locally asserted author.
        throw new NotImplementedException();
    }
}
