using Softwarp.Core;

namespace Softwarp.Bindings.Tests;

public sealed class GitIsolationTests : IDisposable
{
    readonly GitRemote git = new();
    static readonly CancellationToken None = CancellationToken.None;
    static readonly OperationId Push = new("0009-push");

    public void Dispose() => git.Dispose();

    /// <summary>Commits as the implementer would, with git outside any sandbox.</summary>
    static CommitId Commit(Workspace workspace, string file = "work.txt")
    {
        File.WriteAllText(Path.Combine(workspace.Path, file), $"work on {file}");
        GitRemote.Git(workspace.Path, "add", file);
        GitRemote.Git(workspace.Path, "commit", "--quiet", "-m", $"work on {file}");
        return new CommitId(GitRemote.Git(workspace.Path, "rev-parse", "HEAD"));
    }

    CommitId? Main => git.RemoteRef("refs/heads/main");

    [Fact]
    public async Task ReadTrunk_ReadsWorkflowAtTheRemoteDefaultBranch_NeverAWorktreeCopy()
    {
        var (run, workspace) = await git.WorktreeAsync();
        File.WriteAllText(Path.Combine(workspace.Path, "WORKFLOW.md"), "forged");
        Commit(workspace, "WORKFLOW.md");
        var trunk = git.Commit("WORKFLOW.md", GitRemote.Workflow + "\nSecond revision.");

        var read = await git.Isolation.ReadTrunkAsync(GitRemote.Item, None);
        Assert.Equal(trunk, read.Commit);
        Assert.EndsWith("Second revision.", read.WorkflowFile);
        Assert.Equal(run.Branch, (await git.Isolation.ObserveAsync(run, None))!.Branch);
    }

    [Fact]
    public async Task ReadTrunk_FollowsTheRemoteDefaultBranchNotTheNewestBranch()
    {
        var main = (await git.Isolation.ReadTrunkAsync(GitRemote.Item, None)).Commit;
        git.Commit("other.txt", "other", branch: "other");
        Assert.Equal(main, (await git.Isolation.ReadTrunkAsync(GitRemote.Item, None)).Commit);
    }

    [Fact]
    public async Task Ensure_MakesAStandaloneRepositoryWithNoRemoteOnTheRunBranchAtTrunk_AndIsIdempotent()
    {
        Assert.Null(await git.Isolation.ObserveAsync(git.Manifest(new CommitId("unused")), None));
        var (run, workspace) = await git.WorktreeAsync();
        Assert.Equal(Path.Combine(git.Slices, "mforce", "widget", "7"), workspace.Path);
        Assert.Equal(workspace, await git.Isolation.EnsureAsync(run, None));

        Assert.True(Directory.Exists(Path.Combine(workspace.Path, ".git", "objects")));
        Assert.False(File.Exists(Path.Combine(workspace.Path, ".git", "objects", "info", "alternates")));
        Assert.Equal("", GitRemote.Git(workspace.Path, "remote"));
        Assert.DoesNotContain(git.Trusted, string.Concat(Directory.EnumerateFiles(Path.Combine(workspace.Path, ".git"))
            .Select(File.ReadAllText)));
        Assert.Equal(new HeadObservation(run.Branch, run.Trunk, Dirt: null, DescendsFromTrunk: true),
            await git.Isolation.ObserveAsync(run, None));
        Assert.Null(git.RemoteRef($"refs/heads/{run.Branch}"));
    }

    [Fact]
    public async Task Ensure_AfterTheRepositoryIsLost_RemakesItAtTheLastImportedHead()
    {
        var (run, workspace) = await git.WorktreeAsync();
        var pushed = Commit(workspace);
        Assert.True(await git.Isolation.PushAsync(run, pushed, Push, None));
        Directory.Delete(workspace.Path, recursive: true);

        await git.Isolation.EnsureAsync(run, None);
        Assert.Equal(pushed, (await git.Isolation.ObserveAsync(run, None))!.Head);
    }

