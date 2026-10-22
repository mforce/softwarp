using System.Diagnostics;
using System.Text.RegularExpressions;
using Softwarp.Core;

namespace Softwarp.Bindings.Tests;

/// <summary>A worktree on a local remote, and a way to run one command in it as a gate.</summary>
public abstract class GateFixture : IAsyncLifetime
{
    internal readonly GitRemote git = new();
    internal static readonly CancellationToken None = CancellationToken.None;
    internal Workspace workspace = null!;
    internal RunManifest run = null!;

    internal string RunStore => Path.Combine(git.Root, "runs");
    internal string Scratch => Path.Combine(git.Root, "gates");

    public async Task InitializeAsync()
    {
        (run, workspace) = await git.WorktreeAsync();
    }

    public Task DisposeAsync()
    {
        git.Dispose();
        return Task.CompletedTask;
    }

    internal GateRunner Runner(GateLimits? limits = null, string systemdRun = "/usr/bin/systemd-run", string bwrap = "/usr/bin/bwrap") =>
        new(new Sandbox(limits ?? GateLimits.Default with { BuildTimeout = TimeSpan.FromMinutes(2) }, GitRemote.Home, systemdRun, bwrap), Scratch);

    /// <summary>Runs the command as a gate, with its log where the factory puts it: an empty file in the run store.</summary>
    internal async Task<(GateRun Run, string Log)> GateAsync(CommandSpec command, GateRunner? runner = null,
        GateKind kind = GateKind.Build, TestReport report = TestReport.Junit)
    {
        var log = Path.Combine(RunStore, "mforce", "widget", "7", "ops", "0005-gates", $"{kind}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        new FileStream(log, FileMode.CreateNew).Dispose();
        var gate = await (runner ?? Runner()).RunAsync(new GateRequest(workspace, run.Item.Id, run.Trunk, kind, command, report, [], log,
            Path.ChangeExtension(log, ".packages")), None);
        var text = File.ReadAllText(log);
        File.Delete(log);
        return (gate, text);
    }

    internal static CommandSpec Bash(string script) => new("bash", ["-c", script]);
}

public sealed class GateRunnerTests : GateFixture
{
    [Fact]
    public async Task Sandbox_HidesHomeSecretsTheRunStoreTheTrustedCloneAndHostSockets()
    {
        var home = GitRemote.Home;
        string[] hidden =
        [
            $"{home}/.config", $"{home}/.config/gh/config.yml", $"{home}/.claude", $"{home}/.claude.json", $"{home}/.ssh",
            $"{home}/.codex", $"{home}/.docker", $"{home}/.paseo", $"{home}/dev", $"{home}/.softwarp", RunStore, git.Trusted,
            git.Remote, git.Seed, Scratch, "/var/run/docker.sock", "/run/docker.sock", "/run/user", "/opt", "/var", "/srv", "/root", "/media", "/mnt",
        ];
        var (gate, log) = await GateAsync(Bash(
            $"leak=0; for p in {string.Join(' ', hidden.Select(p => $"'{p}'"))}; do [ -e \"$p\" ] && {{ echo \"LEAK $p\"; leak=1; }}; done; " +
            "echo \"HOME: $(ls -A \"$HOME\" | tr '\\n' ' ')\"; exit $leak"));

        Assert.True(gate.ExitCode == 0, log);
        // The home shows .nuget only when softwarp's package cache exists, and, when the worktree lives under home, the
        // mount point leading to it.
        var under = Path.GetRelativePath(home, workspace.Path) is var relative && !relative.StartsWith("..") ? $"{relative.Split('/')[0]} " : "";
        Assert.Matches($@"HOME: (\.nuget )?{Regex.Escape(under)}\n", log);
    }

    [Fact]
    public async Task Gate_SeesOnlySoftwarpsPackageCache_AtTheNuGetPath()
    {
        var home = Path.Combine(git.Root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".nuget", "packages", "maintainers.own"));
        Directory.CreateDirectory(Path.Combine(home, ".softwarp", "packages", "softwarps.own"));
        var runner = new GateRunner(new Sandbox(GateLimits.Default, home), Scratch);

