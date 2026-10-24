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

    /// <summary>Each ticket reads <c>id[blockers]</c>. Duplicate ids and a blocker named twice must not free a ticket early.</summary>
    [Theory]
    [InlineData("T1[T2] T2[T1] T3[] T4[T2,T3] T5[T3]", "tickets T1, T2, T4 are in or behind a blocking cycle")]
    [InlineData("A[] A[] B[A,C] C[B]", "ticket id 'A' is not unique; tickets B, C are in or behind a blocking cycle")]
    [InlineData("A[] B[A,A,C] C[C]", "tickets B, C are in or behind a blocking cycle")]
    public void TicketsInOrBehindACycle_AreNamedAndTheRestAreNot(string tickets, string expected) => Assert.Equal(expected,
        string.Join("; ", Validation.Validate(Valid with
        {
            Tickets = [.. tickets.Split(' ').Select(t => t.Split('[', ']')).Select(p => new Ticket(p[0], "x", [.. p[1].Split(',', StringSplitOptions.RemoveEmptyEntries)]))],
        })));

    /// <summary>Listed last-blocker-first, the worst order for a check that rescans the list for the next ticket.</summary>
    [Fact]
    public async Task LongChainOfTickets_IsCheckedInLinearTime()
    {
        var chain = Enumerable.Range(0, 50_000).Reverse()
            .Select(i => new Ticket($"T{i}", "step", i == 0 ? [] : [$"T{i - 1}"])).ToImmutableArray();

        Assert.Empty(await Task.Run(() => Validation.Validate(Valid with { Tickets = chain })).WaitAsync(TimeSpan.FromSeconds(5)));
    }

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
    [InlineData("/p:X=Y")]
    [InlineData("/flp:logfile=/tmp/x")]
    [InlineData("/abs/path")]
    [InlineData("../x.csproj")]
    [InlineData("tests/../../x")]
    [InlineData("..")]
    [InlineData("./..")]
    public void TestTargetOutsideTheAllowPattern_IsRejected(string target) =>
        Assert.Single(Validation.Validate(Valid with { TestTargets = [target] }));

    [Theory]
    [InlineData("FullyQualifiedName~Foo.Bar")]
    [InlineData("tests/test_x.py::TestA::test_b")]
    [InlineData("./pkg/...")]
    [InlineData("Name=Slow")]
    [InlineData("a.b-c_d")]
    public void TestTargetOfAnyStack_IsAccepted(string target) =>
        Assert.Empty(Validation.Validate(Valid with { TestTargets = [target] }));

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

    [Fact]
    public void Violation_QuotesOnlyTheStartOfLongAgentText()
    {
        var id = new string('x', 10_000);
        var violations = Validation.Validate(Valid with
        {
            Tickets = [new(id, "a", [id])],
            Criteria = [.. Valid.Criteria, new(id, " ")],
            TestTargets = [id + " "],
        });
        Assert.Equal(3, violations.Length);
        Assert.All(violations, v => Assert.InRange(v.Length, 1, 300));
    }

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

    [Fact]
    public void CleanupWithTitleAndBody_HasNoViolation() =>
        Assert.Empty(Validation.Validate(new CleanupOutput("Cap legacy file growth", "Closes #1223.")));

    [Theory]
    [InlineData(" ", "body")]
    [InlineData("two\nlines", "body")]
    [InlineData("title", "")]
    public void CleanupWithoutUsablePrText_IsRejected(string title, string body) =>
        Assert.Single(Validation.Validate(new CleanupOutput(title, body)));
}
