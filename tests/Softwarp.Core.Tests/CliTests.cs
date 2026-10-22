using System.Text.Json;
using Softwarp.Cli;

namespace Softwarp.Core.Tests;

/// <summary>The softwarp CLI over a factory built from this project's fakes.</summary>
public sealed class CliTests : IDisposable
{
    readonly Harness h = new();
    public void Dispose() => h.Dispose();

    Task<(int Exit, string Out, string Error)> RunAsync(params string[] args) => RunAsync(() => h.Factory, args);

    static async Task<(int Exit, string Out, string Error)> RunAsync(Func<Factory> compose, params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var exit = await Cli.Cli.RunAsync(args, compose, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    static string Item => $"{Harness.Item.Owner}/{Harness.Item.Repo}";
    static string Issue => $"{Harness.Item.Number}";

    [Theory]
    [InlineData]
    [InlineData("merge", "mforce/cluckwork/1223/abc")]
    [InlineData("start", "mforce/cluckwork")]
    [InlineData("start", "mforce", "1223")]
    [InlineData("start", "mforce/cluckwork", "-1")]
    [InlineData("start", "mforce/cluckwork", "12a")]
    [InlineData("advance", "run", "--wait", "5x")]
    [InlineData("advance", "run", "--wait", "-5s")]
    [InlineData("advance", "run", "--force")]
    [InlineData("status")]
    [InlineData("status", "a", "b")]
    public async Task ArgumentsThatNameNoVerb_AreInvalidInputBeforeAnythingIsComposed(params string[] args)
    {
        var (exit, _, stderr) = await RunAsync(() => throw new InvalidOperationException("composed"), args);
        Assert.Equal(2, exit);
        Assert.StartsWith("usage: softwarp start", stderr);
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    public void Wait_IsSecondsMinutesOrHours(string wait, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds),
            Assert.IsType<Command.Advance>(Cli.Cli.Parse(["advance", "r", "--wait", wait, "--json"])!.Command).Wait);

    [Fact]
    public async Task Help_PrintsTheUsageAndTheExitCodes()
    {
        var (exit, stdout, _) = await RunAsync("--help");
        Assert.Equal(0, exit);
        Assert.Contains("4 waiting on an agent, 5 waiting on the maintainer", stdout);
    }

    [Fact]
    public async Task AMachineConfigTheCompositionRefuses_IsInvalidInputAndSaysWhy()
    {
        var (exit, _, stderr) = await RunAsync(() => throw new InvalidOperationException("no tracker binding (slice 2b-3)"), "status", "x");
        Assert.Equal(2, exit);
        Assert.Equal("softwarp: no tracker binding (slice 2b-3)\n", stderr);
    }

    [Fact]
    public async Task Start_ThenAdvanceAndStatus_PrintTheFactorysResultAndExitWithItsStatus()
    {
        var (exit, stdout, _) = await RunAsync("start", Item, Issue, "--json");
        Assert.Equal(0, exit);
        using var started = JsonDocument.Parse(stdout);
        Assert.Equal("Ok", started.RootElement.GetProperty("exit").GetString());
        var run = started.RootElement.GetProperty("run").GetString()!;
        Assert.StartsWith("mforce/cluckwork/1223/", run);

        h.Spawn.FinishOnWait = false;
        var (advanced, advanceOut, _) = await RunAsync("advance", run, "--wait", "0s", "--json");
        Assert.Equal(4, advanced);
        using var waiting = JsonDocument.Parse(advanceOut);
        Assert.Equal("WaitingOnAgent", waiting.RootElement.GetProperty("exit").GetString());
        Assert.Equal("Triage", waiting.RootElement.GetProperty("rule").GetString());
        Assert.StartsWith("tail -f ", waiting.RootElement.GetProperty("agent").GetProperty("watch_command").GetString());

        var (status, statusOut, _) = await RunAsync("status", run);
        Assert.Equal(4, status);
        Assert.StartsWith("an agent is running; watch it with `tail -f ", statusOut);
    }

    [Fact]
    public async Task Spec_PrintsThePostedSpecOrSaysThereIsNone()
    {
        var run = await h.StartAsync();
        var (none, _, noneError) = await RunAsync("spec", run.Value);
        Assert.Equal((2, $"softwarp: run {run.Value} has no spec package\n"), (none, noneError));

        var view = await h.SpecApprovedAsync(run);
        var (exit, text, _) = await RunAsync("spec", run.Value);
        Assert.Equal((0, view.Markdown + "\n"), (exit, text));
        var (_, json, _) = await RunAsync("spec", run.Value, "--json");
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(view.Digest.Value, parsed.RootElement.GetProperty("digest").GetString());
    }

    [Fact]
    public async Task ExitCodes_AreTheFactorysStatuses()
    {
        var run = await h.ImplementedAsync();
        h.Gates.ExitCode = command => command.Arguments[0] == "build" ? 1 : 0;
        Assert.Equal(3, (await RunAsync("advance", run.Value)).Exit);

        h.Tracker.Claim = new RunId("someone/else/1/x");
        Assert.Equal(5, (await RunAsync("advance", run.Value)).Exit);
        Assert.Equal(2, (await RunAsync("advance", "no/such/1/run")).Exit);
    }

    [Fact]
    public async Task AFailureTheFactoryDidNotDecide_ExitsOneAndSaysWhat()
    {
        var run = await h.ImplementedAsync();
        h.Gates.Crash = true;
        var (exit, _, stderr) = await RunAsync("advance", run.Value);
        Assert.Equal((1, "softwarp: SimulatedCrash: simulated crash\n"), (exit, stderr));
    }
}