        var (gate, log) = await GateAsync(Bash("""
            fail=0
            [ -d "$HOME/.nuget/packages/softwarps.own" ] || { echo "MISSING softwarp's cache"; fail=1; }
            [ -e "$HOME/.nuget/packages/maintainers.own" ] && { echo "LEAK the maintainer's cache"; fail=1; }
            [ -e "$HOME/.softwarp" ] && { echo "LEAK ~/.softwarp"; fail=1; }
            exit $fail
            """), runner);

        Assert.True(gate.ExitCode == 0, log);
    }

    [Fact]
    public async Task Gate_WritesTheWorktreeButCannotCommitMoveRefsOrRewriteGit()
    {
        var dotGitFiles = Directory.EnumerateFiles(Path.Combine(workspace.Path, ".git"), "*", SearchOption.AllDirectories).Order().ToList();
        var (gate, log) = await GateAsync(Bash("""
            fail=0
            echo x > probe.tmp && rm probe.tmp || { echo "CANNOT write the worktree"; fail=1; }
            try() { if "$@" >/dev/null 2>&1; then echo "ALLOWED $*"; fail=1; fi; }
            try git -c user.name=gate -c user.email=gate@example.com commit --allow-empty -m forged
            try git update-ref refs/heads/forged HEAD
            try git config core.hooksPath /tmp/evil
            try sh -c 'echo forged > .git/HEAD'
            try sh -c 'echo x > .git/hooks/post-checkout'
            try mv .git .git.moved
            try rm -rf .git
            git status --porcelain >/dev/null || { echo "CANNOT run git status"; fail=1; }
            exit $fail
            """));

        Assert.True(gate.ExitCode == 0, log);
        Assert.Equal(new HeadObservation(run.Branch, run.Trunk, null, true), await git.Isolation.ObserveAsync(run, None));
        Assert.Equal(dotGitFiles, Directory.EnumerateFiles(Path.Combine(workspace.Path, ".git"), "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task Gate_HasNoNetworkSessionBusOrScopeEscape()
    {
        var (gate, log) = await GateAsync(Bash("""
            fail=0
            try() { if "$@" >/dev/null 2>&1; then echo "ALLOWED $*"; fail=1; fi; }
            try getent hosts api.nuget.org
            try timeout 3 bash -c 'exec 3<>/dev/tcp/127.0.0.1/22'
            try timeout 3 bash -c 'exec 3<>/dev/tcp/1.1.1.1/443'
            try systemd-run --user --scope true
            try unshare -U true
            exit $fail
            """));
        Assert.True(gate.ExitCode == 0, log);
    }

    [Fact]
    public async Task Gate_SeesOnlyPipesAndClosedStdin_NeverTheLog()
    {
        var (gate, log) = await GateAsync(Bash("""
            cat; for fd in /proc/self/fd/*; do echo "$fd -> $(readlink "$fd")"; done
            """));
        Assert.Equal(0, gate.ExitCode);
        Assert.DoesNotContain("Build.log", log);
        Assert.Matches(@"/proc/self/fd/1 -> pipe:", log);
        Assert.Matches(@"/proc/self/fd/2 -> pipe:", log);
    }

    [Fact]
    public async Task Gate_ArgumentsReachTheCommandUnexpanded()
    {
        var (_, log) = await GateAsync(new CommandSpec("printf", ["%s|%s\n", "${HOME}", "$(id)"]));
        Assert.Contains("${HOME}|$(id)", log);
    }

    [Fact]
    public async Task Gate_ThatExits_TakesEveryDescendantWithIt_IncludingASetsidDaemon()
    {
        var marker = $"4242.{Random.Shared.Next(100000, 999999)}";
        var (gate, log) = await GateAsync(Bash($"setsid sleep {marker} & sleep {marker}1 & disown -a; echo started"));

        Assert.True(gate.ExitCode == 0, log);
        Assert.False(GitRemote.Alive(marker));
    }

    [Fact]
    public async Task Sandbox_DiesWithTheRunnerThatStartedIt()
    {
        var marker = $"4545.{Random.Shared.Next(100000, 999999)}";
        var reports = Directory.CreateDirectory(Path.Combine(git.Root, "reports")).FullName;
        var (runner, pid) = (Path.Combine(git.Root, "runner.sh"), Path.Combine(git.Root, "runner.pid"));
        // The script plays the runner: systemd-run is its child, and it is then killed alone, with no chance to clean up.
        GitRemote.Script(runner, $"#!/bin/sh\necho $$ > {pid}\n/usr/bin/systemd-run \"$@\" &\nwait\n");
        var sandbox = new Sandbox(GateLimits.Default, GitRemote.Home, systemdRun: runner);
        var run = sandbox.RunAsync(workspace.Path, reports, new CommandSpec("sleep", [marker]), new Captured("the sandbox"),
            TimeSpan.FromMinutes(2), None);
        for (var i = 0; i < 100 && !GitRemote.Alive(marker); i++) await Task.Delay(100);
        Assert.True(GitRemote.Alive(marker));

        using (var started = Process.GetProcessById(int.Parse(File.ReadAllText(pid)))) started.Kill();
        for (var i = 0; i < 100 && GitRemote.Alive(marker); i++) await Task.Delay(100);
        Assert.False(GitRemote.Alive(marker));
        Assert.NotEqual(0, await run);
    }

    [Fact]
    public async Task Gate_PastItsTimeout_IsKilledWithItsTreeAndRecordsTimedOut()
    {
        var marker = $"4343.{Random.Shared.Next(100000, 999999)}";
        var runner = Runner(GateLimits.Default with { BuildTimeout = TimeSpan.FromSeconds(2) });
        var clock = Stopwatch.StartNew();
        var (gate, log) = await GateAsync(Bash($"setsid sleep {marker} & sleep 600"), runner);

        Assert.Equal((GateReceipt.TimedOut, (int?)null), (gate.ExitCode, gate.TestsExecuted));
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        Assert.Contains("timeout", log);
        Assert.False(GitRemote.Alive(marker));
    }

    [Fact]
    public async Task Log_KeepsTheHeadAMarkerAndTheTail_AndNeverPassesItsCap()
    {
        const long cap = 1 << 20;
        var (gate, log) = await GateAsync(new CommandSpec("seq", ["1", "1000000"]), Runner(GateLimits.Default with { LogCapBytes = cap }));

        Assert.Equal(0, gate.ExitCode);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(log), cap - 1024, cap);
        Assert.StartsWith("[softwarp] $ seq 1 1000000\n1\n2\n3\n", log);
        Assert.Matches(@"\n\[softwarp: \d+ bytes cut here\]\n", log);
        Assert.EndsWith("\n999999\n1000000\n", log);
    }

    [Fact]
    public async Task MemoryCap_StopsAGateThatAllocatesPastIt()
    {
        var (gate, log) = await GateAsync(new CommandSpec("python3", ["-c", "b = bytearray(512 << 20); print('allocated', len(b))"]),
            Runner(GateLimits.Default with { MemoryMax = "64M" }));
        Assert.NotEqual(0, gate.ExitCode);
        Assert.DoesNotContain("allocated 536870912", log);
    }

    [Theory]
    [InlineData(GateKind.TargetedTests, 2)]
    [InlineData(GateKind.Build, null)]
    public async Task TargetedTests_CountExecutedTestsFromTheReportInTheReportDirectory(GateKind kind, int? expected)
    {
        var (gate, _) = await GateAsync(Bash("""
            cat > /softwarp/out/report.xml <<'EOF'
            <testsuites><testsuite name="a"><testcase name="one"/><testcase name="two"/>
            <testcase name="three"><skipped/></testcase></testsuite></testsuites>
            EOF
            """), kind: kind);
        Assert.Equal((0, expected), (gate.ExitCode, gate.TestsExecuted));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));
    }

