using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using static Softwarp.Core.Tests.Fixture;

namespace Softwarp.Core.Tests;

public class ContractsTests
{
    static readonly Contracts C = Contracts.Default;

    static readonly SpecArtifact SpecOutput = new("# Cap legacy file growth",
        Criteria: [new("AC1", "a legacy file may grow at most 5%")],
        Tickets: [new("T1", "add the growth cap", []), new("T2", "wire it into CI", ["T1"])],
        Holdouts: [new("AC1", "grow a legacy file by 6%", "the guard fails")],
        TestTargets: ["FullyQualifiedName~GrowthCap"]);

    static void RoundTrips<T>(T value, JsonTypeInfo<T> contract)
    {
        var back = Contracts.Read(Contracts.Write(value, contract), contract);
        Assert.Equal(value!.GetType(), back!.GetType());
        Assert.Equivalent(value, back, strict: true);
    }

    static byte[] Edit<T>(T value, JsonTypeInfo<T> contract, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(Contracts.Write(value, contract))!.AsObject();
        edit(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    static void Rejects<T>(T value, JsonTypeInfo<T> contract, Action<JsonObject> edit) =>
        Assert.Throws<JsonException>(() => Contracts.Read(Edit(value, contract, edit), contract));

    [Fact]
    public void PersistedTypes_RoundTrip()
    {
        RoundTrips(Manifest, C.RunManifest);
        RoundTrips(new PublicSpec(SpecOutput.Markdown, SpecOutput.Criteria, SpecOutput.TestTargets), C.PublicSpec);
        RoundTrips(SpecOutput.Tickets, C.ImmutableArrayTicket);
        RoundTrips(SpecOutput.Holdouts, C.ImmutableArrayHoldout);
        Intent[] intents =
        [
            new Intent.Claim(), new Intent.Agent(ActionKind.Implement, 2, true, Package.Digest),
            new Intent.Agent(ActionKind.Triage, 0, false, null), new Intent.PostSpec(Package.Digest),
            new Intent.Gates(Package.Digest, Head1, 1), new Intent.Push(Head1, 0), new Intent.OpenPr(Package.Digest, Head1),
        ];
        foreach (var intent in intents) RoundTrips(intent, C.Intent);
        Outcome[] outcomes =
        [
            new Outcome.Done(), new Outcome.NotRun(), new Outcome.Invalid(["the spec has no test target"]),
            new Outcome.Triaged(Triaged().Triage!), new Outcome.Specified(Package),
            new Outcome.Implemented(new(Package.Digest)), new Outcome.Cleaned(CleanedUp().Cleanup!),
            new Outcome.Gated([Receipt(GateKind.Build), Receipt(GateKind.TargetedTests, exitCode: 75)]),
        ];
        foreach (var outcome in outcomes) RoundTrips(outcome, C.Outcome);
    }

    [Fact]
    public void ReceiptsAndIntents_NameTheirVariantAndRejectUnknownMembers()
    {
        Assert.Equal("open-pr", JsonNode.Parse(Contracts.Write<Intent>(new Intent.OpenPr(Package.Digest, Head1), C.Intent))!["effect"]!.GetValue<string>());
        Rejects<Intent>(new Intent.Push(Head1, 0), C.Intent, o => o["force"] = true);
        Rejects<Outcome>(new Outcome.Done(), C.Outcome, o => o["pr"] = "https://example.com");
        Rejects<Intent>(new Intent.Push(Head1, 0), C.Intent, o => o["effect"] = "merge");
    }

    [Fact]
    public void AgentOutputs_RoundTrip()
    {
        RoundTrips(new TriageOutput(TriageDecision.NeedsInfo, new Reproduction(Trunk, "dotnet test", "red"), "none", "none"),
            C.TriageOutput);
        RoundTrips(SpecOutput, C.SpecArtifact);
        RoundTrips(new ImplementationOutput(), C.ImplementationOutput);
        RoundTrips(new CleanupOutput("Cap legacy file growth", "Closes #1223."), C.CleanupOutput);
    }

    [Fact]
    public void IdentifiersAndEnums_AreWrittenAsStrings()
    {
        var json = JsonNode.Parse(Contracts.Write<Outcome>(new Outcome.Gated([Receipt(GateKind.TargetedTests)]), C.Outcome))!["receipts"]![0]!;
        Assert.Equal("TargetedTests", json["kind"]!.GetValue<string>());
        Assert.Equal(Head1.Value, json["head"]!.GetValue<string>());
        Assert.Equal(Package.Digest.Value, json["spec"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownMember_IsRejected()
    {
        Rejects(Manifest, C.RunManifest, o => o["unexpected"] = 1);
        Rejects(Manifest, C.RunManifest, o => o["workflow"]!["build"]!.AsObject()["shell"] = "sh");
        Rejects(SpecOutput, C.SpecArtifact, o => o["criteria"]![0]!.AsObject()["weight"] = 2);
        Rejects(new ImplementationOutput(), C.ImplementationOutput, o => o["spec"] = Package.Digest.Value);
        Rejects(new CleanupOutput("t", "b"), C.CleanupOutput, o => o["head"] = Head1.Value);
    }

    [Fact]
    public void AgentOutput_CannotCarryFactoryStampedFields()
    {
        var triage = new TriageOutput(TriageDecision.Agent, new Reproduction(Trunk, "dotnet test", "red"), "none", "none");
        Rejects(triage, C.TriageOutput, o => o["epoch"] = 7);
        Rejects(new CleanupOutput("t", "b"), C.CleanupOutput, o => o["spec"] = Package.Digest.Value);
    }

    [Fact]
    public void MissingOrNullMember_IsRejected()
    {
        Rejects(SpecOutput, C.SpecArtifact, o => o.Remove("test_targets"));
        Rejects(SpecOutput, C.SpecArtifact, o => o["markdown"] = null);
        Rejects(new CleanupOutput("t", "b"), C.CleanupOutput, o => o.Remove("pr_body"));
        Rejects(Manifest, C.RunManifest, o => o["trunk"] = null);
    }

    [Fact]
    public void NullArrayElement_IsRejected()
    {
        Rejects(SpecOutput, C.SpecArtifact, o => o["test_targets"]!.AsArray().Add(null));
        Rejects(SpecOutput, C.SpecArtifact, o => o["criteria"]!.AsArray().Add(null));
    }

    [Fact]
    public void EnumAsInteger_IsRejected()
    {
        var triage = new TriageOutput(TriageDecision.Agent, new Reproduction(Trunk, "dotnet test", "red"), "none", "none");
        Rejects(triage, C.TriageOutput, o => o["decision"] = 0);
        Rejects(triage, C.TriageOutput, o => o["decision"] = "Merge");
    }

    [Fact]
    public void DuplicateMember_IsRejected()
    {
        var json = """{"pr_title": "safe", "pr_body": "b", "pr_title": "other"}"""u8.ToArray();
        Assert.Throws<JsonException>(() => Contracts.Read(json, C.CleanupOutput));
    }

    [Fact]
    public void AgentArtifacts_HaveClosedObjectSchemas()
    {
        JsonNode[] schemas = [Contracts.Schema(C.TriageOutput), Contracts.Schema(C.SpecArtifact),
            Contracts.Schema(C.ImplementationOutput), Contracts.Schema(C.CleanupOutput)];
        Assert.All(schemas, schema =>
        {
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        });
    }

    /// <summary>Codex refuses an object schema without `properties`, so the empty implementer output would never reach it.</summary>
    [Fact]
    public void EveryObjectInAnAgentSchema_ListsItsProperties_EvenWhenItHasNone()
    {
        static IEnumerable<JsonObject> Objects(JsonNode? node) => node switch
        {
            JsonObject o => (o["type"]?.ToJsonString() ?? "").Contains("\"object\"")
                ? o.SelectMany(p => Objects(p.Value)).Prepend(o)
                : o.SelectMany(p => Objects(p.Value)),
            JsonArray a => a.SelectMany(Objects),
            _ => [],
        };
        JsonNode[] schemas = [Contracts.Schema(C.TriageOutput), Contracts.Schema(C.SpecArtifact),
            Contracts.Schema(C.ImplementationOutput), Contracts.Schema(C.CleanupOutput)];

        Assert.All(schemas.SelectMany(Objects), o => Assert.IsType<JsonObject>(o["properties"]));
        Assert.Empty(Contracts.Schema(C.ImplementationOutput)["properties"]!.AsObject());
    }

    [Fact]
    public void TriageSchema_RequiresEveryMemberAndNamesTheDecisions()
    {
        var schema = Contracts.Schema(C.TriageOutput);
        Assert.Equal(["decision", "reproduction", "prior_rejection_search", "redundancy_search"],
            schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(Enum.GetNames<TriageDecision>(),
            schema["properties"]!["decision"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("string", schema["properties"]!["reproduction"]!["properties"]!["trunk"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void CleanupSchema_AsksForThePrText() =>
        Assert.Equal(["pr_title", "pr_body"],
            Contracts.Schema(C.CleanupOutput)["required"]!.AsArray().Select(n => n!.GetValue<string>()));
}