    [Fact]
    public async Task Observe_ReportsUntrackedFilesAsDirty()
    {
        var (run, workspace) = await git.WorktreeAsync();
        File.WriteAllText(Path.Combine(workspace.Path, "new.txt"), "untracked");
        Assert.False((await git.Isolation.ObserveAsync(run, None))!.Clean);
    }

    [Fact]
    public async Task Observe_ReportsAnotherBranchAndAHeadThatDoesNotDescendFromTrunk()
    {
        var (run, workspace) = await git.WorktreeAsync();
        GitRemote.Git(workspace.Path, "checkout", "--quiet", "--orphan", "elsewhere");
        Commit(workspace);

        var seen = (await git.Isolation.ObserveAsync(run, None))!;
        Assert.Equal(("elsewhere", false), (seen.Branch, seen.DescendsFromTrunk));
    }

    [Fact]
    public async Task Observe_ReportsTheBranchAndHeadOfOneAdvertisement_WhenHeadMovesBeforeTheFetch()
    {
        var (run, workspace) = await git.WorktreeAsync();
        var head = Commit(workspace);
        var (launcher, moved) = (Path.Combine(git.Root, "systemd-run"), Path.Combine(git.Root, "moved"));
        // After the first sandboxed call, the advertisement, HEAD moves to a new commit on another branch.
        GitRemote.Script(launcher, $"""
            #!/bin/sh
            /usr/bin/systemd-run "$@"
            code=$?
            if [ ! -e {moved} ]; then
              touch {moved}
              git -C {workspace.Path} checkout -q -b elsewhere
              git -C {workspace.Path} -c user.name=agent -c user.email=agent@example.com commit -q --allow-empty -m moved
            fi > /dev/null 2>&1
            exit $code
            """);
        var isolation = new GitIsolation(git.Slices, _ => git.Remote, new Sandbox(GateLimits.Default, GitRemote.Home, systemdRun: launcher));

        var seen = (await isolation.ObserveAsync(run, None))!;
        Assert.True(File.Exists(moved));
        Assert.Equal((run.Branch, head), (seen.Branch, seen.Head));
    }

