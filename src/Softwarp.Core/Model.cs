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

public sealed record RunManifest(RunId Run, CommitId Trunk, string Branch, string MaintainerAccountId, Workflow Workflow);

public sealed record CommandSpec(string Executable, ImmutableArray<string> Arguments)
{
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
/// <summary>Epoch is stamped by the factory, never taken from agent output: the epoch the triage ran in.</summary>
public sealed record TriageArtifact(TriageDecision Decision, Reproduction Reproduction,
    string PriorRejectionSearch, string RedundancySearch, int Epoch);
public sealed record Criterion(string Id, string Text);
public sealed record Ticket(string Id, string Text, ImmutableArray<string> BlockedBy);
public sealed record Holdout(string CriterionId, string Scenario, string Expected);
/// <summary>TestTargets is the approved test filter the targeted-test gate runs.</summary>
public sealed record SpecArtifact(string Markdown, ImmutableArray<Criterion> Criteria,
    ImmutableArray<Ticket> Tickets, ImmutableArray<Holdout> Holdouts, ImmutableArray<string> TestTargets);
/// <summary>Spec is the package digest the factory dispatched this work for, stamped by the factory and never taken
/// from agent output.</summary>
public sealed record ImplementationArtifact(Digest Spec);
/// <summary>Spec and Head are stamped by the factory, never taken from agent output: the package it dispatched the
/// cleanup for, and the head git shows when the agent returns.</summary>
public sealed record CleanupArtifact(Digest Spec, CommitId Head);

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
    /// <summary>Revise is the maintainer's revise comment when this rewrites a posted spec.</summary>
    public sealed record Specify(MaintainerComment? Revise) : NextAction;
    /// <summary>Post the spec if the forge does not show it yet, then wait for `/softwarp approve {digest}` or
    /// `/softwarp revise {digest}: ...` in the forge UI.</summary>
    public sealed record AwaitMaintainer(SpecPackage Package) : NextAction;
    /// <summary>Repair is the failed gate receipt whose log the implementer must fix.</summary>
    public sealed record Implement(ApprovedSpec Spec, GateReceipt? Repair) : NextAction;
    public sealed record Gates(ApprovedSpec Spec, CommitId Head) : NextAction;
    public sealed record Cleanup(ApprovedSpec Spec, CommitId Head) : NextAction;
    public sealed record Publish(ApprovedSpec Spec, CommitId Head) : NextAction;
    public sealed record Complete(Publication Pr) : NextAction;
}
