using System.Diagnostics;
using Softwarp.Core;

namespace Softwarp.Bindings.Tests;

/// <summary>A temp directory holding a bare repository that plays the forge remote, and a seed clone that pushes to it as
/// the maintainer would.</summary>
sealed class GitRemote : IDisposable
{
    public const string Workflow = """
        ---
        {
          "branch": "feat/{issue}-{slug}",
          "test_report": "trx",
          "commands": { "build": ["dotnet", "build"], "test_targeted": ["dotnet", "test", "--filter", "{targets}"] }
        }
        ---
        Agents read AGENTS.md first.
        """;

    public static readonly ItemId Item = new("mforce", "widget", 7);

    public string Root { get; } = Directory.CreateTempSubdirectory("softwarp-git-").FullName;
    public string Remote => Path.Combine(Root, "remote.git");
    public string Seed => Path.Combine(Root, "seed");
    public string Slices => Path.Combine(Root, "slices");
    public string Trusted => Path.Combine(Slices, "mforce", "widget", "trusted.git");
    public static string Home => Environment.GetEnvironmentVariable("HOME")!;
    public Sandbox Sandbox { get; } = new(GateLimits.Default, Home);
    public GitIsolation Isolation { get; }

    public GitRemote()
    {
        Git(Root, "init", "--quiet", "--bare", "--initial-branch=main", Remote);
        Git(Root, "clone", "--quiet", Remote, Seed);
        Commit("WORKFLOW.md", Workflow);
        Isolation = new GitIsolation(Slices, _ => Remote, Sandbox);
    }

    /// <summary>Commits the file on the seed's current branch and pushes it; returns the new head.</summary>
    public CommitId Commit(string file, string text, string? branch = null)
    {
        if (branch is not null) Git(Seed, "checkout", "--quiet", "-B", branch);
        File.WriteAllText(Path.Combine(Seed, file), text);
        Git(Seed, "add", file);
        Git(Seed, "commit", "--quiet", "-m", $"seed {file}");
        Git(Seed, "push", "--quiet", "origin", "HEAD");
        return new CommitId(Git(Seed, "rev-parse", "HEAD"));
    }

    /// <summary>The commit a ref names on the remote; null when it does not exist.</summary>
    public CommitId? RemoteRef(string name) => TryGit(Remote, "rev-parse", "--verify", "--quiet", name) is { Code: 0 } r
        ? new CommitId(r.Output)
        : null;

    /// <summary>Reads trunk and makes the implementer's repository on the run branch at it.</summary>
    public async Task<(RunManifest Run, Workspace Workspace)> WorktreeAsync()
    {
        var run = Manifest((await Isolation.ReadTrunkAsync(Item, CancellationToken.None)).Commit);
        return (run, await Isolation.EnsureAsync(run, CancellationToken.None));
    }

    public RunManifest Manifest(CommitId trunk, ItemId? item = null) => new(new RunId("mforce/widget/7/abc"), trunk,
        $"feat/{(item ?? Item).Number}-widget", "maintainer-id",
        new Core.Workflow(new("dotnet", ["build"]), new("dotnet", ["test", "--filter", "{targets}"]), TestReport.Trx, null),
        new WorkItem(item ?? Item, "Widget", "Make the widget.", "rev-1", new Uri("https://github.com/mforce/widget/issues/7")),
        new Digest("workflow-digest"), new AgentPins(new(Harness.Claude, "t", "low"), new(Harness.Claude, "s", "low"),
            new(Harness.Claude, "i", "low"), new(Harness.Claude, "c", "low")));

    public static string Git(string directory, params string[] arguments)
    {
        var result = TryGit(directory, arguments);
        return result.Code == 0 ? result.Output : throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {result.Error}");
    }

    public static (int Code, string Output, string Error) TryGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in new[] { ("GIT_AUTHOR_NAME", "seed"), ("GIT_AUTHOR_EMAIL", "seed@example.com"),
                     ("GIT_COMMITTER_NAME", "seed"), ("GIT_COMMITTER_EMAIL", "seed@example.com") })
            start.Environment[key] = value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result.Trim(), error);
    }

    /// <summary>Whether a `sleep` with the marker in its arguments runs; matching on the command name skips the launchers
    /// (systemd-run, bwrap) whose own arguments also hold the marker.</summary>
    public static bool Alive(string marker) => Directory.EnumerateDirectories("/proc").Any(proc =>
    {
        try
        {
            return File.ReadAllText(Path.Combine(proc, "comm")).Trim() == "sleep"
                   && File.ReadAllText(Path.Combine(proc, "cmdline")).Replace('\0', ' ').Contains(marker);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    });

    /// <summary>Writes an executable script.</summary>
    public static void Script(string path, string text)
    {
        File.WriteAllText(path, text);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Sets variables in the test process's own environment until disposed. Only tests in the
    /// <see cref="ProcessEnvironment"/> collection may use it.</summary>
    public static IDisposable Planted(IReadOnlyDictionary<string, string> variables)
    {
        var saved = variables.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        foreach (var (name, value) in variables) Environment.SetEnvironmentVariable(name, value);
        return new Restore(saved);
    }

    sealed class Restore(Dictionary<string, string?> saved) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

/// <summary>Tests that change the test process's own environment run alone.</summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;
