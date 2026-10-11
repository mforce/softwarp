namespace Factory;

public enum AgentJob { Triage, Spec, Implement, Clean, PrBody }
public enum Tier { Cheap, Standard, Strong }

/// <summary>Least privilege per job. Bindings translate it: token scope, codex `-s`, claude permission settings.</summary>
public enum AgentRole { ReadOnly, WorktreeWrite }

/// <summary>The only place a job's station, template, tier, role, runtime and skills are set.</summary>
public sealed record JobSpec(int Station, string Template, Tier Tier, AgentRole Role, TimeSpan Expected, IReadOnlyList<string> Skills);

public static class Jobs
{
    public static JobSpec Of(AgentJob job) => job switch
    {
        AgentJob.Triage => new(1, "triage.md", Tier.Standard, AgentRole.ReadOnly, TimeSpan.FromMinutes(10), ["triage"]),
        AgentJob.Spec => new(2, "spec.md", Tier.Strong, AgentRole.ReadOnly, TimeSpan.FromMinutes(25), ["to-spec", "to-tickets"]),
        AgentJob.Implement => new(3, "implement.md", Tier.Strong, AgentRole.WorktreeWrite, TimeSpan.FromMinutes(60), ["tdd"]),
        AgentJob.Clean => new(4, "clean.md", Tier.Standard, AgentRole.WorktreeWrite, TimeSpan.FromMinutes(15), ["pstack:deslop"]),
        AgentJob.PrBody => new(4, "prbody.md", Tier.Standard, AgentRole.ReadOnly, TimeSpan.FromMinutes(10), ["pstack:unslop", "pr"]),
        _ => throw new ArgumentOutOfRangeException(nameof(job)),
    };
}

public enum Ask { ApproveSpec, AnswerNotice }

public abstract record HaltReason
{
    private HaltReason() { }
    public sealed record OnAgent(AttemptKey Attempt, AgentHandle Handle) : HaltReason;
    public sealed record OnMaintainer(Ask Ask, Uri Where) : HaltReason;
    public sealed record Refused(string Why) : HaltReason;
    public sealed record Published(PullRequest Pr) : HaltReason;
}

/// <summary>
/// One side effect, or a halt. Invariants: every non-halt step changes the observed facts, so Next never returns it
/// twice in a row (the progress property, tested with fakes). Every step is safe to repeat after a crash.
/// </summary>
public abstract record Step
{
    private Step() { }
    /// <summary>Post the claim marker and write item.json. Station 0's gate: no claim marker without a local run dir.</summary>
    public sealed record Claim : Step;
    /// <summary>`git worktree add -b factory/n ~/.factory-slices/&lt;repo&gt;/&lt;n&gt; origin/main`.</summary>
    public sealed record CreateWorkspace : Step;
    /// <summary>mkdir the attempt dir → render brief and pins → call.json → binding.Start → handle.json.</summary>
    public sealed record StartAgent(AgentJob Job, AttemptReason Reason) : Step;
    /// <summary>Write holdout/*.md, then post the rendered SpecBody, its marker and the exact approve line.</summary>
    public sealed record PostSpec(Digest Spec) : Step;
    /// <summary>Questionnaire comment. Resolved only by a maintainer `/factory resume`, which opens the next epoch.</summary>
    public sealed record PostNotice(NoticeKind Kind, Escalation Body) : Step;
    /// <summary>The branch has no commits and trunk moved: move it to trunk before implementing (nothing to lose).</summary>
    public sealed record RefreshBase(Sha Trunk) : Step;
    public sealed record RunGate(Sha Head, GateKind Gate) : Step;
    /// <summary>Deslop broke a gate: reset the unpushed branch to the pre-clean SHA and publish without it.</summary>
    public sealed record DiscardClean(Sha KeepAt) : Step;
    public sealed record Push(Sha Head) : Step;
    public sealed record OpenPr(Sha Head) : Step;
    public sealed record Halt(HaltReason Reason) : Step;
}

public static class Process
{
    /// <summary>
    /// The state machine. Pure: same facts, same step. Each station returns null once its exit gate holds, so the
    /// first non-null step is the next thing to do. The CLI cannot name a step, so no invalid one can be requested.
    /// </summary>
    public static Step Next(Facts f) =>
        Intake(f) ?? Triage(f) ?? Spec(f) ?? Implement(f) ?? Publish(f)
        ?? new Step.Halt(new HaltReason.Published(f.Pr!));

    // TODO: !Item.Open ⇒ Refused("issue closed"). Claim != null && !RunDirExists ⇒ Refused("claimed by " + host).
    //       OpenNotice ⇒ Halt(OnMaintainer(AnswerNotice, notice.Url))  — here, so a notice pauses every station.
    //       Claim is null ⇒ Claim. Workspace is null ⇒ CreateWorkspace. Else null.
    static Step? Intake(Facts f) => throw new NotImplementedException();

