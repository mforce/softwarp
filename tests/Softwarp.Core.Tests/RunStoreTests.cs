using static Softwarp.Core.Tests.Fixture;

namespace Softwarp.Core.Tests;

public sealed class TempRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "softwarp-tests", Guid.NewGuid().ToString("n"));
    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

public sealed class RunStoreTests : IDisposable
{
    readonly TempRoot root = new();
    readonly RunStore store;
    readonly RunManifest manifest = Manifest with { Run = RunStore.NewRunId(Item) };

    public RunStoreTests() => store = new RunStore(root.Path);

    public void Dispose() => root.Dispose();

    RunLease Created()
    {
        var lease = store.TryLock(Item)!;
        lease.Create(manifest);
        return lease;
    }

    static readonly SpecArtifact Spec = FakeSpawn.Spec;

    [Fact]
    public void RunDirectory_IsOwnerRepoIssueUnderTheRoot()
    {
        Assert.Equal(Path.Combine(root.Path, "mforce", "cluckwork", "1223"), store.Directory(Item));
    }

    [Fact]
    public void RunId_NamesItsItemAndIsNewEachTime()
    {
        var run = RunStore.NewRunId(Item);
        Assert.StartsWith("mforce/cluckwork/1223/", run.Value);
        Assert.Equal(Item, RunStore.Item(run));
        Assert.NotEqual(run, RunStore.NewRunId(Item));
    }

    [Theory]
    [InlineData("mforce/cluckwork/1223")]
    [InlineData("../cluckwork/1223/abc")]
    [InlineData("mforce/../1223/abc")]
    [InlineData("mforce/./1223/abc")]
    [InlineData("mforce/cluck work/1223/abc")]
    [InlineData("mforce/cluckwork/-1/abc")]
    [InlineData("mforce/cluckwork/0/abc")]
    [InlineData("mforce/cluckwork/12a/abc")]
    [InlineData("mforce/cluckwork/+1223/abc")]
    [InlineData("mforce/cluckwork/1223/a/b")]
    [InlineData("mforce/cluckwork/1223/")]
    [InlineData("/etc/passwd/1/abc")]
    public void MalformedRunId_NamesNoItem(string run)
    {
        Assert.Null(RunStore.Item(new RunId(run)));
        Assert.Null(store.Read(new RunId(run)));
    }

    [Theory]
    [InlineData("..", "cluckwork", 1)]
    [InlineData("mforce", "a/b", 1)]
    [InlineData("mforce", "", 1)]
    [InlineData("mforce", "cluckwork", 0)]
    public void ItemThatIsNotOneSafeSegmentEach_IsRefused(string owner, string repo, long number)
    {
        Assert.Throws<ArgumentException>(() => store.TryLock(new ItemId(owner, repo, number)));
        Assert.False(Directory.Exists(root.Path));
    }

    [Fact]
    public void SecondCaller_GetsBusyUntilTheFirstReleases()
    {
        var first = store.TryLock(Item);
        Assert.NotNull(first);
        Assert.Null(store.TryLock(Item));
        Assert.Null(new RunStore(root.Path).TryLock(Item));
        first.Dispose();
        using var again = store.TryLock(Item);
        Assert.NotNull(again);
    }

    [Fact]
    public void OtherItems_DoNotShareALock()
    {
        using var first = store.TryLock(Item);
        using var other = store.TryLock(Item with { Number = 1224 });
        Assert.NotNull(other);
    }

    [Fact]
    public void Manifest_IsWrittenOnceAndReadBack()
    {
        using (var lease = Created())
        {
            Assert.Equal(manifest.Run, lease.Manifest!.Run);
            Assert.Throws<IOException>(() => lease.Create(manifest with { Branch = "other" }));
        }
        Assert.Equal("feat/1223-cap", store.Read(manifest.Run)!.Manifest.Branch);
        using var reopened = store.TryLock(Item)!;
        Assert.Equal(manifest.Run, reopened.Manifest!.Run);
    }

    [Fact]
    public void AnotherRunOfTheSameItem_IsNotFound()
    {
        using (Created()) { }
        Assert.Null(store.Read(RunStore.NewRunId(Item)));
        Assert.NotNull(store.Read(manifest.Run));
    }

    [Fact]
    public void IntentWithoutReceipt_SurvivesACrashAsTheOpenOperation()
    {
        OperationId op;
        using (var lease = Created())
        {
            lease.Complete(lease.Begin(new Intent.Claim()), new Outcome.Done());
            op = lease.Begin(new Intent.Push(Head1));
        } // the process dies here: no receipt

        var record = new RunStore(root.Path).Read(manifest.Run)!;
        Assert.Equal(op, record.Open!.Id);
        Assert.Equal(new Intent.Push(Head1), record.Open.Intent);
        Assert.Null(record.Open.Outcome);
    }

