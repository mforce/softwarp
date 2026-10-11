namespace Softwarp;

/// <summary>Pure transition policy. The caller cannot select a station or certify a gate.</summary>
public static class Policy
{
    public const int InvalidArtifactRetries = 1;
    public const int RevisionCycles = 2;

    /// <summary>
    /// First row that holds wins. Each row is one test in the planner table (no fakes needed).
    /// Budgets count attempts within the current Epoch only; a maintainer `/softwarp resume` starts a new epoch.
    /// </summary>
    public static NextAction Next(Evidence evidence, Workflow workflow)
    {
        // TODO:
        //  1  Foreign claim, corrupt pins, unexpected branch, inconsistent remote facts       -> Stop(NeedsHuman)
        //  2  An attempt's outcome is unknown (intent without receipt)                       -> reconcile by operation id first
        //  3  A worker is running                                                            -> AwaitAgent(handle)
        //  4  Triage absent or invalid  -> Agent(Triage); second invalid in epoch           -> Stop(NeedsHuman)
        //  5  Triage decision Human / NeedsInfo / WontFix                                   -> Stop(matching disposition)
        //     Agent-ready requires reproduction on pinned trunk plus redundancy and prior-rejection searches.
        //  6  Spec absent or invalid    -> Agent(Spec);   second invalid in epoch           -> Stop(NeedsHuman)
        //  7  Newest valid maintainer comment for the CURRENT package digest:
        //       none        -> AwaitMaintainer(package, "/softwarp approve <digest>")   (local decisions.tsv never counts)
        //       Revise      -> Agent(Spec, feedback); after RevisionCycles revisions     -> Stop(NeedsHuman)
        //       Approve     -> continue. A changed package voids the approval and every downstream receipt.
        //  8  Implementation absent for the approved package -> Agent(Implement, ImplementationInput without holdouts)
        //  9  Head must be clean, descend from the pinned base and sit on the run branch; else Stop(NeedsHuman).
        // 10  A FAILED gate receipt exists for this exact head and command                  -> repair:
        //       Agent(Implement, RepairLog = gate log); after RevisionCycles repairs         -> Stop(NeedsHuman)
        //     (Checked BEFORE row 11, so a red gate is never just re-run on the same head.)
        // 11  Missing a passing Build or TargetedTests receipt for this head and command    -> Gates(head)
        // 12  Cleanup absent for this implementation                                        -> Agent(Cleanup)
        //     Cleanup may add commits; rows 9–11 then apply again to its head.
        // 13  No PR for the run branch                                                      -> Publish(approved, head, cleanup)
        //     A PR whose head differs from ours                                             -> Stop(NeedsHuman), never overwrite
        // 14                                                                                -> Complete(pr)
        throw new NotImplementedException();
    }

    /// <summary>
    /// Accepts only: a non-bot author whose repository role (fetched from the forge) is admin or owner, matching the
    /// pinned maintainer account id; the comment not edited by anyone else; posted after the spec comment; quoting the
    /// current package digest. Nothing local (config, decisions.tsv, a supplied URL) grants authority on its own,
    /// so an agent that edits local files cannot approve.
    /// </summary>
    public static ApprovedSpec? FindApproval(RunManifest run, SpecPackage package,
        IReadOnlyList<MaintainerComment> comments, DateTimeOffset specPostedAt)
        => throw new NotImplementedException();

    /// <summary>Pure validation in addition to generated schema checks at each agent boundary.</summary>
    public static void Validate(SpecArtifact spec)
    {
        // TODO: Require unique criteria/ticket ids, acyclic edges referencing existing tickets,
        // nonempty acceptance, holdouts referring to criteria, at least one test target; reject empty strings.
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
        // TODO: Under item lock, snapshot issue, trunk and WORKFLOW.md AT THE TRUNK COMMIT (never a worktree copy),
        // and resolve input versions once. Persist the manifest before claiming; reconcile a matching claim on retry.
        // A foreign claim or a workflow without required commands fails closed.
        throw new NotImplementedException();
    }

    public Task<AdvanceResult> AdvanceAsync(RunId run, TimeSpan? wait, CancellationToken ct)
    {
        // TODO: Lock the run; load local receipts; refresh claim, maintainer comments, branch, PR.
        // Hydrate eligible evidence by content prerequisites; Policy.Next chooses one action.
        // Persist intent; ensure or reconcile the effect; atomically import the receipt; refresh labels and views.
        // Print role, log path, watch command and expected duration when a worker starts.
        // AwaitAgent: wait until min(wait, worker deadline); past the deadline with no side effect -> stop and replace.
        // Gates: run each configured command at the frozen head; record HeadAfter, TreeClean and TestsExecuted.
        // Recheck branch, head and approval immediately before push; create or find the PR by branch and run.
        // A push without a PR resumes PR creation, never a second implementation.
        // NeedsHuman transitions post a short questionnaire comment.
        throw new NotImplementedException();
    }

    public Task<AdvanceResult> StatusAsync(RunId run, CancellationToken ct) => throw new NotImplementedException();
    public Task<SpecView> SpecAsync(RunId run, CancellationToken ct) => throw new NotImplementedException();
}
