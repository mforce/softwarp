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
public enum ExitStatus { Ok = 0, InvalidInput = 2, GateFailed = 3, Waiting = 4, Busy = 5 }

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
public sealed record SpecArtifact(string Markdown, ImmutableArray<Criterion> Criteria,
    ImmutableArray<Ticket> Tickets, ImmutableArray<Holdout> Holdouts);
public sealed record ImplementationArtifact(CommitId Head, ImmutableArray<CommitId> Commits,
    ImmutableArray<string> AddressedCriteria, string Summary);
public sealed record CleanupArtifact(CommitId Head, string PrTitle, string PrBody);

/// <summary>Digest covers canonical spec, ticket graph, holdouts and all run input pins.</summary>
public sealed record SpecPackage(Digest Digest, ArtifactRef PublicSpec, ArtifactRef Tickets,
    ArtifactRef PrivateHoldouts);
public sealed record GateReceipt(GateKind Kind, CommitId Head, Digest Command,
    int ExitCode, ArtifactRef Log);
public sealed record Publication(Uri Url, string Branch, CommitId Head, Digest Spec);
public sealed record ApprovalEvidence(Uri Url, string AuthorAccountId, string Text,
    string RemoteRevision, DateTimeOffset ObservedAt);

/// <summary>Created only by policy from freshly fetched external evidence, never deserialized.</summary>
public sealed class ApprovedSpec
{
    internal ApprovedSpec(SpecPackage package, ApprovalEvidence evidence) => throw new NotImplementedException();
    public SpecPackage Package => throw new NotImplementedException();
    public ApprovalEvidence Evidence => throw new NotImplementedException();
}

/// <summary>One immutable receipt per operation; Inputs identifies exact prerequisite content.</summary>
public sealed record Attempt(OperationId Id, ActionKind Action, Digest Inputs, int Revision,
    AttemptOutcome Outcome, ImmutableArray<ArtifactRef> Outputs, string Reason, Station? BounceTo);
public sealed record AgentResult(AttemptOutcome Outcome, string OutputFile, string Reason,
    Station? BounceTo);
public sealed record IntakeRequest(string RepositoryPath, long Issue);
public sealed record ApprovalCheck(bool Accepted, string Explanation);
public sealed record AdvanceResult(ExitStatus Exit, string Message, RunId Run, Uri? Pr);
public sealed record SpecView(string Markdown, Digest Digest, Uri Issue, string ApprovalText);

/// <summary>Optional evidence means an obligation is unmet, not a partially constructed station.</summary>
public sealed record Evidence(RunManifest Manifest, Disposition Disposition,
    ImmutableArray<Attempt> Attempts, TriageArtifact? Triage, SpecPackage? Spec,
    ApprovedSpec? Approval, Workspace? Workspace, HeadObservation? Head,
    ImplementationArtifact? Implementation, ImmutableArray<GateReceipt> Gates,
    CleanupArtifact? Cleanup, Publication? Pr);

public abstract record NextAction
{
    private NextAction() { }
    public sealed record Agent(ActionKind Kind, Role Role, Digest Inputs) : NextAction;
    public sealed record Gates(CommitId Head) : NextAction;
    public sealed record AwaitApproval(SpecPackage Package) : NextAction;
    public sealed record Publish(ApprovedSpec Spec, CommitId Head, CleanupArtifact Copy) : NextAction;
    public sealed record Stop(Disposition Disposition, string Reason) : NextAction;
    public sealed record Complete(Publication Pr) : NextAction;
}
