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
    /// <summary>Pushes the run branch at head, with no force. False when the remote rejects the update, for example
    /// because its branch holds commits the head lacks; any other failure throws.</summary>
    Task<bool> PushAsync(RunManifest run, CommitId head, OperationId operation, CancellationToken ct);
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
    /// <summary>Returns when the agent exits or at the deadline, whichever comes first: true when it exited.</summary>
    Task<bool> WaitAsync(RunManifest run, OperationId operation, DateTimeOffset deadline, CancellationToken ct);
    /// <summary>The output the agent's harness gave, read once the agent has exited, and never anything else the agent
    /// wrote. It is as untrusted as the agent; the factory reads it through the role's contract.</summary>
    /// <exception cref="InvalidDataException">There is none, or it is not a regular file, or it holds more than
    /// <see cref="Factory.MaxOutputBytes"/>.</exception>
    byte[] Output(RunManifest run, OperationId operation);
}

public abstract record AgentState
{
    private AgentState() { }
    public sealed record NotLaunched : AgentState;
    public sealed record Running(AgentHandle Handle) : AgentState;
    public sealed record Exited : AgentState;
}

/// <summary>What an agent gets. The agent reads its role's input from InputFile and gives JSON matching SchemaFile as its
/// final answer; a harness that writes that answer to a file writes OutputFile. Those three sit in the agent's own
/// directory; LogFile sits beside it, where the agent cannot reach its path.</summary>
/// <param name="Agent">The role's harness, model and effort, as the run pinned them.</param>
public sealed record Brief(ActionKind Role, AgentPin Agent, string Prompt, string InputFile, string SchemaFile,
    string OutputFile, string LogFile, TimeSpan ExpectedDuration);

public interface IGates
{
    /// <summary>Runs the command as argv in the workspace, never through a shell, and writes its output to the log file.</summary>
    Task<GateRun> RunAsync(GateRequest request, CancellationToken ct);
}

/// <summary>One gate run.</summary>
/// <param name="Item">The run's item.</param>
/// <param name="Trunk">The run's pinned trunk commit; packages its lock files do not name count as new.</param>
/// <param name="Report">The format a targeted-test command writes its report in; the runner counts the tests that ran
/// from it.</param>
/// <param name="Admitted">Every package from outside the pinned trunk that earlier gate runs of this run recorded letting
/// in (<see cref="RunRecord.AdmittedPackages"/>).</param>
/// <param name="LogFile">The factory has already created it, empty.</param>
/// <param name="PackagesFile">Where the runner records, before it fetches anything, the packages from outside the pinned
/// trunk it lets in, one `id@version` per line: a factory record that no agent is handed.</param>
public sealed record GateRequest(Workspace Workspace, ItemId Item, CommitId Trunk, GateKind Kind, CommandSpec Command,
    TestReport Report, ImmutableArray<string> Admitted, string LogFile, string PackagesFile);

/// <param name="ExitCode">The command's exit code, or <see cref="GateReceipt.TimedOut"/>.</param>
/// <param name="TestsExecuted">How many tests ran, when the command runs tests; null when no report says.</param>
public sealed record GateRun(int ExitCode, int? TestsExecuted);
