using System.Text.Json.Nodes;

namespace Factory;

/// <summary>
/// DESIGN §9 `tracker`. There is deliberately no method that creates an issue; the policy "never file issues" is the
/// absence of a member, not a check. Only the orchestrator holds an instance (agents never see a token).
/// </summary>
public interface ITracker
{
    Task<IssueState> GetIssue(RunId id);
    Task<IReadOnlyList<Comment>> Comments(RunId id);
    /// <summary>Replaces whichever `factory:*` label is present; idempotent.</summary>
    Task SetLabel(RunId id, FactoryLabel label);
    Task<long> Comment(RunId id, string body);
    Task<PullRef?> FindPullRequest(RepoSlug repo, string branch);
    Task<PullRef> OpenPullRequest(RepoSlug repo, string branch, string baseBranch, string title, string body);
}

/// <summary>What a spawner needs to start one agent. `Schema` is the artifact's exported JSON schema; `Watch` is printed at launch.</summary>
public sealed record SpawnRequest(
    RunId Run,
    Station Station,
    string Title,
    string Brief,
    JsonNode Schema,
    string Model,
    string Cwd,
    bool ReadOnly,
    TimeSpan Expected,
    string LogPath);

/// <summary>
/// DESIGN §9 `spawn` / `status` / `send`. Three bindings on this machine (Paseo tab, `claude -p`, `codex exec`) and a fake
/// in tests. <see cref="Send"/> MUST prefix `[coordinator] ` itself; callers cannot forget it.
/// </summary>
public interface ISpawner
{
    string Name { get; }
    Task<Child> Spawn(SpawnRequest request);
    Task<ChildStatus> Status(Child child);
    /// <summary>The structured output, once Finished. Null if the harness returned none (counts as an invalid artifact).</summary>
    Task<JsonNode?> Result(Child child);
    Task Send(Child child, string message);
    Task Stop(Child child);
}

public sealed record Worktree(string Path, string Branch);

/// <summary>DESIGN §9 `isolate`, plus the only push in the system.</summary>
public interface IGit
{
    Task<string> TrunkSha(string clonePath, string baseBranch);
    Task<string> FileAtTrunk(string clonePath, string baseBranch, string relativePath);
    /// <summary>Creates `~/.factory-slices/<owner>/<name>/<issue>` on `branch` from `baseSha`, or returns it if present.</summary>
    Task<Worktree> EnsureWorktree(RunId id, string clonePath, string branch, string baseSha);
    Task<BranchState?> Inspect(RunId id, string baseSha);
    /// <summary>Plain `git push -u origin HEAD` after checking `git branch --show-current` equals the run branch. No refspec, no force.</summary>
    Task Push(Worktree worktree);
}

/// <summary>DESIGN §4: a gate is a command with an exit code. Runs in the worktree, optionally through the machine's shell shim (`sg docker -c`).</summary>
public interface IGateRunner
{
    Task<GateResult> Run(string gate, string command, Worktree worktree, string sha, string? shim, string logPath);
}
