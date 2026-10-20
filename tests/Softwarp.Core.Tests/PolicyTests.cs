using static Softwarp.Core.Tests.Fixture;

namespace Softwarp.Core.Tests;

public class PolicyTests
{
    static NextAction Decide(Evidence evidence, int row)
    {
        var (fired, action) = Policy.Explain(evidence);
        Assert.Equal(row, (int)fired);
        return action;
    }

    static NextAction.Stop AssertStops(Evidence evidence, int row, Disposition disposition = Disposition.NeedsHuman)
    {
        var stop = Assert.IsType<NextAction.Stop>(Decide(evidence, row));
        Assert.Equal(disposition, stop.Disposition);
        return stop;
    }

    static GateReceipt Failed(GateKind kind) => Receipt(kind, exitCode: 1);

    static GateReceipt[] Unavailable(GateKind kind, int count, CommitId? head = null) =>
        [.. Enumerable.Repeat(Receipt(kind, exitCode: 75, head: head), count)];

    static GateReceipt Untrusted(string cause, int exitCode)
    {
        var receipt = Receipt(GateKind.TargetedTests, exitCode);
        return cause switch
        {
            "dirty" => receipt with { TreeClean = false },
            "moved" => receipt with { HeadAfter = Head2 },
            "command" => receipt with { Command = new CommandSpec("dotnet", ["test", "--filter", "FullyQualifiedName~GrowthCap"]).Digest },
            _ => throw new ArgumentOutOfRangeException(nameof(cause)),
        };
    }

    [Fact]
    public void Row01_ForeignClaim_Stops() =>
        AssertStops(GatesPassed() with { Claim = new RunId("another-run") }, row: 1);

    [Fact]
    public void Row01_UnclaimedItem_Stops() =>
        AssertStops(GatesPassed() with { Claim = null }, row: 1);

    [Theory]
    [InlineData("implementation")]
    [InlineData("implementation for another package")]
    [InlineData("cleanup")]
    [InlineData("cleanup for another package")]
    [InlineData("package")]
    public void Row01_PrWithoutTheWorkForTheCurrentPackage_Stops(string missing)
    {
        var e = Published();
        AssertStops(missing switch
        {
            "implementation" => e with { Implementation = null },
            "implementation for another package" => e with { Implementation = new(Package2.Digest) },
            "cleanup" => e with { Cleanup = null },
            "cleanup for another package" => e with { Cleanup = e.Cleanup! with { Spec = Package2.Digest } },
            _ => Reapproved(e),
        }, row: 1);
    }

    [Fact]
    public void Row02_IntentWithoutReceipt_ReconcilesBeforeAnythingElse()
    {
        var operation = new OperationId("op-7");
        var action = Decide(Approved() with { Unresolved = operation, Worker = Worker }, row: 2);
        Assert.Equal(operation, Assert.IsType<NextAction.Reconcile>(action).Operation);
    }

    [Fact]
    public void Row03_RunningWorker_IsAwaitedNotDuplicated() =>
        Assert.Equal(Worker, Assert.IsType<NextAction.AwaitAgent>(Decide(Triaged() with { Worker = Worker }, row: 3)).Handle);

    [Fact]
    public void Row04_NoTriage_Triages() =>
        Assert.IsType<NextAction.Triage>(Decide(Claimed(), row: 4));

    [Theory]
    [InlineData(TriageDecision.NeedsInfo)]
    [InlineData(TriageDecision.Human)]
    public void Row04_TriageThatStoppedInAnEarlierEpoch_RunsAgainAfterResume(TriageDecision decision)
    {
        var e = Triaged();
        Assert.IsType<NextAction.Triage>(Decide(Resumed(e) with { Triage = e.Triage! with { Decision = decision } }, row: 4));
    }

    [Fact]
    public void Row04_ReTriageAfterResume_StopsOnItsSecondInvalidArtifact()
    {
        var e = Triaged();
        var invalid = Tried(ActionKind.Triage, AttemptOutcome.InvalidArtifact, epoch: 1);
        AssertStops(Resumed(e) with { Triage = e.Triage! with { Decision = TriageDecision.NeedsInfo }, Attempts = [invalid, invalid] }, row: 4);
    }

