using System.Collections.Immutable;

namespace Softwarp;

/// <summary>No item creation, merge, admin or arbitrary authenticated request capability.</summary>
public interface ITracker
{
    Task<WorkItem> ReadAsync(ItemId item, CancellationToken ct);
    Task<TrackerObservation> ObserveAsync(RunManifest run, CancellationToken ct);
    Task<ApprovalEvidence> ReadApprovalAsync(ItemId item, Uri receipt, CancellationToken ct);
    Task ClaimAsync(ItemId item, RunId run, CancellationToken ct);
    Task ProjectAsync(RunId run, Disposition disposition, string comment,
        OperationId operation, CancellationToken ct);
    Task<Publication> OpenPrAsync(PublishRequest request, CancellationToken ct);
}
public sealed record TrackerObservation(Disposition Disposition, RunId? Claim, Publication? Pr);
public sealed record PublishRequest(RunManifest Run, CommitId Head, SpecPackage Spec,
    string Title, string Body, OperationId Operation);

/// <summary>Returns short-lived non-maintainer identity to trusted bindings only.</summary>
public interface IIdentity
{
    Task<CredentialLease> AcquireAsync(IdentityPurpose purpose, CancellationToken ct);
}
public enum IdentityPurpose { ReadForge, Publish }
public abstract class CredentialLease : IAsyncDisposable
{
    public abstract string AccountId { get; }
    public abstract DateTimeOffset ExpiresAt { get; }
    // TODO: Bindings apply credentials to one request; never expose tokens to briefs or logs.
    public abstract ValueTask DisposeAsync();
}

/// <summary>Owns branch and sandbox lifecycle, including read-only trunk reproduction.</summary>
public interface IIsolation
{
    Task<Workspace> EnsureAsync(RunManifest run, CancellationToken ct);
    Task<HeadObservation> ObserveAsync(Workspace workspace, CancellationToken ct);
    Task<ExecutionScope> PrepareAsync(RunManifest run, Role role, string attemptPath, CancellationToken ct);
    /// <summary>Check current branch; push without refspec or force; reconcile remote head on retry.</summary>
    Task PushAsync(Workspace workspace, CommitId expectedHead, OperationId operation, CancellationToken ct);
}
/// <summary>Opaque enforced scope. No forge write credentials; holdouts visible only to spec role.</summary>
public abstract class ExecutionScope : IAsyncDisposable
{
    public abstract string WorkingDirectory { get; }
    public abstract ValueTask DisposeAsync();
}
public sealed record Brief(Role Role, string ModelId, string Prompt, ArtifactRef Schema,
    string OutputFile, string LogFile, TimeSpan ExpectedDuration);
public sealed record AgentHandle(string Id, string LogFile, string WatchCommand);
public sealed record AgentObservation(bool Running, ImmutableArray<string> SideEffects);

/// <summary>Ensure reconciles by operation id before launch; unknown outcome cannot launch twice.</summary>
public interface ISpawn
{
    Task<AgentHandle> EnsureAsync(OperationId operation, Brief brief, ExecutionScope scope, CancellationToken ct);
    Task<AgentObservation> InspectAsync(AgentHandle handle, CancellationToken ct);
    Task<AgentResult> WaitAsync(AgentHandle handle, CancellationToken ct);
    Task SendAsync(AgentHandle handle, string message, CancellationToken ct);
}

/// <summary>Trusted runner executes configured commands against a frozen head, never agent claims.</summary>
public interface IGates
{
    Task<ImmutableArray<GateReceipt>> RunAsync(Workspace workspace, CommitId head,
        Workflow workflow, ImmutableArray<string> changedPaths, CancellationToken ct);
}

/// <summary>One canonical directory per item, with attempt-local writes and an exclusive OS lock.</summary>
public interface IRunStore
{
    Task<RunManifest> CreateOrReadAsync(IntakeRequest request, CancellationToken ct);
    Task<IRunLease> AcquireAsync(RunId run, CancellationToken ct);
}
public interface IRunLease : IAsyncDisposable
{
    /// <summary>Hash-check local artifacts; fetch remote authority separately before acting.</summary>
    Task<LocalEvidence> ReadAsync(CancellationToken ct);
    Task<OperationId> BeginAsync(NextAction action, CancellationToken ct);
    Task CommitAsync(Attempt attempt, CancellationToken ct);
    Task SaveApprovalReferenceAsync(Uri receipt, CancellationToken ct);
    Task RenderViewsAsync(Evidence evidence, CancellationToken ct);
}
public sealed record LocalEvidence(RunManifest Manifest, Workflow Workflow,
    ImmutableArray<Attempt> Attempts, Uri? ApprovalReference);
