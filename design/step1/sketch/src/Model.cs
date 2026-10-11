using System.Collections.Immutable;

namespace Softwarp;

public readonly record struct RunId(string Value);
public readonly record struct Digest(string Value);
public readonly record struct CommitId(string Value);
public readonly record struct OperationId(string Value);
public sealed record ItemId(string Repository, long Number);
public sealed record ArtifactRef(Digest Hash, string RelativePath);
public enum Station { Intake, Triage, Spec, Implement, Publish }
public enum Disposition { NeedsTriage, NeedsInfo, Ready, NeedsHuman, Active, Review, Done, WontFix }
public enum WorkClass { Feature, Bug, Performance, Refactor }
public enum Size { Small, Medium, Large }
public enum Door { Reversible, Irreversible }
public enum TriageDecision { Agent, Human, NeedsInfo, WontFix }
public enum Role { Triage, Specification, Implementation, Cleanup }
public enum GateKind { Build, TargetedTests, Lint }
public enum ActionKind { Triage, Spec, Implement, Gates, Cleanup, Publish }
public enum AttemptOutcome { Completed, InvalidArtifact, Bounce, Escalate, Interrupted }
/// <summary>The coordinator branches on these alone. Waiting on an agent and waiting on the maintainer differ on purpose.</summary>
public enum ExitStatus { Ok = 0, InvalidInput = 2, GateFailed = 3, WaitingOnAgent = 4, WaitingOnMaintainer = 5, Busy = 6 }
public enum MaintainerVerb { Approve, Revise, Resume }

public sealed record WorkItem(ItemId Id, string Text, string Revision, Uri Url);
public sealed record InputPin(string Name, string Version, Digest Content);
public sealed record BindingChoice(string Capability, string BindingId, string Version);
public sealed record RunManifest(RunId Run, WorkItem Item, CommitId Trunk,
    CommitId WorkflowCommit, Digest WorkflowHash, ImmutableArray<InputPin> Inputs,
    ImmutableArray<BindingChoice> Bindings, string Branch, string MaintainerAccountId);
public sealed record CommandSpec(string Executable, ImmutableArray<string> Arguments);
public sealed record Workflow(int Trust, ImmutableDictionary<GateKind, CommandSpec> Commands,
    ImmutableDictionary<string, string> Bindings, string PromptBody);
public sealed record Workspace(string Path, string Branch, CommitId Base);
public sealed record HeadObservation(CommitId Head, bool Clean, ImmutableArray<string> ChangedPaths);

public sealed record Reproduction(CommitId Trunk, string Command, int ExitCode, string Evidence);
public sealed record TriageArtifact(TriageDecision Decision, WorkClass Class, Size Size,
    Door Door, ImmutableArray<string> Risks, ImmutableArray<ItemId> Related,
    string PriorRejectionSearch, string RedundancySearch, Reproduction Reproduction, string Brief);
public sealed record Criterion(string Id, string Text);
public sealed record Ticket(string Id, string Text, ImmutableArray<string> BlockedBy);
public sealed record Holdout(string CriterionId, string Scenario, string Expected);
/// <summary>TestTargets is the approved test filter; the targeted-test gate uses it, so the implementer cannot narrow its own gate.</summary>
public sealed record SpecArtifact(string Markdown, ImmutableArray<Criterion> Criteria,
    ImmutableArray<Ticket> Tickets, ImmutableArray<Holdout> Holdouts, ImmutableArray<string> TestTargets);

/// <summary>Everything the implementer receives. Holdouts are absent by type, not by convention.</summary>
public sealed record ImplementationInput(string PublicSpecMarkdown, ImmutableArray<Criterion> Criteria,
    ImmutableArray<Ticket> Tickets, ImmutableArray<string> TestTargets, string? RepairLog);
public sealed record ImplementationArtifact(CommitId Head, ImmutableArray<CommitId> Commits,
    ImmutableArray<string> AddressedCriteria, string Summary);
public sealed record CleanupArtifact(CommitId Head, string PrTitle, string PrBody);

