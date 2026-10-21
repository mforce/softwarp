using System.Collections.Immutable;
using System.Text.Json;

// The suite makes FIFOs, links to /dev/zero and unreadable files.
[assembly: System.Runtime.Versioning.UnsupportedOSPlatform("windows")]

namespace Softwarp.Core.Tests;

/// <summary>The process dies at this point: the exception escapes AdvanceAsync and the run lock is released.</summary>
sealed class SimulatedCrash() : Exception("simulated crash");

sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

sealed class FakeTracker(WorkItem item, FakeClock clock) : ITracker
{
    public RunId? Claim { get; set; }
    public int Claims { get; private set; }
    public bool CrashAfterClaim { get; set; }
    public bool CrashBeforePost { get; set; }
    public bool CrashAfterPost { get; set; }
    public bool CrashBeforePr { get; set; }
    public List<MaintainerComment> Comments { get; } = [];
    public Dictionary<Digest, DateTimeOffset> Posted { get; } = [];
    public List<SpecView> Views { get; } = [];
    public Publication? Pr { get; private set; }
    public PublishRequest? Published { get; private set; }
    public int PrsOpened { get; private set; }
    public bool CrashAfterPr { get; set; }

    public Task<WorkItem> ReadAsync(ItemId id, CancellationToken ct) => Task.FromResult(item);

    public Task<TrackerFacts> ObserveAsync(RunManifest run, Digest? spec, CancellationToken ct) =>
        Task.FromResult(new TrackerFacts(Claim, [.. Comments],
            spec is { } digest && Posted.TryGetValue(digest, out var at) ? at : null, Pr));

    public Task ClaimAsync(RunManifest run, OperationId operation, CancellationToken ct)
    {
        (Claim, Claims) = (run.Run, Claims + 1);
        if (CrashAfterClaim) throw new SimulatedCrash();
        return Task.CompletedTask;
    }

    public Task PostSpecAsync(RunManifest run, SpecView spec, OperationId operation, CancellationToken ct)
    {
        if (CrashBeforePost) throw new SimulatedCrash();
        Posted[spec.Digest] = clock.Now;
        Views.Add(spec);
        if (CrashAfterPost) throw new SimulatedCrash();
        return Task.CompletedTask;
    }

    public Task OpenPrAsync(PublishRequest request, CancellationToken ct)
    {
        if (CrashBeforePr) throw new SimulatedCrash();
        Pr = new Publication(new Uri("https://github.com/mforce/cluckwork/pull/1300"), request.Run.Branch, request.Head, request.Spec);
        (Published, PrsOpened) = (request, PrsOpened + 1);
        if (CrashAfterPr) throw new SimulatedCrash();
        return Task.CompletedTask;
    }

    public MaintainerComment Comment(MaintainerVerb verb, Digest? digest, string account = "maintainer-id", bool bot = false,
        bool admin = true, bool edited = false, string? feedback = null) =>
        new(new Uri($"https://github.com/mforce/cluckwork/issues/1223#issuecomment-{Comments.Count + 1}"), verb, digest,
            feedback, account, bot, admin, edited, clock.Now.AddMinutes(1));
}

sealed class FakeIsolation(CommitId trunk) : IIsolation
{
    public string WorkflowFile { get; set; } = """
        ---
        {
          "branch": "feat/{issue}-{slug}",
          "commands": {
            "build": ["dotnet", "build"],
            "test_targeted": ["dotnet", "test", "--filter", "{targets}"]
          }
        }
        ---
        Agents read AGENTS.md first.
        """;
    public HeadObservation? Head { get; set; }
    public CommitId? Remote { get; private set; }
    public int Pushes { get; private set; }
    public bool CrashBeforePush { get; set; }
    public bool CrashAfterPush { get; set; }
    int commits;

    public Task<TrunkSnapshot> ReadTrunkAsync(ItemId item, CancellationToken ct) => Task.FromResult(new TrunkSnapshot(trunk, WorkflowFile));

    public Task<Workspace> EnsureAsync(RunManifest run, CancellationToken ct)
    {
        Head ??= new HeadObservation(run.Branch, trunk, Clean: true, DescendsFromTrunk: true);
        return Task.FromResult(new Workspace("/worktrees/cluckwork/1223"));
    }

    public Task<HeadObservation?> ObserveAsync(RunManifest run, CancellationToken ct) => Task.FromResult(Head);
    public Task<CommitId?> RemoteHeadAsync(RunManifest run, CancellationToken ct) => Task.FromResult(Remote);

