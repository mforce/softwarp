using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Softwarp.Core;

/// <summary>Owns durable effects and recovery. <see cref="Policy.Explain"/> picks every transition; the factory runs
/// exactly one effect for it per call, with its intent written before the effect and its receipt after.</summary>
public sealed class Factory(RunStore store, ITracker tracker, IIsolation isolation, ISpawn spawn, IGates gates,
    string maintainerAccountId, ModelPins models, TimeProvider time)
{
    internal const int MaxOutputBytes = 1 << 20;
    const int MaxViolations = 20, MaxViolationChars = 300;
    const int LogTailBytes = 8000;
    const long MaxLogBytes = 64 << 20;
    static readonly Contracts C = Contracts.Default;

    /// <summary>Pins the item, trunk and WORKFLOW.md at trunk in a manifest, then claims the item. A retry adopts its
    /// own claim; a claim by anything else fails closed.</summary>
    public async Task<AdvanceResult> StartAsync(ItemId item, CancellationToken ct)
    {
        RunLease? lease;
        try
        {
            lease = store.TryLock(item);
        }
        catch (ArgumentException e)
        {
            return new(ExitStatus.InvalidInput, default, e.Message);
        }
        if (lease is null) return new(ExitStatus.Busy, default, "another caller holds this item");
        using (lease)
        {
            var manifest = lease.Manifest;
            var warnings = "";
            if (manifest is null)
            {
                var work = await tracker.ReadAsync(item, ct);
                var trunk = await isolation.ReadTrunkAsync(item, ct);
                ParsedWorkflow workflow;
                try
                {
                    workflow = WorkflowFile.Parse(trunk.WorkflowFile);
                }
                catch (FormatException e)
                {
                    return new(ExitStatus.InvalidInput, default, e.Message);
                }
                manifest = new RunManifest(RunStore.NewRunId(item), trunk.Commit,
                    WorkflowFile.Branch(workflow.BranchTemplate, work), maintainerAccountId, workflow.Workflow, work,
                    RunStore.Hash(Encoding.UTF8.GetBytes(trunk.WorkflowFile)), models);
                lease.Create(manifest);
                warnings = string.Concat(workflow.Warnings.Select(warning => $"; warning: {warning}"));
            }

            var claim = (await tracker.ObserveAsync(manifest, null, ct)).Claim;
            if (lease.Read().Open is { Intent: Intent.Claim } open) lease.Complete(open.Id, Happened(claim == manifest.Run));
            if (claim == manifest.Run) return new(ExitStatus.Ok, manifest.Run, $"run {manifest.Run.Value} holds the item");
            if (claim is { } other)
                return new(ExitStatus.WaitingOnMaintainer, manifest.Run, $"the item is claimed by {other.Value}, not by this run");
            var op = lease.Begin(new Intent.Claim());
            await tracker.ClaimAsync(manifest, op, ct);
            lease.Complete(op, new Outcome.Done());
            return new(ExitStatus.Ok, manifest.Run, $"run {manifest.Run.Value} claimed the item{warnings}");
        }
    }

    /// <summary>Runs the one effect the policy picks. Wait bounds how long the call may block on a running agent.</summary>
    public async Task<AdvanceResult> AdvanceAsync(RunId run, TimeSpan? wait, CancellationToken ct)
    {
        if (RunStore.Item(run) is not { } item || store.Read(run) is null) return Unknown(run);
        using var lease = store.TryLock(item);
        if (lease is null) return new(ExitStatus.Busy, run, "another caller is advancing this run");
        var now = await ObserveAsync(lease.Read(), ct);
        var (rule, action) = Policy.Explain(now.Evidence);
        return await ActAsync(new Step(lease, now, rule, time.GetUtcNow() + (wait ?? TimeSpan.Zero), ct), action);
    }

    /// <summary>What the next advance would do, read without the lock and without effects.</summary>
    public async Task<AdvanceResult> StatusAsync(RunId run, CancellationToken ct)
    {
        if (store.Read(run) is not { } record) return Unknown(run);
        var now = await ObserveAsync(record, ct);
        var (rule, action) = Policy.Explain(now.Evidence);
        return Settled(record.Manifest, rule, action);
    }

    /// <summary>The newest valid spec package as it is posted for approval; null before one exists.</summary>
    public SpecView? Spec(RunId run) =>
        store.Read(run) is { } record && record.Newest<Outcome.Specified>() is { } spec ? View(record, spec.Package) : null;

    sealed record Snapshot(RunRecord Record, TrackerFacts Facts, Evidence Evidence);

    sealed record Step(RunLease Lease, Snapshot Now, Rule Rule, DateTimeOffset Deadline, CancellationToken Ct)
    {
        public RunManifest Manifest => Now.Record.Manifest;
        public int Epoch => Policy.Epoch(Now.Evidence);

        public AdvanceResult Result(ExitStatus exit, string message, AgentHandle? agent = null) =>
            new(exit, Manifest.Run, message, Rule, agent);
    }

    /// <summary>Local receipts plus fresh remote facts. An open agent operation is the worker while the spawn binding
    /// sees it running; any other open operation is unresolved.</summary>
    async Task<Snapshot> ObserveAsync(RunRecord record, CancellationToken ct)
    {
        var manifest = record.Manifest;
        var spec = record.Newest<Outcome.Specified>()?.Package;
        var facts = await tracker.ObserveAsync(manifest, spec?.Digest, ct);
        var head = await isolation.ObserveAsync(manifest, ct);
        AgentHandle? worker = null;
        OperationId? unresolved = null;
        if (record.Open is { } open)
        {
            if (open.Intent is Intent.Agent && await spawn.InspectAsync(manifest, open.Id, ct) is AgentState.Running running)
                worker = running.Handle;
            else
                unresolved = open.Id;
        }
        return new Snapshot(record, facts, new Evidence(manifest, facts.Claim, unresolved, worker, facts.Maintainer,
            record.Attempts, record.Newest<Outcome.Triaged>()?.Artifact, spec, facts.SpecPostedAt, head,
            record.Newest<Outcome.Implemented>()?.Artifact, record.Gates, record.Newest<Outcome.Cleaned>()?.Artifact, facts.Pr));
    }

    Task<AdvanceResult> ActAsync(Step s, NextAction action) => action switch
    {
        NextAction.Stop or NextAction.Complete => Task.FromResult(Settled(s.Manifest, s.Rule, action)),
        NextAction.AwaitMaintainer when s.Now.Evidence.SpecPostedAt is not null => Task.FromResult(Settled(s.Manifest, s.Rule, action)),
        NextAction.AwaitMaintainer wait => PostSpecAsync(s, wait.Package),
        NextAction.Reconcile reconcile => ReconcileAsync(s, reconcile.Operation),
        NextAction.AwaitAgent running when s.Now.Record.Open is { Intent: Intent.Agent agent } open =>
            AwaitAsync(s, open.Id, agent, running.Handle),
        NextAction.Triage => LaunchAsync(s, new Intent.Agent(ActionKind.Triage, s.Epoch, false, null),
            Contracts.Write(new TriageInput(s.Manifest.Item, s.Manifest.Trunk), C.TriageInput)),
        NextAction.Specify spec => LaunchAsync(s, new Intent.Agent(ActionKind.Spec, s.Epoch, spec.Revise is not null, null),
            Contracts.Write(new SpecInput(s.Manifest.Item, spec.Triaged, spec.Revise is { } revise
                ? new SpecToRevise(s.Now.Record.Spec(revise.Package), revise.Comment.Feedback)
                : null), C.SpecInput)),
        NextAction.Implement implement => LaunchAsync(s,
            new Intent.Agent(ActionKind.Implement, s.Epoch, implement.Repair is not null, implement.Spec.Package.Digest),
            Contracts.Write(new ImplementationInput(s.Now.Record.PublicSpec(implement.Spec.Package), s.Now.Record.Tickets(implement.Spec.Package),
                implement.Repair is { } failed ? new GateFailure(failed.Kind, failed.ExitCode, LogTail(s.Now.Record, failed)) : null),
                C.ImplementationInput)),
        NextAction.Gates run => GatesAsync(s, run.Spec.Package, run.Head),
        NextAction.Cleanup cleanup => LaunchAsync(s, new Intent.Agent(ActionKind.Cleanup, s.Epoch, false, cleanup.Spec.Package.Digest),
            Contracts.Write(new CleanupInput(s.Now.Record.PublicSpec(cleanup.Spec.Package), cleanup.Head), C.CleanupInput)),
        NextAction.Publish publish => PublishAsync(s, publish),
        _ => throw new UnreachableException($"no effect for {action}"),
    };

    /// <summary>The result for an action that needs no effect now.</summary>
    static AdvanceResult Settled(RunManifest run, Rule rule, NextAction action)
    {
        var (exit, message, agent, pr) = action switch
        {
            NextAction.Stop { Disposition: Disposition.WontFix } stop => (ExitStatus.Ok, stop.Reason, null, null),
            NextAction.Stop stop => (ExitStatus.WaitingOnMaintainer, $"{stop.Disposition}: {stop.Reason}", null, null),
            NextAction.AwaitMaintainer wait => (ExitStatus.WaitingOnMaintainer,
                $"waiting for the maintainer to comment `{Approval(wait.Package.Digest)}` or `/softwarp revise {wait.Package.Digest.Value}: ...` on {run.Item.Url}",
                null, null),
            NextAction.AwaitAgent wait => (ExitStatus.WaitingOnAgent, $"an agent is running; watch it with `{wait.Handle.WatchCommand}`",
                wait.Handle, null),
            NextAction.Complete done => (ExitStatus.Ok, $"the PR is open: {done.Pr.Url}", null, done.Pr.Url),
            _ => (ExitStatus.Ok, $"next: {action.GetType().Name}", (AgentHandle?)null, (Uri?)null),
        };
        return new AdvanceResult(exit, run.Run, message, rule, agent, pr);
    }

    async Task<AdvanceResult> LaunchAsync(Step s, Intent.Agent intent, byte[] input)
    {
        var workspace = await isolation.EnsureAsync(s.Manifest, s.Ct);
        var op = s.Lease.Begin(intent);
        var brief = WriteBrief(s, op, intent.Role, input);
        var handle = await spawn.EnsureAsync(s.Manifest, op, brief, workspace, s.Ct);
        return await AwaitAsync(s, op, intent, handle);
    }

    /// <summary>Writes the role's input and schema into the agent's directory and the brief into the operation directory.
    /// The prompt is a stub that names the files.</summary>
    static Brief WriteBrief(Step s, OperationId op, ActionKind role, byte[] input)
    {
        var io = Directory.CreateDirectory(s.Lease.AgentDirectory(op, role)).FullName;
        var (inputFile, schema, output) = (Path.Combine(io, "input.json"), Path.Combine(io, "schema.json"), Path.Combine(io, "output.json"));
        File.WriteAllBytes(inputFile, input);
        File.WriteAllText(schema, Schema(role).ToJsonString());
        var brief = new Brief(role, s.Manifest.Models.For(role),
            $"You are the {role} agent. Read your input from {inputFile}. Write one JSON object that matches the schema in {schema} to {output}.",
            inputFile, schema, output, Path.Combine(io, "agent.log"), ExpectedDuration(role));
        RunStore.WriteAtomic(s.Lease.OperationFile(op, "brief.json"), brief, C.Brief, overwrite: false);
        return brief;
    }

    async Task<AdvanceResult> AwaitAsync(Step s, OperationId op, Intent.Agent intent, AgentHandle handle)
    {
        if (await spawn.WaitAsync(handle, s.Deadline, s.Ct) is AgentState.Running)
            return s.Result(ExitStatus.WaitingOnAgent,
                $"the {intent.Role} agent runs on {s.Manifest.Models.For(intent.Role)} (expected {ExpectedDuration(intent.Role)}); " +
                $"log {handle.LogFile}; watch with `{handle.WatchCommand}`", handle);
        var outcome = await ImportAsync(s, op, intent);
        s.Lease.Complete(op, outcome);
        return s.Result(ExitStatus.Ok, $"the {intent.Role} agent finished: {Describe(outcome)}", handle);
    }

    /// <summary>Reads the agent's output through its contract, validates it and stamps the factory's own facts.</summary>
    async Task<Outcome> ImportAsync(Step s, OperationId op, Intent.Agent intent)
    {
        try
        {
            var bytes = UntrustedFile.Read(Path.Combine(s.Lease.AgentDirectory(op, intent.Role), "output.json"), MaxOutputBytes);
            switch (intent.Role)
            {
                case ActionKind.Triage:
                    var triage = Contracts.Read(bytes, C.TriageOutput).Stamp(intent.Epoch);
                    return Checked(Validation.Validate(triage, s.Manifest), () => new Outcome.Triaged(triage));
                case ActionKind.Spec:
                    var spec = Contracts.Read(bytes, C.SpecArtifact);
                    return Checked(Validation.Validate(spec), () => new Outcome.Specified(s.Lease.StoreSpec(spec)));
                case ActionKind.Implement:
                    Contracts.Read(bytes, C.ImplementationOutput);
                    return new Outcome.Implemented(new ImplementationArtifact(Dispatched(op, intent)));
                case ActionKind.Cleanup:
                    var copy = Contracts.Read(bytes, C.CleanupOutput);
                    if (await isolation.ObserveAsync(s.Manifest, s.Ct) is not { } head) return Invalid(["the worktree is gone"]);
                    return Checked(Validation.Validate(copy),
                        () => new Outcome.Cleaned(new CleanupArtifact(Dispatched(op, intent), head.Head, copy.PrTitle, copy.PrBody)));
                default:
                    throw new UnreachableException($"no import for {intent.Role}");
            }
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            return Invalid([e.Message]);
        }
    }

    static Digest Dispatched(OperationId op, Intent.Agent intent) =>
        intent.Spec ?? throw new InvalidDataException($"{op.Value} records no spec package");

    static Outcome Checked(ImmutableArray<string> errors, Func<Outcome> valid) => errors.IsEmpty ? valid() : Invalid(errors);

    /// <summary>Violations and parse errors quote agent text, so the receipt keeps the first few, each cut short, and
    /// counts the rest. That keeps it far under the record cap.</summary>
    static Outcome.Invalid Invalid(ImmutableArray<string> errors) => new(
    [
        .. errors.Take(MaxViolations).Select(e => e.Length <= MaxViolationChars ? e : $"{e[..MaxViolationChars]}..."),
        .. errors.Length > MaxViolations ? [$"and {errors.Length - MaxViolations} more"] : Array.Empty<string>(),
    ]);

    /// <summary>Runs each gate kind in order on the frozen head and stops at the first that does not pass or that moved
    /// the head or dirtied the tree. Every receipt is stamped from the factory's own facts.</summary>
    async Task<AdvanceResult> GatesAsync(Step s, SpecPackage spec, CommitId head)
    {
        var workspace = await isolation.EnsureAsync(s.Manifest, s.Ct);
        var frozen = new Intent.Gates(spec.Digest, head, s.Epoch);
        var op = s.Lease.Begin(frozen);
        var targets = s.Now.Record.PublicSpec(spec).TestTargets;
        var receipts = ImmutableArray.CreateBuilder<GateReceipt>();
        foreach (var kind in Enum.GetValues<GateKind>())
        {
            var log = s.Lease.GateLog(op, kind);
            // Reconcile names the gate that was running as the last kind with a log, so the log must exist before the gate can crash.
            new FileStream(log, FileMode.CreateNew).Dispose();
            var run = await gates.RunAsync(workspace, s.Manifest.Workflow.Command(kind).Expand(targets), log, s.Ct);
            var after = await isolation.ObserveAsync(s.Manifest, s.Ct)
                ?? throw new InvalidOperationException("the worktree vanished while a gate ran");
            var receipt = Receipt(s, op, frozen, kind, after, run.ExitCode, run.TestsExecuted);
            receipts.Add(receipt);
            if (receipt.Verdict != GateVerdict.Pass || after.Head != head || !after.Clean) break;
        }
        s.Lease.Complete(op, new Outcome.Gated(receipts.ToImmutable()));
        return receipts.FirstOrDefault(r => r.Verdict == GateVerdict.Fail) is { } failed
            ? s.Result(ExitStatus.GateFailed, $"the {failed.Kind} gate failed with exit {failed.ExitCode} on {head.Value}")
            : s.Result(ExitStatus.Ok, $"ran {string.Join(", ", receipts.Select(r => $"{r.Kind} (exit {r.ExitCode})"))} on {head.Value}");
    }

    /// <summary>A gate receipt stamped from the factory's own facts: the frozen spec and head, the head and tree git shows
    /// after the gate, the pinned command, and the log as the factory read it.</summary>
    static GateReceipt Receipt(Step s, OperationId op, Intent.Gates frozen, GateKind kind, HeadObservation after,
        int exitCode, int? testsExecuted)
    {
        var log = s.Lease.GateLog(op, kind);
        var (digest, code) = LogDigest(log, exitCode);
        return new GateReceipt(kind, frozen.Spec, frozen.Head, after.Head, after.Clean, s.Manifest.Workflow.Command(kind).Digest,
            code, testsExecuted, new ArtifactRef(digest, Path.GetRelativePath(s.Now.Record.Directory, log)), frozen.Epoch);
    }

    /// <summary>A log the factory refuses (over the cap, a link, or not openable) fails its gate: the receipt records
    /// <see cref="GateReceipt.RefusedLog"/> and the digest of no bytes.</summary>
    static (Digest Digest, int ExitCode) LogDigest(string log, int exitCode)
    {
        try
        {
            return (UntrustedFile.Hash(log, MaxLogBytes), exitCode);
        }
        catch (InvalidDataException)
        {
            return (RunStore.Hash([]), GateReceipt.RefusedLog);
        }
    }

    async Task<AdvanceResult> PostSpecAsync(Step s, SpecPackage package)
    {
        var op = s.Lease.Begin(new Intent.PostSpec(package.Digest));
        await tracker.PostSpecAsync(s.Manifest, View(s.Now.Record, package), op, s.Ct);
        s.Lease.Complete(op, new Outcome.Done());
        return s.Result(ExitStatus.WaitingOnMaintainer,
            $"posted the spec on {s.Manifest.Item.Url}; waiting for `{Approval(package.Digest)}`");
    }

    /// <summary>Pushes when the remote branch is not at the head yet, otherwise opens the PR: one effect per call.</summary>
    async Task<AdvanceResult> PublishAsync(Step s, NextAction.Publish publish)
    {
        if (await isolation.RemoteHeadAsync(s.Manifest, s.Ct) != publish.Head)
        {
            var push = s.Lease.Begin(new Intent.Push(publish.Head));
            await isolation.PushAsync(s.Manifest, publish.Head, push, s.Ct);
            s.Lease.Complete(push, new Outcome.Done());
            return s.Result(ExitStatus.Ok, $"pushed {s.Manifest.Branch} at {publish.Head.Value}");
        }
        var digest = publish.Spec.Package.Digest;
        var open = s.Lease.Begin(new Intent.OpenPr(digest, publish.Head));
        await tracker.OpenPrAsync(new PublishRequest(s.Manifest, publish.Head, digest, publish.Copy.PrTitle,
            publish.Copy.PrBody, open), s.Ct);
        s.Lease.Complete(open, new Outcome.Done());
        return s.Result(ExitStatus.Ok, $"opened the PR for {s.Manifest.Branch}");
    }

    /// <summary>Asks the capability that owns the effect whether it happened and records that. Never repeats the effect:
    /// a NotRun receipt lets the policy choose it again on the next call.</summary>
    async Task<AdvanceResult> ReconcileAsync(Step s, OperationId id)
    {
        var op = s.Now.Record.Operations.Single(o => o.Id == id);
        var facts = s.Now.Facts;
        var outcome = op.Intent switch
        {
            Intent.Claim => Happened(facts.Claim == s.Manifest.Run),
            Intent.PostSpec => Happened(facts.SpecPostedAt is not null),
            Intent.OpenPr => Happened(facts.Pr is not null),
            Intent.Push push => Happened(await isolation.RemoteHeadAsync(s.Manifest, s.Ct) == push.Head),
            // A gate writes nothing outside the worktree, so an interrupted run is run again, unless git shows that it
            // moved the head or dirtied the tree. Then the gate that wrote the last log is recorded as failed on what git
            // shows, and the policy stops on it.
            Intent.Gates gated => await isolation.ObserveAsync(s.Manifest, s.Ct) is { } now && (now.Head != gated.Head || !now.Clean)
                ? new Outcome.Gated([Receipt(s, id, gated, Enum.GetValues<GateKind>().LastOrDefault(kind =>
                    File.Exists(s.Lease.GateLog(id, kind))), now, GateReceipt.RefusedLog, null)])
                : new Outcome.NotRun(),
            Intent.Agent agent => await spawn.InspectAsync(s.Manifest, id, s.Ct) is AgentState.NotLaunched
                ? new Outcome.NotRun()
                : await ImportAsync(s, id, agent),
            _ => throw new UnreachableException($"no reconcile for {op.Intent}"),
        };
        s.Lease.Complete(id, outcome);
        return s.Result(ExitStatus.Ok, $"{id.Value}: {Describe(outcome)}");
    }

    static Outcome Happened(bool seen) => seen ? new Outcome.Done() : new Outcome.NotRun();

    static string Describe(Outcome outcome) => outcome switch
    {
        Outcome.Invalid invalid => $"invalid output ({string.Join("; ", invalid.Errors)})",
        Outcome.NotRun => "it never happened",
        _ => outcome.GetType().Name.ToLowerInvariant(),
    };

    static SpecView View(RunRecord record, SpecPackage package)
    {
        var spec = record.PublicSpec(package);
        var text = new StringBuilder(spec.Markdown.TrimEnd()).Append("\n\n## Acceptance criteria\n\n");
        foreach (var criterion in spec.Criteria) text.Append("- **").Append(criterion.Id).Append("** ").Append(criterion.Text).Append('\n');
        text.Append("\n## Tickets\n\n");
        foreach (var ticket in record.Tickets(package))
            text.Append("- **").Append(ticket.Id).Append("** ").Append(ticket.Text)
                .Append(ticket.BlockedBy.IsEmpty ? "" : $" (after {string.Join(", ", ticket.BlockedBy)})").Append('\n');
        text.Append("\n## Test targets\n\n");
        foreach (var target in spec.TestTargets) text.Append("- `").Append(target).Append("`\n");
        text.Append("\nApprove with `").Append(Approval(package.Digest)).Append("`, or ask for changes with `/softwarp revise ")
            .Append(package.Digest.Value).Append(": <what to change>`.\n");
        return new SpecView(package.Digest, record.Manifest.Item.Url, text.ToString(), Approval(package.Digest));
    }

    static string Approval(Digest digest) => $"/softwarp approve {digest.Value}";

    static string LogTail(RunRecord record, GateReceipt receipt)
    {
        try
        {
            return UntrustedFile.Tail(Path.Combine(record.Directory, receipt.Log.RelativePath), LogTailBytes);
        }
        catch (InvalidDataException e)
        {
            return $"the factory did not read this log: {e.Message}";
        }
    }

    static JsonNode Schema(ActionKind role) => role switch
    {
        ActionKind.Triage => Contracts.Schema(C.TriageOutput),
        ActionKind.Spec => Contracts.Schema(C.SpecArtifact),
        ActionKind.Implement => Contracts.Schema(C.ImplementationOutput),
        ActionKind.Cleanup => Contracts.Schema(C.CleanupOutput),
        _ => throw new UnreachableException($"no schema for {role}"),
    };

    // ponytail: one fixed duration per role; per-repo values belong in WORKFLOW.md once stall detection reads them.
    static TimeSpan ExpectedDuration(ActionKind role) => role switch
    {
        ActionKind.Triage => TimeSpan.FromMinutes(15),
        ActionKind.Spec => TimeSpan.FromMinutes(30),
        ActionKind.Implement => TimeSpan.FromMinutes(90),
        ActionKind.Cleanup => TimeSpan.FromMinutes(20),
        _ => throw new UnreachableException($"no duration for {role}"),
    };

    static AdvanceResult Unknown(RunId run) => new(ExitStatus.InvalidInput, run, $"no run {run.Value} in the run store");
}