    [Fact]
    public void Row04_ResumeRestoresTheInvalidArtifactRetry() =>
        Assert.IsType<NextAction.Triage>(Decide(Resumed(Claimed()) with
        {
            Attempts = [Tried(ActionKind.Triage, AttemptOutcome.InvalidArtifact), Tried(ActionKind.Triage, AttemptOutcome.InvalidArtifact)],
        }, row: 4));

    [Theory]
    [InlineData(TriageDecision.Human, Disposition.NeedsHuman)]
    [InlineData(TriageDecision.NeedsInfo, Disposition.NeedsInfo)]
    [InlineData(TriageDecision.WontFix, Disposition.WontFix)]
    public void Row05_TriageNotForAnAgent_StopsWithMatchingDisposition(TriageDecision decision, Disposition disposition)
    {
        var e = Triaged();
        AssertStops(e with { Triage = e.Triage! with { Decision = decision } }, row: 5, disposition);
    }

    [Fact]
    public void Row05_WontFixTriage_StaysFinalAfterResume()
    {
        var e = Triaged();
        AssertStops(Resumed(e) with { Triage = e.Triage! with { Decision = TriageDecision.WontFix } }, row: 5, Disposition.WontFix);
    }

    [Fact]
    public void Row06_NoSpec_Specifies() =>
        Assert.Null(Assert.IsType<NextAction.Specify>(Decide(Triaged(), row: 6)).Revise);

    [Fact]
    public void Row07_PostedSpecWithoutDecision_AwaitsMaintainerOnThePackage() =>
        Assert.Equal(Package, Assert.IsType<NextAction.AwaitMaintainer>(Decide(SpecPosted(), row: 7)).Package);

    [Fact]
    public void Row07_SpecNotYetPosted_AwaitsMaintainerEvenWithAnApproval() =>
        Assert.IsType<NextAction.AwaitMaintainer>(Decide(Approved() with { SpecPostedAt = null }, row: 7));

    [Fact]
    public void Row07_Revise_RespecifiesWithTheMaintainersComment()
    {
        var revise = Comment(MaintainerVerb.Revise);
        var action = Decide(SpecPosted() with { Maintainer = [revise] }, row: 7);
        Assert.Equal(revise, Assert.IsType<NextAction.Specify>(action).Revise);
    }

    [Fact]
    public void Row07_ReviseNewerThanApproval_Wins() =>
        Assert.IsType<NextAction.Specify>(Decide(SpecPosted() with
        {
            Maintainer = [Comment(MaintainerVerb.Approve, minutesAfterSpec: 1), Comment(MaintainerVerb.Revise, minutesAfterSpec: 2)],
        }, row: 7));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Row07_ReviseAndApprovePostedTogether_ReviseWinsInEitherOrder(bool reviseListedFirst)
    {
        var approve = Comment(MaintainerVerb.Approve);
        var revise = Comment(MaintainerVerb.Revise);
        Assert.IsType<NextAction.Specify>(Decide(SpecPosted() with
        {
            Maintainer = reviseListedFirst ? [revise, approve] : [approve, revise],
        }, row: 7));
    }

    [Fact]
    public void Row07_InvalidRevision_DoesNotSpendARevisionCycle() =>
        Assert.IsType<NextAction.Specify>(Decide(SpecPosted() with
        {
            Maintainer = [Comment(MaintainerVerb.Revise)],
            Attempts = [.. Enumerable.Repeat(Tried(ActionKind.Spec, revision: true), Policy.RevisionCycles - 1),
                Tried(ActionKind.Spec, AttemptOutcome.InvalidArtifact, revision: true)],
        }, row: 7));

    [Fact]
    public void Row07_ReviseWithACycleLeft_RespecifiesAfterTheFirstSpec() =>
        Assert.IsType<NextAction.Specify>(Decide(SpecPosted() with
        {
            Maintainer = [Comment(MaintainerVerb.Revise)],
            Attempts = [Tried(ActionKind.Spec), .. Enumerable.Repeat(Tried(ActionKind.Spec, revision: true), Policy.RevisionCycles - 1)],
        }, row: 7));

