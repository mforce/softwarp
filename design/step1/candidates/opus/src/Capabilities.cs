namespace Factory;

/// <summary>
/// Tracker and PR host (DESIGN §9 `tracker`). No method creates an issue: "never file issues" is enforced by absence.
/// Bindings parse wire JSON into domain types; none leak out. Bindings: GhForge (`gh api`, orchestrator GH_TOKEN), FakeForge.
/// </summary>
public interface IForge
{
    Task<WorkItem> Item(RunRef run, CancellationToken ct);
    /// <summary>All issue comments in creation order, with author role and last editor.</summary>
    Task<IReadOnlyList<Comment>> Comments(RunRef run, CancellationToken ct);
    /// <summary>Idempotent: if a bot comment already carries <paramref name="marker"/>, returns its URL and posts nothing.</summary>
    Task<Uri> Post(RunRef run, string marker, string markdown, CancellationToken ct);
    /// <summary>Make <paramref name="label"/> the only factory:* label (null removes them all). Set semantics.</summary>
    Task SetFactoryLabel(RunRef run, string? label, CancellationToken ct);
    Task<PullRequest?> FindPr(RunRef run, BranchName head, CancellationToken ct);
    /// <summary>Ready, not draft. Body says "Refs #n", never "Closes": closing needs every criterion met (station 5).</summary>
    Task<PullRequest> OpenPr(RunRef run, BranchName head, string title, string body, CancellationToken ct);
}

/// <summary>
/// Starts and tracks one agent (DESIGN §9 `spawn`, `status`). Bindings: PaseoAgents (a tab, never a workspace),
/// ClaudeCliAgents (`claude -p --output-format json --json-schema`), CodexCliAgents (`codex exec --output-schema -o`), FakeAgents.
/// Each leaves the raw structured output at <see cref="AgentHandle.OutputPath"/>.
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Returns at once. MUST adopt: if an agent tagged with the call's key exists (Paseo `--label factory-attempt=key`,
    /// or a pid file in the attempt dir), return its handle instead of starting another. This closes the crash
    /// window between spawning and writing handle.json.
    /// </summary>
    Task<AgentHandle> Start(AgentCall call, CancellationToken ct);
    Task<AgentStatus> Status(AgentHandle handle, CancellationToken ct);
    /// <summary>Blocks until no longer Running (`paseo wait`, or poll). Only `advance --wait` uses it.</summary>
    Task<AgentStatus> Wait(AgentHandle handle, CancellationToken ct);
}

/// <summary>
/// Brief in, artifact out. The brief starts with `[coordinator]` and the worker rule (dev-rules). Env holds a ReadOnly
/// GH_TOKEN and GIT_AUTHOR_*/GIT_COMMITTER_* set to the bot; bindings strip every other GH_/GITHUB_ variable.
/// DenyReadPaths (run dir, ~/.config/factory) is best effort: claude settings deny rules; codex cannot honor it.
/// </summary>
public sealed record AgentCall(
    AttemptKey Key, string Title, string Brief, string OutputSchema, string Harness, string Model, AgentRole Role,
    string WorkDir, string AttemptDir, TimeSpan Expected, IReadOnlyDictionary<string, string> Env, IReadOnlyList<string> DenyReadPaths);

/// <summary>What `advance` prints for every background agent: what, where, how to watch, how long (dev-rules).</summary>
public sealed record AgentHandle(string Binding, string Id, string OutputPath, string WatchCommand, TimeSpan Expected);

/// <summary>Bindings: ProcessRunner (argument list, never a shell string), FakeCommands.</summary>
public interface ICommandRunner
{
    Task<CommandResult> Run(
        string file, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string>? env,
        TimeSpan timeout, string? logPath, CancellationToken ct);
}

public sealed record CommandResult(int ExitCode, string Stdout, TimeSpan Duration);

/// <summary>All git the factory does, for one run. One implementation; tests use a real temporary repository.</summary>
public sealed class Git(ICommandRunner sh, string clone, string worktree, BranchName branch)
{
    /// <summary>Null when the worktree does not exist. Fetches origin first, so Trunk and Remote are current.</summary>
    public Task<WorkspaceFacts?> Observe(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>`git show sha:path` in the clone. WORKFLOW.md is read this way, never from the branch under test.</summary>
    public Task<string?> ReadAtCommit(Sha sha, string path, CancellationToken ct) => throw new NotImplementedException();
    public Task Create(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Precondition: no commits ahead of the fork. `git reset --hard trunk`.</summary>
    public Task MoveEmptyBranch(Sha trunk, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Precondition: keepAt is an ancestor of HEAD, and HEAD is not on the remote.</summary>
    public Task ResetUnpushed(Sha keepAt, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>
    /// The only push in the system. It takes no ref, so it cannot push anywhere else.
    /// TODO: assert `git branch --show-current` == branch, then
    ///   git -c credential.helper= -c credential.helper='!factory token --git-credential' push --set-upstream origin branch
    /// No `+`, no --force. A rejected push (diverged remote) escalates.
    /// </summary>
    public Task Push(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>`bash -c command` in the worktree. Refuses a dirty tree; discards the result if HEAD moved during the run.</summary>
    public Task<GateResult> RunGate(Sha sha, GateKind gate, string command, string logPath, CancellationToken ct) =>
        throw new NotImplementedException();
}

/// <summary>ReadOnly mints with contents/issues/pull_requests: read, so GitHub itself refuses an agent's push or label.</summary>
public enum TokenScope { ReadOnly, Orchestrator }

public sealed record InstallationToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>
/// GitHub App installation tokens (DESIGN §18 row 9): an RS256 JWT from app.pem (RSA.ImportFromPem), then
/// POST /app/installations/{id}/access_tokens, with a `permissions` body for ReadOnly. Cached per scope in
/// ~/.cache/factory (0600) until 5 minutes before expiry.
/// </summary>
public sealed class AppTokens(GitHubApp app, HttpClient http)
{
    public Task<InstallationToken> Get(TokenScope scope, CancellationToken ct) => throw new NotImplementedException();
}
