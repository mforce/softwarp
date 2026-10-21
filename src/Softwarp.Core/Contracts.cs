using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Softwarp.Core;

/// <summary>The one wire format for every persisted and agent-facing type. Source-generated, so no reflection runs
/// under Native AOT. Strict on read: unknown, duplicate, missing or null-where-not-nullable members fail, enums must be
/// their names, and no array holds a null.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false,
    WriteIndented = true,
    Converters =
    [
        typeof(StringValueJson.Run), typeof(StringValueJson.Hash), typeof(StringValueJson.Commit),
        typeof(StrictEnumJson<TriageDecision>), typeof(StrictEnumJson<GateKind>), typeof(StrictEnumJson<ActionKind>),
    ])]
[JsonSerializable(typeof(RunManifest))]
[JsonSerializable(typeof(TriageOutput))]
[JsonSerializable(typeof(SpecArtifact))]
[JsonSerializable(typeof(ImplementationOutput))]
[JsonSerializable(typeof(CleanupOutput))]
[JsonSerializable(typeof(WorkflowSettings))]
[JsonSerializable(typeof(Intent))]
[JsonSerializable(typeof(Outcome))]
[JsonSerializable(typeof(PublicSpec))]
[JsonSerializable(typeof(ImmutableArray<Ticket>))]
[JsonSerializable(typeof(ImmutableArray<Holdout>))]
[JsonSerializable(typeof(Brief))]
[JsonSerializable(typeof(TriageInput))]
[JsonSerializable(typeof(SpecInput))]
[JsonSerializable(typeof(ImplementationInput))]
[JsonSerializable(typeof(CleanupInput))]
public sealed partial class Contracts : JsonSerializerContext
{
    public static byte[] Write<T>(T value, JsonTypeInfo<T> contract) => JsonSerializer.SerializeToUtf8Bytes(value, contract);

    /// <exception cref="JsonException">The payload breaks the contract.</exception>
    public static T Read<T>(ReadOnlySpan<byte> json, JsonTypeInfo<T> contract)
    {
        if (HasNullArrayElement(json)) throw new JsonException("an array holds a null element");
        return JsonSerializer.Deserialize(json, contract) ?? throw new JsonException("the payload is null");
    }

    /// <summary>The JSON schema an agent's output must match, from the same metadata <see cref="Read{T}"/> enforces.</summary>
    public static JsonNode Schema<T>(JsonTypeInfo<T> contract) => JsonSchemaExporter.GetJsonSchemaAsNode(contract,
        new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, schema) =>
                StringValueJson.Types.Contains(context.TypeInfo.Type) ? new JsonObject { ["type"] = "string" } : schema,
        });

    // Nullable annotations on generic arguments are not enforced, so `["a", null]` would reach validation as a null
    // string. No contract has a nullable array element, so any null inside an array is rejected.
    static bool HasNullArrayElement(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        var inArray = new Stack<bool>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray: inArray.Push(true); break;
                case JsonTokenType.StartObject: inArray.Push(false); break;
                case JsonTokenType.EndArray or JsonTokenType.EndObject: inArray.Pop(); break;
                case JsonTokenType.Null when inArray.TryPeek(out var array) && array: return true;
            }
        }
        return false;
    }
}

/// <summary>Identifiers travel as plain JSON strings.</summary>
public abstract class StringValueJson<T>(Func<string, T> create, Func<T, string> text) : JsonConverter<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? create(reader.GetString()!)
            : throw new JsonException($"{typeof(T).Name} must be a string");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(text(value));
}

public static class StringValueJson
{
    public sealed class Run() : StringValueJson<RunId>(v => new(v), v => v.Value);
    public sealed class Hash() : StringValueJson<Digest>(v => new(v), v => v.Value);
    public sealed class Commit() : StringValueJson<CommitId>(v => new(v), v => v.Value);

    internal static readonly HashSet<Type> Types = [typeof(RunId), typeof(Digest), typeof(CommitId)];
}

/// <summary>Enum members by name only: an integer, even a defined one, is rejected.</summary>
public sealed class StrictEnumJson<T>() : JsonStringEnumConverter<T>(namingPolicy: null, allowIntegerValues: false)
    where T : struct, Enum;
