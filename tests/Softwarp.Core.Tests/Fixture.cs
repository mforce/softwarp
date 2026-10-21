namespace Softwarp.Core.Tests;

static class Fixture
{
    public static readonly CommitId Trunk = new("trunk");
    public static readonly CommitId Head1 = new("head-1");
    public static readonly CommitId Head2 = new("head-2");
    public static readonly DateTimeOffset SpecPostedAt = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    public static readonly Workflow Workflow = new(new("dotnet", ["build"]), new("dotnet", ["test", "--filter", "{targets}"]));
    public static readonly ItemId Item = new("mforce", "cluckwork", 1223);
    public static readonly RunManifest Manifest = new(new RunId("cluckwork-1223"), Trunk, "feat/1223-cap", "maintainer-id", Workflow,
        new WorkItem(Item, "Cap legacy file growth", "Legacy files grow without limit.", "rev-1",
            new Uri("https://github.com/mforce/cluckwork/issues/1223")),
        new Digest("workflow-digest"), new ModelPins("triage-model", "spec-model", "implement-model", "cleanup-model"));
    public static readonly SpecPackage Package = Spec("digest-1");
    public static readonly SpecPackage Package2 = Spec("digest-2");
    public static readonly AgentHandle Worker = new("agent-1", "/tmp/agent-1.log", "tail -f /tmp/agent-1.log");

    public static Evidence Claimed() => new(Manifest, Claim: Manifest.Run, Unresolved: null, Worker: null,
        Maintainer: [], Attempts: [], Triage: null, Spec: null, SpecPostedAt: null, Head: null, Implementation: null,
        Gates: [], Cleanup: null, Pr: null);

    public static Evidence Triaged() => Claimed() with
    {
        Triage = new(TriageDecision.Agent, new Reproduction(Trunk, "dotnet test", "cap not enforced"),
            PriorRejectionSearch: "no prior rejection", RedundancySearch: "not built yet", Epoch: 0),
    };

    public static Evidence SpecPosted() => Triaged() with { Spec = Package, SpecPostedAt = SpecPostedAt };

    public static Evidence Approved() => SpecPosted() with { Maintainer = [Comment(MaintainerVerb.Approve)] };

    public static Evidence Implemented() => Approved() with
    {
        Implementation = new(Package.Digest),
        Head = new(Manifest.Branch, Head1, Clean: true, DescendsFromTrunk: true),
    };

    public static Evidence GatesPassed() => Implemented() with
    {
        Gates = [Receipt(GateKind.Build), Receipt(GateKind.TargetedTests)],
    };

    public static Evidence CleanedUp() => GatesPassed() with
    {
        Cleanup = new(Package.Digest, Head1, "Cap legacy file growth", "Closes #1223."),
    };

    public static Evidence Published() => CleanedUp() with
    {
        Pr = new(new Uri("https://github.com/mforce/cluckwork/pull/1300"), Manifest.Branch, Head1, Package.Digest),
    };

    public static Evidence Reapproved(Evidence e) => e with
    {
        Spec = Package2,
        Maintainer = [Comment(MaintainerVerb.Approve) with { SpecDigest = Package2.Digest }],
    };

    public static Evidence Resumed(Evidence e) =>
        e with { Maintainer = [.. e.Maintainer, Comment(MaintainerVerb.Resume, minutesAfterSpec: 60)] };

    public static SpecPackage Spec(string digest) =>
        new(new Digest(digest), Ref("spec.md"), Ref("tickets.json"), Ref("holdouts.json"));

    public static MaintainerComment Comment(MaintainerVerb verb, int minutesAfterSpec = 1) => new(
        new Uri("https://github.com/mforce/cluckwork/issues/1223#issuecomment-1"), verb,
        verb == MaintainerVerb.Resume ? null : Package.Digest, verb == MaintainerVerb.Revise ? "cap at +5%" : null,
        AuthorAccountId: Manifest.MaintainerAccountId, AuthorIsBot: false, AuthorIsRepoAdmin: true, EditedByOther: false,
        PostedAt: SpecPostedAt.AddMinutes(minutesAfterSpec));

    public static GateReceipt Receipt(GateKind kind, int exitCode = 0, CommitId? head = null, int epoch = 0) => new(
        kind, Package.Digest, head ?? Head1, HeadAfter: head ?? Head1, TreeClean: true, Workflow.Command(kind).Digest, exitCode,
        TestsExecuted: kind == GateKind.TargetedTests ? 12 : null, Ref($"{kind}.log"), epoch);

    public static Attempt Tried(ActionKind action, AttemptOutcome outcome = AttemptOutcome.Completed,
        bool revision = false, int epoch = 0) => new(new OperationId($"{action}-{outcome}"), action, epoch, outcome, revision);

    static ArtifactRef Ref(string path) => new(new Digest($"sha-{path}"), path);
}
