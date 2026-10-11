namespace Factory;

/// <summary>"owner/repo#123". DESIGN §8 gives one run per issue, so this is also the run key.
/// Parsed once at the CLI edge. A bare "1223" resolves against the cwd's git remote.</summary>
public readonly record struct IssueRef(string Repo, int Number)
{
    public static IssueRef Parse(string text, string? defaultRepo) => throw new NotImplementedException();
    public override string ToString() => $"{Repo}#{Number}";
}

/// <summary>SHA-256 hex. The 12-char <see cref="Short"/> form is what the maintainer types in /approve.</summary>
public readonly record struct Digest(string Hex)
{
    public string Short => Hex[..12];
    public static Digest Of(string text) => throw new NotImplementedException();
}

/// <summary>Station N is also agent role N. Station 0 (intake) is the only one with no agent.</summary>
public enum Station { Intake = 0, Triage = 1, Spec = 2, Implement = 3, Publish = 4 }

public enum ModelTier { Cheap, Standard, Strong }

/// <summary>Triage and Spec read a trunk checkout. Implement and Publish write only their own worktree.
/// No role ever receives a forge token: that is the absence of a field on <see cref="AgentCall"/>.</summary>
public enum Access { ReadOnly, WorktreeWrite }

public enum GateKind { Build, TestTargeted }

public enum FactoryLabel { NeedsTriage, NeedsInfo, Ready, NeedsHuman, Active, Review, Done, WontFix }

/// <summary>Always derived by <see cref="Machine.Plan"/>, never stored. Step 1 ends at Published.</summary>
public enum Phase { Triage, Spec, Approval, Implement, Publish, Published, Halted }

public enum HaltReason { ClaimedElsewhere, NeedsInfo, WontFix, ReadyForHuman, Escalated, Stopped }

/// <summary>Why a ledger entry is a rejection. Only the machine causes count toward the 2-cycle
/// escalation limit (DESIGN §7). A maintainer revision never escalates to the maintainer.</summary>
public enum Cause { Schema, Gate, AgentDied, Maintainer }

/// <summary>Written once by <c>factory run</c>. Replays read these, never "whatever is current" (DESIGN §4).</summary>
public sealed record RunPins(string FactoryVersion, string WorkflowCommit, string TrunkAtClaim, DateTimeOffset ClaimedAt);

/// <summary>Recorded on every agent attempt, accepted or not.</summary>
public sealed record CallPins(string Harness, string Model, string BriefSha256, IReadOnlyDictionary<string, string> SkillVersions);

/// <summary>The only run state the factory itself writes, besides pins and children.tsv. One immutable file
/// per entry. A station's current artifact is its LAST entry, and only if that entry is accepted.</summary>
public sealed record LedgerEntry(
    int Seq, Station Station, int Attempt, string? ArtifactJson, Cause? RejectedBy,
    IReadOnlyList<string> Problems, string CauseKey, CallPins? Pins, DateTimeOffset At);

public sealed class Ledger(IReadOnlyList<LedgerEntry> entries)
{
    public IReadOnlyList<LedgerEntry> Entries { get; } = entries;
    public LedgerEntry? Last(Station s) => throw new NotImplementedException();
    /// <summary>Parsed body of the last entry for T.Home, or null when that entry is a rejection or missing.</summary>
    public T? Current<T>() where T : class, IArtifact<T> => throw new NotImplementedException();
    /// <summary>Entries so far for the station. The next attempt number is this plus 1.</summary>
    public int Attempts(Station s) => throw new NotImplementedException();
    /// <summary>Non-maintainer rejections since the last maintainer-caused entry.</summary>
    public int MachineRejections(Station s) => throw new NotImplementedException();
}

/// <summary>Proof of one gate run. Fresh only while HeadSha equals the workspace head (DESIGN §7, proof freshness).</summary>
public sealed record GateReceipt(GateKind Gate, string Command, int ExitCode, string HeadSha, string LogPath);
