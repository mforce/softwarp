using System.Text.Json.Serialization;

namespace Factory;

/// <summary>
/// Everything <see cref="Process.Next"/> may look at, observed fresh on each loop iteration. Immutable.
/// There is no "current station" field: the station is whatever Next derives from these facts.
/// </summary>
public sealed record Facts(
    RunRef Run,
    WorkItem Item,
    Timeline Timeline,
    bool RunDirExists,
    WorkspaceFacts? Workspace,
    Workflow? Workflow,                      // WORKFLOW.md at the fork commit; null until the worktree exists
    StationLog<TriageVerdict> Triage,
    StationLog<SpecArtifact> Spec,
    StationLog<ImplementReport> Implement,
    StationLog<CleanReport> Clean,
    StationLog<PrDraft> PrBody,
    IReadOnlyList<GateResult> Gates,
    PullRequest? Pr);

public sealed record WorkItem(int Number, string Title, string Body, bool Open, Uri Url);

/// <summary>Admin = `author_association == OWNER` on a user-owned repo (step 1); admin/maintain permission on an org repo (later).</summary>
public enum AuthorRole { Admin, Other }

/// <summary>Forge-asserted identity. Bindings map wire data into this; the policy over it lives in <see cref="Timeline.From"/>.</summary>
public sealed record Author(string Login, bool IsBot, AuthorRole Role);

/// <summary>An issue comment. <see cref="LastEditor"/> is null when never edited (GraphQL `editor`).</summary>
public sealed record Comment(long Id, Uri Url, Author Author, string Body, Author? LastEditor);

public enum NoticeKind { Escalation, NeedsInfo, WontFix }

/// <summary>A comment that means something to the factory, in forge order.</summary>
public abstract record TimelineEntry(long Id, Uri Url)
{
    public sealed record Claimed(long Id, Uri Url, string Host) : TimelineEntry(Id, Url);
    public sealed record SpecPosted(long Id, Uri Url, Digest Spec) : TimelineEntry(Id, Url);
    public sealed record NoticePosted(long Id, Uri Url, NoticeKind Kind) : TimelineEntry(Id, Url);
    public sealed record Approved(long Id, Uri Url, string By, string DigestPrefix) : TimelineEntry(Id, Url);
    public sealed record ReviseRequested(long Id, Uri Url, string By, string Feedback) : TimelineEntry(Id, Url);
    public sealed record Resumed(long Id, Uri Url, string By, string Guidance) : TimelineEntry(Id, Url);
}

/// <summary>The issue's comments reduced to factory meaning. Approval forgery is stopped here, in pure code.</summary>
public sealed record Timeline(IReadOnlyList<TimelineEntry> Entries)
{
    /// <summary>
    /// Markers (`&lt;!-- factory:… --&gt;`) count only on comments by <paramref name="botLogin"/>. Commands
    /// (`/factory approve|revise|resume` on the first line) count only when
    /// <c>!IsBot &amp;&amp; Role == Admin &amp;&amp; (LastEditor is null || LastEditor.Login == Author.Login)</c>.
    /// Everything else is dropped: a bot quoting `/factory approve`, or anyone editing the maintainer's words, approves nothing.
    /// </summary>
    public static Timeline From(IReadOnlyList<Comment> comments, string botLogin) => throw new NotImplementedException();
    // The marker format lives only here (rendered below, parsed in From). Each key makes Post idempotent.
    public static string ClaimMarker(string host) => $"<!-- factory:claim host={host} -->";
    public static string SpecMarker(Digest d) => $"<!-- factory:spec digest={d.Hex} -->";
    /// <summary>Keyed by epoch: a notice halts the run until a resume opens the next epoch, so each epoch has at most one.</summary>
    public static string NoticeMarker(NoticeKind k, int epoch) => $"<!-- factory:notice kind={k} epoch={epoch} -->";
    public TimelineEntry.Claimed? Claim => Entries.OfType<TimelineEntry.Claimed>().FirstOrDefault();
    /// <summary>Budget epoch = number of maintainer resumes. Attempts record it at spawn; budgets count within it.</summary>
    public int Epoch => Entries.Count(e => e is TimelineEntry.Resumed);
    /// <summary>The last notice with no Resumed after it.</summary>
    public TimelineEntry.NoticePosted? OpenNotice => throw new NotImplementedException();
    public TimelineEntry.SpecPosted? LastSpec => Entries.OfType<TimelineEntry.SpecPosted>().LastOrDefault();
    /// <summary>An Approved after <see cref="LastSpec"/> whose prefix matches <paramref name="current"/>. Stale prefixes are ignored.</summary>
    public TimelineEntry.Approved? ApprovalOf(Digest current) => throw new NotImplementedException();
    /// <summary>Revise requests after the last spec post.</summary>
    public IReadOnlyList<TimelineEntry.ReviseRequested> PendingRevisions => throw new NotImplementedException();
    /// <summary>Every revise and resume text, verbatim, for later briefs. Trusted: the maintainer wrote it.</summary>
    public IReadOnlyList<string> MaintainerGuidance => throw new NotImplementedException();
}