    public Task PushAsync(RunManifest run, CommitId head, OperationId operation, CancellationToken ct)
    {
        if (CrashBeforePush) throw new SimulatedCrash();
        (Remote, Pushes) = (head, Pushes + 1);
        if (CrashAfterPush) throw new SimulatedCrash();
        return Task.CompletedTask;
    }

    public CommitId Commit()
    {
        Head = Head! with { Head = new CommitId($"commit-{++commits}") };
        return Head.Head;
    }
}

sealed class FakeSpawn(FakeIsolation git, CommitId trunk, FakeClock clock) : ISpawn
{
    sealed record Agent(Brief Brief, AgentHandle Handle)
    {
        public bool Exited { get; set; }
    }

    readonly Dictionary<OperationId, Agent> agents = [];

    public static readonly SpecArtifact Spec = new("# Cap legacy file growth",
        Criteria: [new("AC1", "a legacy file may grow at most 5%")],
        Tickets: [new("T1", "add the growth cap", [])],
        Holdouts: [new("AC1", "grow legacy.cs by 6% in one commit", "the guard names legacy.cs")],
        TestTargets: ["FullyQualifiedName~GrowthCap", "Category=Guards"]);

    /// <summary>What each role's agent does when it runs: its side effects, then the output it writes (null: none).</summary>
    public Dictionary<ActionKind, Func<Brief, string?>> Work { get; } = new()
    {
        [ActionKind.Triage] = _ => Json(new TriageOutput(TriageDecision.Agent,
            new Reproduction(trunk, "dotnet test --filter GrowthCap", "legacy.cs grew 40% with no failure"),
            PriorRejectionSearch: "no .out-of-scope entry", RedundancySearch: "no growth cap in src/")),
        [ActionKind.Spec] = _ => Json(Spec),
        [ActionKind.Implement] = _ =>
        {
            git.Commit();
            return "{}";
        },
        [ActionKind.Cleanup] = _ =>
        {
            git.Commit();
            return Json(new CleanupOutput("Cap legacy file growth", "Closes #1223."));
        },
    };
    public List<Brief> Briefs { get; } = [];
    /// <summary>True lets an agent finish within any deadline later than now; false keeps it running.</summary>
    public bool FinishOnWait { get; set; } = true;
    public ActionKind? CrashBeforeLaunch { get; set; }
    public ActionKind? CrashAfterLaunch { get; set; }
    public ActionKind? CrashAfterExit { get; set; }
    /// <summary>When set, a wait blocks until it completes.</summary>
    public TaskCompletionSource? Hold { get; set; }
    public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int LaunchCount(ActionKind role) => Briefs.Count(b => b.Role == role);

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, typeof(T), Contracts.Default);

    public Task<AgentHandle> EnsureAsync(RunManifest run, OperationId operation, Brief brief, Workspace workspace, CancellationToken ct)
    {
        if (agents.TryGetValue(operation, out var running)) return Task.FromResult(running.Handle);
        if (CrashBeforeLaunch == brief.Role)
        {
            CrashBeforeLaunch = null;
            throw new SimulatedCrash();
        }
        var handle = new AgentHandle($"agent-{operation.Value}", brief.LogFile, $"tail -f {brief.LogFile}");
        agents[operation] = new Agent(brief, handle);
        Briefs.Add(brief);
        if (CrashAfterLaunch == brief.Role)
        {
            CrashAfterLaunch = null;
            throw new SimulatedCrash();
        }
        return Task.FromResult(handle);
    }

    public Task<AgentState> InspectAsync(RunManifest run, OperationId operation, CancellationToken ct) =>
        Task.FromResult<AgentState>(!agents.TryGetValue(operation, out var agent) ? new AgentState.NotLaunched()
            : agent.Exited ? new AgentState.Exited() : new AgentState.Running(agent.Handle));

    public async Task<AgentState> WaitAsync(AgentHandle handle, DateTimeOffset deadline, CancellationToken ct)
    {
        var agent = agents.Values.Single(a => a.Handle == handle);
        if (Hold is { } hold)
        {
            Holding.TrySetResult();
            await hold.Task.WaitAsync(ct);
        }
        else if (!FinishOnWait || deadline <= clock.Now) return new AgentState.Running(handle);
        Finish(agent);
        if (CrashAfterExit == agent.Brief.Role)
        {
            CrashAfterExit = null;
            throw new SimulatedCrash();
        }
        return new AgentState.Exited();
    }

    void Finish(Agent agent)
    {
        if (agent.Exited) return;
        if (Work[agent.Brief.Role](agent.Brief) is { } output)
        {
            File.WriteAllText(agent.Brief.OutputFile, output);
            File.WriteAllText(agent.Brief.LogFile, output); // a real agent's log quotes what it wrote
        }
        agent.Exited = true;
    }
}

