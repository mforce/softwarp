using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Softwarp.Core;

/// <summary>An effect the factory is about to run. It is written and flushed before the effect starts, in its own
/// operation directory, whose name is the operation id.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "effect")]
[JsonDerivedType(typeof(Claim), "claim")]
[JsonDerivedType(typeof(Agent), "agent")]
[JsonDerivedType(typeof(PostSpec), "post-spec")]
[JsonDerivedType(typeof(Gates), "gates")]
[JsonDerivedType(typeof(Push), "push")]
[JsonDerivedType(typeof(OpenPr), "open-pr")]
public abstract record Intent
{
    private Intent() { }
    public sealed record Claim : Intent;
    /// <summary>Spec is the package the work is for, stamped into the artifact at import. Revision marks an answer to a
    /// revise comment or a red gate.</summary>
    public sealed record Agent(ActionKind Role, int Epoch, bool Revision, Digest? Spec) : Intent;
    public sealed record PostSpec(Digest Spec) : Intent;
    public sealed record Gates(Digest Spec, CommitId Head, int Epoch) : Intent;
    public sealed record Push(CommitId Head, int Epoch) : Intent;
    public sealed record OpenPr(Digest Spec, CommitId Head) : Intent;
}

/// <summary>What an operation's receipt records.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "outcome")]
[JsonDerivedType(typeof(Done), "done")]
[JsonDerivedType(typeof(NotRun), "not-run")]
[JsonDerivedType(typeof(Invalid), "invalid")]
[JsonDerivedType(typeof(Triaged), "triaged")]
[JsonDerivedType(typeof(Specified), "specified")]
[JsonDerivedType(typeof(Implemented), "implemented")]
[JsonDerivedType(typeof(Cleaned), "cleaned")]
[JsonDerivedType(typeof(Gated), "gated")]
public abstract record Outcome
{
    private Outcome() { }
    /// <summary>The tracker or git shows the effect.</summary>
    public sealed record Done : Outcome;
    /// <summary>Reconcile found that the effect never happened, so the policy may choose it again.</summary>
    public sealed record NotRun : Outcome;
    /// <summary>The agent returned no output, or output that broke its contract or failed validation; or the remote
    /// rejected a push.</summary>
    public sealed record Invalid(ImmutableArray<string> Errors) : Outcome;
    public sealed record Triaged(TriageArtifact Artifact) : Outcome;
    public sealed record Specified(SpecPackage Package) : Outcome;
    public sealed record Implemented(ImplementationArtifact Artifact) : Outcome;
    public sealed record Cleaned(CleanupArtifact Artifact) : Outcome;
    public sealed record Gated(ImmutableArray<GateReceipt> Receipts) : Outcome;
}

/// <summary>Outcome is null while the receipt is missing: the effect may or may not have happened.</summary>
public sealed record Operation(OperationId Id, Intent Intent, Outcome? Outcome);

/// <summary>Everything the run directory holds, in operation order.</summary>
public sealed record RunRecord(string Directory, RunManifest Manifest, ImmutableArray<Operation> Operations)
{
    /// <summary>The oldest operation with no receipt.</summary>
    public Operation? Open => Operations.FirstOrDefault(o => o.Outcome is null);

    /// <summary>One attempt per agent receipt; an agent that never ran has none.</summary>
    public ImmutableArray<Attempt> Attempts =>
    [
        .. Operations.Where(o => o.Outcome is not (null or Outcome.NotRun)).Select(o => o.Intent is Intent.Agent agent
            ? new Attempt(o.Id, agent.Role, agent.Epoch,
                o.Outcome is Outcome.Invalid ? AttemptOutcome.InvalidArtifact : AttemptOutcome.Completed, agent.Revision)
            : null).OfType<Attempt>(),
    ];

    /// <summary>The newest outcome of this kind, never an older one.</summary>
    public T? Newest<T>() where T : Outcome => Operations.Select(o => o.Outcome).OfType<T>().LastOrDefault();

    public ImmutableArray<GateReceipt> Gates =>
        [.. Operations.Select(o => o.Outcome).OfType<Outcome.Gated>().SelectMany(g => g.Receipts)];