    // TODO: RunAgent(f, f.Triage, AgentJob.Triage) ?? switch on the accepted verdict's Outcome:
    //       ReadyForAgent ⇒ null.
    //       verdict attempt's Epoch < Timeline.Epoch (the maintainer resumed after the notice):
    //         NeedsInfo ⇒ StartAgent(Triage, Revision(guidance)); ReadyForHuman/WontFix ⇒ null (maintainer overrode).
    //       NeedsInfo ⇒ PostNotice(NeedsInfo, questions). WontFix ⇒ PostNotice(WontFix, reason). ReadyForHuman ⇒ PostNotice(Escalation, reason).
    static Step? Triage(Facts f) => throw new NotImplementedException();

    // TODO: RunAgent(f, f.Spec, AgentJob.Spec) ?? with d = accepted digest:
    //       Timeline.LastSpec?.Spec != d ⇒ PostSpec(d)
    //       PendingRevisions.Any ⇒ Spent<Revision>(epoch) < 2 ? StartAgent(Spec, Revision(text)) : PostNotice(Escalation, …)
    //       ApprovalOf(d) is null ⇒ Halt(OnMaintainer(ApproveSpec, LastSpec.Url))
    //       else null.
    static Step? Spec(Facts f) => throw new NotImplementedException();

    // TODO: ws = f.Workspace!; head = ws.Head
    //       no implement attempt && !ws.HasCommits && ws.Fork != ws.Trunk ⇒ RefreshBase(ws.Trunk)
    //       RunAgent(f, f.Implement, AgentJob.Implement) ?? …
    //       !ws.HasCommits / ws.Dirty ⇒ Revise("no commits" / "uncommitted changes")
    //       MissingGate(f, head) is { } g ⇒ RunGate(head, g)
    //       FailedGate(f, head) is { } bad ⇒ Revise(tail of bad.LogPath)
    //       else null.       Revise(x) = Spent<Revision>(epoch) < 2 ? StartAgent(Implement, Revision(x)) : PostNotice(Escalation, …)
    static Step? Implement(Facts f) => throw new NotImplementedException();

    // TODO: ws = f.Workspace!; head = ws.Head; clean = f.Clean.Latest
    //       clean is null ⇒ StartAgent(Clean, Initial)                (call.HeadAtStart = head)
    //       RunAgent(f, f.Clean, AgentJob.Clean) ?? …               (Invalid twice ⇒ DiscardClean, never escalate)
    //       MissingGate(f, head) is { } g ⇒ RunGate(head, g)
    //       FailedGate(f, head) is not null ⇒ DiscardClean(clean.HeadAtStart)   (gates there already passed)
    //       ws.Remote != head ⇒ Push(head)
    //       PrBody latest missing or HeadAtStart != head ⇒ StartAgent(PrBody, …) via RunAgent
    //       f.Pr is null ⇒ OpenPr(head)
    //       else null ⇒ Published.
    static Step? Publish(Facts f) => throw new NotImplementedException();
    /// <summary>
    /// The attempt policy shared by every agent job. Null when the latest attempt is Valid and not escalating.
    /// <code>
    /// no attempt                                ⇒ StartAgent(job, Initial)
    /// Running                                   ⇒ Halt(OnAgent)
    /// Lost, Missing or Invalid                  ⇒ Spent&lt;RetryInvalid&gt;(epoch) &lt; 1 ? StartAgent(job, RetryInvalid(problems)) : PostNotice(Escalation)
    /// Valid, escalating, from an earlier epoch  ⇒ StartAgent(job, Revision(guidance))   (the maintainer answered)
    /// Valid, escalating                         ⇒ PostNotice(Escalation, value.Escalate)
    /// Valid                                     ⇒ null                                (whatever the epoch: a resume never redoes finished work)
    /// </code>
    /// </summary>
    static Step? RunAgent<T>(Facts f, StationLog<T> log, AgentJob job) where T : IArtifact<T> => throw new NotImplementedException();
    static GateKind? MissingGate(Facts f, Sha head) =>
        f.Workflow!.RequiredGates.Cast<GateKind?>().FirstOrDefault(g => !f.Gates.Any(r => r.Sha == head && r.Gate == g));
    static GateResult? FailedGate(Facts f, Sha head) => f.Gates.FirstOrDefault(r => r.Sha == head && !r.Passed);
    /// <summary>
    /// The one factory:* label the issue should carry now; written after every Next and never read back.
    /// Published ⇒ review · notice NeedsInfo ⇒ needs-info · WontFix ⇒ wontfix · Escalation or awaiting approval ⇒ needs-human ·
    /// claimed ⇒ active · else none.
    /// </summary>
    public static string? Label(Facts f, Step next) => throw new NotImplementedException();
    public static ExitCode ExitCodeOf(HaltReason h) => h switch
    {
        HaltReason.OnAgent => ExitCode.WaitingOnAgent,
        HaltReason.OnMaintainer => ExitCode.WaitingOnMaintainer,
        HaltReason.Refused => ExitCode.Refused,
        HaltReason.Published => ExitCode.Ok,
        _ => throw new ArgumentOutOfRangeException(nameof(h)),
    };
}
