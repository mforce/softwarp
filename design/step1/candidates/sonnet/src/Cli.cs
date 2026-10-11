namespace Factory;

// Five verbs, no flags that skip a gate, no `approve`, no `--force`. Hand-rolled parsing: nothing to trim under AOT.

public abstract record Command;
/// <summary>factory run mforce/cluckwork#1223 [more issues...]   Station 0 for each issue.</summary>
public sealed record RunCmd(IReadOnlyList<IssueRef> Issues) : Command;
/// <summary>factory advance &lt;issue&gt; [--expect spec] [--follow 900] [--json]   --follow takes seconds.</summary>
public sealed record AdvanceCmd(IssueRef Issue, Phase? Expect, TimeSpan Follow, bool Json) : Command;
/// <summary>factory status &lt;issue&gt; [--json]</summary>
public sealed record StatusCmd(IssueRef Issue, bool Json) : Command;
/// <summary>factory stop &lt;issue&gt; --reason "..."</summary>
public sealed record StopCmd(IssueRef Issue, string Reason) : Command;

public static class CommandLine
{
    /// <summary>Pure. Throws UsageException (exit 2) on a bad shape. Unit-testable without any capability.</summary>
    public static Command Parse(string[] args, string? defaultRepo) => throw new NotImplementedException();
}

public static class Program
{
    /// <summary>Composition root, the only place bindings are chosen: MachineConfig.Load; GitHubTracker and GitHubIdentity;
    /// GitWorkspaces; PaseoSpawner, ClaudeSpawner or CodexSpawner by config.Spawner; ShellGates. Then Engine.
    /// Prints AdvanceResult as text or --json (source-generated). Returns AdvanceResult.ExitCode.</summary>
    public static Task<int> Main(string[] args) => throw new NotImplementedException();
}