/// <summary>Git facts for the worktree. Fork = merge-base(factory/n, origin/main): derived, never stored.</summary>
public sealed record WorkspaceFacts(string Path, Sha Fork, Sha Head, Sha Trunk, bool Dirty, Sha? Remote)
{
    public bool HasCommits => Head != Fork;
}

public enum GateKind { Build, TestTargeted, Lint }

/// <summary>Evidence for one SHA only. Written only if the tree was clean and HEAD == Sha before and after the run.</summary>
public sealed record GateResult(Sha Sha, GateKind Gate, string Command, int ExitCode, TimeSpan Duration, string LogPath)
{
    public bool Passed => ExitCode == 0;
}

/// <summary>Live status from the agent binding. Exited says nothing about quality; the artifact does.</summary>
public abstract record AgentStatus
{
    private AgentStatus() { }
    public sealed record Running : AgentStatus;
    public sealed record Exited : AgentStatus;
    public sealed record Lost(string Why) : AgentStatus;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Initial), "initial")]
[JsonDerivedType(typeof(RetryInvalid), "retry-invalid")]
[JsonDerivedType(typeof(Revision), "revision")]
public abstract record AttemptReason
{
    private AttemptReason() { }
    public sealed record Initial : AttemptReason;
    /// <summary>Budget 1 per job per epoch. Covers invalid, missing and lost output.</summary>
    public sealed record RetryInvalid(IReadOnlyList<string> Problems) : AttemptReason;
    /// <summary>Budget 2 per job per epoch. Feedback: a failing gate log, a dirty tree, or the maintainer's words.</summary>
    public sealed record Revision(string Feedback) : AttemptReason;
}

/// <summary>Every input pinned for replay (DESIGN §4).</summary>
public sealed record Pins(
    string FactoryVersion, string BriefTemplate, string BriefDigest, IReadOnlyList<SkillPin> Skills,
    string Harness, string Model, string WorkflowCommit, string WorkflowDigest);

public sealed record SkillPin(string Name, string Path, string SkillMdDigest);

/// <summary>call.json, written before the spawn. <see cref="HeadAtStart"/> lets Clean be undone (DiscardClean).</summary>
public sealed record AttemptCall(int Number, AgentJob Job, int Epoch, AttemptReason Reason, Pins Pins, string? HeadAtStart);

public sealed record Attempt<T>(AttemptCall Call, AgentHandle? Handle, AgentStatus Status, ArtifactState<T> Artifact)
    where T : IArtifact<T>;

/// <summary>All attempts of one job, oldest first, read from agents/&lt;job&gt;-*/. Counting dirs is the budget.</summary>
public sealed record StationLog<T>(IReadOnlyList<Attempt<T>> Attempts) where T : IArtifact<T>
{
    public Attempt<T>? Latest => Attempts.Count == 0 ? null : Attempts[^1];
    public int Spent<TReason>(int epoch) where TReason : AttemptReason =>
        Attempts.Count(a => a.Call.Epoch == epoch && a.Call.Reason is TReason);
}

public sealed record PullRequest(int Number, Uri Url, Sha Head, bool Open);