/// <summary>Digest covers canonical spec, ticket graph, holdouts and all run input pins.</summary>
public sealed record SpecPackage(Digest Digest, ArtifactRef PublicSpec, ArtifactRef Tickets,
    ArtifactRef PrivateHoldouts);
/// <summary>Counts only when HeadAfter == Head and the tree was clean for the whole run.
/// A TargetedTests receipt also needs TestsExecuted > 0: a filter that matches nothing fails.</summary>
public sealed record GateReceipt(GateKind Kind, CommitId Head, CommitId HeadAfter, bool TreeClean,
    Digest Command, int ExitCode, int? TestsExecuted, ArtifactRef Log);
public sealed record Publication(Uri Url, string Branch, CommitId Head, Digest Spec);
/// <summary>A maintainer command found on the item (`/softwarp approve|revise|resume`). Author facts come from the forge,
/// never from local config: account id, author type (User or Bot), repository role, and whether anyone else edited it.</summary>
public sealed record MaintainerComment(Uri Url, MaintainerVerb Verb, Digest? SpecDigest, string? Feedback,
    string AuthorAccountId, bool AuthorIsBot, bool AuthorIsRepoAdmin, bool EditedByOther, DateTimeOffset PostedAt);

/// <summary>Created only by policy from freshly fetched external evidence, never deserialized.</summary>
public sealed class ApprovedSpec
{
    internal ApprovedSpec(SpecPackage package, MaintainerComment approval) => throw new NotImplementedException();
    public SpecPackage Package => throw new NotImplementedException();
    public MaintainerComment Approval => throw new NotImplementedException();
}

/// <summary>One immutable receipt per operation; Inputs identifies exact prerequisite content.</summary>
/// <summary>Epoch = number of maintainer Resume comments when the attempt began; budgets count within one epoch.</summary>
public sealed record Attempt(OperationId Id, ActionKind Action, Digest Inputs, int Revision, int Epoch,
    AttemptOutcome Outcome, ImmutableArray<ArtifactRef> Outputs, string Reason, Station? BounceTo);
public sealed record AgentResult(AttemptOutcome Outcome, string OutputFile, string Reason,
    Station? BounceTo);
public sealed record IntakeRequest(string RepositoryPath, long Issue);
public sealed record AdvanceResult(ExitStatus Exit, string Message, RunId Run, Uri? Pr);
public sealed record SpecView(string Markdown, Digest Digest, Uri Issue, string ApprovalText);

/// <summary>Optional evidence means an obligation is unmet, not a partially constructed station.</summary>
public sealed record Evidence(RunManifest Manifest, Disposition Disposition,
    ImmutableArray<MaintainerComment> Maintainer, int Epoch, ImmutableArray<Attempt> Attempts, TriageArtifact? Triage, SpecPackage? Spec,
    ApprovedSpec? Approval, Workspace? Workspace, HeadObservation? Head,
    ImplementationArtifact? Implementation, ImmutableArray<GateReceipt> Gates,
    CleanupArtifact? Cleanup, Publication? Pr);

public abstract record NextAction
{
    private NextAction() { }
    public sealed record Agent(ActionKind Kind, Role Role, Digest Inputs) : NextAction;
    public sealed record Gates(CommitId Head) : NextAction;
    /// <summary>Spec posted; waits for `/softwarp approve <digest>` or `/softwarp revise <digest>: ...` in the forge UI.</summary>
    public sealed record AwaitMaintainer(SpecPackage Package, string Instruction) : NextAction;
    /// <summary>A worker is running; the call returns WaitingOnAgent unless --wait covers its finish.</summary>
    public sealed record AwaitAgent(AgentHandle Handle) : NextAction;
    public sealed record Publish(ApprovedSpec Spec, CommitId Head, CleanupArtifact Copy) : NextAction;
    public sealed record Stop(Disposition Disposition, string Reason) : NextAction;
    public sealed record Complete(Publication Pr) : NextAction;
}
