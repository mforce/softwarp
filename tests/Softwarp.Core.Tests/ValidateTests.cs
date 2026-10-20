using System.Collections.Immutable;

namespace Softwarp.Core.Tests;

public class ValidateTests
{
    static readonly SpecArtifact Valid = new("# Cap legacy file growth",
        Criteria: [new("AC1", "a legacy file may grow at most 5%"), new("AC2", "a test file may not pass 1,500 lines")],
        Tickets: [new("T1", "add the growth cap", []), new("T2", "add the test-file cap", ["T1"])],
        Holdouts: [new("AC1", "grow a legacy file by 6%", "the guard fails")],
        TestTargets: ["FullyQualifiedName~GrowthCap", "Category=Guards", "tests/Guards.Tests:~/x.y_z-1"]);

    [Fact]
    public void ValidSpec_HasNoViolation() => Assert.Empty(Validation.Validate(Valid));

    [Fact]
    public void CyclicTickets_AreRejected() => Assert.Single(Validation.Validate(Valid with
    {
        Tickets = [new("T1", "a", ["T2"]), new("T2", "b", ["T1"])],
    }));

    [Fact]
    public void SelfBlockingTicket_IsRejected() => Assert.Single(Validation.Validate(Valid with
    {
        Tickets = [new("T1", "a", ["T1"])],
    }));

    [Fact]
    public void BlockerThatIsNotATicket_IsRejected() => Assert.Single(Validation.Validate(Valid with
    {
        Tickets = [new("T1", "a", ["T9"])],
    }));

    [Theory]
    [InlineData("Name~Foo Bar")]
    [InlineData("Name~Foo;rm -rf /")]
    [InlineData("$(whoami)")]
    [InlineData("`id`")]
    [InlineData("Name~'Foo'")]
    [InlineData("Name~Foo\nBar")]
    [InlineData("Name~Föo")]
    [InlineData("")]
    [InlineData("-x")]
    [InlineData("--filter")]
    [InlineData("A|B")]
    [InlineData("A&B")]
    [InlineData("(A)")]
    [InlineData("Name!=Slow")]
    public void TestTargetOutsideTheAllowPattern_IsRejected(string target) =>
        Assert.Single(Validation.Validate(Valid with { TestTargets = [target] }));

    [Fact]
    public void SpecWithoutTestTarget_IsRejected() =>
        Assert.Single(Validation.Validate(Valid with { TestTargets = [] }));

    [Fact]
    public void SpecWithoutCriterion_IsRejected() =>
        Assert.Single(Validation.Validate(Valid with { Criteria = [], Holdouts = [] }));

    [Fact]
    public void DuplicateCriterionId_IsRejected() =>
        Assert.Single(Validation.Validate(Valid with { Criteria = [new("AC1", "a"), new("AC1", "b")] }));

    [Fact]
    public void DuplicateTicketId_IsRejected() =>
        Assert.Single(Validation.Validate(Valid with { Tickets = [new("T1", "a", []), new("T1", "b", [])] }));

    [Fact]
    public void HoldoutForUnknownCriterion_IsRejected() =>
        Assert.Single(Validation.Validate(Valid with { Holdouts = [new("AC9", "scenario", "expected")] }));

    [Theory]
    [InlineData("spec text")]
    [InlineData("criterion id")]
    [InlineData("criterion text")]
    [InlineData("ticket id")]
    [InlineData("ticket text")]
    [InlineData("holdout scenario")]
    [InlineData("holdout expected")]
    public void BlankField_IsRejected(string field) => Assert.Single(Validation.Validate(field switch
    {
        "spec text" => Valid with { Markdown = "" },
        "criterion id" => Valid with { Criteria = [Valid.Criteria[0], Valid.Criteria[1] with { Id = " " }] },
        "criterion text" => Valid with { Criteria = [Valid.Criteria[0], Valid.Criteria[1] with { Text = " " }] },
        "ticket id" => Valid with { Tickets = [Valid.Tickets[0], Valid.Tickets[1] with { Id = "" }] },
        "ticket text" => Valid with { Tickets = [Valid.Tickets[0], Valid.Tickets[1] with { Text = "" }] },
        "holdout scenario" => Valid with { Holdouts = [Valid.Holdouts[0] with { Scenario = " " }] },
        _ => Valid with { Holdouts = [Valid.Holdouts[0] with { Expected = "" }] },
    }));

    [Fact]
    public void EveryViolation_IsListed() => Assert.Equal(3, Validation.Validate(Valid with
    {
        Tickets = [new("T1", "a", ["T1"])],
        Holdouts = [new("AC9", "scenario", "expected")],
        TestTargets = ["a b"],
    }).Length);

    static readonly TriageArtifact AgentReady = Fixture.Triaged().Triage!;

    static ImmutableArray<string> Violations(TriageArtifact triage) => Validation.Validate(triage, Fixture.Manifest);

    [Fact]
    public void AgentReadyTriage_HasNoViolation() => Assert.Empty(Violations(AgentReady));

    [Fact]
    public void TriageReproducedOnAnotherTrunk_IsRejected() => Assert.Single(Violations(AgentReady with
    {
        Reproduction = AgentReady.Reproduction with { Trunk = new CommitId("stale-trunk") },
    }));

    [Fact]
    public void ReproductionWithoutCommand_IsRejected() => Assert.Single(Violations(AgentReady with
    {
        Reproduction = AgentReady.Reproduction with { Command = " " },
    }));

    [Fact]
    public void ReproductionWithoutEvidence_IsRejected() => Assert.Single(Violations(AgentReady with
    {
        Reproduction = AgentReady.Reproduction with { Evidence = "" },
    }));

    [Fact]
    public void TriageWithoutRedundancySearch_IsRejected() =>
        Assert.Single(Violations(AgentReady with { RedundancySearch = " " }));

    [Fact]
    public void TriageWithoutPriorRejectionSearch_IsRejected() =>
        Assert.Single(Violations(AgentReady with { PriorRejectionSearch = "" }));

    [Theory]
    [InlineData(TriageDecision.Human)]
    [InlineData(TriageDecision.NeedsInfo)]
    [InlineData(TriageDecision.WontFix)]
    public void TriageThatStops_NeedsNoReproductionOrSearch(TriageDecision decision) => Assert.Empty(Violations(new(
        decision, new Reproduction(new CommitId("other"), "", ""), PriorRejectionSearch: "", RedundancySearch: "", Epoch: 0)));
}