    /// <summary>Every package from outside the pinned trunk that a gate runner recorded letting in, in any gate operation
    /// of the run, one cut off by a crash included: what the maintainer is shown and what the run's cap counts.</summary>
    public ImmutableArray<string> AdmittedPackages =>
    [
        .. Operations.Where(o => o.Intent is Intent.Gates)
            .SelectMany(o => Enum.GetValues<GateKind>().Select(kind => RunStore.GatePackages(Directory, o.Id, kind)))
            .SelectMany(RunStore.Packages).Distinct().Order(StringComparer.Ordinal),
    ];

    public ImmutableArray<Intent.Push> RejectedPushes =>
        [.. Operations.Where(o => o.Outcome is Outcome.Invalid).Select(o => o.Intent).OfType<Intent.Push>()];

    /// <summary>The package's public part, hash-checked.</summary>
    public PublicSpec PublicSpec(SpecPackage package) => Load(package.PublicSpec, Contracts.Default.PublicSpec);
    public ImmutableArray<Ticket> Tickets(SpecPackage package) => Load(package.Tickets, Contracts.Default.ImmutableArrayTicket);

    /// <summary>The whole package as the spec agent wrote it, holdouts included: for the spec role only.</summary>
    public SpecArtifact Spec(SpecPackage package)
    {
        var spec = PublicSpec(package);
        return new(spec.Markdown, spec.Criteria, Tickets(package),
            Load(package.PrivateHoldouts, Contracts.Default.ImmutableArrayHoldout), spec.TestTargets);
    }

    T Load<T>(ArtifactRef artifact, JsonTypeInfo<T> contract)
    {
        var bytes = RunStore.ReadRecord(Path.Combine(Directory, artifact.RelativePath));
        if (RunStore.Hash(bytes) != artifact.Hash)
            throw new InvalidDataException($"{artifact.RelativePath} does not match its digest {artifact.Hash.Value}");
        return Contracts.Read(bytes, contract);
    }
}

/// <summary>One directory per item: `root/owner/repo/issue/`. Holds the manifest; one directory per operation under
/// `ops/` with the factory's own records (intent, receipt, brief, gate logs), which no agent is handed; one directory
/// per agent operation under `io/`, the only part of the run an agent may read or write; the public spec files under
/// `spec/`; and the holdouts under `holdout/`, with the spec agent's own directories under `holdout/io/`.</summary>
public sealed class RunStore(string root)
{
    internal const string ManifestFile = "manifest.json", IntentFile = "intent.json", ReceiptFile = "receipt.json";

    /// <summary>A record holds at most one agent output, and the encoder may write each of its bytes as a six-byte
    /// `\uXXXX` escape.</summary>
    internal const int MaxRecordBytes = 8 * Factory.MaxOutputBytes;

    /// <exception cref="ArgumentException">The item's owner or repository is not a single safe path segment.</exception>
    public string Directory(ItemId item) => IsSegment(item.Owner) && IsSegment(item.Repo) && item.Number > 0
        ? Path.Combine(root, item.Owner, item.Repo, item.Number.ToString(Invariant))
        : throw new ArgumentException($"'{item.Owner}/{item.Repo}#{item.Number}' does not name an issue", nameof(item));

    /// <summary>A new run id for the item: `owner/repo/issue/nonce`, so a claim by another store is told apart.</summary>
    public static RunId NewRunId(ItemId item) =>
        new($"{item.Owner}/{item.Repo}/{item.Number.ToString(Invariant)}/{RandomNumberGenerator.GetHexString(12, lowercase: true)}");

    /// <summary>The item a run id names; null when the id is malformed or would leave the root.</summary>
    public static ItemId? Item(RunId run) =>
        run.Value?.Split('/') is [var owner, var repo, var issue, var nonce]
        && IsSegment(owner) && IsSegment(repo) && IsSegment(nonce) && issue.All(char.IsAsciiDigit)
        && long.TryParse(issue, Invariant, out var number) && number > 0
            ? new ItemId(owner, repo, number)
            : null;

