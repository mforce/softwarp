namespace Softwarp.Core;

/// <summary>The rule that chose an action, numbered in the order Explain applies them.</summary>
public enum Rule
{
    InconsistentEvidence = 1,
    UnknownOutcome, WorkerRunning, Triage, TriageStops, Spec, Approval, HeadIntact,
    Implement, RedGate, Gates, Cleanup, Publish, Complete,
}

/// <summary>Pure transition policy.</summary>
public static class Policy
{
    public const int InvalidArtifactRetries = 1;
    public const int RevisionCycles = 2;
    /// <summary>Exit-75 re-runs per gate kind on one head and package in one epoch, until that kind passes.</summary>
    public const int UnavailableGateRetries = 3;

    public static NextAction Next(Evidence evidence) => Explain(evidence).Action;

    /// <summary>The first rule that applies wins. The compiler enforces the order only where a later rule reads a local
    /// that an earlier rule narrowed; tests hold the rest, for example HeadIntact before Implement. Budgets count only the
    /// current epoch; a maintainer `/softwarp resume` starts a new one.</summary>
    public static (Rule Rule, NextAction Action) Explain(Evidence e)
    {
        var run = e.Manifest;
        if (e.Claim != run.Run) return (Rule.InconsistentEvidence, Stop("the item is not claimed by this run"));
        if (e.Pr is not null && (e.Spec is not { } current
                || e.Implementation?.Spec != current.Digest || e.Cleanup?.Spec != current.Digest))
            return (Rule.InconsistentEvidence, Stop("a PR exists without the implementation and cleanup for the current spec package"));
        if (e.Unresolved is { } op) return (Rule.UnknownOutcome, new NextAction.Reconcile(op));
        if (e.Worker is { } worker) return (Rule.WorkerRunning, new NextAction.AwaitAgent(worker));

        var epoch = Epoch(e);
        var thisEpoch = e.Attempts.Where(a => a.Epoch == epoch).ToList();
        int Revisions(ActionKind kind) =>
            thisEpoch.Count(a => a.Action == kind && a.Revision && a.Outcome == AttemptOutcome.Completed);
        NextAction Dispatch(ActionKind kind, NextAction action)
        {
            var invalid = thisEpoch.Count(a => a.Action == kind && a.Outcome == AttemptOutcome.InvalidArtifact);
            return invalid > InvalidArtifactRetries ? Stop($"{kind} returned {invalid} invalid artifacts this epoch") : action;
        }

        if (e.Triage is not { } triage
            || (triage.Epoch != epoch && triage.Decision is TriageDecision.NeedsInfo or TriageDecision.Human))
            return (Rule.Triage, Dispatch(ActionKind.Triage, new NextAction.Triage()));
        if (triage.Decision != TriageDecision.Agent)
            return (Rule.TriageStops, triage.Decision switch
            {
                TriageDecision.NeedsInfo => new NextAction.Stop(Disposition.NeedsInfo, "triage needs information from the reporter"),
                TriageDecision.WontFix => new NextAction.Stop(Disposition.WontFix, "triage declined the item"),
                _ => Stop("triage asks for a human"),
            });

        if (e.Spec is not { } package) return (Rule.Spec, Dispatch(ActionKind.Spec, new NextAction.Specify(triage, null)));
        var decision = e.SpecPostedAt is { } posted ? Decision(run, package, e.Maintainer, posted) : null;
        if (decision is null)
            return (Rule.Approval, new NextAction.AwaitMaintainer(package));
        if (decision.Verb == MaintainerVerb.Revise)
            return (Rule.Approval, Revisions(ActionKind.Spec) >= RevisionCycles
                ? Stop($"the spec was revised {RevisionCycles} times this epoch")
                : Dispatch(ActionKind.Spec, new NextAction.Specify(triage, new SpecRevision(package, decision))));
        var approved = new ApprovedSpec(package, decision);

        if (HeadProblem(e) is { } problem) return (Rule.HeadIntact, Stop(problem));
        if (e.Implementation?.Spec != package.Digest || e.Head is not { } observed)
            return (Rule.Implement, Dispatch(ActionKind.Implement, new NextAction.Implement(approved, null)));
        var head = observed.Head;

        var touchingHead = e.Gates.Where(r => r.Spec == package.Digest && (r.Head == head || r.HeadAfter == head)).ToList();
        foreach (var r in touchingHead.Where(r => r.Epoch == epoch))
            if (Distrust(r, run.Workflow) is { } cause) return (Rule.RedGate, Stop(cause));
        var counted = touchingHead.Where(r => Distrust(r, run.Workflow) is null).ToList();
        if (counted.Where(r => r.Verdict == GateVerdict.Fail).MinBy(r => r.Kind) is { } failed)
            return (Rule.RedGate, Revisions(ActionKind.Implement) >= RevisionCycles
                ? Stop($"a gate still fails after {RevisionCycles} repairs this epoch")
                : Dispatch(ActionKind.Implement, new NextAction.Implement(approved, failed)));
        var unpassed = Enum.GetValues<GateKind>()
            .Where(kind => !counted.Any(r => r.Kind == kind && r.Verdict == GateVerdict.Pass)).ToList();
        foreach (var kind in unpassed)
        {
            var unavailable = counted.Count(r => r.Kind == kind && r.Epoch == epoch && r.Verdict == GateVerdict.Unavailable);
            if (unavailable > UnavailableGateRetries)
                return (Rule.RedGate, Stop($"the {kind} gate could not run (exit 75) {unavailable} times on this head this epoch"));
        }
        if (unpassed.Count > 0) return (Rule.Gates, new NextAction.Gates(approved, head));

        if (e.Cleanup is not { } cleanup || cleanup.Head != head || cleanup.Spec != package.Digest)
        {
            // Each approved package and each repair needs its own cleanup: an epoch has at most 1 + RevisionCycles
            // packages and RevisionCycles repairs.
            var cleanups = thisEpoch.Count(a => a.Action == ActionKind.Cleanup && a.Outcome == AttemptOutcome.Completed);
            return (Rule.Cleanup, cleanups > 2 * RevisionCycles
                ? Stop($"cleanup ran {cleanups} times this epoch and the head still needs one")
                : Dispatch(ActionKind.Cleanup, new NextAction.Cleanup(approved, head)));
        }

        if (e.Pr is not { } pr)
            return (Rule.Publish, e.RejectedPushes.Any(p => p.Head == head && p.Epoch == epoch)
                ? Stop($"the remote rejected the push of {head.Value} to {run.Branch}; its branch may hold commits the head lacks")
                : new NextAction.Publish(approved, head, cleanup));
        if (pr.Branch != run.Branch) return (Rule.Publish, Stop($"the PR is on {pr.Branch}, not the run branch {run.Branch}"));
        if (pr.Spec != package.Digest) return (Rule.Publish, Stop("the PR was published for another spec package"));
        if (pr.Head != head) return (Rule.Publish, Stop("the PR head differs from the run head"));
        return (Rule.Complete, new NextAction.Complete(pr));
    }

