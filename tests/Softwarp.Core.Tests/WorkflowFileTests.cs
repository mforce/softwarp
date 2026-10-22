namespace Softwarp.Core.Tests;

public class WorkflowFileTests
{
    const string Commands = """
        "build": ["dotnet", "build", "-warnaserror"],
        "test_targeted": ["dotnet", "test", "--filter", "{targets}"]
        """;

    static string File(string commands = Commands, string extra = "") => $$"""
        ---
        {
          "branch": "feat/{issue}-{slug}",
          "test_report": "trx",
          {{extra}}
          "commands": { {{commands}} }
        }
        ---
        Repo-specific guidance for every agent.
        """;

    static string Rejected(string text) => Assert.Throws<FormatException>(() => WorkflowFile.Parse(text)).Message;

    [Fact]
    public void ValidFile_GivesTheGateCommandsAndBranchTemplate()
    {
        var (workflow, branch, warnings) = WorkflowFile.Parse(File());
        Assert.Empty(warnings);
        Assert.Equal("dotnet", workflow.Build.Executable);
        Assert.Equal(new[] { "build", "-warnaserror" }, workflow.Build.Arguments);
        Assert.Equal("dotnet", workflow.TargetedTests.Executable);
        Assert.Equal(new[] { "test", "--filter", "{targets}" }, workflow.TargetedTests.Arguments);
        Assert.Equal("feat/{issue}-{slug}", branch);
    }

    [Fact]
    public void EveryDocumentedKey_IsAccepted()
    {
        var (workflow, _, _) = WorkflowFile.Parse(File(
            commands: Commands + """
                ,
                "test": ["dotnet", "test"],
                "lint": ["dotnet", "format", "--verify-no-changes"],
                "run": ["dotnet", "run"],
                "services_up": ["sg", "docker", "-c", "docker compose up -d"]
                """,
            extra: """
                "trust": 2, "tracker": "github", "verify_skill": ".agents/skills/verify", "review_bot": "none",
                "wip_limit": 3, "routines": [{ "name": "banned-patterns", "every": "1d", "skill": "gardening-scan" }],
                """));
        Assert.Equal("dotnet", workflow.Build.Executable);
    }

    [Fact]
    public void CrlfLineEndingsAndByteOrderMark_AreAccepted() =>
        Assert.Equal("feat/{issue}-{slug}", WorkflowFile.Parse("﻿" + File().ReplaceLineEndings("\r\n")).BranchTemplate);

    [Theory]
    [InlineData("""["dotnet", "build\u0000--no-restore"]""")]
    [InlineData("""["dotnet\u0000", "build"]""")]
    public void NulInAnArgvElement_IsRejected(string build) =>
        Assert.Contains("NUL", Rejected(File(Commands.Replace("""["dotnet", "build", "-warnaserror"]""", build))));

    [Fact]
    public void NulInAnOptionalCommand_IsRejected() =>
        Assert.Contains("NUL", Rejected(File(Commands + """, "lint": ["dotnet", "format\u0000"]""")));

    [Theory]
    [InlineData("""["dotnet", "test", "--filter={targets}"]""")]
    [InlineData("""["dotnet", "test", "--filter", "Category={targets}"]""")]
    [InlineData("""["dotnet", "test", "--filter", "{targets} "]""")]
    public void TargetsInsideAnArgvElement_IsRejected(string testTargeted) =>
        Assert.Contains("whole argv element",
            Rejected(File(Commands.Replace("""["dotnet", "test", "--filter", "{targets}"]""", testTargeted))));

    [Theory]
    [InlineData("""["sh", "-c", "dotnet test --filter \"$1\"", "sh", "{targets}"]""")]
    [InlineData("""["bash", "-c", "{targets}"]""")]
    [InlineData("""["/usr/bin/bash", "-ec", "dotnet test \"$@\"", "bash", "{targets}"]""")]
    [InlineData("""["/bin/zsh", "-lc", "x", "{targets}"]""")]
    [InlineData("""["dash", "-x", "-c", "x", "{targets}"]""")]
    public void TargetsInAShellCommandWithDashC_IsRejected(string testTargeted) =>
        Assert.Contains("shell",
            Rejected(File(Commands.Replace("""["dotnet", "test", "--filter", "{targets}"]""", testTargeted))));

    [Fact]
    public void TargetsAsTheExecutable_IsRejected() =>
        Assert.Contains("executable",
            Rejected(File(Commands.Replace("""["dotnet", "test", "--filter", "{targets}"]""", """["{targets}"]"""))));

    [Fact]
    public void TargetedTestsWithoutTargets_IsRejected() =>
        Assert.Contains("{targets}",
            Rejected(File(Commands.Replace("""["dotnet", "test", "--filter", "{targets}"]""", """["dotnet", "test"]"""))));

    [Theory]
    [InlineData("""["bash", "scripts/test-targets.sh", "{targets}"]""")]
    [InlineData("""["/opt/bashful", "-c", "x", "{targets}"]""")]
    public void TargetsInAShellWithoutDashCOrInANonShell_Parse(string testTargeted) =>
        Assert.Equal(["{targets}"], WorkflowFile.Parse(
            File(Commands.Replace("""["dotnet", "test", "--filter", "{targets}"]""", testTargeted))).Workflow.TargetedTests.Arguments.TakeLast(1));

    [Theory]
    [InlineData("make")]
    [InlineData("/usr/bin/gmake")]
    [InlineData("just")]
    public void TargetsPassedToMakeOrJust_ParseWithAWarning(string executable)
    {
        var parsed = WorkflowFile.Parse(File(Commands.Replace("""["dotnet", "test", "--filter", "{targets}"]""",
            $$"""["{{executable}}", "test", "{targets}"]""")));
        var warning = Assert.Single(parsed.Warnings);
        Assert.Contains("test_targeted", warning);
        Assert.Contains("'='", warning);
        Assert.Equal(executable, parsed.Workflow.TargetedTests.Executable);
    }

