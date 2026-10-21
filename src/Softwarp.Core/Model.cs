using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Softwarp.Core;

public readonly record struct RunId(string Value);
public readonly record struct Digest(string Value);
public readonly record struct CommitId(string Value);
public readonly record struct OperationId(string Value);
public sealed record ArtifactRef(Digest Hash, string RelativePath);

public enum Disposition { NeedsHuman, NeedsInfo, WontFix }
public enum TriageDecision { Agent, Human, NeedsInfo, WontFix }
public enum GateKind { Build, TargetedTests }
public enum ActionKind { Triage, Spec, Implement, Cleanup }
public enum AttemptOutcome { Completed, InvalidArtifact }
public enum MaintainerVerb { Approve, Revise, Resume }

/// <summary>An issue on the forge. Owner and Repo are single path segments of letters, digits and `._-`.</summary>
public sealed record ItemId(string Owner, string Repo, long Number);
/// <summary>The issue as the tracker showed it at intake. Revision is the forge's version marker for that text.</summary>
public sealed record WorkItem(ItemId Id, string Title, string Body, string Revision, Uri Url);
/// <summary>The model id each agent role runs on, pinned for the whole run.</summary>
public sealed record ModelPins(string Triage, string Spec, string Implement, string Cleanup)
{
    public string For(ActionKind role) => role switch
    {
        ActionKind.Triage => Triage,
        ActionKind.Spec => Spec,
        ActionKind.Implement => Implement,
        ActionKind.Cleanup => Cleanup,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

/// <summary>Written once at intake and never rewritten. WorkflowFile is the digest of WORKFLOW.md's bytes at Trunk.</summary>
public sealed record RunManifest(RunId Run, CommitId Trunk, string Branch, string MaintainerAccountId, Workflow Workflow,
    WorkItem Item, Digest WorkflowFile, ModelPins Models);

public sealed record CommandSpec(string Executable, ImmutableArray<string> Arguments)
{
    /// <summary>Each target replaces a `{targets}` element as its own argv element.</summary>
    public CommandSpec Expand(ImmutableArray<string> targets) =>
        new(Executable, [.. Arguments.SelectMany(arg => arg == "{targets}" ? targets.AsEnumerable() : [arg])]);

    // Argv elements cannot contain NUL, so joining on it is unambiguous.
    public Digest Digest => new(Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', Arguments.Prepend(Executable))))));
}

/// <summary>The gate commands from WORKFLOW.md at the pinned trunk commit. Commands run as argv, never through a
/// shell: each approved test target replaces `{targets}` as its own argv element, and the gate runner must refuse a
/// template that puts `{targets}` inside a shell `-c` script.</summary>
public sealed record Workflow(CommandSpec Build, CommandSpec TargetedTests)
{
    public CommandSpec Command(GateKind kind) => kind switch
    {
        GateKind.Build => Build,
        GateKind.TargetedTests => TargetedTests,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>Observed by git in the run's worktree, never reported by an agent.</summary>
public sealed record HeadObservation(string Branch, CommitId Head, bool Clean, bool DescendsFromTrunk);

public sealed record Reproduction(CommitId Trunk, string Command, string Evidence);
/// <summary>What the triage agent returns. The factory stamps it into a <see cref="TriageArtifact"/> at import.</summary>
public sealed record TriageOutput(TriageDecision Decision, Reproduction Reproduction, string PriorRejectionSearch,
    string RedundancySearch)
{
    public TriageArtifact Stamp(int epoch) => new(Decision, Reproduction, PriorRejectionSearch, RedundancySearch, epoch);
}
/// <summary>Epoch is stamped by the factory, never taken from agent output: the epoch the triage ran in.</summary>
public sealed record TriageArtifact(TriageDecision Decision, Reproduction Reproduction,
    string PriorRejectionSearch, string RedundancySearch, int Epoch);
public sealed record Criterion(string Id, string Text);
public sealed record Ticket(string Id, string Text, ImmutableArray<string> BlockedBy);
public sealed record Holdout(string CriterionId, string Scenario, string Expected);
/// <summary>TestTargets is the approved test filter the targeted-test gate runs.</summary>
public sealed record SpecArtifact(string Markdown, ImmutableArray<Criterion> Criteria,
    ImmutableArray<Ticket> Tickets, ImmutableArray<Holdout> Holdouts, ImmutableArray<string> TestTargets);
/// <summary>What the implementer returns. Its work is the commits git shows; the factory reads nothing else from it.</summary>
public sealed record ImplementationOutput;
/// <summary>Spec is the package digest the factory dispatched this work for, stamped by the factory and never taken
/// from agent output.</summary>
public sealed record ImplementationArtifact(Digest Spec);
/// <summary>What the cleanup agent returns: the PR text the factory publishes.</summary>
public sealed record CleanupOutput(string PrTitle, string PrBody);
/// <summary>Spec and Head are stamped by the factory, never taken from agent output: the package it dispatched the
/// cleanup for, and the head git shows when the agent returns.</summary>
public sealed record CleanupArtifact(Digest Spec, CommitId Head, string PrTitle, string PrBody);

/// <summary>The part of an approved spec that anyone may read: everything except the holdouts.</summary>
public sealed record PublicSpec(string Markdown, ImmutableArray<Criterion> Criteria, ImmutableArray<string> TestTargets);

/// <summary>Digest covers canonical spec, ticket graph, holdouts and all run input pins.</summary>
public sealed record SpecPackage(Digest Digest, ArtifactRef PublicSpec, ArtifactRef Tickets,
    ArtifactRef PrivateHoldouts);

/// <summary>Exit 0 passes; 75 (EX_TEMPFAIL) means the gate could not check; any other code fails.</summary>
public enum GateVerdict { Pass, Fail, Unavailable }

/// <summary>Written by the factory's gate runner. Gates run on a frozen head. A receipt for the approved package that
/// started or ended on the current head is trusted only if HeadAfter equals Head, the tree stayed clean and Command
/// matches the pinned workflow; an untrusted one stops the run until a maintainer resume. Epoch is the epoch the gate
/// run began in.</summary>
/// <param name="Command">The pinned workflow's <see cref="CommandSpec.Digest"/> for Kind, taken over the template with
/// `{targets}` unfilled. Spec binds the filled targets.</param>
public sealed record GateReceipt(GateKind Kind, Digest Spec, CommitId Head, CommitId HeadAfter, bool TreeClean,
    Digest Command, int ExitCode, int? TestsExecuted, ArtifactRef Log, int Epoch)
{
    /// <summary>The exit code the factory records when it refuses the gate's log (over its cap, a link, or not openable),
    /// or when a gate run cut off by a crash had moved the head or dirtied the tree: no process exits with it, and the
    /// gate fails.</summary>
    public const int RefusedLog = -1;

    /// <summary>A targeted-test run that executed no test fails.</summary>
    public GateVerdict Verdict => ExitCode switch
    {
        0 when Kind != GateKind.TargetedTests || TestsExecuted > 0 => GateVerdict.Pass,
        75 => GateVerdict.Unavailable,
        _ => GateVerdict.Fail,
    };
}

public sealed record Publication(Uri Url, string Branch, CommitId Head, Digest Spec);

/// <summary>A maintainer command found on the item (`/softwarp approve|revise|resume`). Author facts come from the forge,
/// never from local config.</summary>
public sealed record MaintainerComment(Uri Url, MaintainerVerb Verb, Digest? SpecDigest, string? Feedback,
    string AuthorAccountId, bool AuthorIsBot, bool AuthorIsRepoAdmin, bool EditedByOther, DateTimeOffset PostedAt);

/// <summary>The posted package and the maintainer's revise comment on it.</summary>
public sealed record SpecRevision(SpecPackage Package, MaintainerComment Comment);

/// <summary>Proof that the maintainer approved this exact package on the forge.</summary>
public sealed record ApprovedSpec
{
    internal ApprovedSpec(SpecPackage package, MaintainerComment approval) => (Package, Approval) = (package, approval);
    public SpecPackage Package { get; }
    public MaintainerComment Approval { get; }
}

/// <summary>Epoch is the number of maintainer resume comments when the attempt began. Revision marks an attempt that
/// answered a revise comment or a red gate.</summary>
public sealed record Attempt(OperationId Id, ActionKind Action, int Epoch, AttemptOutcome Outcome, bool Revision);

public sealed record AgentHandle(string Id, string LogFile, string WatchCommand);

/// <summary>Triage input: the item as intake pinned it, and the trunk to reproduce on.</summary>
public sealed record TriageInput(WorkItem Item, CommitId Trunk);
/// <summary>Spec input. Revise is set when this rewrites a posted spec.</summary>
public sealed record SpecInput(WorkItem Item, TriageArtifact Triage, SpecToRevise? Revise);
/// <summary>The posted spec, holdouts included, and the maintainer's feedback on it. Only the spec role sees it.</summary>
public sealed record SpecToRevise(SpecArtifact Spec, string? Feedback);
/// <summary>The failed gate an implementer must fix, with the end of its log.</summary>
public sealed record GateFailure(GateKind Kind, int ExitCode, string LogTail);
/// <summary>Everything the implementer receives. Holdouts are absent by type.</summary>
public sealed record ImplementationInput(PublicSpec Spec, ImmutableArray<Ticket> Tickets, GateFailure? Repair);
public sealed record CleanupInput(PublicSpec Spec, CommitId Head);

/// <summary>The coordinator branches on these alone. Waiting on an agent and waiting on the maintainer differ on
/// purpose.</summary>
public enum ExitStatus { Ok = 0, InvalidInput = 2, GateFailed = 3, WaitingOnAgent = 4, WaitingOnMaintainer = 5, Busy = 6 }

/// <summary>What one factory call did or found. Agent is the worker it started or waits on; Rule is the policy rule that
/// chose the action, when the policy ran.</summary>
public sealed record AdvanceResult(ExitStatus Exit, RunId Run, string Message, Rule? Rule = null,
    AgentHandle? Agent = null, Uri? Pr = null);

/// <summary>The spec as posted for approval. Markdown is the comment body; holdouts never appear in it.</summary>
public sealed record SpecView(Digest Digest, Uri Issue, string Markdown, string ApprovalText);

/// <summary>Facts fetched fresh for one decision. Each artifact field holds the newest valid artifact of its kind from
/// any epoch, never an older one, because rules 4 and 9 have no budget for completed attempts and would re-dispatch on
/// an older one forever. Artifacts that failed validation appear only as InvalidArtifact attempts.</summary>
/// <param name="Claim">The run the forge shows as holding the item.</param>
/// <param name="Unresolved">An operation whose intent was persisted with no receipt and no live worker.</param>
/// <param name="Worker">A worker the spawn binding observed running.</param>
/// <param name="SpecPostedAt">When the forge shows the spec comment for Spec's digest.</param>
public sealed record Evidence(RunManifest Manifest, RunId? Claim, OperationId? Unresolved, AgentHandle? Worker,
    ImmutableArray<MaintainerComment> Maintainer, ImmutableArray<Attempt> Attempts,
    TriageArtifact? Triage, SpecPackage? Spec, DateTimeOffset? SpecPostedAt,
    HeadObservation? Head, ImplementationArtifact? Implementation, ImmutableArray<GateReceipt> Gates,
    CleanupArtifact? Cleanup, Publication? Pr);

public abstract record NextAction
{
    private NextAction() { }
    public sealed record Stop(Disposition Disposition, string Reason) : NextAction;
    /// <summary>Resolve the operation's outcome from the forge, git or the spawn binding before anything else runs.</summary>
    public sealed record Reconcile(OperationId Operation) : NextAction;
    public sealed record AwaitAgent(AgentHandle Handle) : NextAction;
    public sealed record Triage : NextAction;
    /// <summary>Triaged is the agent-ready triage the spec builds on. Revise is set when this rewrites a posted
    /// spec.</summary>
    public sealed record Specify(TriageArtifact Triaged, SpecRevision? Revise) : NextAction;
    /// <summary>Post the spec if the forge does not show it yet, then wait for `/softwarp approve {digest}` or
    /// `/softwarp revise {digest}: ...` in the forge UI.</summary>
    public sealed record AwaitMaintainer(SpecPackage Package) : NextAction;
    /// <summary>Repair is the failed gate receipt whose log the implementer must fix.</summary>
    public sealed record Implement(ApprovedSpec Spec, GateReceipt? Repair) : NextAction;
    public sealed record Gates(ApprovedSpec Spec, CommitId Head) : NextAction;
    public sealed record Cleanup(ApprovedSpec Spec, CommitId Head) : NextAction;
    /// <summary>Copy is the cleanup artifact for this head and package; it carries the PR text.</summary>
    public sealed record Publish(ApprovedSpec Spec, CommitId Head, CleanupArtifact Copy) : NextAction;
    public sealed record Complete(Publication Pr) : NextAction;
}
