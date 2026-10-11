using Factory;

/// <summary>
/// factory run     &lt;issue&gt; [--repo owner/name] [--step] [--wait 30m]   claim, then advance
/// factory advance &lt;issue&gt; [--repo owner/name] [--step] [--wait 30m]   advance as far as the gates allow
/// factory status  &lt;issue&gt; [--repo owner/name] [--json]                observe only
/// factory schema  triage|spec|implement                                print an artifact schema
/// Exit: 0 done, 2 waiting on an agent, 3 waiting on the maintainer (approval, stop state, escalation), 1 error.
/// `--repo` defaults to the `origin` remote of the current directory.
/// </summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        // TODO: parse by hand (four verbs, three flags; no System.CommandLine dependency);
        //       config = MachineConfig from ~/.config/factory/bindings.json via FactoryJson;
        //       driver = new Driver(config, new GitHubTracker(...), { paseo, claude, codex }, new GitCli(...), new ShellGateRunner(...), TimeProvider.System, Console.Out);
        //       dispatch; print Outcome.Status; return Outcome.ExitCode. Exceptions -> stderr, exit 1.
        await Task.CompletedTask;
        throw new NotImplementedException();
    }

    /// <summary>Parsed argv. Invalid input fails here, at the boundary; the driver trusts it.</summary>
    internal sealed record Invocation(string Verb, RunId Run, bool Step, TimeSpan? Wait, bool Json)
    {
        public static Invocation Parse(string[] args, Func<RepoSlug> repoFromCwd) => throw new NotImplementedException();
    }
}
