using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Factory;

/// <summary>
/// An agent's output type. The record IS the schema: <see cref="Artifact.Schema{T}"/> exports it for the harness
/// (`--json-schema` / `--output-schema`), and <see cref="Artifact.Read{T}"/> parses the reply into the same record.
/// </summary>
public interface IArtifact<TSelf> where TSelf : IArtifact<TSelf>
{
    static abstract JsonTypeInfo<TSelf> Json { get; }
    /// <summary>Cross-field rules JSON Schema cannot express. Empty means valid.</summary>
    IEnumerable<string> Problems() => [];
}

/// <summary>An agent returns this instead of finishing. It becomes a questionnaire on the issue (`to-questionnaire`).</summary>
public sealed record Escalation(string Reason, IReadOnlyList<Question> Questions);
public sealed record Question(string Text, IReadOnlyList<string> Options);

public enum TriageOutcome { ReadyForAgent, ReadyForHuman, NeedsInfo, WontFix }
public enum WorkClass { Feature, Bug, Perf, Refactor, Chore }
public enum Size { S, M, L }
public enum Door { TwoWay, OneWay }
public enum CheckKind { Mutation, Test, Inspection }

/// <summary>"Reproduces on main", generalized: evidence the premise holds on trunk. Recorded, not re-run, in step 1.</summary>
public sealed record PremiseCheck(string TrunkSha, string Command, string Observed, bool Holds);

/// <summary>Station 1. Read-only agent; follows `/triage` as steps, never labels or comments.</summary>
public sealed record TriageVerdict(
    TriageOutcome Outcome, WorkClass Class, Size Size, Door Door, IReadOnlyList<string> RiskFlags,
    PremiseCheck Premise, string? AlreadyBuilt, string? PriorRejection, string Reason,
    IReadOnlyList<string> QuestionsForReporter) : IArtifact<TriageVerdict>
{
    public static JsonTypeInfo<TriageVerdict> Json => FactoryJson.Default.TriageVerdict;

    // TODO: NeedsInfo ⇒ questions. ReadyForAgent ⇒ Premise.Holds, AlreadyBuilt and PriorRejection null.
    //       A risk flag of migration/auth/money/data-deletion/public-api ⇒ Door == OneWay.
    public IEnumerable<string> Problems() => throw new NotImplementedException();
}

/// <summary>What the maintainer approves. Its canonical digest is the approval key. Holdouts are not part of it.</summary>
public sealed record SpecBody(
    string Title, string Problem, IReadOnlyList<Criterion> Acceptance, IReadOnlyList<Ticket> Tickets,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyList<string> TargetedTests);  // fills `{targets}` in test_targeted; approved here, so the implementer cannot weaken its gate

public sealed record Criterion(string Id, string Statement, CheckKind Check, string How);
public sealed record Ticket(string Id, string Title, IReadOnlyList<string> BlockedBy);
public sealed record Holdout(string Name, string Scenario);

/// <summary>Station 2. Read-only agent (`/to-spec`, `/to-tickets`). Holdouts go to the run dir, never to the forge.</summary>
public sealed record SpecArtifact(SpecBody Spec, IReadOnlyList<Holdout> Holdouts, Escalation? Escalate) : IArtifact<SpecArtifact>
{
    public static JsonTypeInfo<SpecArtifact> Json => FactoryJson.Default.SpecArtifact;

    // TODO: unless escalating: ≥1 criterion, ticket and targeted test; ids unique; BlockedBy names known ids, acyclic;
    //       each TargetedTests entry matches ^[\w.~=!&|()\- ]+$ (it reaches a shell, quoted).
    public IEnumerable<string> Problems() => throw new NotImplementedException();
}

/// <summary>Station 3. Advisory only: commits, head and gate results are observed, never taken from this report.</summary>
public sealed record ImplementReport(string Summary, IReadOnlyList<CriterionNote> Criteria, Escalation? Escalate) : IArtifact<ImplementReport>
{
    public static JsonTypeInfo<ImplementReport> Json => FactoryJson.Default.ImplementReport;
}

public sealed record CriterionNote(string CriterionId, string How);

/// <summary>Station 4: `/deslop` in the worktree.</summary>
public sealed record CleanReport(string Summary) : IArtifact<CleanReport>
{
    public static JsonTypeInfo<CleanReport> Json => FactoryJson.Default.CleanReport;
}

/// <summary>Station 4: `/unslop`ed prose. The factory appends the footer: spec link and approver, gates at SHA, pins.</summary>
public sealed record PrDraft(string Title, string Summary, IReadOnlyList<string> KnownGaps) : IArtifact<PrDraft>
{
    public static JsonTypeInfo<PrDraft> Json => FactoryJson.Default.PrDraft;
}

/// <summary>One attempt's output, as read. Produced only by <see cref="Artifact.Read{T}"/>.</summary>
public abstract record ArtifactState<T>
{
    private ArtifactState() { }
    public sealed record Missing : ArtifactState<T>;
    public sealed record Invalid(IReadOnlyList<string> Problems) : ArtifactState<T>;
    public sealed record Valid(T Value, Digest Digest) : ArtifactState<T>;
}

public static class Artifact
{
    /// <summary>Checked: every member required, nullable as ["string","null"], additionalProperties false (Codex strict mode accepts this).</summary>
    // TODO: T.Json.GetJsonSchemaAsNode(new() { TreatNullObliviousAsNonNullable = true }).ToJsonString(). Snapshot-tested.
    public static string Schema<T>() where T : IArtifact<T> => throw new NotImplementedException();
    /// <summary>
    /// The only door from agent text to a domain value. No file ⇒ Missing; JsonException or Problems() ⇒ Invalid;
    /// else Valid, with the digest of the re-serialized value (canonical), not of the agent's bytes.
    /// </summary>
    public static ArtifactState<T> Read<T>(string outputPath) where T : IArtifact<T> => throw new NotImplementedException();
}

/// <summary>Every type that crosses a file or process boundary. AOT has no reflection serializer.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TriageVerdict))]
[JsonSerializable(typeof(SpecArtifact))]
[JsonSerializable(typeof(ImplementReport))]
[JsonSerializable(typeof(CleanReport))]
[JsonSerializable(typeof(PrDraft))]
[JsonSerializable(typeof(GateResult))]
[JsonSerializable(typeof(AttemptCall))]
[JsonSerializable(typeof(AgentHandle))]
[JsonSerializable(typeof(AdvanceReport))]
[JsonSerializable(typeof(WorkflowConfig))]
[JsonSerializable(typeof(MachineBindings))]
public sealed partial class FactoryJson : JsonSerializerContext;