    [Fact]
    public void ReceiptCutOffBeforeTheRename_IsNoReceiptAndDoesNotBlockTheNext()
    {
        using var lease = Created();
        var op = lease.Begin(new Intent.Push(Head1));
        File.WriteAllText(lease.OperationFile(op, "receipt.json.tmp"), """{"outcome": "do""");
        Assert.Equal(op, lease.Read().Open!.Id);
        lease.Complete(op, new Outcome.NotRun());
        Assert.IsType<Outcome.NotRun>(lease.Read().Operations.Single().Outcome);
    }

    [Fact]
    public void LinkPlantedAtATempPath_IsNeverWrittenThrough()
    {
        using var lease = Created();
        var victim = Path.Combine(root.Path, "victim.txt");
        File.WriteAllText(victim, "the maintainer's file");
        var op = lease.Begin(new Intent.Push(Head1));
        File.CreateSymbolicLink(lease.OperationFile(op, "receipt.json.tmp"), victim);

        lease.Complete(op, new Outcome.Done());
        Assert.Equal("the maintainer's file", File.ReadAllText(victim));
        Assert.IsType<Outcome.Done>(lease.Read().Operations.Single().Outcome);
    }

    [Fact]
    public void LinkAtASpecPath_IsReplacedNotWrittenThrough()
    {
        using var lease = Created();
        var victim = Path.Combine(root.Path, "victim.txt");
        File.WriteAllText(victim, "the maintainer's file");
        var package = lease.StoreSpec(Spec);
        var path = Path.Combine(store.Directory(Item), package.PublicSpec.RelativePath);
        File.Delete(path);
        File.CreateSymbolicLink(path, victim);

        lease.StoreSpec(Spec);
        Assert.Equal("the maintainer's file", File.ReadAllText(victim));
        Assert.Equal(Spec.Markdown, lease.Read().PublicSpec(package).Markdown);
    }

    [Theory]
    [InlineData("manifest.json", "link")]
    [InlineData("intent.json", "fifo")]
    [InlineData("receipt.json", "link")]
    [InlineData("receipt.json", "fifo")]
    [InlineData("receipt.json", "oversized")]
    public async Task RecordThatIsALinkAFifoOrOversized_IsRefusedNotFollowed(string record, string planted)
    {
        using var lease = Created();
        var op = lease.Begin(new Intent.Push(Head1));
        lease.Complete(op, new Outcome.Done());
        var path = record == "manifest.json" ? Path.Combine(store.Directory(Item), record) : lease.OperationFile(op, record);
        var moved = Path.Combine(root.Path, record);
        File.Move(path, moved);
        if (planted == "link") File.CreateSymbolicLink(path, moved);
        else if (planted == "fifo") using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", path)) mkfifo.WaitForExit();
        else File.WriteAllText(path, File.ReadAllText(moved) + new string(' ', 8 << 20)); // valid JSON, over the cap

        static Task Refused(Action read) =>
            Assert.ThrowsAsync<InvalidDataException>(() => Task.Run(read).WaitAsync(TimeSpan.FromSeconds(10)));
        await Refused(() => lease.Read());
        if (record != "manifest.json") return;
        await Refused(() => lease.StoreSpec(Spec)); // the spec digest covers the manifest
        lease.Dispose();
        await Refused(() => store.TryLock(Item)); // a lease reads the manifest when it opens
    }

    [Fact]
    public void RecordsForTheLargestAgentOutput_ReadBack()
    {
        using var lease = Created();
        // 1 MiB of '<' is a valid agent output, and the encoder writes each '<' as the six bytes \u003C.
        var text = new string('<', 1 << 20);
        var triage = Triage(0) with { Reproduction = new(Trunk, "dotnet test", text) };
        lease.Complete(lease.Begin(new Intent.Agent(ActionKind.Triage, 0, false, null)), new Outcome.Triaged(triage));
        lease.Complete(lease.Begin(new Intent.Agent(ActionKind.Cleanup, 0, false, Package.Digest)),
            new Outcome.Cleaned(new CleanupArtifact(Package.Digest, Head1, "title", text)));
        var package = lease.StoreSpec(Spec with { Markdown = text });

        var record = lease.Read();
        Assert.Equal(text, record.Newest<Outcome.Triaged>()!.Artifact.Reproduction.Evidence);
        Assert.Equal(text, record.Newest<Outcome.Cleaned>()!.Artifact.PrBody);
        Assert.Equal(text, record.PublicSpec(package).Markdown);
    }

    [Fact]
    public void ReadingASmallRun_AllocatesFarLessThanTheRecordCap()
    {
        using var lease = Created();
        for (var i = 0; i < 5; i++) lease.Complete(lease.Begin(new Intent.Push(Head1)), new Outcome.Done());
        lease.Read();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var record = lease.Read();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(5, record.Operations.Length);
        Assert.InRange(allocated, 0, 1 << 20); // one record read used to allocate the 8 MiB record cap
    }

