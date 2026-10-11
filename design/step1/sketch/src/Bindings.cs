using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Softwarp.Bindings;

/// <summary>Forge HTTP and token transport stay inside these compiled implementations.</summary>
public static class GitHubBinding
{
    public static IIdentity AppIdentity(string appId, string installationId, Uri signingBroker)
        => throw new NotImplementedException();
    public static ITracker Tracker(IIdentity identity) => throw new NotImplementedException();
}

/// <summary>Compose a host and harness without exposing their flags to policy.</summary>
public static class AgentBinding
{
    public static ISpawn Paseo(IAgentHarness harness) => throw new NotImplementedException();
    public static ISpawn ChildProcess(IAgentHarness harness) => throw new NotImplementedException();
    public static IAgentHarness ClaudeCli() => throw new NotImplementedException();
    public static IAgentHarness CodexCli() => throw new NotImplementedException();
    public static IIsolation GitWorktree(string sliceRoot, string sandboxProfile)
        => throw new NotImplementedException();
}

/// <summary>Binding-private launch and output adaptation, not a core capability.</summary>
public interface IAgentHarness
{
    CommandSpec Invocation(Brief brief, ExecutionScope scope);
    AgentResult Decode(string outputFile);
}

/// <summary>Closed composition root. Repo choices override machine, detection, then bare defaults.</summary>
public static class Composition
{
    public static Factory Create(string repositoryPath) => throw new NotImplementedException();
    public static ImmutableArray<BindingChoice> Resolve(Workflow workflow, string machineConfig)
    {
        // TODO: Instantiate compiled GitHubTracker/AppIdentity, GitWorktreeIsolation and
        // PaseoSpawner with ClaudeCliHarness or CodexCliHarness; otherwise use a child process.
        // Explicit unavailable bindings fail; freeze selected ids, versions, model ids and skills.
        // Every brief includes fallback instructions; use a slash skill only when supported.
        // Paseo uses tabs with no new-workspace flag. Send prepends [coordinator].
        // Child launch must enforce filesystem/process/network restrictions or fail closed.
        // App signing happens outside child visibility; refresh <=1h tokens inside bindings.
        // Reject maintainer account and admin scope. HTTPS push gets only orchestrator credentials.
        throw new NotImplementedException();
    }
}

/// <summary>Owns wire decoding; harness envelopes never escape this binding.</summary>
public static class AgentWire
{
    public static T Read<T>(ReadOnlySpan<byte> payload, JsonTypeInfo<T> contract)
    {
        // TODO: Decode harness envelope; enforce generated schema and reject unknown fields,
        // missing/null members, oversized output and paths outside the current attempt.
        // Deserialize with contract, then apply artifact-specific semantic validation.
        throw new NotImplementedException();
    }
    public static JsonElement Schema<T>(JsonTypeInfo<T> contract)
    {
        // TODO: JsonSchemaExporter exports from the same source-generated metadata used by Read.
        throw new NotImplementedException();
    }
}

public static class WorkflowFile
{
    /// <summary>Reads WORKFLOW.md as it exists at the pinned trunk commit (`git show <trunk>:WORKFLOW.md`).</summary>
    public static Workflow Parse(string markdown)
    {
        // TODO: Front matter is JSON between the `---` fences. JSON is valid YAML 1.2, so Symphony-format readers still
        // parse it, and softwarp needs no YAML dependency under AOT (open question: YamlDotNet's static generator
        // if the maintainer wants YAML syntax). Parse with the source-generated Contracts context; reject unknown keys.
        // Keys: trust, tracker, commands {build, test_targeted "{targets}", lint}, verify_skill, review_bot, wip_limit.
        // Step 1 permits trust 0..2 only and always requires spec approval; never infer merge rights.
        // Commands are argv tokens; `{targets}` expands from the APPROVED spec's TestTargets, never from the implementer.
        // Each target replaces exactly one whole argv element; it is never spliced into a string or passed to a shell.
        throw new NotImplementedException();
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RunManifest))]
[JsonSerializable(typeof(Attempt))]
[JsonSerializable(typeof(TriageArtifact))]
[JsonSerializable(typeof(SpecArtifact))]
[JsonSerializable(typeof(ImplementationArtifact))]
[JsonSerializable(typeof(CleanupArtifact))]
[JsonSerializable(typeof(GateReceipt))]
[JsonSerializable(typeof(SpecPackage))]
[JsonSerializable(typeof(Publication))]
[JsonSerializable(typeof(AdvanceResult))]
[JsonSerializable(typeof(SpecView))]
internal partial class Contracts : JsonSerializerContext;
