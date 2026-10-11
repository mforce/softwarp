using System.Text.Json.Nodes;

namespace Factory;

// Capability interfaces speak domain types only. Bindings (GitHub REST, git, Paseo, claude -p, codex exec)
// parse their wire formats inside themselves. Tests use in-memory fakes of exactly these five.

public enum AuthorKind { User, Bot }

/// <param name="Key">Hidden marker key, set ONLY for comments the factory's own identity wrote (the binding checks the
/// author). A marker typed by anyone else comes back as null, so it cannot fake "spec posted" or "claimed".</param>
public sealed record Comment(long Id, string Login, AuthorKind Kind, DateTimeOffset At, string? Key, string Body);

public sealed record IssueSnapshot(IssueRef Ref, string Title, string Body, IReadOnlyList<string> Labels,
    IReadOnlyList<Comment> Comments, string? ClaimedByRun);

public sealed record PrInfo(int Number, string Url, string HeadSha);

public enum ClaimResult { Won, AlreadyMine, LostToOther }

/// <summary>Issues and PRs on the forge. DELIBERATELY ABSENT: create issue, merge, approve, edit rulesets (DESIGN §9, §12).
/// The absence is the enforcement. Nothing here can be called to break those rules.</summary>
public interface ITracker
{
    Task<IssueSnapshot> Read(IssueRef issue, CancellationToken ct);
    /// <summary>Claim marker comment, then re-read. The earliest marker wins. Safe to repeat.</summary>
    Task<ClaimResult> TryClaim(IssueRef issue, string runId, CancellationToken ct);
    /// <summary>Create or edit the comment carrying this key. Idempotent.</summary>
    Task Upsert(IssueRef issue, string key, string markdown, CancellationToken ct);
    /// <summary>Afterwards exactly one factory:* label is on the issue. Labels without that prefix are untouched.</summary>
    Task SetLabel(IssueRef issue, FactoryLabel label, CancellationToken ct);
    Task<PrInfo?> FindPr(IssueRef issue, string branch, CancellationToken ct);
    /// <summary>Opens a ready PR (not a draft: DESIGN §19.3) that links the issue.</summary>
    Task<PrInfo> OpenPr(IssueRef issue, string branch, string title, string body, CancellationToken ct);
}

/// <summary>The GitHub App's one-hour installation token (RS256 JWT from app.pem, then POST access_tokens).
/// Only bindings hold an IIdentity. The Engine never sees a token, and no agent call can carry one.</summary>
public interface IIdentity
{
    Task<AccessToken> Token(CancellationToken ct);
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "***";
}

public enum WorkspaceKind { Trunk, Branch }

public sealed record Workspace(string Path, string Branch, WorkspaceKind Kind);

public sealed record WorkspaceState(Workspace Ws, string HeadSha, string? RemoteHeadSha, bool Dirty);

/// <summary>Worktrees under ~/.factory-slices/&lt;repo&gt;/&lt;n&gt;. Trunk is a detached checkout of origin/main for read-only roles.
/// Branch is factory/&lt;n&gt; off origin/main. Push takes no refspec and no remote name: it can only publish the
/// workspace's own branch to the same-named ref, fast-forward only (dev-rules: never push src:dst).</summary>
public interface IWorkspaces
{
    Task<Workspace> Ensure(IssueRef issue, WorkspaceKind kind, CancellationToken ct);
    Task<WorkspaceState?> Inspect(IssueRef issue, WorkspaceKind kind, CancellationToken ct);
    Task Push(IssueRef issue, CancellationToken ct);
    /// <summary>The text of a file at a commit, for reading WORKFLOW.md from trunk and never from a writable worktree.</summary>
    Task<string> ReadAtCommit(IssueRef issue, string commit, string path, CancellationToken ct);
}

public sealed record AgentKey(IssueRef Issue, Station Station, int Attempt);

public sealed record RenderedBrief(string Text, string TemplateSha256);

/// <summary>Everything an agent gets. There is no token, env or tracker field on purpose. The binding starts from a scrubbed
/// environment, so GH_TOKEN and the maintainer's credentials never reach an agent.</summary>
public sealed record AgentCall(AgentKey Key, Workspace Cwd, Access Access, ModelTier Tier, RenderedBrief Brief,
    JsonNode Schema, TimeSpan ExpectedRuntime);

/// <param name="WatchCommand">Printed to the user at launch, e.g. "paseo logs 3f2a" or "tail -f &lt;path&gt;" (dev-rules: background visibility).</param>
public sealed record ChildRef(AgentKey Key, string Harness, string Handle, string Model, string WatchCommand,
    string OutputPath, DateTimeOffset StartedAt);

public abstract record ChildState;
public sealed record Running(DateTimeOffset LastSideEffectAt) : ChildState;
/// <param name="ArtifactJson">Already unwrapped: structured_output (claude), the -o file (codex), the --output-schema result (paseo).</param>
public sealed record Finished(string ArtifactJson) : ChildState;
public sealed record Died(string Reason) : ChildState;

/// <summary>Starts one agent and lets later processes poll it. Never blocks until the agent ends, so a crashed CLI loses nothing.
/// Find is the idempotence hook: Paseo by --label, claude/codex by a pid + output file under the run dir.</summary>
public interface ISpawner
{
    Task<ChildRef?> Find(AgentKey key, CancellationToken ct);
    Task<ChildRef> Start(AgentCall call, CancellationToken ct);
    Task<ChildState> Poll(ChildRef child, CancellationToken ct);
}

/// <summary>Runs the repo's command (with the machine's shell shim, e.g. "sg docker -c") and keeps the log.
/// The receipt's HeadSha is read before AND after the run. If the head moved, the call throws.</summary>
public interface IGates
{
    Task<GateReceipt> Run(GateKind gate, Workspace ws, string command, CancellationToken ct);
}

public sealed record Caps(ITracker Tracker, IWorkspaces Work, ISpawner Spawn, IGates Gates, TimeProvider Clock);