    [Fact]
    public void MakeWithoutTargets_HasNoWarning() =>
        Assert.Empty(WorkflowFile.Parse(File(Commands.Replace("""["dotnet", "build", "-warnaserror"]""", """["make", "build"]"""))).Warnings);

    [Fact]
    public void ShellDashCWithoutTargets_Parses() =>
        Assert.Equal("sh", WorkflowFile.Parse(
            File(Commands.Replace("""["dotnet", "build", "-warnaserror"]""", """["sh", "-c", "make build"]"""))).Workflow.Build.Executable);

    [Theory]
    [InlineData("\"trust_level\": 1,")]
    [InlineData("\"merge\": true,")]
    public void UnknownTopLevelKey_IsRejected(string extra) => Rejected(File(extra: extra));

    [Fact]
    public void UnknownCommandKey_IsRejected() => Rejected(File(Commands + """, "deploy": ["./deploy.sh"]"""));

    [Theory]
    [InlineData("\"test_report\": \"trx\"", TestReport.Trx)]
    [InlineData("\"test_report\": \"junit\"", TestReport.Junit)]
    public void TestReport_IsParsed(string line, TestReport report) =>
        Assert.Equal(report, WorkflowFile.Parse(File().Replace("\"test_report\": \"trx\"", line)).Workflow.TestReport);

    [Theory]
    [InlineData("")]
    [InlineData("\"test_report\": \"TRX\",")]
    [InlineData("\"test_report\": \"xunit\",")]
    public void MissingOrUnknownTestReport_IsRejected(string line) =>
        Assert.Contains("test_report", Rejected(File().Replace("\"test_report\": \"trx\",", line)));

    [Fact]
    public void TargetsJoin_IsParsedAndAbsentMeansNone()
    {
        Assert.Null(WorkflowFile.Parse(File()).Workflow.TargetsJoin);
        Assert.Equal("|", WorkflowFile.Parse(File(extra: "\"targets_join\": \"|\",")).Workflow.TargetsJoin);
    }

    [Theory]
    [InlineData("\"targets_join\": \"\",")]
    [InlineData("\"targets_join\": \"|\\n\",")]
    [InlineData("\"targets_join\": \"\\u0000\",")]
    public void EmptyOrControlCharacterTargetsJoin_IsRejected(string extra) =>
        Assert.Contains("targets_join", Rejected(File(extra: extra)));

    [Theory]
    [InlineData("build")]
    [InlineData("test_targeted")]
    public void MissingGateCommand_IsRejected(string command) =>
        Assert.Contains(command, Rejected(File(string.Join(",\n",
            Commands.Split(",\n").Where(line => !line.TrimStart().StartsWith($"\"{command}\""))))));

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""[""]""")]
    [InlineData("""null""")]
    public void EmptyOrNullBuildCommand_IsRejected(string build) =>
        Rejected(File(Commands.Replace("""["dotnet", "build", "-warnaserror"]""", build)));

    [Theory]
    [InlineData("\"branch\": \"feat/{slug}\"")]
    [InlineData("\"branch\": \"-{issue}\"")]
    [InlineData("\"branch\": \"feat/../{issue}\"")]
    [InlineData("\"branch\": \"feat/{issue} x\"")]
    public void BranchTemplateBreakingTheConvention_IsRejected(string branch) =>
        Assert.Contains("branch", Rejected(File().Replace("\"branch\": \"feat/{issue}-{slug}\"", branch)));

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void TrustOutsideStep1Rungs_IsRejected(int trust) =>
        Assert.Contains("trust", Rejected(File(extra: $"\"trust\": {trust},")));

    [Theory]
    [InlineData("{ \"branch\": \"b\" }\n---\n")]
    [InlineData("---\n{ \"branch\": \"b\" }\n")]
    [InlineData("---\n{ \"branch\": ")]
    [InlineData("---\n{ \"branch\": \"{issue}\", }\n---\n")]
    [InlineData("")]
    public void MissingOrBrokenFrontMatter_IsRejected(string text) => Rejected(text);

    [Fact]
    public void FrontMatterAfterAnotherFirstLine_IsRejected() =>
        Assert.Contains("must start with", Rejected("# Workflow" + File()["---".Length..]));

    [Fact]
    public void MultipleRules_AreAllNamed()
    {
        var message = Rejected(File("""
            "build": ["dotnet\u0000"],
            "test_targeted": ["dotnet", "test", "--filter={targets}"]
            """));
        Assert.Contains("NUL", message);
        Assert.Contains("whole argv element", message);
    }

    [Theory]
    [InlineData("feat/{issue}-{slug}", "Cap legacy file growth!", "feat/1223-cap-legacy-file-growth")]
    [InlineData("softwarp/{issue}", "anything", "softwarp/1223")]
    [InlineData("fix/{issue}-{slug}", "Ünïcode — only…", "fix/1223-n-code-only")]
    [InlineData("fix/{issue}-{slug}", "!!!", "fix/1223-item")]
    public void Branch_FillsIssueAndSlug(string template, string title, string branch) =>
        Assert.Equal(branch, WorkflowFile.Branch(template, Fixture.Manifest.Item with { Title = title }));

    [Fact]
    public void Branch_SlugIsBounded() =>
        Assert.Equal("f/1223-" + new string('a', 40),
            WorkflowFile.Branch("f/{issue}-{slug}", Fixture.Manifest.Item with { Title = new string('a', 100) }));
}