    [Fact]
    public async Task TargetedTestsThatLockTheirReportDirectory_CountNothing()
    {
        var (gate, _) = await GateAsync(Bash("echo '<testsuites><testcase/></testsuites>' > /softwarp/out/r.xml; chmod 000 /softwarp/out"),
            kind: GateKind.TargetedTests);
        Assert.Equal((0, (int?)null), (gate.ExitCode, gate.TestsExecuted));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));
    }

    [Fact]
    public async Task TargetedTestsThatWriteNoReport_CountNothing() =>
        Assert.Null((await GateAsync(new CommandSpec("true", []), kind: GateKind.TargetedTests)).Run.TestsExecuted);

    [Theory]
    [InlineData("/nonexistent/systemd-run", "/usr/bin/bwrap")]
    [InlineData("/usr/bin/systemd-run", "/usr/bin/false")]
    public async Task SandboxThatCannotStart_IsUnavailableNotFailed(string systemdRun, string bwrap)
    {
        var (gate, log) = await GateAsync(new CommandSpec("true", []), Runner(systemdRun: systemdRun, bwrap: bwrap));
        Assert.Equal((75, (int?)null), (gate.ExitCode, gate.TestsExecuted));
        Assert.Contains("the sandbox could not start", log);
    }

    [Fact]
    public async Task DotGitThatIsALink_IsNeverBound()
    {
        var dotGit = Path.Combine(workspace.Path, ".git");
        Directory.Move(dotGit, Path.Combine(git.Root, "moved.git"));
        Directory.CreateSymbolicLink(dotGit, Path.Combine(GitRemote.Home, ".config"));

        var (gate, log) = await GateAsync(Bash("ls .git/"));
        Assert.Equal((75, (int?)null), (gate.ExitCode, gate.TestsExecuted));
        Assert.Contains("will not bind it", log);
        Assert.DoesNotContain("gh", log.Split('\n').Skip(2));
    }

    [Fact]
    public void DotGitThatIsALink_IsRefusedByTheSandboxItself_BeforeAnyProcessStarts()
    {
        var dotGit = Path.Combine(workspace.Path, ".git");
        Directory.Move(dotGit, Path.Combine(git.Root, "moved.git"));
        Directory.CreateSymbolicLink(dotGit, git.Trusted);
        var started = Path.Combine(git.Root, "started");
        var launcher = Path.Combine(git.Root, "systemd-run");
        GitRemote.Script(launcher, $"#!/bin/sh\ntouch '{started}'\n");
        var sandbox = new Sandbox(GateLimits.Default, GitRemote.Home, launcher);

        Assert.Contains("will not bind it", Assert.Throws<InvalidOperationException>(() =>
            sandbox.UploadPack(workspace.Path, TimeSpan.FromMinutes(1))).Message);
        Assert.Contains("will not bind it", Assert.Throws<InvalidOperationException>(() =>
        {
            _ = sandbox.RunAsync(workspace.Path, null, new CommandSpec("true", []), new Captured("x"), TimeSpan.FromMinutes(1), None);
        }).Message);
        Assert.False(File.Exists(started));
    }
}

