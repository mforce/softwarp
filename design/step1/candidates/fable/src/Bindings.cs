using System.Text.Json.Nodes;

namespace Factory.Bindings;

/// <summary>
/// GitHub App installation token (DESIGN §18 row 9): RS256 JWT from `app.pem`, then `POST /app/installations/{id}/access_tokens`.
/// Cached, renewed 5 minutes before the 1-hour expiry. The only credential in the process; agents are spawned without it.
/// </summary>
public sealed class AppToken(GitHubApp app, TimeProvider clock, HttpClient http)
{
    public string Login => $"{app.Slug}[bot]";
    public Task<string> Get(CancellationToken ct = default) => throw new NotImplementedException();
}

/// <summary>REST over HttpClient with source-generated DTOs private to this file. Wire types never leave this class.</summary>
public sealed class GitHubTracker(AppToken token, HttpClient http) : ITracker
{
    public Task<IssueState> GetIssue(RunId id) => throw new NotImplementedException();
    public Task<IReadOnlyList<Comment>> Comments(RunId id) => throw new NotImplementedException();
    public Task SetLabel(RunId id, FactoryLabel label) => throw new NotImplementedException();
    public Task<long> Comment(RunId id, string body) => throw new NotImplementedException();
    public Task<PullRef?> FindPullRequest(RepoSlug repo, string branch) => throw new NotImplementedException();
    public Task<PullRef> OpenPullRequest(RepoSlug repo, string branch, string baseBranch, string title, string body) => throw new NotImplementedException();
}

/// <summary>`paseo run --cwd --title --model --output-schema <file> --json <prompt>` (never `--new-workspace`); `paseo inspect|send|stop`.</summary>
public sealed class PaseoSpawner(string paseoPath) : ISpawner
{
    public string Name => "paseo";
    public Task<Child> Spawn(SpawnRequest request) => throw new NotImplementedException();
    public Task<ChildStatus> Status(Child child) => throw new NotImplementedException();
    public Task<JsonNode?> Result(Child child) => throw new NotImplementedException();
    /// <summary>`paseo send <id> "[coordinator] <message>"`.</summary>
    public Task Send(Child child, string message) => throw new NotImplementedException();
    public Task Stop(Child child) => throw new NotImplementedException();
}

public enum Harness { Claude, Codex }

/// <summary>
/// One detached process per spawn, stdout to the log path; Handle is the session id. The two harnesses differ only in argv:
///   Claude: `claude -p --output-format json --json-schema <schema> --model <m> --session-id <id> [--permission-mode plan] <brief>`; Send = `--resume <id>`.
///   Codex:  `codex exec -C <cwd> -s <read-only|workspace-write> -m <m> --output-schema <file> -o <last-message> --json <brief>`.
/// Status = process alive? Result = parse `structured_output` (Claude) or the last-message file (Codex).
/// </summary>
public sealed class HeadlessSpawner(Harness harness) : ISpawner
{
    public string Name => harness == Harness.Claude ? "claude" : "codex";
    public Task<Child> Spawn(SpawnRequest request) => throw new NotImplementedException();
    public Task<ChildStatus> Status(Child child) => throw new NotImplementedException();
    public Task<JsonNode?> Result(Child child) => throw new NotImplementedException();
    public Task Send(Child child, string message) => throw new NotImplementedException();
    public Task Stop(Child child) => throw new NotImplementedException();
}

/// <summary>Shells out to `git`. Push is the one place a refspec could appear, and the signature has no slot for one.</summary>
public sealed class GitCli(AppToken token) : IGit
{
    public Task<string> TrunkSha(string clonePath, string baseBranch) => throw new NotImplementedException();
    public Task<string> FileAtTrunk(string clonePath, string baseBranch, string relativePath) => throw new NotImplementedException();
    public Task<Worktree> EnsureWorktree(RunId id, string clonePath, string branch, string baseSha) => throw new NotImplementedException();
    public Task<BranchState?> Inspect(RunId id, string baseSha) => throw new NotImplementedException();
    /// <summary>`GIT_ASKPASS` answers with the App token so HTTPS push never sees the maintainer's identity.</summary>
    public Task Push(Worktree worktree) => throw new NotImplementedException();
}

/// <summary>`sh -c <command>` (or `<shim> '<command>'`) in the worktree; stdout+stderr to the log; exit code and duration recorded.</summary>
public sealed class ShellGateRunner(TimeProvider clock) : IGateRunner
{
    public Task<GateResult> Run(string gate, string command, Worktree worktree, string sha, string? shim, string logPath) => throw new NotImplementedException();
}
