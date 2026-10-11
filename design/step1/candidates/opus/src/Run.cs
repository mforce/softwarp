namespace Factory;

/// <summary>One work item. The run IS the item: one run per issue (DESIGN §8 path).</summary>
public sealed record RunRef(string Owner, string Repo, int Issue)
{
    /// <summary>"owner/repo#123", or "123" when cwd is a clone (repo from its origin).</summary>
    public static RunRef Parse(string text, string cwd) => throw new NotImplementedException();
    /// <summary>A pure function of the run, so a crash can never orphan a second branch.</summary>
    public BranchName Branch => new($"factory/{Issue}");
    public override string ToString() => $"{Owner}/{Repo}#{Issue}";
}

/// <summary>A full commit id, constructed only from git output. (Real code adds a plain-string JsonConverter, likewise for Digest.)</summary>
public readonly record struct Sha(string Value);

/// <summary>sha256 of an artifact's canonical JSON. The approval quotes <see cref="Prefix"/> (48 bits: the threat is drift, not malice).</summary>
public readonly record struct Digest(string Hex)
{
    public string Prefix => Hex[..12];
}

/// <summary>Only <see cref="RunRef.Branch"/> makes one, so the push path cannot name another ref.</summary>
public readonly record struct BranchName(string Value);

/// <summary>"triage-1", "implement-2": the attempt dir name, the Paseo label, the idempotency key.</summary>
public readonly record struct AttemptKey(AgentJob Job, int Number)
{
    public override string ToString() => $"{Job.ToString().ToLowerInvariant()}-{Number}";
}

/// <summary>The contract the coordinator branches on. Never renumber.</summary>
public enum ExitCode { Ok = 0, Failure = 1, Usage = 2, Refused = 3, Busy = 4, WaitingOnAgent = 10, WaitingOnMaintainer = 11 }

/// <summary>
/// Owns the layout of ~/.factory/runs/&lt;repo&gt;/&lt;issue&gt;/. Observe and Execute both go through here.
/// <code>
/// item.json                  issue snapshot at claim (pinned input)
/// agents/&lt;key&gt;/             call.json (pins, epoch, reason) · brief.md · handle.json · output.json
/// gates/&lt;sha&gt;/&lt;gate&gt;.json   GateResult, written by temp file + rename after the command exits
/// holdout/*.md               never under the worktree
/// progress.md                regenerated view, same text as `factory status`  ┐
/// decisions.tsv              append-only, one line per executed step          ├ written, never read
/// metrics.jsonl              append-only                                      ┘
/// </code>
/// </summary>
public sealed class RunDir(string root, RunRef run)
{
    public string Path { get; } = System.IO.Path.Combine(root, run.Repo, run.Issue.ToString());
    public string Attempt(AttemptKey key) => System.IO.Path.Combine(Path, "agents", key.ToString());
    public string Gate(Sha sha, GateKind gate) => System.IO.Path.Combine(Path, "gates", sha.Value, $"{gate}.json");
    /// <summary>
    /// Exclusive non-blocking lock on .lock (FileShare.None, flock on Linux); null ⇒ <see cref="ExitCode.Busy"/>. `status` never locks.
    /// ponytail: one lock per run, held across `--wait`; release it while waiting if it blocks the maintainer.
    /// </summary>
    public IDisposable? TryLock() => throw new NotImplementedException();
    /// <summary>mkdir as the claim on an attempt: false when it exists already (a crash, or another driver).</summary>
    public bool TryCreateAttempt(AttemptKey key) => throw new NotImplementedException();
}