/// <summary>bwrap's first process inside the sandbox keeps the environment systemd-run started with, and the gate can
/// read it at /proc/1/environ, so a gate's own environment says nothing about what the gate can read.</summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class SandboxEnvironmentTests : GateFixture
{
    [Fact]
    public async Task Sandbox_ClearsTheEnvironmentToItsAllowlist_AndNoProcessInItShowsTheRunnersEnvironment()
    {
        var marker = $"planted-{Guid.NewGuid():N}";
        using var environment = GitRemote.Planted(new Dictionary<string, string> { ["SOFTWARP_PLANTED_TOKEN"] = marker });
        // The script builds the marker from two halves, so no command line in the sandbox holds it whole.
        var (gate, text) = await GateAsync(Bash($$"""
            printf '%s%s' '{{marker[..12]}}' '{{marker[12..]}}' > /tmp/marker
            read=0; leaks=0
            for f in /proc/[0-9]*/environ /proc/[0-9]*/cmdline; do
              if tr '\0' '\n' < "$f" > /tmp/seen 2>/dev/null; then
                read=$((read+1))
                if grep -qFf /tmp/marker /tmp/seen; then echo "LEAK $f"; leaks=1; fi
              fi
            done
            tr '\0' '\n' < /proc/1/environ > /dev/null && echo "read /proc/1/environ and $read files in all"
            echo "own: $(tr '\0' '\n' < /proc/$$/environ | cut -d= -f1 | LC_ALL=C sort | tr '\n' ' ')"
            echo "NuGetAudit=$NuGetAudit"
            exit $leaks
            """));

        Assert.True(gate.ExitCode == 0, text);
        Assert.Contains("read /proc/1/environ", text);
        Assert.Contains("own: DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_GENERATE_ASPNET_CERTIFICATE DOTNET_NOLOGO DOTNET_SKIP_FIRST_TIME_EXPERIENCE " +
            "HOME LANG NuGetAudit PATH PWD PYTHONDONTWRITEBYTECODE TMPDIR USER \n", text);
        Assert.Contains("NuGetAudit=false", text);
    }
}

