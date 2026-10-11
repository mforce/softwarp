using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Factory;

/// <summary>
/// `~/.factory/runs/<owner>/<name>/<issue>/`. Every file is written whole once or appended; nothing is edited in place,
/// so a crash between two writes leaves a readable directory and the next Observe sees exactly what happened.
///   run.json       claim + pins                    triage.json / spec.json / implement.json   raw agent output, kept even if invalid
///   spec.md        rendered SpecArtifact           holdout/<name>.md                          outside the worktree
///   children.tsv   one row per spawn               decisions.tsv                              one row per executed Move
///   gates.jsonl    one row per gate run            metrics.jsonl                              per-move cost (build step 5 fills it)
///   progress.md    rendered Status, for handoff    children/<id>.log                          agent stdout
/// </summary>
public sealed class RunDir(RunId id)
{
    public RunId Id { get; } = id;
    public string Path => Id.Dir;
    public bool Exists => Directory.Exists(Path);

    public RunRecord? ReadRun() => throw new NotImplementedException();
    public void WriteRun(RunRecord run) => throw new NotImplementedException();
    public void WriteArtifact(Station station, JsonNode raw) => throw new NotImplementedException();
    public Validated<T>? ReadArtifact<T>(Station station, JsonTypeInfo<T> type) where T : IArtifact => throw new NotImplementedException();
    public void WriteSpecAndHoldouts(SpecArtifact spec) => throw new NotImplementedException();
    public bool HoldoutsWritten(SpecArtifact spec) => throw new NotImplementedException();
    public IReadOnlyList<Child> Children() => throw new NotImplementedException();
    public void AppendChild(Child child) => throw new NotImplementedException();
    public IReadOnlyList<Decision> Decisions() => throw new NotImplementedException();
    public void AppendDecision(Decision decision) => throw new NotImplementedException();
    public IReadOnlyList<GateResult> Gates() => throw new NotImplementedException();
    public void AppendGate(GateResult gate) => throw new NotImplementedException();
    public string ChildLog(string childId) => System.IO.Path.Combine(Path, "children", childId + ".log");
    public void WriteProgress(string markdown) => throw new NotImplementedException();
}

/// <summary>
/// Brief templates are embedded resources (`Briefs/<station>.md`): the binary's version pins them and <see cref="Version"/>
/// is their content hash. Templates carry the slash command for user-only skills (`/triage #1223`, `/to-spec`), which
/// the grounding shows start fine headless.
/// </summary>
public static class Briefs
{
    public static string Version(Station station) => throw new NotImplementedException();

    /// <summary>Variables: issue, repo, workflow body, spec.md (station 3), feedback or validation errors (retries), worktree path.</summary>
    public static string Render(Station station, RunRecord run, IssueState issue, Workflow workflow, SpecArtifact? spec, string? feedback) =>
        throw new NotImplementedException();

    /// <summary>PR body: spec link and digest, criteria table from the report, test plan, known gaps, pins footer. Deterministic.</summary>
    public static string PullRequestBody(RunRecord run, SpecState spec, ImplementReport report) => throw new NotImplementedException();

    /// <summary>Escalation as a short questionnaire, not a thread (DESIGN §7).</summary>
    public static string Questionnaire(RunRecord run, string reason, Observation o) => throw new NotImplementedException();
}