    /// <summary>Owner and repository names as the forge allows them: no separator, and not `.` or `..`.</summary>
    public static bool IsSegment(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Takes the item's exclusive OS lock; null when another caller holds it. The lock never waits.</summary>
    public RunLease? TryLock(ItemId item)
    {
        var directory = Directory(item);
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            // FileShare.None takes flock(LOCK_EX | LOCK_NB) on Unix; the OS drops it when the process dies.
            var lockFile = new FileStream(Path.Combine(directory, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new RunLease(directory, lockFile);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Reads the run without its lock; null when no such run exists.</summary>
    public RunRecord? Read(RunId run) =>
        Item(run) is { } item && File.Exists(Path.Combine(Directory(item), ManifestFile))
        && RunLease.Read(Directory(item)) is { } record && record.Manifest.Run == run
            ? record
            : null;

    internal static string GatePackages(string runDirectory, OperationId operation, GateKind kind) =>
        Path.Combine(runDirectory, "ops", operation.Value, $"{kind}.packages");

    /// <summary>The packages a gate runner recorded in the file, one per line; none when it wrote nothing.</summary>
    internal static IEnumerable<string> Packages(string file) =>
        File.Exists(file) && new FileInfo(file).Length > 0
            ? System.Text.Encoding.UTF8.GetString(ReadRecord(file)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];

    internal static Digest Hash(ReadOnlySpan<byte> bytes) => new(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    /// <summary>A record the factory wrote, read with the checks agent output gets: a link, a FIFO or an oversized file is
    /// refused, never followed or blocked on.</summary>
    internal static byte[] ReadRecord(string path) => UntrustedFile.Read(path, MaxRecordBytes);

    /// <summary>Checks the value reads back through its contract, then writes it atomically.</summary>
    internal static void WriteAtomic<T>(string path, T value, JsonTypeInfo<T> contract, bool overwrite)
    {
        var bytes = Contracts.Write(value, contract);
        Contracts.Read(bytes, contract);
        WriteAtomic(path, bytes, overwrite);
    }

    /// <summary>Writes a temp file, flushes it to disk, then renames it into place. A leftover temp file is unlinked first and
    /// the new one is created exclusively, so a link at the temp path is removed, never written through. A record over
    /// the read cap is refused here, so no record lands that the factory would then refuse to read.</summary>
    internal static void WriteAtomic(string path, byte[] bytes, bool overwrite)
    {
        if (bytes.Length > MaxRecordBytes)
            throw new InvalidDataException($"{Path.GetFileName(path)} would be {bytes.Length} bytes, over the {MaxRecordBytes}-byte record cap");
        var temp = path + ".tmp";
        File.Delete(temp);
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        // ponytail: the directory entry is not fsynced, so a power cut can lose a rename; a crash cannot half-write it.
        File.Move(temp, path, overwrite);
    }

    static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;
}

/// <summary>Holds an item's lock. Disposing it, or the process dying, releases the lock.</summary>
public sealed class RunLease : IDisposable
{
    readonly FileStream lockFile;

    internal RunLease(string directory, FileStream lockFile)
    {
        (Directory, this.lockFile) = (directory, lockFile);
        var manifest = Path.Combine(directory, RunStore.ManifestFile);
        Manifest = File.Exists(manifest) ? Contracts.Read(RunStore.ReadRecord(manifest), Contracts.Default.RunManifest) : null;
    }

    public RunManifest? Manifest { get; private set; }
    public string Directory { get; }
    string Ops => Path.Combine(Directory, "ops");

    /// <summary>Writes the manifest once; a second write throws.</summary>
    public void Create(RunManifest manifest)
    {
        RunStore.WriteAtomic(Path.Combine(Directory, RunStore.ManifestFile), manifest, Contracts.Default.RunManifest, overwrite: false);
        Manifest = manifest;
    }

    public RunRecord Read() => Read(Directory);

    internal static RunRecord Read(string directory)
    {
        var manifest = Contracts.Read(RunStore.ReadRecord(Path.Combine(directory, RunStore.ManifestFile)), Contracts.Default.RunManifest);
        var ops = Path.Combine(directory, "ops");
        var operations = System.IO.Directory.Exists(ops)
            ? System.IO.Directory.GetDirectories(ops).OrderBy(Sequence)
                .Where(dir => File.Exists(Path.Combine(dir, RunStore.IntentFile)))
                .Select(dir => new Operation(new OperationId(Path.GetFileName(dir)),
                    Contracts.Read(RunStore.ReadRecord(Path.Combine(dir, RunStore.IntentFile)), Contracts.Default.Intent),
                    File.Exists(Path.Combine(dir, RunStore.ReceiptFile))
                        ? Contracts.Read(RunStore.ReadRecord(Path.Combine(dir, RunStore.ReceiptFile)), Contracts.Default.Outcome)
                        : null))
            : [];
        return new RunRecord(directory, manifest, [.. operations]);
    }

    /// <summary>Allocates the next operation id and writes and flushes the intent before returning.</summary>
    public OperationId Begin(Intent intent)
    {
        var last = System.IO.Directory.Exists(Ops) ? System.IO.Directory.GetDirectories(Ops).Select(Sequence).DefaultIfEmpty().Max() : 0;
        var id = new OperationId($"{last + 1:D4}-{Effect(intent)}");
        System.IO.Directory.CreateDirectory(Path.Combine(Ops, id.Value));
        RunStore.WriteAtomic(OperationFile(id, RunStore.IntentFile), intent, Contracts.Default.Intent, overwrite: false);
        return id;
    }

    /// <summary>Writes the receipt to a temp file, reads it back through the contract, then renames it in. A second
    /// receipt for the same operation throws.</summary>
    public void Complete(OperationId operation, Outcome outcome) =>
        RunStore.WriteAtomic(OperationFile(operation, RunStore.ReceiptFile), outcome, Contracts.Default.Outcome, overwrite: false);

    /// <summary>A file inside the operation's directory, among the factory's own records.</summary>
    public string OperationFile(OperationId operation, string name) => Path.Combine(Ops, operation.Value, name);

    /// <summary>Where a gate's output goes, among the factory's own records.</summary>
    public string GateLog(OperationId operation, GateKind kind) => OperationFile(operation, $"{kind}.log");

    /// <summary>Where a gate runner records the packages from outside the pinned trunk it lets in.</summary>
    public string GatePackages(OperationId operation, GateKind kind) => RunStore.GatePackages(Directory, operation, kind);

    /// <summary>The agent's own directory, the one part of the run its sandbox may grant: the agent's input, schema,
    /// output and log, and nothing the factory trusts. The spec agent writes holdouts, so its directory sits under
    /// `holdout/`, which no other role may read.</summary>
    public string AgentDirectory(OperationId operation, ActionKind role) =>
        Path.Combine(Directory, role == ActionKind.Spec ? "holdout" : "", "io", operation.Value);

    /// <summary>Stores the spec's public part under `spec/` and its holdouts under `holdout/`. The digest covers the
    /// public spec, the tickets, the holdouts and the manifest.</summary>
    public SpecPackage StoreSpec(SpecArtifact spec)
    {
        var publicSpec = Contracts.Write(new PublicSpec(spec.Markdown, spec.Criteria, spec.TestTargets), Contracts.Default.PublicSpec);
        var tickets = Contracts.Write(spec.Tickets, Contracts.Default.ImmutableArrayTicket);
        var holdouts = Contracts.Write(spec.Holdouts, Contracts.Default.ImmutableArrayHoldout);
        var manifest = RunStore.ReadRecord(Path.Combine(Directory, RunStore.ManifestFile));
        var parts = new[] { publicSpec, tickets, holdouts, manifest }.Select(bytes => RunStore.Hash(bytes).Value);
        var digest = RunStore.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', parts)));

        ArtifactRef Put(string relativePath, byte[] bytes)
        {
            var path = Path.Combine(Directory, relativePath);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RunStore.WriteAtomic(path, bytes, overwrite: true);
            return new ArtifactRef(RunStore.Hash(bytes), relativePath);
        }
        return new SpecPackage(digest, Put($"spec/{digest.Value}/spec.json", publicSpec),
            Put($"spec/{digest.Value}/tickets.json", tickets), Put($"holdout/{digest.Value}.json", holdouts));
    }

    public void Dispose() => lockFile.Dispose();

    /// <summary>An operation directory is named `{sequence:D4}-{effect}`.</summary>
    static int Sequence(string directory)
    {
        var name = Path.GetFileName(directory);
        return int.Parse(name.AsSpan(0, name.IndexOf('-')), System.Globalization.CultureInfo.InvariantCulture);
    }

    static string Effect(Intent intent) => intent switch
    {
        Intent.Claim => "claim",
        Intent.Agent agent => agent.Role.ToString().ToLowerInvariant(),
        Intent.PostSpec => "post-spec",
        Intent.Gates => "gates",
        Intent.Push => "push",
        Intent.OpenPr => "open-pr",
        _ => throw new ArgumentOutOfRangeException(nameof(intent)),
    };
}