    [Fact]
    public void RecordOverTheCap_IsRefusedBeforeAnythingIsWritten()
    {
        using var lease = Created();
        var op = lease.Begin(new Intent.Agent(ActionKind.Spec, 0, false, null));
        Assert.Throws<InvalidDataException>(() => lease.Complete(op, new Outcome.Invalid([new string('<', 2 << 20)])));
        Assert.Equal(["intent.json"], Directory.GetFiles(Path.GetDirectoryName(lease.OperationFile(op, "x"))!).Select(Path.GetFileName));
        Assert.Equal(op, lease.Read().Open!.Id);
    }

    [Fact]
    public void ReceiptThatDoesNotReadBack_IsNeverRenamedIn()
    {
        using var lease = Created();
        var op = lease.Begin(new Intent.Agent(ActionKind.Spec, 0, false, null));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => lease.Complete(op, new Outcome.Invalid([null!])));
        Assert.False(File.Exists(lease.OperationFile(op, "receipt.json")));
        Assert.Equal(op, lease.Read().Open!.Id);
    }

    [Fact]
    public void IntentCutOffBeforeTheRename_IsNoOperation()
    {
        using var lease = Created();
        lease.Complete(lease.Begin(new Intent.Claim()), new Outcome.Done());
        Directory.CreateDirectory(Path.Combine(store.Directory(Item), "ops", "0002-push"));
        File.WriteAllText(lease.OperationFile(new("0002-push"), "intent.json.tmp"), """{"effect": "pu""");
        Assert.Null(lease.Read().Open);
        Assert.Equal("0003-push", lease.Begin(new Intent.Push(Head1)).Value);
    }

    [Fact]
    public void Receipt_IsWrittenOnce()
    {
        using var lease = Created();
        var op = lease.Begin(new Intent.Push(Head1));
        lease.Complete(op, new Outcome.Done());
        Assert.Throws<IOException>(() => lease.Complete(op, new Outcome.NotRun()));
        Assert.IsType<Outcome.Done>(lease.Read().Operations.Single().Outcome);
        Assert.Null(lease.Read().Open);
    }

    [Fact]
    public void Operations_AreNumberedInOrderAndNamedByEffect()
    {
        using var lease = Created();
        var ids = new[]
        {
            lease.Begin(new Intent.Claim()), lease.Begin(new Intent.Agent(ActionKind.Triage, 0, false, null)),
            lease.Begin(new Intent.PostSpec(Package.Digest)), lease.Begin(new Intent.Gates(Package.Digest, Head1, 0)),
            lease.Begin(new Intent.Push(Head1)), lease.Begin(new Intent.OpenPr(Package.Digest, Head1)),
        };
        Assert.Equal(["0001-claim", "0002-triage", "0003-post-spec", "0004-gates", "0005-push", "0006-open-pr"],
            ids.Select(id => id.Value));
        Assert.Equal(ids, lease.Read().Operations.Select(o => o.Id));
    }

    [Fact]
    public void OperationsPastTenThousand_StayInOrder()
    {
        using var lease = Created();
        Directory.CreateDirectory(Path.Combine(store.Directory(Item), "ops", "9999-claim"));
        File.WriteAllText(lease.OperationFile(new("9999-claim"), "intent.json"), """{"effect": "claim"}""");
        var next = lease.Begin(new Intent.Push(Head1));
        Assert.Equal("10000-push", next.Value);
        Assert.Equal(["9999-claim", "10000-push"], lease.Read().Operations.Select(o => o.Id.Value));
        Assert.Equal("10001-push", lease.Begin(new Intent.Push(Head1)).Value);
    }

    [Fact]
    public void OperationNumbers_ContinueAfterAReopen()
    {
        using (var lease = Created()) lease.Begin(new Intent.Claim());
        using var reopened = store.TryLock(Item)!;
        Assert.Equal("0002-push", reopened.Begin(new Intent.Push(Head1)).Value);
    }

    static TriageArtifact Triage(int epoch) => Triaged().Triage! with { Epoch = epoch };

    [Fact]
    public void Evidence_TakesTheNewestValidArtifactAndEveryAttempt()
    {
        using var lease = Created();
        void Agent(ActionKind role, Outcome outcome, int epoch = 0, bool revision = false) =>
            lease.Complete(lease.Begin(new Intent.Agent(role, epoch, revision, null)), outcome);

        Agent(ActionKind.Triage, new Outcome.Triaged(Triage(0)));
        Agent(ActionKind.Triage, new Outcome.Triaged(Triage(1)), epoch: 1);
        Agent(ActionKind.Triage, new Outcome.Invalid(["no reproduction"]), epoch: 1);
        Agent(ActionKind.Spec, new Outcome.NotRun());
        Agent(ActionKind.Implement, new Outcome.Implemented(new(Package.Digest)), revision: true);
        lease.Complete(lease.Begin(new Intent.Claim()), new Outcome.Done());
        lease.Complete(lease.Begin(new Intent.Gates(Package.Digest, Head1, 0)),
            new Outcome.Gated([Receipt(GateKind.Build), Receipt(GateKind.TargetedTests, exitCode: 1)]));
        lease.Complete(lease.Begin(new Intent.Gates(Package.Digest, Head1, 0)), new Outcome.Gated([Receipt(GateKind.Build)]));
        lease.Begin(new Intent.Agent(ActionKind.Cleanup, 0, false, Package.Digest));

        var record = lease.Read();
        Assert.Equal(1, record.Newest<Outcome.Triaged>()!.Artifact.Epoch);
        Assert.Null(record.Newest<Outcome.Specified>());
        Assert.Equal(
        [
            new Attempt(new("0001-triage"), ActionKind.Triage, 0, AttemptOutcome.Completed, false),
            new Attempt(new("0002-triage"), ActionKind.Triage, 1, AttemptOutcome.Completed, false),
            new Attempt(new("0003-triage"), ActionKind.Triage, 1, AttemptOutcome.InvalidArtifact, false),
            new Attempt(new("0005-implement"), ActionKind.Implement, 0, AttemptOutcome.Completed, true),
        ], record.Attempts.AsEnumerable());
        Assert.Equal([GateKind.Build, GateKind.TargetedTests, GateKind.Build], record.Gates.Select(g => g.Kind));
        Assert.Equal([0, 1, 0], record.Gates.Select(g => g.ExitCode));
    }

    [Fact]
    public void Spec_KeepsHoldoutsOutsideThePublicFilesAndOperationDirectories()
    {
        using var lease = Created();
        var package = lease.StoreSpec(Spec);
        var run = store.Directory(Item);

        Assert.StartsWith("holdout/", package.PrivateHoldouts.RelativePath);
        foreach (var file in Directory.EnumerateFiles(run, "*", SearchOption.AllDirectories)
                     .Where(f => !f.StartsWith(Path.Combine(run, "holdout") + Path.DirectorySeparatorChar) && Path.GetFileName(f) != "lock"))
            Assert.DoesNotContain("grow legacy.cs by 6%", File.ReadAllText(file));
        Assert.Contains("grow legacy.cs by 6%", File.ReadAllText(Path.Combine(run, package.PrivateHoldouts.RelativePath)));

        var record = lease.Read();
        Assert.Equal(Spec.Markdown, record.PublicSpec(package).Markdown);
        Assert.Equal(Spec.TestTargets, record.PublicSpec(package).TestTargets.AsEnumerable());
        Assert.Equal("T1", Assert.Single(record.Tickets(package)).Id);
    }

    [Fact]
    public void SpecDigest_CoversHoldoutsTicketsTextAndTheManifest()
    {
        using var lease = Created();
        var digest = lease.StoreSpec(Spec).Digest;
        Assert.Equal(digest, lease.StoreSpec(Spec).Digest);
        Assert.NotEqual(digest, lease.StoreSpec(Spec with { Holdouts = [Spec.Holdouts[0] with { Expected = "passes" }] }).Digest);
        Assert.NotEqual(digest, lease.StoreSpec(Spec with { Tickets = [] }).Digest);
        Assert.NotEqual(digest, lease.StoreSpec(Spec with { Markdown = "# Other" }).Digest);
        Assert.NotEqual(digest, lease.StoreSpec(Spec with { TestTargets = ["Category=Guards"] }).Digest);

        using var otherRoot = new TempRoot();
        using var otherRun = new RunStore(otherRoot.Path).TryLock(Item)!;
        otherRun.Create(manifest with { Trunk = new CommitId("other-trunk") });
        Assert.NotEqual(digest, otherRun.StoreSpec(Spec).Digest);
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("a link to an unchanged copy")]
    public void TamperedSpecFile_IsRefused(string tampered)
    {
        using var lease = Created();
        var package = lease.StoreSpec(Spec);
        var path = Path.Combine(store.Directory(Item), package.PublicSpec.RelativePath);
        if (tampered == "edited")
            File.WriteAllText(path, """{"markdown": "# Other", "criteria": [], "test_targets": ["x"]}""");
        else
        {
            File.Move(path, Path.Combine(root.Path, "spec.json"));
            File.CreateSymbolicLink(path, Path.Combine(root.Path, "spec.json"));
        }
        Assert.Throws<InvalidDataException>(() => lease.Read().PublicSpec(package));
    }
}
