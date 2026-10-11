using System.Text.Json.Serialization;

namespace Factory;

/// <summary>`owner/name` as GitHub spells it in a URL ("owner" is GitHub's path segment, not a role).</summary>
public readonly record struct RepoSlug(string Owner, string Name)
{
    public override string ToString() => $"{Owner}/{Name}";
    /// <summary>Accepts `owner/name` or an HTTPS remote URL. Throws on anything else.</summary>
    public static RepoSlug Parse(string text) => throw new NotImplementedException();
}

/// <summary>
/// One issue in one repo. The run id is the only key: it names the run dir, the worktree and the branch.
/// Everything else about a run is derived from what sits under these paths plus the tracker.
/// </summary>
public readonly record struct RunId(RepoSlug Repo, int Issue)
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string Dir => Path.Combine(Home, ".factory", "runs", Repo.Owner, Repo.Name, Issue.ToString());
    public string Worktree => Path.Combine(Home, ".factory-slices", Repo.Owner, Repo.Name, Issue.ToString());
    public override string ToString() => $"{Repo}#{Issue}";
}

/// <summary>Build step 1 covers Intake..Publish. Later stations append here and the planner's table grows rows.</summary>
public enum Station { Intake, Triage, Spec, Implement, Publish }

/// <summary>DESIGN §9 tiers, mapped to model ids by <see cref="Models"/> and pinned per run.</summary>
public enum Tier { Cheap, Standard, Strong }

/// <summary>DESIGN §8 labels. A projection of run state, written only by the orchestrator.</summary>
public enum FactoryLabel { NeedsTriage, NeedsInfo, Ready, NeedsHuman, Active, Review, Done, Wontfix }

public sealed record Models(string Cheap, string Standard, string Strong)
{
    public string For(Tier tier) => tier switch { Tier.Cheap => Cheap, Tier.Standard => Standard, _ => Strong };
}

/// <summary>
/// Every input that can change an agent's behavior, recorded once at claim (DESIGN §4). Briefs and skills are
/// name -> content hash, so a drift is a diff, not a guess.
/// </summary>
public sealed record Pins(string WorkflowCommit, Dictionary<string, string> Briefs, Dictionary<string, string> Skills, Models Models, string FactoryVersion);

/// <summary>
/// `run.json`. Written once at claim, never edited; its presence IS the claim. Branch and BaseSha are fixed here so a
/// later title edit or trunk move cannot change what the run means.
/// </summary>
public sealed record RunRecord(RepoSlug Repo, int Issue, string IssueTitle, string Branch, string BaseBranch, string BaseSha, DateTimeOffset ClaimedAt, Pins Pins);

/// <summary>One row of `children.tsv`: a spawned agent. `Id` is `<station>-<attempt>`; attempts derive from counting rows.</summary>
public sealed record Child(string Id, Station Station, string Spawner, string Handle, DateTimeOffset StartedAt, TimeSpan Expected, string HeadAtSpawn, string Watch);

/// <summary>One row of `decisions.tsv`: every move the driver executed, with its evidence. Append-only; the planner counts rows.</summary>
public sealed record Decision(DateTimeOffset At, Station Station, string Move, string Detail);

/// <summary>One line of `gates.jsonl`. A gate is a command and an exit code, keyed by the SHA it ran on (DESIGN §4, §7).</summary>
public sealed record GateResult(string Sha, string Gate, int ExitCode, TimeSpan Duration, string LogPath, DateTimeOffset At)
{
    [JsonIgnore] public bool Passed => ExitCode == 0;
}

/// <summary>`~/.config/factory/bindings.json`. Machine facts; the repo's WORKFLOW.md cannot override them.</summary>
public sealed record MachineConfig(string MaintainerLogin, GitHubApp App, Models Models, Dictionary<string, string> Clones, StationBinding[] Stations, string? ShellShim)
{
    public StationBinding Binding(Station s) => Stations.First(b => b.Station == s);
}

public sealed record GitHubApp(long AppId, long InstallationId, string Slug, string PemPath);

/// <summary>Who runs a station's agent, at which tier, and how long before it counts as stalled.</summary>
public sealed record StationBinding(Station Station, string Spawner, Tier Tier, int ExpectedMinutes, bool ReadOnly);