    /// <summary>Each trick hides a change from plain `git status`, which the first assertion shows; the observation
    /// still reports the tree dirty and names the path.</summary>
    [Theory]
    [InlineData("skip-worktree", "changed work.txt")]
    [InlineData("assume-unchanged", "changed work.txt")]
    [InlineData("info/exclude", "untracked secret.txt")]
    [InlineData("core.excludesFile", "untracked secret.txt")]
    [InlineData("replace", "changed work.txt")]
    [InlineData("fsmonitor", "changed work.txt")]
    [InlineData("filter in .gitattributes", "changed work.txt")]
    [InlineData("filter in info/attributes", "changed work.txt")]
    [InlineData("core.worktree", "changed work.txt")]
    [InlineData("untracked .gitignore", "untracked .gitignore")]
    [InlineData("nested untracked .gitignore", "untracked sub/.gitignore")]
    public async Task ChangeAGitSettingHidesFromStatus_IsStillDirty(string trick, string dirt)
    {
        var (run, workspace) = await git.WorktreeAsync();
        Commit(workspace);
        var dotGit = Path.Combine(workspace.Path, ".git");
        string Git(params string[] arguments) => GitRemote.Git(workspace.Path, arguments);
        // Same length as the committed text: git hashes a file whose size is unchanged, and a filter can then lie.
        void Change() => File.WriteAllText(Path.Combine(workspace.Path, "work.txt"), "WORK ON WORK.TXT");
        switch (trick)
        {
            case "filter in .gitattributes" or "filter in info/attributes":
                // The filter turns any content back into the committed text, so git hashes the edit as unchanged.
                File.Copy(Path.Combine(workspace.Path, "work.txt"), Path.Combine(dotGit, "original.txt"));
                GitRemote.Script(Path.Combine(dotGit, "hide.sh"), $"#!/bin/sh\ncat >/dev/null\ncat {Path.Combine(dotGit, "original.txt")}\n");
                Git("config", "filter.hide.clean", Path.Combine(dotGit, "hide.sh"));
                if (trick == "filter in info/attributes") File.WriteAllText(Path.Combine(dotGit, "info", "attributes"), "work.txt filter=hide\n");
                else
                {
                    File.WriteAllText(Path.Combine(workspace.Path, ".gitattributes"), "work.txt filter=hide\n");
                    Git("add", ".gitattributes");
                    Git("commit", "--quiet", "-m", "attributes");
                }
                await Task.Delay(TimeSpan.FromSeconds(1.1)); // past git's racy-timestamp window
                Change();
                break;
            case "core.worktree":
                var decoy = Path.Combine(git.Root, "decoy");
                Directory.CreateDirectory(decoy);
                File.Copy(Path.Combine(workspace.Path, "work.txt"), Path.Combine(decoy, "work.txt"));
                File.Copy(Path.Combine(workspace.Path, "WORKFLOW.md"), Path.Combine(decoy, "WORKFLOW.md"));
                Git("config", "core.worktree", decoy);
                Change();
                break;
            case "untracked .gitignore":
                File.WriteAllText(Path.Combine(workspace.Path, ".gitignore"), "*\n");
                File.WriteAllText(Path.Combine(workspace.Path, "new.txt"), "hidden");
                break;
            case "nested untracked .gitignore":
                Directory.CreateDirectory(Path.Combine(workspace.Path, "sub"));
                File.WriteAllText(Path.Combine(workspace.Path, "sub", ".gitignore"), "*\n");
                File.WriteAllText(Path.Combine(workspace.Path, "sub", "new.txt"), "hidden");
                break;
            case "skip-worktree" or "assume-unchanged":
                Git("update-index", $"--{trick}", "work.txt");
                Change();
                break;
            case "info/exclude" or "core.excludesFile":
                File.WriteAllText(Path.Combine(workspace.Path, "secret.txt"), "untracked");
                var excludes = trick == "info/exclude" ? Path.Combine(dotGit, "info", "exclude") : Path.Combine(dotGit, "excludes");
                File.WriteAllText(excludes, "secret.txt\n");
                if (trick == "core.excludesFile") Git("config", "core.excludesFile", excludes);
                break;
            case "replace":
                Change();
                Git("add", "work.txt");
                Git("replace", "HEAD", Git("commit-tree", Git("write-tree"), "-m", "the tree as changed"));
                Git("read-tree", "HEAD");
                break;
            case "fsmonitor":
                var monitor = Path.Combine(dotGit, "monitor.sh");
                GitRemote.Script(monitor, "#!/bin/sh\nexit 0\n");
                Git("config", "core.fsmonitor", monitor);
                Git("config", "core.fsmonitorHookVersion", "1");
                await Task.Delay(TimeSpan.FromSeconds(1.1)); // past git's racy-timestamp window
                Git("update-index", "--fsmonitor");
                Git("status", "--porcelain");
                Change();
                break;
        }

        Assert.Equal("", Git("status", "--porcelain"));
        Assert.Equal(dirt, (await git.Isolation.ObserveAsync(run, None))!.Dirt);
    }

    [Fact]
    public async Task CoreBareInTheImplementersConfig_DoesNotStopTheCheck()
    {
        var (run, workspace) = await git.WorktreeAsync();
        Commit(workspace);
        GitRemote.Git(workspace.Path, "config", "core.bare", "true");
        File.WriteAllText(Path.Combine(workspace.Path, "work.txt"), "changed after the commit");
        Assert.Equal("changed work.txt", (await git.Isolation.ObserveAsync(run, None))!.Dirt);
    }