public sealed class TestReportTests : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("softwarp-report-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    int? Count(TestReport report, params (string Name, string Xml)[] files)
    {
        foreach (var (name, xml) in files) File.WriteAllText(Path.Combine(directory, name), xml);
        return GateRunner.Count(directory, report);
    }

    const string Trx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <ResultSummary outcome="Completed"><Counters total="352" executed="352" passed="352" failed="0" /></ResultSummary>
        </TestRun>
        """;

    [Fact]
    public void Trx_CountsExecutedAndSumsEveryReport() =>
        Assert.Equal(352 + 12, Count(TestReport.Trx, ("a.trx", Trx), ("b.trx", Trx.Replace("executed=\"352\"", "executed=\"12\"")),
            ("ignored.xml", "<testsuites><testcase/></testsuites>")));

    [Fact]
    public void Trx_WithAFilterThatMatchedNothing_CountsZero() =>
        Assert.Equal(0, Count(TestReport.Trx, ("a.trx", Trx.Replace("executed=\"352\"", "executed=\"0\""))));

    [Fact]
    public void Junit_CountsTestcasesAnywhereExceptSkipped() =>
        Assert.Equal(3, Count(TestReport.Junit,
            ("pytest.xml", "<testsuites><testsuite><testcase/><testcase><skipped/></testcase></testsuite></testsuites>"),
            ("node.xml", "<testsuites><testcase/><testcase><failure/></testcase></testsuites>")));

    [Theory]
    [InlineData("a.trx", "<TestRun><ResultSummary/></TestRun>")]
    [InlineData("a.trx", "<TestRun><Counters executed=\"-1\"/></TestRun>")]
    [InlineData("a.trx", "<TestRun><Counters executed=\"12\">")]
    [InlineData("a.trx", """<!DOCTYPE TestRun [<!ENTITY n "352">]><TestRun><Counters executed="&n;"/></TestRun>""")]
    public void TrxThatIsBrokenHasNoCountOrUsesADtd_CountsNothing(string name, string xml) =>
        Assert.Null(Count(TestReport.Trx, (name, xml)));

    [Fact]
    public void NoReport_CountsNothing() => Assert.Null(Count(TestReport.Junit));

    [Fact]
    public void ReportThatIsALink_IsNeverFollowed()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"softwarp-outside-{Guid.NewGuid():N}.xml");
        File.WriteAllText(outside, "<testsuites><testcase/></testsuites>");
        try
        {
            File.CreateSymbolicLink(Path.Combine(directory, "report.xml"), outside);
            Assert.Null(GateRunner.Count(directory, TestReport.Junit));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