    [Fact]
    public void Row07_ReviseAfterRevisionCyclesSpent_Stops() =>
        AssertStops(SpecPosted() with
        {
            Maintainer = [Comment(MaintainerVerb.Revise)],
            Attempts = [Tried(ActionKind.Spec), Tried(ActionKind.Spec, revision: true), Tried(ActionKind.Spec, revision: true)],
        }, row: 7);

    [Theory]
    [InlineData("dirty", false)]
    [InlineData("off-trunk", false)]
    [InlineData("other-branch", false)]
    [InlineData("dirty", true)]
    [InlineData("off-trunk", true)]
    [InlineData("other-branch", true)]
    public void Row08_DefectiveWorktree_StopsBeforeAnyImplementerOrGate(string defect, bool implemented)
    {
        var head = new HeadObservation(Manifest.Branch, implemented ? Head1 : Trunk, Clean: true, DescendsFromTrunk: true);
        AssertStops((implemented ? GatesPassed() : Approved()) with
        {
            Head = defect switch
            {
                "dirty" => head with { Clean = false },
                "off-trunk" => head with { DescendsFromTrunk = false },
                _ => head with { Branch = "main" },
            },
        }, row: 8);
    }

    [Fact]
    public void Row08_ImplementationWithoutWorktreeHead_Stops() =>
        AssertStops(GatesPassed() with { Head = null }, row: 8);

    [Fact]
    public void Row09_ApprovalNewerThanARevise_Implements() =>
        Assert.IsType<NextAction.Implement>(Decide(SpecPosted() with
        {
            Maintainer = [Comment(MaintainerVerb.Revise, minutesAfterSpec: 1), Comment(MaintainerVerb.Approve, minutesAfterSpec: 2)],
        }, row: 9));

    [Fact]
    public void Row09_FitWorktreeWithoutImplementation_Implements() =>
        Assert.IsType<NextAction.Implement>(Decide(Approved() with
        {
            Head = new(Manifest.Branch, Trunk, Clean: true, DescendsFromTrunk: true),
        }, row: 9));

    [Fact]
    public void Row09_ApprovedWithoutImplementation_ImplementsTheApprovedPackage()
    {
        var implement = Assert.IsType<NextAction.Implement>(Decide(Approved(), row: 9));
        Assert.Equal(Package, implement.Spec.Package);
        Assert.Null(implement.Repair);
    }

    [Fact]
    public void Row09_ImplementationForAnOlderPackage_ImplementsTheApprovedOne()
    {
        var implement = Assert.IsType<NextAction.Implement>(Decide(Reapproved(CleanedUp()), row: 9));
        Assert.Equal(Package2, implement.Spec.Package);
    }