    /// <summary>The number of maintainer `/softwarp resume` comments: attempts and gate runs record the epoch they began
    /// in.</summary>
    public static int Epoch(Evidence e) =>
        e.Maintainer.Count(c => c.Verb == MaintainerVerb.Resume && IsMaintainer(e.Manifest, c));

    /// <summary>
    /// The newest approve or revise for the package. Accepts only: a non-bot author whose repository role (fetched
    /// from the forge) is admin or owner, matching the pinned maintainer account id; the comment not edited by anyone
    /// else; posted after the spec comment; quoting the current package digest. Nothing local (config, decisions.tsv,
    /// a supplied URL) grants authority on its own, so an agent that edits local files cannot approve.
    /// </summary>
    static MaintainerComment? Decision(RunManifest run, SpecPackage package,
        IEnumerable<MaintainerComment> comments, DateTimeOffset specPostedAt) =>
        comments.Where(c => c.Verb is MaintainerVerb.Approve or MaintainerVerb.Revise
                && c.SpecDigest == package.Digest && c.PostedAt > specPostedAt && IsMaintainer(run, c))
            .MaxBy(c => (c.PostedAt, ReviseWinsATie: c.Verb == MaintainerVerb.Revise));

    static bool IsMaintainer(RunManifest run, MaintainerComment c) =>
        !c.AuthorIsBot && c.AuthorIsRepoAdmin && !c.EditedByOther && c.AuthorAccountId == run.MaintainerAccountId;

    static string? HeadProblem(Evidence e) => e.Head switch
    {
        null => e.Implementation is null ? null : "the run has an implementation but no worktree head",
        { Dirt: { } dirt } => $"the worktree has uncommitted changes: {dirt}",
        { DescendsFromTrunk: false } => "the head does not descend from the pinned trunk",
        var head when head.Branch != e.Manifest.Branch => $"the worktree is on {head.Branch}, not {e.Manifest.Branch}",
        _ => null,
    };

    static string? Distrust(GateReceipt r, Workflow workflow) => r switch
    {
        _ when r.HeadAfter != r.Head => $"the {r.Kind} gate moved the head from {r.Head.Value} to {r.HeadAfter.Value} while it ran",
        { TreeClean: false } => $"the {r.Kind} gate left the tree dirty",
        _ when r.Command != workflow.Command(r.Kind).Digest => $"the {r.Kind} gate ran a command other than the pinned workflow's",
        _ => null,
    };

    static NextAction.Stop Stop(string reason) => new(Disposition.NeedsHuman, reason);
}
