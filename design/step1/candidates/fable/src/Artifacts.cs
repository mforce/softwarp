using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Factory;

/// <summary>
/// An agent call is a function: brief in, artifact out (DESIGN §4). Every artifact is ONE record returned as the
/// harness's structured output, never a file the agent writes. The record is both the schema (exported by
/// <see cref="Artifacts.Schema"/>) and the type the factory parses; spec.md is rendered from it.
/// <see cref="Check"/> holds the semantic rules a JSON schema cannot say.
/// </summary>
public interface IArtifact
{
    IEnumerable<string> Check();
}

public abstract record Validated<T> where T : IArtifact;
public sealed record Valid<T>(T Value) : Validated<T> where T : IArtifact;
public sealed record Invalid<T>(IReadOnlyList<string> Errors) : Validated<T> where T : IArtifact;

public enum Verdict { ReadyForAgent, ReadyForHuman, NeedsInfo, Wontfix }
public enum WorkClass { Bug, Feature, Perf, Chore }
public enum Size { S, M, L }
public enum Door { TwoWay, OneWay }

/// <summary>Station 1 output. A false premise stops here: Ready requires ReproducedOnMain and no redundancy.</summary>
public sealed record TriageVerdict(Verdict Verdict, string Reason, WorkClass Class, Size Size, Door Door, string[] RiskFlags,
    bool ReproducedOnMain, string Reproduction, string[] AlreadyCoveredBy) : IArtifact
{
    public IEnumerable<string> Check()
    {
        // TODO: Ready && !ReproducedOnMain -> "ready without reproduction"; Ready && AlreadyCoveredBy.Any() -> "ready but redundant"
        throw new NotImplementedException();
    }
}

public sealed record Criterion(string Id, string Text, string HowToCheck);
public sealed record Ticket(string Id, string Title, string Body, string[] BlockedBy);
/// <summary>Written to `holdout/` outside the implementer's worktree; the implementer brief never sees them.</summary>
public sealed record Holdout(string Name, string Scenario, string Expected);

/// <summary>Station 2 output. <see cref="Render"/> is the spec.md the maintainer reads; <see cref="Digest"/> is what they approve.</summary>
public sealed record SpecArtifact(string Title, string Problem, string Approach, Criterion[] AcceptanceCriteria, string[] OutOfScope,
    Ticket[] Tickets, Holdout[] Holdouts) : IArtifact
{
    public IEnumerable<string> Check()
    {
        // TODO: >=1 criterion; criterion ids unique; every BlockedBy id exists and the ticket graph has no cycle; >=1 holdout
        throw new NotImplementedException();
    }

    /// <summary>Deterministic markdown. Same record, same bytes, same digest.</summary>
    public string Render() => throw new NotImplementedException();

    /// <summary>First 12 hex of SHA-256 over <see cref="Render"/>. Shown in the posted comment; quoted back by the maintainer.</summary>
    public string Digest() => throw new NotImplementedException();
}

public sealed record CriterionStatus(string Id, bool Met, string Evidence);

/// <summary>
/// Station 3 output: the implementer's final structured output after it ran deslop and unslopped its prose.
/// Commits are not here; git is the source for those. Publish renders the PR body from this plus the spec link.
/// </summary>
public sealed record ImplementReport(string PrTitle, string Summary, string TestPlan, CriterionStatus[] Criteria, string[] KnownGaps) : IArtifact
{
    public IEnumerable<string> Check()
    {
        // TODO: every spec criterion id appears exactly once; PrTitle <= 72 chars
        throw new NotImplementedException();
    }
}

/// <summary>Front matter of the repo's WORKFLOW.md; see <see cref="Workflow"/>.</summary>
public sealed record WorkflowMeta(int Trust, string Tracker, Commands Commands, string? VerifySkill, int WipLimit);

/// <summary>Source-generated context: the only JSON path in the binary (Native AOT forbids reflection-based JSON).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(RunRecord))]
[JsonSerializable(typeof(GateResult))]
[JsonSerializable(typeof(MachineConfig))]
[JsonSerializable(typeof(WorkflowMeta))]
[JsonSerializable(typeof(TriageVerdict))]
[JsonSerializable(typeof(SpecArtifact))]
[JsonSerializable(typeof(ImplementReport))]
internal sealed partial class FactoryJson : JsonSerializerContext;

public static class Artifacts
{
    /// <summary>The schema handed to `claude --json-schema` / `codex --output-schema` / `paseo --output-schema`.</summary>
    public static JsonNode Schema<T>(JsonTypeInfo<T> type) =>
        type.GetJsonSchemaAsNode(new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });

    /// <summary>Shape (deserialize) then meaning (<see cref="IArtifact.Check"/>). Both failures are Invalid with the reasons.</summary>
    public static Validated<T> Parse<T>(JsonNode raw, JsonTypeInfo<T> type) where T : IArtifact => throw new NotImplementedException();

    public static JsonTypeInfo<TriageVerdict> Triage => FactoryJson.Default.TriageVerdict;
    public static JsonTypeInfo<SpecArtifact> Spec => FactoryJson.Default.SpecArtifact;
    public static JsonTypeInfo<ImplementReport> Implement => FactoryJson.Default.ImplementReport;
}
