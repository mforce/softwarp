namespace Factory;

/// <summary>WORKFLOW.md front matter. Read from the PINNED commit on origin/main (IWorkspaces.ReadAtCommit),
/// never from a worktree, so an implementer's own PR cannot change the commands or the trust rung that judge it.
/// {paths} is mandatory in TestTargeted.</summary>
public sealed record WorkflowSettings(
    int Trust, string Tracker, Commands Commands, string? VerifySkill, int WipLimit,
    string Guidance, IReadOnlyDictionary<string, string> Unrecognized);

public sealed record Commands(string Build, string Test, string TestTargeted, string? Lint, string? Run, string? ServicesUp);

/// <param name="Line">1-based line in the file, so the error names the fix.</param>
public sealed class WorkflowException(int line, string message) : Exception($"line {line}: {message}");

public static class Workflow
{
    /// <summary>Symphony format: "---", YAML, "---", then a markdown body that becomes Guidance.
    /// TODO fail closed on anything outside the subset (anchors, flow style, tabs, duplicate keys). Unknown top-level keys
    /// (routines, ...) are kept in Unrecognized and shown by `factory status`, not rejected, so later build steps can add keys.</summary>
    public static WorkflowSettings Parse(string fileText) => throw new NotImplementedException();
}

/// <summary>Native AOT has no reflection-based YAML, and a static-generator dependency is a lot of trust surface for
/// about 15 lines of config. The accepted subset is: nested maps by 2-space indent, "- " lists of scalars or maps, plain or
/// quoted scalars, and "|" literal blocks (multi-line commands). One ~120-line parser reads WORKFLOW.md and bindings.yaml.</summary>
internal static class MiniYaml
{
    internal abstract record Node(int Line);
    internal sealed record Scalar(string Value, int Line) : Node(Line);
    internal sealed record Map(IReadOnlyDictionary<string, Node> Items, int Line) : Node(Line);
    internal sealed record Seq(IReadOnlyList<Node> Items, int Line) : Node(Line);
    internal static Map Parse(string text) => throw new NotImplementedException();
}

/// <summary>~/.config/factory/bindings.yaml. `factory doctor` will generate it later. Step 1 reads a hand-written one.
/// Maintainers lives here, not in the repo's WORKFLOW.md, because agents can edit the repo.</summary>
public sealed record MachineConfig(
    IReadOnlySet<string> Maintainers, long AppId, long InstallationId, string AppSlug, string PemPath,
    string Spawner, IReadOnlyDictionary<ModelTier, string> TierModels, string? ShellShim,
    string FactoryHome, string SlicesRoot)
{
    public static MachineConfig Load(string path) => throw new NotImplementedException();
}

/// <summary>Briefs are embedded resources: prompts/&lt;station&gt;.md with {{name}} holes. The template SHA-256 is the version,
/// so a pin cannot go out of date with the file (derive, don't sync). Inputs are typed per station.</summary>
public abstract record BriefInput(IssueSnapshot Issue, string? Feedback);
public sealed record TriageInput(IssueSnapshot Issue, string? Feedback) : BriefInput(Issue, Feedback);
public sealed record SpecInput(IssueSnapshot Issue, TriageReport Triage, string? Feedback) : BriefInput(Issue, Feedback);
/// <summary>Takes <see cref="Spec"/>, not SpecBundle: no holdout can reach the implementer's brief.</summary>
public sealed record ImplementInput(IssueSnapshot Issue, Spec Spec, WorkflowSettings Workflow, string? Feedback) : BriefInput(Issue, Feedback);
public sealed record PublishInput(IssueSnapshot Issue, Spec Spec, ImplementReport Report, WorkflowSettings Workflow, string? Feedback) : BriefInput(Issue, Feedback);

public static class Briefs
{
    /// <summary>TODO the template begins with the slash command that starts the skill (e.g. "/triage #{{issue}}"). Skills marked
    /// user-only still run under `claude -p` / `codex exec` (grounding.md). Unfilled holes throw.</summary>
    public static RenderedBrief Render(BriefInput input) => throw new NotImplementedException();
}
