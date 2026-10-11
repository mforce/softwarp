namespace Factory;

/// <summary>Tracker view of the issue. Labels are read to detect a foreign claim, never to decide the station.</summary>
public sealed record IssueState(string Title, string Body, bool Open, string[] Labels, long? RunCommentId);

public sealed record Comment(long Id, string AuthorLogin, bool AuthorIsBot, string Body, DateTimeOffset At);

public enum ApprovalKind { Approved, Revise }

/// <summary>
/// The maintainer's verdict on one spec digest. Counts only when the comment author is the configured maintainer login,
/// is not a bot, and quotes the digest of the spec currently on disk. Agents hold only the App token, which authors as
/// `<slug>[bot]` and nothing else, so an approval cannot be forged from inside the factory.
/// </summary>
public sealed record Approval(ApprovalKind Kind, string Digest, long CommentId, string Author, string? Feedback)
{
    /// <summary>Newest matching comment wins. Pattern on its own line: `approve <digest>` or `revise <digest>: <feedback>`.</summary>
    public static Approval? Find(IReadOnlyList<Comment> comments, string maintainerLogin, string digest) => throw new NotImplementedException();
}

public sealed record SpecState(SpecArtifact Spec, string Digest, long? PostedCommentId, bool HoldoutsWritten);

public enum ChildStatus { Running, Finished, Failed, Stopped }

/// <summary>Live status of the newest child, joined from children.tsv and the spawner.</summary>
public sealed record ChildState(Child Child, ChildStatus Status, TimeSpan Elapsed, bool ArtifactCollected);

/// <summary>Git facts about the run branch in the worktree. Null when the worktree does not exist yet.</summary>
public sealed record BranchState(string Head, int Ahead, bool Clean);

public sealed record PullRef(int Number, string Url, string HeadSha);

/// <summary>
/// Everything the planner may look at, read fresh at the start of each loop iteration. Nothing in here is a stored
/// "current station": the station is a function of these facts (derive, do not sync). Reading has no side effects,
/// so Observe can run as often as `status` is typed.
/// </summary>
public sealed record Observation(
    RunRecord? Run,
    IssueState Issue,
    Validated<TriageVerdict>? Triage,
    SpecState? Spec,
    Approval? Approval,
    Validated<ImplementReport>? Report,
    ChildState? Child,
    BranchState? Branch,
    IReadOnlyList<GateResult> Gates,
    PullRef? Pr,
    IReadOnlyList<Child> Children,
    IReadOnlyList<Decision> Log,
    DateTimeOffset Now)
{
    public int Attempts(Station s) => Children.Count(c => c.Station == s);
    public int Bounces => Log.Count(d => d.Move == nameof(Bounce));
    public int Revisions => Log.Count(d => d.Move == nameof(Spawn) && d.Station == Station.Spec) - 1;
    public IEnumerable<GateResult> GatesAt(string sha) => Gates.Where(g => g.Sha == sha);
}

/// <summary>The one move the driver may make next. A closed set; the shell switches over it with no default arm.</summary>
public abstract record Move;
public sealed record Claim : Move;
public sealed record Spawn(Station Station, string? Feedback) : Move;
public sealed record Wait(Child Child, TimeSpan Elapsed) : Move;
public sealed record Replace(Child Stalled) : Move;
public sealed record Collect(Child Child) : Move;
public sealed record PostSpec(string Digest) : Move;
public sealed record RunGates(string Sha) : Move;
public sealed record Bounce(Child Child, GateResult Failed) : Move;
public sealed record Publish(string Sha) : Move;
public sealed record Stopped(Verdict Verdict, string Reason) : Move;
public sealed record Blocked(string Reason) : Move;
public sealed record Escalate(Station Station, string Reason) : Move;
public sealed record Done(PullRef Pr) : Move;

/// <summary>
/// The state machine. Pure: Observation in, Move out. No I/O, no clock reads, no config.
/// There is no "transition" verb for a driver to request, so there is nothing to refuse: the only legal move is the
/// one this table returns. Skipping approval is not something the CLI can be asked to do.
/// </summary>
public static class Planner
{
    public const int MaxAttempts = 2;

    public static Move Next(Observation o)
    {
        // TODO, first row that holds wins:
        //  1  Run == null                                        -> Claim
        //  2  Child.Status == Running && !Stalled(o)              -> Wait(child, elapsed)
        //  3  Child.Status == Running && Stalled(o)               -> Replace(child)
        //  4  Child.Status != Running && !ArtifactCollected       -> Collect(child)          (Failed/Stopped collect an empty object)
        //  5  Triage is null or Invalid:
        //        Attempts(Triage) >= MaxAttempts                 -> Escalate(Triage, "artifact invalid twice: " + errors)
        //        else                                            -> Spawn(Triage, errors)    (errors feed the retry brief)
        //  6  Triage.Verdict != ReadyForAgent                    -> Stopped(verdict, reason)
        //  7  Spec == null (or Invalid):
        //        Attempts(Spec) - Revisions >= MaxAttempts       -> Escalate(Spec, "spec invalid twice")
        //        else                                            -> Spawn(Spec, feedback-or-errors)
        //  8  Spec.PostedCommentId == null || !HoldoutsWritten    -> PostSpec(digest)
        //  9  Approval == null                                   -> Blocked("approve " + digest)
        // 10  Approval.Kind == Revise:
        //        Revisions >= MaxAttempts                        -> Escalate(Spec, "revised twice")
        //        else                                            -> Spawn(Spec, feedback)
        // 11  Branch == null || Branch.Ahead == 0 || Report == null:
        //        Attempts(Implement) >= MaxAttempts              -> Escalate(Implement, "no commits or no report after two implementers")
        //        else                                            -> Spawn(Implement, null)
        // 12  GatesAt(head) lacks build or test_targeted         -> RunGates(head)
        // 13  GatesAt(head) has a red gate:
        //        Bounces >= MaxAttempts                          -> Escalate(Implement, "gates red after two fixes")
        //        else                                            -> Bounce(child, red)
        // 14  Pr == null                                         -> Publish(head)
        // 15                                                     -> Done(pr)
        // Row 2 before 5: a running triage child never gets a sibling. Row 12 before 14: Publish never pushes a head
        // whose gates were not recorded. A Bounce makes a new head, so row 12 re-runs the gates on the fix.
        throw new NotImplementedException();
    }

    /// <summary>Stall = past Expected with no side effect since spawn. For an implementer, head == HeadAtSpawn; read-only stations have no side effect but the artifact, so Elapsed alone decides.</summary>
    public static bool Stalled(Observation o) => throw new NotImplementedException();
}