    [Fact]
    public void Row10_RedGateOnUnchangedHead_RepairsInsteadOfRerunning()
    {
        var failed = Failed(GateKind.TargetedTests);
        var action = Decide(Implemented() with { Gates = [Receipt(GateKind.Build), failed] }, row: 10);
        Assert.Equal(failed, Assert.IsType<NextAction.Implement>(action).Repair);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void Row10_TargetedTestsWithoutAnExecutedTest_FailAndRepair(int? executed)
    {
        var noTests = Receipt(GateKind.TargetedTests) with { TestsExecuted = executed };
        var action = Decide(Implemented() with { Gates = [Receipt(GateKind.Build), noTests] }, row: 10);
        Assert.Equal(noTests, Assert.IsType<NextAction.Implement>(action).Repair);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Row10_TwoRedGates_RepairTheBuildFirstInEitherOrder(bool buildListedFirst)
    {
        GateReceipt build = Failed(GateKind.Build), tests = Failed(GateKind.TargetedTests);
        var action = Decide(Implemented() with { Gates = buildListedFirst ? [build, tests] : [tests, build] }, row: 10);
        Assert.Equal(build, Assert.IsType<NextAction.Implement>(action).Repair);
    }

    [Fact]
    public void Row10_RedGateWithARepairLeft_RepairsAfterTheFirstImplementation() =>
        Assert.IsType<NextAction.Implement>(Decide(Implemented() with
        {
            Gates = [Failed(GateKind.Build)],
            Attempts = [Tried(ActionKind.Implement), .. Enumerable.Repeat(Tried(ActionKind.Implement, revision: true), Policy.RevisionCycles - 1)],
        }, row: 10));

    [Fact]
    public void Row10_RedGateAfterRepairsSpent_Stops() =>
        AssertStops(Implemented() with
        {
            Gates = [Failed(GateKind.Build)],
            Attempts = [Tried(ActionKind.Implement), Tried(ActionKind.Implement, revision: true), Tried(ActionKind.Implement, revision: true)],
        }, row: 10);

    [Fact]
    public void Row10_SpecRevisions_DoNotSpendRepairCycles() =>
        Assert.IsType<NextAction.Implement>(Decide(Implemented() with
        {
            Gates = [Failed(GateKind.Build)],
            Attempts = [.. Enumerable.Repeat(Tried(ActionKind.Spec, revision: true), Policy.RevisionCycles)],
        }, row: 10));

    [Fact]
    public void Row10_ResumeStartsANewEpochAndRestoresTheRepairBudget() =>
        Assert.IsType<NextAction.Implement>(Decide(Resumed(Implemented()) with
        {
            Gates = [Failed(GateKind.Build)],
            Attempts = [Tried(ActionKind.Implement), Tried(ActionKind.Implement, revision: true), Tried(ActionKind.Implement, revision: true)],
        }, row: 10));

    [Fact]
    public void Row10_ResumeByABot_DoesNotRestoreTheBudget() =>
        AssertStops(Implemented() with
        {
            Gates = [Failed(GateKind.Build)],
            Maintainer = [Comment(MaintainerVerb.Approve), Comment(MaintainerVerb.Resume, minutesAfterSpec: 60) with { AuthorIsBot = true }],
            Attempts = [Tried(ActionKind.Implement), Tried(ActionKind.Implement, revision: true), Tried(ActionKind.Implement, revision: true)],
        }, row: 10);

    [Fact]
    public void Row10_GateUnavailableBeyondRetries_Stops() =>
        AssertStops(Implemented() with
        {
            Gates = [.. Unavailable(GateKind.TargetedTests, Policy.UnavailableGateRetries + 1)],
        }, row: 10);

    [Theory]
    [InlineData("dirty", 0)]
    [InlineData("dirty", 1)]
    [InlineData("dirty", 75)]
    [InlineData("moved", 0)]
    [InlineData("moved", 1)]
    [InlineData("command", 0)]
    [InlineData("command", 1)]
    public void Row10_UntrustedGateResultOnTheHead_StopsNamingTheCause(string cause, int exitCode) =>
        Assert.Contains(cause, AssertStops(Implemented() with { Gates = [Receipt(GateKind.Build), Untrusted(cause, exitCode)] }, row: 10).Reason);

    [Theory]
    [InlineData(GateKind.TargetedTests, "pwsh", "test", "--filter", "{targets}")]
    [InlineData(GateKind.TargetedTests, "dotnet", "test --filter", "{targets}")]
    [InlineData(GateKind.Build, "dotnet", "test", "--filter", "{targets}")]
    public void Row10_GateThatRanAnotherArgv_Stops(GateKind kind, string executable, params string[] arguments) =>
        Assert.Contains("command", AssertStops(Implemented() with
        {
            Gates = [Receipt(kind) with { Command = new CommandSpec(executable, [.. arguments]).Digest }],
        }, row: 10).Reason);

    [Fact]
    public void Row10_GateThatMovedTheHeadOntoTheCurrentHead_StopsInsteadOfLooping() =>
        Assert.Contains("moved", AssertStops(Implemented() with
        {
            Gates = [Receipt(GateKind.Build), Receipt(GateKind.TargetedTests) with { Head = Head2 }],
        }, row: 10).Reason);

    [Fact]
    public void Row10_GateMadeCommit_IsNotPublished()
    {
        var e = CleanedUp();
        Assert.Contains("moved", AssertStops(e with
        {
            Head = e.Head! with { Head = Head2 },
            Gates = [Receipt(GateKind.Build) with { HeadAfter = Head2 }, Receipt(GateKind.Build, head: Head2), Receipt(GateKind.TargetedTests, head: Head2)],
            Cleanup = e.Cleanup! with { Head = Head2 },
        }, row: 10).Reason);
    }

    [Fact]
    public void Row11_EachGateKindHasItsOwnUnavailableBudget() =>
        Assert.IsType<NextAction.Gates>(Decide(Implemented() with
        {
            Gates = [.. Unavailable(GateKind.Build, Policy.UnavailableGateRetries), .. Unavailable(GateKind.TargetedTests, Policy.UnavailableGateRetries)],
        }, row: 11));

    [Fact]
    public void Row11_UnavailableOnAnOldHead_DoesNotSpendTheHeadsBudget() =>
        Assert.IsType<NextAction.Gates>(Decide(Implemented() with
        {
            Gates = [.. Unavailable(GateKind.Build, Policy.UnavailableGateRetries, head: Head2), .. Unavailable(GateKind.Build, 1)],
        }, row: 11));

    [Fact]
    public void Row11_UnavailableForAnOlderPackage_DoesNotSpendTheBudget() =>
        Assert.IsType<NextAction.Gates>(Decide(Implemented() with
        {
            Gates = [.. Unavailable(GateKind.Build, Policy.UnavailableGateRetries + 1).Select(r => r with { Spec = Package2.Digest })],
        }, row: 11));

    [Fact]
    public void Row11_APassClearsTheUnavailableBudgetForItsGate() =>
        Assert.IsType<NextAction.Gates>(Decide(Implemented() with
        {
            Gates = [.. Unavailable(GateKind.Build, Policy.UnavailableGateRetries + 1), Receipt(GateKind.Build)],
        }, row: 11));

    [Fact]
    public void Row11_GateUnavailableWithinRetries_RerunsGates() =>
        Assert.IsType<NextAction.Gates>(Decide(Implemented() with
        {
            Gates = [Receipt(GateKind.Build), .. Unavailable(GateKind.TargetedTests, Policy.UnavailableGateRetries)],
        }, row: 11));

    [Fact]
    public void Row11_ResumeRestoresTheUnavailableBudget() =>
        Assert.IsType<NextAction.Gates>(Decide(Resumed(Implemented()) with
        {
            Gates = [.. Unavailable(GateKind.TargetedTests, Policy.UnavailableGateRetries + 1)],
        }, row: 11));

    [Fact]
    public void Row11_NoReceipts_RunsGatesOnTheHead() =>
        Assert.Equal(Head1, Assert.IsType<NextAction.Gates>(Decide(Implemented(), row: 11)).Head);

    [Fact]
    public void Row11_PassingReceiptsForAnOldHead_AreIgnored() =>
        Assert.IsType<NextAction.Gates>(Decide(Implemented() with
        {
            Gates = [Receipt(GateKind.Build, head: Head2), Receipt(GateKind.TargetedTests, head: Head2)],
        }, row: 11));

    [Theory]
    [InlineData("dirty", 0)]
    [InlineData("dirty", 1)]
    [InlineData("moved", 0)]
    [InlineData("moved", 1)]
    [InlineData("command", 0)]
    [InlineData("command", 1)]
    public void Row11_UntrustedGateResultFromAnEarlierEpoch_NeverCounts(string cause, int exitCode) =>
        Assert.IsType<NextAction.Gates>(Decide(Resumed(Implemented()) with
        {
            Gates = [Receipt(GateKind.Build), Untrusted(cause, exitCode)],
        }, row: 11));

    [Fact]
    public void Row11_GateMadeCommitAfterResume_IsGatedAgain() =>
        Assert.Equal(Head2, Assert.IsType<NextAction.Gates>(Decide(Resumed(Implemented()) with
        {
            Head = Implemented().Head! with { Head = Head2 },
            Gates = [Receipt(GateKind.Build) with { HeadAfter = Head2 }, Receipt(GateKind.TargetedTests) with { HeadAfter = Head2 }],
        }, row: 11)).Head);

    [Fact]
    public void Row11_PassingReceiptsForAnOlderPackageOnTheSameHead_AreIgnored()
    {
        var e = Reapproved(GatesPassed());
        var action = Decide(e with { Implementation = e.Implementation! with { Spec = Package2.Digest } }, row: 11);
        Assert.Equal(Package2, Assert.IsType<NextAction.Gates>(action).Spec.Package);
    }

    [Fact]
    public void Row11_CleanupThatMovedHead_RerunsGatesOnTheNewHead()
    {
        var e = CleanedUp();
        var action = Decide(e with { Cleanup = e.Cleanup! with { Head = Head2 }, Head = e.Head! with { Head = Head2 } }, row: 11);
        Assert.Equal(Head2, Assert.IsType<NextAction.Gates>(action).Head);
    }

    [Fact]
    public void Row12_GatesPassed_CleansUpTheHead() =>
        Assert.Equal(Head1, Assert.IsType<NextAction.Cleanup>(Decide(GatesPassed(), row: 12)).Head);

    [Fact]
    public void Row12_RepairAfterCleanup_CleansUpAgain()
    {
        var e = CleanedUp();
        var action = Decide(e with
        {
            Head = e.Head! with { Head = Head2 },
            Gates = [Receipt(GateKind.Build, head: Head2), Receipt(GateKind.TargetedTests, head: Head2)],
        }, row: 12);
        Assert.Equal(Head2, Assert.IsType<NextAction.Cleanup>(action).Head);
    }

    [Fact]
    public void Row12_CleanupForAnOlderPackage_IsRedone()
    {
        var e = Reapproved(CleanedUp());
        var action = Decide(e with
        {
            Implementation = e.Implementation! with { Spec = Package2.Digest },
            Gates = [.. e.Gates.Select(r => r with { Spec = Package2.Digest })],
        }, row: 12);
        var cleanup = Assert.IsType<NextAction.Cleanup>(action);
        Assert.Equal(Package2, cleanup.Spec.Package);
        Assert.Equal(Head1, cleanup.Head);
    }

    [Fact]
    public void Row12_CompletedCleanupsAtTheBound_Stop()
    {
        var e = CleanedUp();
        AssertStops(e with
        {
            Cleanup = e.Cleanup! with { Head = Head2 },
            Attempts = [.. Enumerable.Repeat(Tried(ActionKind.Cleanup), 2 * Policy.RevisionCycles + 1)],
        }, row: 12);
    }

    [Fact]
    public void Row12_CompletedCleanupsBelowTheBound_CleanUpAgain()
    {
        var e = CleanedUp();
        Assert.IsType<NextAction.Cleanup>(Decide(e with
        {
            Cleanup = e.Cleanup! with { Head = Head2 },
            Attempts =
            [
                Tried(ActionKind.Triage), Tried(ActionKind.Spec), Tried(ActionKind.Implement),
                .. Enumerable.Repeat(Tried(ActionKind.Cleanup), 2 * Policy.RevisionCycles), Tried(ActionKind.Cleanup, AttemptOutcome.InvalidArtifact),
            ],
        }, row: 12));
    }

    [Fact]
    public void Row12_ResumeRestoresTheCleanupBound()
    {
        var e = CleanedUp();
        Assert.IsType<NextAction.Cleanup>(Decide(Resumed(e) with
        {
            Cleanup = e.Cleanup! with { Head = Head2 },
            Attempts = [.. Enumerable.Repeat(Tried(ActionKind.Cleanup), 2 * Policy.RevisionCycles + 1)],
        }, row: 12));
    }

    [Fact]
    public void Row13_CleanedUpWithoutPr_PublishesTheApprovedHead()
    {
        var publish = Assert.IsType<NextAction.Publish>(Decide(CleanedUp(), row: 13));
        Assert.Equal(Package, publish.Spec.Package);
        Assert.Equal(Head1, publish.Head);
    }

    [Fact]
    public void Row13_PrOnAnotherHead_StopsInsteadOfOverwriting()
    {
        var e = Published();
        AssertStops(e with { Pr = e.Pr! with { Head = Head2 } }, row: 13);
    }

    [Fact]
    public void Row13_PrOnAnotherBranch_Stops()
    {
        var e = Published();
        AssertStops(e with { Pr = e.Pr! with { Branch = "feat/someone-else" } }, row: 13);
    }

    [Fact]
    public void Row13_PrForAnotherPackage_Stops()
    {
        var e = Published();
        AssertStops(e with { Pr = e.Pr! with { Spec = new Digest("digest-0") } }, row: 13);
    }

    [Fact]
    public void Row14_PrOnTheRunHead_Completes()
    {
        var e = Published();
        Assert.Equal(e.Pr, Assert.IsType<NextAction.Complete>(Decide(e, row: 14)).Pr);
    }

    [Theory]
    [InlineData(4, ActionKind.Triage)]
    [InlineData(6, ActionKind.Spec)]
    [InlineData(7, ActionKind.Spec)]
    [InlineData(9, ActionKind.Implement)]
    [InlineData(10, ActionKind.Implement)]
    [InlineData(12, ActionKind.Cleanup)]
    public void InvalidArtifact_GetsOneRetryAtEveryDispatchRule(int row, ActionKind kind)
    {
        var e = row switch
        {
            4 => Claimed(),
            6 => Triaged(),
            7 => SpecPosted() with { Maintainer = [Comment(MaintainerVerb.Revise)] },
            9 => Approved(),
            10 => Implemented() with { Gates = [Failed(GateKind.Build)] },
            _ => GatesPassed(),
        };
        var invalid = Tried(kind, AttemptOutcome.InvalidArtifact, revision: row is 7 or 10);
        var otherKinds = Enum.GetValues<ActionKind>().Where(k => k != kind).Select(k => Tried(k, AttemptOutcome.InvalidArtifact));
        Assert.IsNotType<NextAction.Stop>(Decide(e with { Attempts = [invalid, .. otherKinds] }, row));
        AssertStops(e with { Attempts = [invalid, invalid] }, row);
    }

    [Fact]
    public void ApprovedSpec_OnlyPolicyCanMintOne()
    {
        Assert.Empty(typeof(ApprovedSpec).GetConstructors());
        Assert.All(typeof(ApprovedSpec).GetProperties(), p => Assert.Null(p.SetMethod));
    }

    [Fact]
    public void SameEvidence_GivesEqualActions() => Assert.Equal(Policy.Next(Approved()), Policy.Next(Approved()));

    [Fact]
    public void Approval_ByABot_DoesNotCount() =>
        AssertNotApproved(Comment(MaintainerVerb.Approve) with { AuthorIsBot = true });

    [Fact]
    public void Approval_ByANonAdmin_DoesNotCount() =>
        AssertNotApproved(Comment(MaintainerVerb.Approve) with { AuthorIsRepoAdmin = false });

    [Fact]
    public void Approval_ByAnotherAccount_DoesNotCount() =>
        AssertNotApproved(Comment(MaintainerVerb.Approve) with { AuthorAccountId = "other-admin" });

    [Fact]
    public void Approval_EditedBySomeoneElse_DoesNotCount() =>
        AssertNotApproved(Comment(MaintainerVerb.Approve) with { EditedByOther = true });

    [Fact]
    public void Approval_FromAResumeThatQuotesTheDigest_DoesNotCount() =>
        AssertNotApproved(Comment(MaintainerVerb.Resume) with { SpecDigest = Package.Digest });

    [Fact]
    public void Approval_ForAnOldDigest_DoesNotCount() =>
        AssertNotApproved(Comment(MaintainerVerb.Approve) with { SpecDigest = new Digest("digest-0") });

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Approval_NotPostedAfterTheSpec_DoesNotCount(int minutesAfterSpec) =>
        AssertNotApproved(Comment(MaintainerVerb.Approve, minutesAfterSpec));

    static void AssertNotApproved(MaintainerComment approval) =>
        Assert.IsType<NextAction.AwaitMaintainer>(Decide(SpecPosted() with { Maintainer = [approval] }, row: 7));
}