    [Fact]
    public async Task UntrackedGitattributes_IsDirt()
    {
        var (run, workspace) = await git.WorktreeAsync();
        File.WriteAllText(Path.Combine(workspace.Path, ".gitattributes"), "* -text\n");
        Assert.Equal("untracked .gitattributes", (await git.Isolation.ObserveAsync(run, None))!.Dirt);
    }

    [Fact]
    public async Task BuildOutputATrackedGitignoreCovers_IsClean()
    {
        var (run, workspace) = await git.WorktreeAsync();
        File.WriteAllText(Path.Combine(workspace.Path, ".gitignore"), "bin/\n");
        GitRemote.Git(workspace.Path, "add", ".gitignore");
        GitRemote.Git(workspace.Path, "commit", "--quiet", "-m", "ignore build output");
        var head = new CommitId(GitRemote.Git(workspace.Path, "rev-parse", "HEAD"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "bin"));
        File.WriteAllText(Path.Combine(workspace.Path, "bin", "out.dll"), "built");
        Assert.Equal(new HeadObservation(run.Branch, head, null, true), await git.Isolation.ObserveAsync(run, None));
    }

    [Fact]
    public async Task SandboxStartedFromAThreadThatThenExits_RunsToTheEnd()
    {
        Task<int?>? run = null;
        // The caller outlives bwrap's own setup, so a fork from this thread would be killed when the thread exits.
        var caller = new Thread(() =>
        {
            run = Tool.RunAsync("/usr/bin/bwrap", ["--die-with-parent", "--ro-bind", "/", "/", "sleep", "3"], Tool.Trusted,
                new Captured("bwrap"), TimeSpan.FromMinutes(1), None);
            Thread.Sleep(TimeSpan.FromSeconds(1));
        });
        caller.Start();
        caller.Join();

        Assert.Equal(0, await run!);
    }

