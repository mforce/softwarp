using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Factory;

/// <summary>One record is three things: the schema the agent must follow (exported from <see cref="Json"/>), the type the
/// factory parses, and the ledger body. <see cref="Home"/> ties each artifact to exactly one station.
/// <see cref="Problems"/> holds the checks a schema cannot state. Gates that need git or a shell live in the Engine.</summary>
public interface IArtifact<TSelf> where TSelf : IArtifact<TSelf>
{
    static abstract Station Home { get; }
    static abstract JsonTypeInfo<TSelf> Json { get; }
    IEnumerable<string> Problems();
}

public readonly record struct Parsed<T>(T? Value, IReadOnlyList<string> Problems);

public static class Artifacts
{
    public static JsonNode SchemaFor<T>() where T : IArtifact<T> => JsonSchemaExporter.GetJsonSchemaAsNode(T.Json);

    /// <summary>Malformed JSON, schema violations (nullable annotations and required members are enforced) and
    /// <see cref="IArtifact{TSelf}.Problems"/> all come back as strings the retry brief can quote.</summary>
    public static Parsed<T> Parse<T>(string json) where T : IArtifact<T> => throw new NotImplementedException();
}

// ---- Station 1 -------------------------------------------------------------------------------------------------

public enum WorkClass { Feature, Bug, Perf, Chore }
public enum Size { S, M, L }
public enum Door { TwoWay, OneWay }
public enum TriageVerdict { ReadyForAgent, ReadyForHuman, NeedsInfo, WontFix }
public enum ReproOutcome { Reproduced, NotReproduced, NotApplicable }

/// <summary>Command is re-run by the Engine on a trunk checkout when present. An exit code is evidence, a claim is not.</summary>
public sealed record Repro(ReproOutcome Outcome, string? Command, string Evidence);

public sealed record TriageReport(
    WorkClass Class, Size Size, Door Door, IReadOnlyList<string> RiskFlags,
    string? AlreadyBuiltEvidence, string? OutOfScopeMatch, Repro Repro,
    TriageVerdict Verdict, string Reason, string? Brief) : IArtifact<TriageReport>
{
    public static Station Home => Station.Triage;
    public static JsonTypeInfo<TriageReport> Json => FactoryJson.Strict.TriageReport;
    /// <summary>TODO: ReadyForAgent needs a Brief and Repro.Outcome != NotReproduced. Any OneWay door or risk flag
    /// forces a human to look at the spec; the Machine reads that from Door, not from the agent's Verdict.</summary>
    public IEnumerable<string> Problems() => throw new NotImplementedException();
}

// ---- Station 2 -------------------------------------------------------------------------------------------------

public sealed record Criterion(string Id, string Text);

/// <summary>TestTargets fill {paths} in the repo's test_targeted command. They are fixed here, before the
/// maintainer approves, so an implementer cannot pick a filter that matches nothing.</summary>
public sealed record Ticket(string Id, string Title, IReadOnlyList<string> BlockedBy, IReadOnlyList<string> TestTargets);

/// <summary>Everything the implementer may see. Holdouts are a sibling of Spec, not a member, so
/// <see cref="ImplementInput"/> cannot carry them even by mistake.</summary>
public sealed record Spec(string Markdown, IReadOnlyList<Criterion> Criteria, IReadOnlyList<Ticket> Tickets);

public sealed record Holdout(string Id, string Scenario);

public sealed record SpecBundle(Spec Spec, IReadOnlyList<Holdout> Holdouts) : IArtifact<SpecBundle>
{
    public static Station Home => Station.Spec;
    public static JsonTypeInfo<SpecBundle> Json => FactoryJson.Strict.SpecBundle;
    /// <summary>TODO: unique ids; >= 1 criterion; every ticket has >= 1 TestTarget; BlockedBy refers to known
    /// tickets and is acyclic (Kahn); Markdown has the required headings.</summary>
    public IEnumerable<string> Problems() => throw new NotImplementedException();
}

/// <summary>The text posted to the issue, and the thing the maintainer approves. Holdouts appear as id + SHA-256
/// only, so an agent that reads the issue learns nothing. The full text goes to holdout/ in the run dir.</summary>
public static class SpecReview
{
    public static string Render(SpecBundle bundle) => throw new NotImplementedException();
    /// <summary>Digest of Render(). Computed fresh on every advance: edit the ledger after approval and the approval stops matching.</summary>
    public static Digest DigestOf(SpecBundle bundle) => Digest.Of(Render(bundle));
}

// ---- Stations 3 and 4 ------------------------------------------------------------------------------------------

/// <summary>HeadSha must equal the workspace head at ingest. ChangedPaths must be a subset of the real diff.</summary>
public sealed record ImplementReport(string HeadSha, IReadOnlyList<string> ChangedPaths, string Summary, IReadOnlyList<string> Deviations)
    : IArtifact<ImplementReport>
{
    public static Station Home => Station.Implement;
    public static JsonTypeInfo<ImplementReport> Json => FactoryJson.Strict.ImplementReport;
    public IEnumerable<string> Problems() => throw new NotImplementedException();
}

/// <summary>The publisher cleans the code (commits it) and writes the PR text. It cannot push. HeadSha is the cleaned head.</summary>
public sealed record PublishDraft(string HeadSha, string PrTitle, string PrBody) : IArtifact<PublishDraft>
{
    public static Station Home => Station.Publish;
    public static JsonTypeInfo<PublishDraft> Json => FactoryJson.Strict.PublishDraft;
    public IEnumerable<string> Problems() => throw new NotImplementedException();
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TriageReport))]
[JsonSerializable(typeof(SpecBundle))]
[JsonSerializable(typeof(ImplementReport))]
[JsonSerializable(typeof(PublishDraft))]
[JsonSerializable(typeof(GateReceipt))]
[JsonSerializable(typeof(RunPins))]
[JsonSerializable(typeof(AdvanceResult))]
internal sealed partial class FactoryJson : JsonSerializerContext
{
    /// <summary>Verified on .NET 10: with only the attribute options, {"class":"feature"} parses into a TriageReport with null members.
    /// These two options (not settable on the attribute) make a missing or null member a JsonException, which becomes a retry
    /// problem. Nullable members are therefore REQUIRED in the schema: agents must write an explicit null, as codex strict mode wants.</summary>
    public static FactoryJson Strict => field ??= new(new JsonSerializerOptions(Default.Options!)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    });
}