sealed class FakeGates : IGates
{
    public List<CommandSpec> Runs { get; } = [];
    public bool Crash { get; set; }
    /// <summary>Written to every log after the command line.</summary>
    public string Output { get; set; } = "";
    /// <summary>When set, each log is padded (sparse) to this many bytes.</summary>
    public long? LogBytes { get; set; }
    /// <summary>When set, each log path is replaced by a link to this file after the command runs.</summary>
    public string? LinkLogTo { get; set; }
    /// <summary>The exit code for each run, by command; 0 by default.</summary>
    public Func<CommandSpec, int> ExitCode { get; set; } = _ => 0;
    /// <summary>Runs after each command has written its log, with the command and the log path.</summary>
    public Action<CommandSpec, string> Ran { get; set; } = (_, _) => { };

    public Task<GateRun> RunAsync(Workspace workspace, CommandSpec command, string logFile, CancellationToken ct)
    {
        if (Crash) throw new SimulatedCrash();
        Runs.Add(command);
        var code = ExitCode(command);
        File.WriteAllText(logFile, $"$ {command.Executable} {string.Join(' ', command.Arguments)}\n{Output}exit {code}\n");
        if (LogBytes is { } size)
            using (var log = new FileStream(logFile, FileMode.Open)) log.SetLength(size);
        if (LinkLogTo is { } target)
        {
            File.Delete(logFile);
            File.CreateSymbolicLink(logFile, target);
        }
        Ran(command, logFile);
        return Task.FromResult(new GateRun(code, command.Arguments[0] == "test" ? 3 : null));
    }
}

sealed class Harness : IDisposable
{
    public static readonly CommitId Trunk = new("trunk-sha");
    public static readonly ItemId Item = Fixture.Item;
    public static readonly WorkItem WorkItem = Fixture.Manifest.Item;

    public TempRoot Root { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeTracker Tracker { get; }
    public FakeIsolation Git { get; } = new(Trunk);
    public FakeSpawn Spawn { get; }
    public FakeGates Gates { get; } = new();
    public RunStore Store { get; }
    public Factory Factory { get; }

    public Harness()
    {
        Tracker = new FakeTracker(WorkItem, Clock);
        Spawn = new FakeSpawn(Git, Trunk, Clock);
        Store = new RunStore(Root.Path);
        Factory = new Factory(Store, Tracker, Git, Spawn, Gates, "maintainer-id",
            new ModelPins("glm-5.3", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5"), Clock);
    }

    public void Dispose() => Root.Dispose();

    public async Task<RunId> StartAsync()
    {
        var started = await Factory.StartAsync(Item, CancellationToken.None);
        Assert.Equal(ExitStatus.Ok, started.Exit);
        return started.Run;
    }

    public Task<AdvanceResult> AdvanceAsync(RunId run, TimeSpan? wait = null) =>
        Factory.AdvanceAsync(run, wait ?? TimeSpan.FromMinutes(30), CancellationToken.None);

    public RunRecord Record(RunId run) => Store.Read(run)!;

    /// <summary>Advances until the run waits on the maintainer for the spec, then approves it as the maintainer.</summary>
    public async Task<SpecView> SpecApprovedAsync(RunId run)
    {
        await AdvanceAsync(run); // triage
        await AdvanceAsync(run); // spec
        Assert.Equal(ExitStatus.WaitingOnMaintainer, (await AdvanceAsync(run)).Exit); // post the spec
        var view = Factory.Spec(run)!;
        Tracker.Comments.Add(Tracker.Comment(MaintainerVerb.Approve, view.Digest));
        return view;
    }

    /// <summary>Starts a run and takes it to an approved spec.</summary>
    public async Task<RunId> ApprovedAsync()
    {
        var run = await StartAsync();
        await SpecApprovedAsync(run);
        return run;
    }

    /// <summary>Starts a run and takes it through the implementer, up to the gates.</summary>
    public async Task<RunId> ImplementedAsync()
    {
        var run = await ApprovedAsync();
        await AdvanceAsync(run);
        return run;
    }

    public async Task<AdvanceResult> AdvanceUntilAsync(RunId run, Func<AdvanceResult, bool> done, int limit = 20)
    {
        for (var i = 0; i < limit; i++)
        {
            var result = await AdvanceAsync(run);
            if (done(result)) return result;
        }
        throw new InvalidOperationException($"the run did not settle in {limit} advances");
    }

    public static string[] Effects(RunRecord record) => [.. record.Operations.Select(o => o.Id.Value[5..])];
}