    [Fact]
    public async Task ToolThatFloodsAStream_IsKilledAndRefused()
    {
        var marker = $"flood-{Guid.NewGuid():N}";
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Tool.ReadAsync("sh", ["-c", $"yes {marker}"], TimeSpan.FromMinutes(1), None));
        Assert.Contains("wrote more than", refused.Message);
    }

    [Fact]
    public async Task ToolPastItsTimeout_IsKilledWithItsTreeAndRefused()
    {
        var marker = $"4646.{Random.Shared.Next(100000, 999999)}";
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tool.ReadAsync("sh", ["-c", $"sleep {marker} & sleep {marker}1"], TimeSpan.FromSeconds(1), cancel.Token));
        Assert.Contains("ran past its", refused.Message);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        Assert.False(GitRemote.Alive(marker));
    }

    [Fact]
    public async Task Push_PushesTheRunBranchAtHead_AndARetryPushesNothing()
    {
        var (run, workspace) = await git.WorktreeAsync();
        var head = Commit(workspace);
        var log = Path.Combine(git.Root, "received.log");
        GitRemote.Script(Path.Combine(git.Remote, "hooks", "pre-receive"), $"#!/bin/sh\ncat >> {log}\n");
        var main = Main;

        Assert.Null(await git.Isolation.RemoteHeadAsync(run, None));
        Assert.True(await git.Isolation.PushAsync(run, head, Push, None));
        Assert.True(await git.Isolation.PushAsync(run, head, Push, None));

        Assert.Equal(head, await git.Isolation.RemoteHeadAsync(run, None));
        Assert.Equal(head, git.RemoteRef($"refs/heads/{run.Branch}"));
        Assert.Single(File.ReadAllLines(log));
        Assert.Equal(main, Main);
    }

    [Fact]
    public async Task Push_ThatTheRemoteRejectsAsNotAFastForward_ReturnsFalseAndLeavesTheRemote()
    {
        var (run, workspace) = await git.WorktreeAsync();
        var head = Commit(workspace);
        var foreign = git.Commit("foreign.txt", "someone else", branch: run.Branch);

        Assert.False(await git.Isolation.PushAsync(run, head, Push, None));
        Assert.Equal(foreign, git.RemoteRef($"refs/heads/{run.Branch}"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Push_OfAHeadTheRunBranchIsNotAtOrThatDoesNotDescendFromTrunk_ThrowsAndPushesNothing(bool moved)
    {
        var (run, workspace) = await git.WorktreeAsync();
        if (!moved) GitRemote.Git(workspace.Path, "checkout", "--quiet", "--orphan", "rootless");
        var head = Commit(workspace);
        if (moved) Commit(workspace, "later.txt");
        else GitRemote.Git(workspace.Path, "branch", "--quiet", "-f", run.Branch, head.Value);

        await Assert.ThrowsAsync<InvalidOperationException>(() => git.Isolation.PushAsync(run, head, Push, None));
        Assert.Null(git.RemoteRef($"refs/heads/{run.Branch}"));
    }

    [Fact]
    public async Task PushConfigInTheImplementersRepository_CannotRedirectForceOrMirrorThePush()
    {
        var (run, workspace) = await git.WorktreeAsync();
        var head = Commit(workspace);
        var evil = Path.Combine(git.Root, "evil.git");
        GitRemote.Git(git.Root, "init", "--quiet", "--bare", evil);
        foreach (var (key, value) in new[]
                 {
                     ("remote.origin.url", evil), ("remote.origin.pushurl", evil), ("remote.origin.mirror", "true"),
                     ("remote.origin.push", "+refs/heads/*:refs/heads/main"), ("push.default", "matching"),
                     ($"url.{evil}.insteadOf", git.Remote), ($"url.{evil}.pushInsteadOf", git.Remote),
                 })
            GitRemote.Git(workspace.Path, "config", key, value);
        var main = Main;

        Assert.True(await git.Isolation.PushAsync(run, head, Push, None));
        Assert.Equal((main, head), (Main, git.RemoteRef($"refs/heads/{run.Branch}")));
        Assert.Equal("", GitRemote.Git(evil, "for-each-ref"));
    }

    [Fact]
    public async Task TrustedCloneWhoseConfigLeftTheBaselineOrThatBorrowsObjects_IsRefused()
    {
        var (run, workspace) = await git.WorktreeAsync();
        var head = Commit(workspace);
        var config = Path.Combine(git.Trusted, "config");
        var baseline = File.ReadAllText(config);
        // A valid and harmless setting: the trusted clone refuses any config other than its baseline.
        File.AppendAllText(config, "[gc]\n\tauto = 0\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.Isolation.PushAsync(run, head, Push, None));

        File.WriteAllText(config, baseline);
        var alternates = Path.Combine(git.Trusted, "objects", "info", "alternates");
        File.WriteAllText(alternates, Path.Combine(git.Root, "elsewhere.git", "objects") + "\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.Isolation.ReadTrunkAsync(GitRemote.Item, None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.Isolation.PushAsync(run, head, Push, None));
        Assert.Null(git.RemoteRef($"refs/heads/{run.Branch}"));
    }

    [Fact]
    public async Task FilterFsmonitorAndHooksTheImplementerConfigured_NeverRun()
    {
        var (run, workspace) = await git.WorktreeAsync();
        var head = Commit(workspace);
        var dotGit = Path.Combine(workspace.Path, ".git");
        var (outside, inside) = (Path.Combine(git.Root, "marker"), Path.Combine(workspace.Path, "marker"));
        // The script sits in .git, where the implementer's own git would run it.
        var script = Path.Combine(dotGit, "evil.sh");
        GitRemote.Script(script, $"#!/bin/sh\ntouch {outside} {inside}\ncat\nexit 1\n");
        foreach (var (key, value) in new[]
                 {
                     ("filter.evil.clean", script), ("filter.evil.required", "true"), ("core.fsmonitor", script),
                     ("core.hooksPath", dotGit), ("uploadpack.packObjectsHook", script),
                 })
            GitRemote.Git(workspace.Path, "config", key, value);
        File.WriteAllText(Path.Combine(dotGit, "info", "attributes"), "* filter=evil\n");
        foreach (var hook in new[] { "post-checkout", "pre-push", "reference-transaction", "post-index-change" })
            File.Copy(script, Path.Combine(dotGit, hook));
        File.SetLastWriteTimeUtc(Path.Combine(workspace.Path, "work.txt"), DateTime.UtcNow.AddMinutes(5));

        // The check reads none of the implementer's config, so the required filter that would fail never runs.
        Assert.Null((await git.Isolation.ObserveAsync(run, None))!.Dirt);
        Assert.True(await git.Isolation.PushAsync(run, head, Push, None));

        Assert.False(File.Exists(outside));
        Assert.False(File.Exists(inside));
    }

    /// <summary>Each trick makes a git that reads the implementer's repository on the host read another repository or a
    /// host file instead. The import reads that repository only inside the sandbox, where those paths resolve to nothing:
    /// no object of the other repository reaches the trusted clone, and a FIFO or /dev/zero costs at most the sandbox's
    /// time and memory, never the host's.</summary>
    [Theory]
    [InlineData("commondir")]
    [InlineData("alternates")]
    [InlineData("gitfile")]
    [InlineData("objects link")]
    [InlineData("ref link")]
    [InlineData("fifo")]
    [InlineData("dev zero")]
    public async Task ImplementersRepositoryThatBorrowsAnotherRepository_IsNotImported(string trick)
    {
        var (run, workspace) = await git.WorktreeAsync();
        var head = Commit(workspace);
        var foreign = Path.Combine(git.Root, "foreign");
        GitRemote.Git(git.Root, "init", "--quiet", "--initial-branch=private", foreign);
        File.WriteAllText(Path.Combine(foreign, "private.txt"), "private");
        GitRemote.Git(foreign, "add", "private.txt");
        GitRemote.Git(foreign, "commit", "--quiet", "-m", "private");
        var (foreignHead, foreignBlob) = (GitRemote.Git(foreign, "rev-parse", "HEAD"), GitRemote.Git(foreign, "rev-parse", "HEAD:private.txt"));
        var (dotGit, foreignGit) = (Path.Combine(workspace.Path, ".git"), Path.Combine(foreign, ".git"));
        var branch = Path.Combine(dotGit, "refs", "heads", run.Branch);
        void LinkObjects()
        {
            Directory.Delete(Path.Combine(dotGit, "objects"), recursive: true);
            Directory.CreateSymbolicLink(Path.Combine(dotGit, "objects"), Path.Combine(foreignGit, "objects"));
        }
        File.Delete(branch);
        switch (trick)
        {
            case "commondir":
                File.WriteAllText(Path.Combine(dotGit, "commondir"), foreignGit);
                File.WriteAllText(branch, foreignHead);
                break;
            case "alternates":
                File.WriteAllText(Path.Combine(dotGit, "objects", "info", "alternates"), Path.Combine(foreignGit, "objects"));
                File.WriteAllText(branch, foreignHead);
                break;
            case "gitfile":
                File.WriteAllText(Path.Combine(dotGit, ".git"), $"gitdir: {foreignGit}\n");
                File.WriteAllText(branch, head.Value);
                break;
            case "objects link":
                LinkObjects();
                File.WriteAllText(branch, foreignHead);
                break;
            case "ref link":
                LinkObjects();
                File.CreateSymbolicLink(branch, Path.Combine(foreignGit, "refs", "heads", "private"));
                break;
            case "fifo":
                using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [branch])) await mkfifo.WaitForExitAsync();
                break;
            case "dev zero":
                File.CreateSymbolicLink(branch, "/dev/zero");
                break;
        }
        var isolation = new GitIsolation(git.Slices, _ => git.Remote,
            new Sandbox(GateLimits.Default with { MemoryMax = "256M" }, GitRemote.Home), TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var (refusal, peak) = await HostPeakAsync(workspace.Path, async () =>
            (await Refusal(() => isolation.ObserveAsync(run, cancel.Token)), await Refusal(() => isolation.PushAsync(run, head, Push, cancel.Token))));

        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(40));
        Assert.InRange(peak, 0, 100 << 20);
        if (trick is "fifo" or "dev zero") Assert.NotNull(refusal.Observe);
        Assert.NotEqual(0, GitRemote.TryGit(git.Trusted, "cat-file", "-e", foreignHead).Code);
        Assert.NotEqual(0, GitRemote.TryGit(git.Trusted, "cat-file", "-e", foreignBlob).Code);
    }

    static async Task<InvalidOperationException?> Refusal(Func<Task> import)
    {
        try
        {
            await import();
            return null;
        }
        catch (InvalidOperationException e)
        {
            return e;
        }
    }

    /// <summary>Runs the action while it samples, every 10 ms, the resident memory of every process outside a softwarp
    /// scope whose command line names the path, and returns the action's result and the largest sample in bytes.</summary>
    static async Task<((InvalidOperationException? Observe, InvalidOperationException? Push), long)> HostPeakAsync(string path,
        Func<Task<(InvalidOperationException?, InvalidOperationException?)>> action)
    {
        long peak = 0;
        using var done = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                foreach (var process in Directory.EnumerateDirectories("/proc"))
                {
                    try
                    {
                        if (!File.ReadAllText(Path.Combine(process, "cmdline")).Contains(path)
                            || File.ReadAllText(Path.Combine(process, "cgroup")).Contains("/softwarp-")) continue;
                        var rss = File.ReadLines(Path.Combine(process, "status")).FirstOrDefault(line => line.StartsWith("VmRSS:"));
                        if (rss is not null) peak = Math.Max(peak, long.Parse(rss.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                    }
                }
                await Task.Delay(10);
            }
        });
        (InvalidOperationException?, InvalidOperationException?) result;
        try
        {
            result = await action();
        }
        finally
        {
            await done.CancelAsync();
            await sampler;
        }
        return (result, peak);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DotGitReplacedByAFileOrALinkToTheTrustedClone_IsRefused(bool link)
    {
        var (run, workspace) = await git.WorktreeAsync();
        var head = Commit(workspace);
        var dotGit = Path.Combine(workspace.Path, ".git");
        Directory.Move(dotGit, Path.Combine(git.Root, "moved.git"));
        if (link) Directory.CreateSymbolicLink(dotGit, git.Trusted);
        else File.WriteAllText(dotGit, $"gitdir: {git.Trusted}\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => git.Isolation.ObserveAsync(run, None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.Isolation.PushAsync(run, head, Push, None));
        Assert.Null(git.RemoteRef($"refs/heads/{run.Branch}"));
    }

    [Theory]
    [InlineData("..", "widget")]
    [InlineData("mforce", "a/b")]
    public async Task ItemThatIsNotOneSafePathSegment_IsRefused(string owner, string repo) =>
        await Assert.ThrowsAsync<ArgumentException>(() => git.Isolation.ReadTrunkAsync(new ItemId(owner, repo, 7), None));

    [Theory]
    [InlineData("ssh://git@github.com/mforce/widget.git")]
    [InlineData("git://github.com/mforce/widget.git")]
    [InlineData("ext::sh -c touch% /tmp/pwned")]
    public async Task RemoteOverAnyTransportButHttpsOrALocalPath_IsRefused(string url) =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new GitIsolation(git.Slices, _ => url, git.Sandbox).ReadTrunkAsync(GitRemote.Item, None));
}

[Collection(nameof(ProcessEnvironment))]
public sealed class GitEnvironmentTests : IDisposable
{
    readonly GitRemote git = new();

    public void Dispose() => git.Dispose();

    [Fact]
    public async Task GitConfigFromTheCallersEnvironment_CannotRedirectFetchOrPush()
    {
        var evil = Path.Combine(git.Root, "evil.git");
        GitRemote.Git(git.Root, "clone", "--quiet", "--bare", git.Remote, evil);
        var planted = Path.Combine(git.Root, "planted.gitconfig");
        File.WriteAllText(planted, $"[url \"{evil}\"]\n\tinsteadOf = {git.Remote}\n\tpushInsteadOf = {git.Remote}\n");
        var main = git.Commit("WORKFLOW.md", GitRemote.Workflow + "\nThe real trunk.");
        var home = Directory.CreateDirectory(Path.Combine(git.Root, "home")).FullName;
        File.Copy(planted, Path.Combine(home, ".gitconfig"));
        using var environment = GitRemote.Planted(new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = planted, ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = $"url.{evil}.insteadOf",
            ["GIT_CONFIG_VALUE_0"] = git.Remote, ["GIT_DIR"] = evil, ["HOME"] = home, ["XDG_CONFIG_HOME"] = home,
        });
        var trunk = await git.Isolation.ReadTrunkAsync(GitRemote.Item, CancellationToken.None);
        Assert.Equal(main, trunk.Commit);
        Assert.EndsWith("The real trunk.", trunk.WorkflowFile);
    }

    [Fact]
    public async Task TheMaintainersOwnNuGetConfig_IsNeverReadByTheFetch()
    {
        // The maintainer's config names a private feed that has the package; the fetch's one source does not.
        var (scratch, privateFeed, feed) = (Path.Combine(git.Root, "scratch"),
            Directory.CreateDirectory(Path.Combine(git.Root, "private")).FullName, Directory.CreateDirectory(Path.Combine(git.Root, "feed")).FullName);
        File.Copy(Path.Combine(GitRemote.Home, ".nuget", "packages", "xunit.abstractions", "2.0.3", "xunit.abstractions.2.0.3.nupkg"),
            Path.Combine(privateFeed, "xunit.abstractions.2.0.3.nupkg"));
        var home = Directory.CreateDirectory(Path.Combine(git.Root, "home", ".nuget", "NuGet")).Parent!.Parent!.FullName;
        File.WriteAllText(Path.Combine(home, ".nuget", "NuGet", "NuGet.Config"),
            $"""<configuration><packageSources><clear /><add key="private" value="{privateFeed}" /></packageSources></configuration>""");
        var (run, workspace) = await git.WorktreeAsync();
        File.WriteAllText(Path.Combine(workspace.Path, "packages.lock.json"),
            """{ "version": 1, "dependencies": { "net10.0": { "xunit.abstractions": { "type": "Direct", "resolved": "2.0.3" } } } }""");
        var sandbox = new Sandbox(GateLimits.Default, home);
        var fetch = new PackageFetch(git.Isolation, sandbox, scratch, 20, feed);
        using (GitRemote.Planted(new Dictionary<string, string> { ["HOME"] = home }))
            await Assert.ThrowsAsync<FetchRefusedException>(() =>
                fetch.EnsureAsync(new GateRequest(workspace, run.Item.Id, run.Trunk, GateKind.Build, new CommandSpec("true", []), TestReport.Trx,
                    [], Path.Combine(git.Root, "Build.log"), Path.Combine(git.Root, "Build.packages")), Path.Combine(git.Root, "Build.fetch.log"),
                    TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(sandbox.Packages, "xunit.abstractions")));
    }

    [Fact]
    public async Task TheCallersEnvironment_NeverReachesATrustedTool()
    {
        using var environment = GitRemote.Planted(new[] { "DOTNET_STARTUP_HOOKS", "MSBuildSDKsPath", "SOFTWARP_CANARY_TOKEN", "LD_LIBRARY_PATH" }
            .ToDictionary(name => name, _ => "/nonexistent/planted"));
        var env = await Tool.ReadAsync("env", [], TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.DoesNotContain("planted", env.Output);
        Assert.Equal(0, (await Tool.ReadAsync("dotnet", ["--version"], TimeSpan.FromMinutes(1), CancellationToken.None)).ExitCode);
    }
}
