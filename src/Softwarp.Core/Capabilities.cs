using System.Collections.Immutable;

namespace Softwarp.Core;

/// <summary>The forge item. No item creation, merge, admin or arbitrary request.</summary>
public interface ITracker
{
    Task<WorkItem> ReadAsync(ItemId item, CancellationToken ct);
    /// <summary>Fresh forge facts for one decision. Spec is the package whose spec comment to look for.</summary>
    Task<TrackerFacts> ObserveAsync(RunManifest run, Digest? spec, CancellationToken ct);
    /// <summary>Marks the item as held by the run, naming the run id.</summary>
    Task ClaimAsync(RunManifest run, OperationId operation, CancellationToken ct);
    /// <summary>Posts the spec as an item comment that carries its digest.</summary>
    Task PostSpecAsync(RunManifest run, SpecView spec, OperationId operation, CancellationToken ct);
    /// <summary>Opens the PR for the run branch; the forge finds it again by that branch.</summary>
    Task OpenPrAsync(PublishRequest request, CancellationToken ct);
}

/// <param name="Claim">The run the forge shows holding the item; null when nothing holds it.</param>
/// <param name="Maintainer">Every `/softwarp` command on the item, with author facts from the forge.</param>
/// <param name="SpecPostedAt">When the spec comment for the requested digest was posted.</param>
/// <param name="Pr">The PR on the run branch.</param>
public sealed record TrackerFacts(RunId? Claim, ImmutableArray<MaintainerComment> Maintainer, DateTimeOffset? SpecPostedAt,
    Publication? Pr);

public sealed record PublishRequest(RunManifest Run, CommitId Head, Digest Spec, string Title, string Body,
    OperationId Operation);

/// <summary>Git: trunk, the run's worktree and its branch.</summary>
public interface IIsolation
{
    /// <summary>The trunk commit and WORKFLOW.md as that commit holds it, never a worktree copy.</summary>
    Task<TrunkSnapshot> ReadTrunkAsync(ItemId item, CancellationToken ct);
    /// <summary>Creates the run's worktree on its branch at the pinned trunk, or returns the one that exists.</summary>
    Task<Workspace> EnsureAsync(RunManifest run, CancellationToken ct);
    /// <summary>The worktree as git shows it; null before the worktree exists.</summary>
    Task<HeadObservation?> ObserveAsync(RunManifest run, CancellationToken ct);
    /// <summary>The run branch's head on the remote; null when it was never pushed.</summary>
    Task<CommitId?> RemoteHeadAsync(RunManifest run, CancellationToken ct);
    /// <summary>Pushes the run branch at head: the branch the worktree is on, no refspec, no force.</summary>
    Task PushAsync(RunManifest run, CommitId head, OperationId operation, CancellationToken ct);
}

public sealed record TrunkSnapshot(CommitId Commit, string WorkflowFile);
public sealed record Workspace(string Path);

/// <summary>Agent processes, one per operation.</summary>
public interface ISpawn
{
    /// <summary>Starts the agent for the operation, or adopts the one already started for it; never a second.</summary>
    Task<AgentHandle> EnsureAsync(RunManifest run, OperationId operation, Brief brief, Workspace workspace,
        CancellationToken ct);
    Task<AgentState> InspectAsync(RunManifest run, OperationId operation, CancellationToken ct);
    /// <summary>Returns when the agent exits or at the deadline, whichever comes first.</summary>
    Task<AgentState> WaitAsync(AgentHandle handle, DateTimeOffset deadline, CancellationToken ct);
}

public abstract record AgentState
{
    private AgentState() { }
    public sealed record NotLaunched : AgentState;
    public sealed record Running(AgentHandle Handle) : AgentState;
    public sealed record Exited : AgentState;
}

/// <summary>What an agent gets. The agent reads its role's input from InputFile and writes JSON matching SchemaFile to
/// OutputFile. All four files sit in the agent's own directory.</summary>
public sealed record Brief(ActionKind Role, string ModelId, string Prompt, string InputFile, string SchemaFile,
    string OutputFile, string LogFile, TimeSpan ExpectedDuration);

public interface IGates
{
    /// <summary>Runs the command as argv in the workspace, never through a shell, and writes its output to the log file.
    /// The factory has already created the file, empty.</summary>
    Task<GateRun> RunAsync(Workspace workspace, CommandSpec command, string logFile, CancellationToken ct);
}

/// <param name="TestsExecuted">How many tests ran, when the command runs tests.</param>
public sealed record GateRun(int ExitCode, int? TestsExecuted);
