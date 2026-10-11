using System.Text.Json.Nodes;

namespace Factory;

/// <summary>WORKFLOW.md front matter (DESIGN §11). Unknown keys are errors (UnmappedMemberHandling.Disallow).</summary>
public sealed record WorkflowConfig(
    int Trust, string Tracker, CommandSet Commands, string? VerifySkill, int WipLimit, IReadOnlyList<Routine>? Routines);

/// <summary>`{targets}` in TestTargeted takes the approved spec's TargetedTests, each shell-quoted by the factory.</summary>
public sealed record CommandSet(string Build, string Test, string TestTargeted, string? Lint, string? Run, string? ServicesUp);
public sealed record Routine(string Name, string Every, string Skill);

/// <summary>A parsed WORKFLOW.md, pinned to the trunk commit it was read from.</summary>
public sealed record Workflow(WorkflowConfig Config, string Body, string Commit, Digest Digest)
{
    /// <summary>
    /// Input is `git show fork:WORKFLOW.md`. Fallback when trunk has none: ~/.config/factory/workflows/owner/repo.md,
    /// pinned by digest and flagged by `status` as agent-writable.
    /// </summary>
    public static Workflow Parse(string markdown, string commit) => throw new NotImplementedException();
    public IReadOnlyList<GateKind> RequiredGates =>
        Config.Commands.Lint is null ? [GateKind.Build, GateKind.TestTargeted] : [GateKind.Build, GateKind.TestTargeted, GateKind.Lint];

    // TODO: TestTargeted.Replace("{targets}", string.Join(' ', targets.Select(ShellQuote))). Targets were charset-checked by SpecArtifact.
    public string GateCommand(GateKind gate, IReadOnlyList<string> targets) => throw new NotImplementedException();
}

/// <summary>~/.config/factory/bindings.yaml. Hand-written in step 1; `factory doctor` writes it from step 4.</summary>
public sealed record MachineBindings(
    GitHubApp GithubApp, string Spawn, TierMap Tiers, IReadOnlyDictionary<string, string> Repos, string RunsRoot, string SlicesRoot);

public sealed record GitHubApp(long Id, long Installation, string Slug, long BotUserId, string Key)
{
    public string BotLogin => $"{Slug}[bot]";
}

/// <summary>"harness/model" per tier, e.g. `standard: claude/sonnet`. Pinned into every call.json.</summary>
public sealed record TierMap(string Cheap, string Standard, string Strong);

/// <summary>
/// The strict YAML subset both config files use: block maps, block lists, `#` comments, and plain/'…'/"…" scalars.
/// Plain ints, bools and null become JSON numbers, bools and null (YAML 1.2 core schema). Anchors, tags, flow
/// collections, `|`/`>` and tabs are errors naming the line. The JsonObject then goes through FactoryJson, so typing
/// and unknown-key checks are STJ's, not ours.
/// ponytail: about 100 lines of hand parser. Upgrade: YamlDotNet with its static generator; any file valid here stays valid.
/// </summary>
public static class SubsetYaml
{
    public static JsonObject Parse(string yaml) => throw new NotImplementedException();
}

/// <summary>prompts/*.md are embedded resources, so binary version + template digest pin the brief.</summary>
public sealed record RenderedBrief(string Text, string Template, Digest TemplateDigest, IReadOnlyList<SkillPin> Skills);

public static class Briefs
{
    /// <summary>
    /// `{{name}}` substitution, no template engine. Invariants: the text starts with `[coordinator]` and the worker rule;
    /// the issue body is fenced as untrusted data; holdouts never appear; maintainer guidance appears verbatim; a retry
    /// or revision includes its problems or feedback; WORKFLOW.md's body is appended. Skills resolve to SKILL.md in
    /// the harness's own skill dir and are pinned by digest.
    /// </summary>
    public static RenderedBrief Render(AgentJob job, AttemptReason reason, Facts facts) => throw new NotImplementedException();
}

/// <summary>`--json` output of advance and status. Flat, so the coordinator can read it with jq or by eye.</summary>
public sealed record AdvanceReport(
    string Run, IReadOnlyList<string> Executed, string Halt, string? Ask, string? Url, string? Watch, ExitCode Exit);

/// <summary>The shell: observe → <see cref="Process.Next"/> → execute one step → observe again.</summary>
public sealed class Reconciler(RunRef run, RunDir dir, MachineBindings machine, IForge forge, IAgentRunner agents, Git git, AppTokens tokens)
{
    public const int MaxSteps = 40;   // more means a step made no observable progress: Failure, not a hang

    /// <summary>
    /// Writes nothing. Reads: forge item; comments → Timeline.From(…, BotLogin); git.Observe; WORKFLOW.md at the fork;
    /// attempt dirs (call.json, agents.Status for Running ones, Artifact.Read for Exited ones); gate files; PR by branch.
    /// </summary>
    public Task<Facts> Observe(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>
    /// One step, then one decisions.tsv line. Idempotent per step: Claim/PostSpec/PostNotice upsert by marker; StartAgent
    /// claims its attempt dir by mkdir and the binding adopts a live agent; RunGate writes by rename; Push is a no-op when
    /// Remote == Head; OpenPr looks the PR up by branch first.
    /// </summary>
    public Task Execute(Step step, Facts facts, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>
    /// dir.TryLock() (null ⇒ Busy), then up to MaxSteps: Observe; Next; SetFactoryLabel(Label); rewrite progress.md;
    /// Halt(OnAgent) with <paramref name="wait"/> ⇒ agents.Wait and loop; other Halt ⇒ return; else Execute and loop.
    /// Every agent start prints what, where, watch command and expected duration.
    /// </summary>
    public Task<AdvanceReport> Advance(bool wait, Action<string> say, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>The station table plus the next step: the text of `factory status` and of progress.md.</summary>
    public static string Describe(Facts facts, Step next) => throw new NotImplementedException();
}

/// <summary>
/// Hand-parsed: three verbs do not justify a parser package.
/// <code>
/// factory advance &lt;run&gt; [--wait] [--json]     the only command that moves a run
/// factory status  [&lt;run&gt;] [--json]           read-only, no lock; every run when none is given
/// factory token   [--read-only] [--git-credential get]
/// </code>
/// No approve, skip, force or goto: maintainer decisions are forge comments, and the next step is computed.
/// </summary>
public static class Cli
{
    public static async Task<int> Main(string[] args) => (int)await Run(args, CancellationToken.None);

    // TODO: args switch { ["advance", r, ..] ⇒ Advance; ["status", ..] ⇒ Status; ["token", ..] ⇒ Token; _ ⇒ Usage }
    static Task<ExitCode> Run(string[] args, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Composition root: bindings.yaml → GhForge, Git, AppTokens, and the IAgentRunner named by `spawn` (compiled in; AOT has no plugins).</summary>
    static Reconciler Compose(RunRef run) => throw new NotImplementedException();
    static Task<ExitCode> Advance(RunRef run, bool wait, bool json, CancellationToken ct) => throw new NotImplementedException();
    static Task<ExitCode> Status(RunRef? run, bool json, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Prints a token for GH_TOKEN, or speaks git's credential-helper protocol (username x-access-token).</summary>
    static Task<ExitCode> Token(TokenScope scope, bool gitCredential, CancellationToken ct) => throw new NotImplementedException();
}
