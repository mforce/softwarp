using System.Diagnostics;
using System.Security.Cryptography;
using Softwarp.Core;

namespace Softwarp.Bindings.Tests;

/// <summary>A worktree on a local remote, a run store with one agent operation's directories, and a fake harness: a
/// script bound where the harness's executable would be. Every unit a test starts is stopped when it ends.</summary>
public abstract class SpawnFixture : IAsyncLifetime
{
    internal readonly GitRemote git = new();
    internal static readonly CancellationToken None = CancellationToken.None;
    internal Workspace workspace = null!;
    internal RunManifest run = null!;
    internal RunStore store = null!;
    internal readonly string token = $"fake-token-{Guid.NewGuid():N}", specToken = $"fake-spec-token-{Guid.NewGuid():N}";
    readonly List<string> units = [];

    internal string Credential => Path.Combine(git.Root, "credential");
    internal string SpecCredential => Path.Combine(git.Root, "spec-credential");
    internal string ClaudeInstall => Path.Combine(git.Root, "claude");
    internal string CodexInstall => Path.Combine(git.Root, "codex-vendor");
    internal string RunDirectory => store.Directory(run.Item.Id);

    public async Task InitializeAsync()
    {
        (run, workspace) = await git.WorktreeAsync();
        // A run id of its own, so tests that run at once never share a unit name.
        run = run with { Run = new RunId($"mforce/widget/7/{Guid.NewGuid():N}") };
        store = new RunStore(Path.Combine(git.Root, "runs"));
        foreach (var (file, content) in new[] { (Credential, token), (SpecCredential, specToken) })
        {
            File.WriteAllText(file, content);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public Task DisposeAsync()
    {
        foreach (var unit in units) Systemctl("stop", unit);
        git.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Writes the fake harness: Claude's is the install itself, Codex's sits at bin/codex in its install.</summary>
    internal void Fake(Harness harness, string script)
    {
        var executable = HarnessProfile.All[harness].Executable(harness == Harness.Claude ? ClaudeInstall : CodexInstall);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        GitRemote.Script(executable, $"#!/bin/bash\n{script}\n");
    }

    internal AgentSpawn Spawn(string systemdRun = "/usr/bin/systemd-run") =>
        new(new Sandbox(GateLimits.Default, GitRemote.Home, systemdRun), store, new Dictionary<Harness, HarnessInstall>
        {
            [Harness.Claude] = new(ClaudeInstall, Credential, SpecCredential), [Harness.Codex] = new(CodexInstall, Credential, SpecCredential),
        });

    /// <summary>The operation's directories and files as the factory writes them before it launches the agent.</summary>
    internal Brief Brief(OperationId op, ActionKind role = ActionKind.Implement, TimeSpan? expected = null, AgentPin? agent = null)
    {
        using var lease = store.TryLock(run.Item.Id)!;
        var io = Directory.CreateDirectory(lease.AgentDirectory(op, role)).FullName;
        Directory.CreateDirectory(Path.GetDirectoryName(lease.OperationFile(op, "brief.json"))!);
        File.WriteAllText(Path.Combine(io, "input.json"), """{"task":"MARKER-OWN-INPUT"}""");
        File.WriteAllText(Path.Combine(io, "schema.json"), Contracts.Schema(Contracts.Default.CleanupOutput).ToJsonString());
        var brief = new Brief(role, agent ?? new AgentPin(Harness.Claude, "pinned-model", "high"), "You are the agent.", Path.Combine(io, "input.json"), Path.Combine(io, "schema.json"),
            Path.Combine(io, "output.json"), lease.AgentLog(op, role), expected ?? TimeSpan.FromMinutes(10));
        Directory.CreateDirectory(Path.GetDirectoryName(brief.LogFile)!);
        File.WriteAllBytes(lease.OperationFile(op, "brief.json"), Contracts.Write(brief, Contracts.Default.Brief));
        return brief;
    }

    internal async Task<AgentHandle> LaunchAsync(AgentSpawn spawn, OperationId op, Brief brief)
    {
        units.Add(AgentSpawn.Unit(run.Run, op));
        return await spawn.EnsureAsync(run, op, brief, workspace, None);
    }

    /// <summary>Waits for the agent to exit and returns its log.</summary>
    internal async Task<string> ExitAsync(AgentSpawn spawn, OperationId op)
    {
        Assert.True(await spawn.WaitAsync(run, op, DateTimeOffset.UtcNow.AddMinutes(1), None));
        return File.ReadAllText(store.Brief(run.Item.Id, op).LogFile);
    }

    /// <summary>Starts the agent, waits for it to exit and returns its log.</summary>
    internal async Task<string> RunToEndAsync(AgentSpawn spawn, OperationId op, Brief brief)
    {
        await LaunchAsync(spawn, op, brief);
        return await ExitAsync(spawn, op);
    }

    internal static string Systemctl(params string[] arguments)
    {
        var start = new ProcessStartInfo("systemctl") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--user" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}

public sealed class AgentSpawnTests : SpawnFixture
{
    static readonly OperationId Op = new("0003-implement");

    [Fact]
    public async Task Agent_OutlivesTheCallThatStartedIt_IsAdoptedNeverStartedTwice_AndIsExitedOnlyOnceItsUnitIsGone()
    {
        var spawn = Spawn();
        var brief = Brief(Op);
        var go = Path.Combine(Path.GetDirectoryName(brief.OutputFile)!, "go");
        Fake(Harness.Claude, $"echo started >> '{Path.GetDirectoryName(brief.OutputFile)}/starts'; while [ ! -e '{go}' ]; do sleep 0.2; done");

        Assert.IsType<AgentState.NotLaunched>(await spawn.InspectAsync(run, Op, None));
        var handle = await LaunchAsync(spawn, Op, brief);
        Assert.Equal(handle, Assert.IsType<AgentState.Running>(await spawn.InspectAsync(run, Op, None)).Handle);
        Assert.Equal(handle, await LaunchAsync(spawn, Op, brief));
        Assert.False(await spawn.WaitAsync(run, Op, DateTimeOffset.UtcNow.AddSeconds(2), None));

        File.WriteAllText(go, "");
        await ExitAsync(spawn, Op);
        Assert.IsType<AgentState.Exited>(await spawn.InspectAsync(run, Op, None));
        Assert.Equal(handle, await LaunchAsync(spawn, Op, brief));
        await Task.Delay(1000);
        Assert.Equal("started\n", File.ReadAllText(Path.Combine(Path.GetDirectoryName(brief.OutputFile)!, "starts")));
        Assert.Contains("systemctl --user list-units", handle.WatchCommand);
        Assert.Contains($"tail -f '{brief.LogFile}' | cat -v", handle.WatchCommand);
    }

    /// <summary>The agent writes its log, and the maintainer runs the printed watch command in a terminal that acts on
    /// escape sequences.</summary>
    [Fact]
    public async Task WatchCommand_ShowsTheAgentsLogWithItsControlBytesAsText()
    {
        var brief = Brief(Op);
        Fake(Harness.Claude, "printf 'title\\033]0;owned\\007 \\033[2J done\\n'");
        var spawn = Spawn();
        var handle = await LaunchAsync(spawn, Op, brief);
        await ExitAsync(spawn, Op);

        using var watch = Process.Start(new ProcessStartInfo("timeout", ["3", "sh", "-c", handle.WatchCommand]) { RedirectStandardOutput = true })!;
        var shown = await watch.StandardOutput.ReadToEndAsync();
        await watch.WaitForExitAsync();
        Assert.Contains("title^[]0;owned^G ^[[2J done", shown);
        Assert.DoesNotContain(shown, c => char.IsControl(c) && c is not ('\n' or '\t'));
    }

    /// <summary>The watch command follows the log, so an agent that could replace the log could make the maintainer's
    /// terminal, or a coordinating agent, read any host file it chose.</summary>
    [Fact]
    public async Task AgentThatReplacesItsLogPath_ChangesNothingTheWatchCommandShows()
    {
        var host = Path.Combine(git.Root, "host-secret");
        File.WriteAllText(host, "MARKER-HOST-FILE\n");
        var brief = Brief(Op);
        Fake(Harness.Claude, $"mkdir -p '{Path.GetDirectoryName(brief.LogFile)}'; rm -f '{brief.LogFile}'; ln -s '{host}' '{brief.LogFile}'; echo MARKER-AGENT-ACTIVITY");
        var spawn = Spawn();
        var handle = await LaunchAsync(spawn, Op, brief);
        await ExitAsync(spawn, Op);

        using var watch = Process.Start(new ProcessStartInfo("timeout", ["3", "sh", "-c", handle.WatchCommand]) { RedirectStandardOutput = true })!;
        var shown = await watch.StandardOutput.ReadToEndAsync();
        await watch.WaitForExitAsync();
        Assert.Contains("MARKER-AGENT-ACTIVITY", shown);
        Assert.DoesNotContain("MARKER-HOST-FILE", shown);
        Assert.Null(new FileInfo(brief.LogFile).LinkTarget);
    }

    [Fact]
    public async Task UnitThatNeverStarted_IsNotLaunched_EvenAfterItsNameWasUsed()
    {
        var spawn = Spawn();
        var brief = Brief(Op);
        Fake(Harness.Claude, "exit 0");
        Assert.IsType<AgentState.NotLaunched>(await spawn.InspectAsync(run, Op, None));
        await RunToEndAsync(spawn, Op, brief);

        File.Delete(store.OperationFile(run.Item.Id, Op, "agent.started"));
        Assert.IsType<AgentState.NotLaunched>(await spawn.InspectAsync(run, Op, None));
    }

    [Fact]
    public async Task AgentThatExits_TakesItsWholeTree_SetsidDoubleForkAndSigtermIgnoringIncluded_BeforeItIsExited()
    {
        var marker = $"4646.{Random.Shared.Next(100000, 999999)}";
        var brief = Brief(Op);
        Fake(Harness.Claude, $"""
            setsid sleep {marker}1 &
            (nohup sleep {marker}2 > /dev/null 2>&1 &)
            (trap '' TERM; exec -a sleep sleep {marker}3) &
            disown -a
            for i in $(seq 1 50); do grep -q {marker}3 /proc/[0-9]*/cmdline 2>/dev/null && break; sleep 0.1; done
            """);
        var spawn = Spawn();

        await RunToEndAsync(spawn, Op, brief);
        Assert.False(GitRemote.Alive(marker));
    }

    [Fact]
    public async Task AgentPastTwiceItsExpectedDuration_IsStoppedWithItsTree_AndNothingWritesItsDirectoryAfterward()
    {
        var marker = $"4747.{Random.Shared.Next(100000, 999999)}";
        var brief = Brief(Op, expected: TimeSpan.FromSeconds(1));
        var progress = Path.Combine(Path.GetDirectoryName(brief.OutputFile)!, "progress");
        Fake(Harness.Claude, $$"""
            trap '' TERM
            (trap '' TERM; exec -a sleep sleep {{marker}}) &
            i=0; while :; do i=$((i+1)); echo "$i" > '{{progress}}'; sleep 0.1; done
            """);
        var spawn = Spawn();
        var clock = Stopwatch.StartNew();

        await RunToEndAsync(spawn, Op, brief);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        Assert.False(GitRemote.Alive(marker));
        var written = File.ReadAllText(progress);
        await Task.Delay(1000);
        Assert.Equal(written, File.ReadAllText(progress));
    }

    /// <summary>Stopping takes until the whole tree is gone; here the sandbox itself ignores SIGTERM, so the unit stays
    /// stopping for TimeoutStopSec, and nothing that still writes may be called Exited.</summary>
    [Fact]
    public async Task AgentStillStopping_IsNotExited_SoNothingWritesItsDirectoryOnceItIs()
    {
        var brief = Brief(Op, expected: TimeSpan.FromSeconds(1));
        var progress = Path.Combine(Path.GetDirectoryName(brief.OutputFile)!, "progress");
        var bwrap = Path.Combine(git.Root, "fake-bwrap");
        // The agent's sandbox (the one with the network) ignores SIGTERM; the preflight's runs as usual.
        GitRemote.Script(bwrap, $"#!/bin/bash\ncase \" $* \" in *\" --share-net \"*) ;; *) exec /usr/bin/bwrap \"$@\";; esac\n" +
            $"trap '' TERM\ni=0; while :; do i=$((i+1)); echo $i > '{progress}'; sleep 0.1; done\n");
        var spawn = new AgentSpawn(new Sandbox(GateLimits.Default, GitRemote.Home, bwrap: bwrap), store,
            new Dictionary<Harness, HarnessInstall> { [Harness.Claude] = new(ClaudeInstall, Credential, SpecCredential) });
        Fake(Harness.Claude, "exit 0");

        await RunToEndAsync(spawn, Op, brief);
        var written = File.ReadAllText(progress);
        await Task.Delay(1500);
        Assert.Equal(written, File.ReadAllText(progress));
    }

    [Theory]
    [InlineData(ActionKind.Implement)]
    [InlineData(ActionKind.Cleanup)]
    [InlineData(ActionKind.Spec)]
    public async Task Sandbox_ShowsTheWorktreeAndOwnIoDirectory_AndHidesTheRunStoreHoldoutsTrustedCloneAndHomeSecrets(ActionKind role)
    {
        var home = GitRemote.Home;
        var op = new OperationId($"0003-{role.ToString().ToLowerInvariant()}");
        var brief = Brief(op, role);
        var other = Directory.CreateDirectory(Path.Combine(RunDirectory, "io", "0001-triage")).FullName;
        var holdouts = Directory.CreateDirectory(Path.Combine(RunDirectory, "holdout")).FullName;
        var otherSpec = Directory.CreateDirectory(Path.Combine(holdouts, "io", "0001-spec")).FullName;
        File.WriteAllText(Path.Combine(other, "output.json"), "MARKER-OTHER-IO");
        File.WriteAllText(Path.Combine(otherSpec, "output.json"), "MARKER-OTHER-SPEC-IO");
        File.WriteAllText(Path.Combine(holdouts, "digest.json"), "MARKER-HOLDOUT");
        File.WriteAllText(Path.Combine(RunDirectory, "manifest.json"), "MARKER-MANIFEST");
        string[] hidden =
        [
            Path.Combine(other, "output.json"), Path.Combine(otherSpec, "output.json"), Path.Combine(holdouts, "digest.json"), Path.Combine(RunDirectory, "manifest.json"),
            store.OperationFile(run.Item.Id, op, "brief.json"), brief.LogFile, Path.Combine(RunDirectory, "lock"), Credential, SpecCredential, git.Trusted, git.Remote, git.Seed,
            $"{home}/.ssh", $"{home}/.config", $"{home}/.config/gh", $"{home}/.config/softwarp", $"{home}/.claude", $"{home}/.claude.json",
            $"{home}/.codex", $"{home}/.paseo", $"{home}/.softwarp", $"{home}/.softwarp-slices", $"{home}/.gitconfig", $"{home}/dev",
            "/run/user", "/run/docker.sock", "/var/run/docker.sock", "/opt", "/srv", "/root",
        ];
        Fake(Harness.Claude, $$"""
            leak=0
            for p in {{string.Join(' ', hidden.Select(p => $"'{p}'"))}}; do [ -e "$p" ] && { echo "LEAK $p"; leak=1; }; done
            grep -q MARKER-OWN-INPUT '{{brief.InputFile}}' || { echo "MISSING own input"; leak=1; }
            [ "$(cat /run/softwarp/credential)" = "$CLAUDE_CODE_OAUTH_TOKEN" ] || { echo "MISSING credential"; leak=1; }
            [ "$PWD" = '{{workspace.Path}}' ] || { echo "WRONG cwd $PWD"; leak=1; }
            [ -e /etc/resolv.conf ] || { echo "MISSING resolver"; leak=1; }
            git commit --quiet --allow-empty -m agent || { echo "CANNOT commit"; leak=1; }
            echo "HOME: $(ls -A "$HOME" | tr '\n' ' ')"
            echo "exit $leak"
            """);
        var spawn = Spawn();

        var log = await RunToEndAsync(spawn, op, brief);
        Assert.Contains("exit 0", log);
        Assert.DoesNotContain("LEAK", log);
        // The implementer and the cleanup agent work in commits, so theirs must reach the repository.
        if (role == ActionKind.Spec) return;
        Assert.Equal("softwarp implementer <softwarp-implementer@invalid>", GitRemote.Git(workspace.Path, "log", "-1", "--format=%an <%ae>"));
        Assert.Equal("softwarp implementer <softwarp-implementer@invalid>", GitRemote.Git(workspace.Path, "log", "-1", "--format=%cn <%ce>"));
    }

    /// <summary>The spec agent sees holdouts and the triage agent's work is its output, so whatever either writes in the
    /// worktree, its .git included (files, commits, notes), vanishes with its sandbox and never reaches the implementer.</summary>
    [Theory]
    [InlineData(ActionKind.Triage)]
    [InlineData(ActionKind.Spec)]
    public async Task TriageAndSpecAgents_WriteTheWorktreeAndItsGitOnlyInsideTheirSandbox(ActionKind role)
    {
        var op = new OperationId($"0003-{role.ToString().ToLowerInvariant()}");
        var brief = Brief(op, role);
        var before = Snapshot(workspace.Path);
        Fake(Harness.Claude, """
            echo MARKER-HOLDOUT > scenarios.md && echo changed >> WORKFLOW.md && printf MARKER-HOLDOUT > .git/scenarios \
              && git add -A && git commit --quiet -m draft && git notes add -m MARKER-NOTE HEAD && echo wrote
            """);
        var spawn = Spawn();

        Assert.Contains("wrote", await RunToEndAsync(spawn, op, brief));
        Assert.Equal(before, Snapshot(workspace.Path));
    }

    static string Snapshot(string directory) => string.Join('\n', Directory
        .EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => File.Exists(path) ? $"{path} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}" : path));

    [Fact]
    public async Task Claude_RunsWithExplicitSettingsAndNoUserOrRepositoryConfig_AndItsOutputIsItsLastResult()
    {
        var brief = Brief(Op, agent: new AgentPin(Harness.Claude, "pinned-model", "xhigh"));
        var argv = Path.Combine(Path.GetDirectoryName(brief.OutputFile)!, "argv");
        Fake(Harness.Claude, $$$"""
            printf '%s\0' "$@" > '{{{argv}}}'
            echo 'stderr noise' >&2
            echo '{"type":"result","structured_output":{"pr_title":"old","pr_body":"b"}}'
            echo '{"type":"assistant","message":"not a result"}'
            echo '{"type":"result","subtype":"success","structured_output":{"pr_title":"t","pr_body":"b"}}'
            """);
        var spawn = Spawn();

        await RunToEndAsync(spawn, Op, brief);
        Assert.Equal(new CleanupOutput("t", "b"), Contracts.Read(spawn.Output(run, Op), Contracts.Default.CleanupOutput));
        Assert.Equal([
            "-p", brief.Prompt, "--model", "pinned-model", "--effort", "xhigh", "--permission-mode", "bypassPermissions",
            "--setting-sources", "", "--strict-mcp-config", "--no-session-persistence", "--output-format", "stream-json", "--verbose",
            "--json-schema", Contracts.OutputSchema(ActionKind.Implement).ToJsonString(), "",
        ], File.ReadAllText(argv).Split('\0'));
    }

    /// <summary>The factory takes Claude's output from the harness's last result event, never from a file the agent
    /// wrote, whatever the agent did to that file or its directory, and reading it changes no host file. The agent is
    /// finished through InspectAsync alone, as the advance after the CLI's wait finishes it.</summary>
    [Theory]
    [InlineData("file", true)]
    [InlineData("link", true)]
    [InlineData("locked", true)]
    [InlineData("file", false)]
    [InlineData("link", false)]
    [InlineData("locked", false)]
    public async Task ClaudesOutput_IsItsLastResult_NeverAFileTheAgentWrote(string trick, bool result)
    {
        var host = Path.Combine(git.Root, "host-file");
        File.WriteAllText(host, """{"pr_title":"HOST","pr_body":"b"}""");
        var brief = Brief(Op);
        var io = Path.GetDirectoryName(brief.OutputFile)!;
        var own = $$"""echo '{"pr_title":"AGENT-OWN","pr_body":"b"}' > '{{brief.OutputFile}}'""";
        Fake(Harness.Claude, string.Join('\n',
            trick switch
            {
                "link" => $"ln -s '{host}' '{brief.OutputFile}'",
                "locked" => $"{own}; chmod 0500 '{io}'",
                _ => own,
            },
            result
                ? """echo '{"type":"result","structured_output":{"pr_title":"HARNESS","pr_body":"b"}}'"""
                : """
                  echo '{"type":"result","structured_output":{"pr_title":"EARLIER","pr_body":"b"}}'
                  echo '{"type":"result","subtype":"error_max_turns"}'
                  """));
        var spawn = Spawn();
        await LaunchAsync(spawn, Op, brief);
        try
        {
            while (await spawn.InspectAsync(run, Op, None) is AgentState.Running) await Task.Delay(200);

            if (result) Assert.Equal("HARNESS", Contracts.Read(spawn.Output(run, Op), Contracts.Default.CleanupOutput).PrTitle);
            else Assert.Contains("no result event with structured output among the last 64 JSON lines", Assert.Throws<InvalidDataException>(() => spawn.Output(run, Op)).Message);
            Assert.Equal("""{"pr_title":"HOST","pr_body":"b"}""", File.ReadAllText(host));
        }
        finally
        {
            File.SetUnixFileMode(io, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>What an agent can do to the output it leaves is refused at once: never a host file through a link, never a
    /// read that blocks or runs without end, never a result the parse gives up on late.</summary>
    [Theory]
    [InlineData(Harness.Codex, "link to a host file")]
    [InlineData(Harness.Codex, "link to /dev/zero")]
    [InlineData(Harness.Codex, "fifo")]
    [InlineData(Harness.Codex, "directory")]
    [InlineData(Harness.Codex, "over the cap")]
    [InlineData(Harness.Codex, "mode 000")]
    [InlineData(Harness.Claude, "junk lines after the result")]
    public async Task OutputAnAgentCanSpoil_IsInvalidDataAtOnce(Harness harness, string trick)
    {
        var host = Path.Combine(git.Root, "host-file");
        File.WriteAllText(host, """{"pr_title":"HOST","pr_body":"b"}""");
        var brief = Brief(Op, agent: new AgentPin(harness, "pinned-model", "low"));
        var output = brief.OutputFile;
        Fake(harness, (harness, trick) switch
        {
            // Fewer bytes than the log tail holds, so the result lies inside it and only the parse cap stops the search.
            (Harness.Claude, "junk lines after the result") => """
                echo '{"type":"result","structured_output":{"pr_title":"t","pr_body":"b"}}'
                yes '{' | head -c 4000000
                """,
            (Harness.Codex, "link to a host file") => $"ln -s '{host}' '{output}'",
            (Harness.Codex, "link to /dev/zero") => $"ln -s /dev/zero '{output}'",
            (Harness.Codex, "fifo") => $"mkfifo '{output}'",
            (Harness.Codex, "directory") => $"mkdir '{output}'",
            (Harness.Codex, "over the cap") => $"head -c 2000000 /dev/zero | tr '\\0' a > '{output}'",
            (Harness.Codex, "mode 000") => $$"""echo '{"pr_title":"t","pr_body":"b"}' > '{{output}}'; chmod 000 '{{output}}'""",
            _ => throw new ArgumentOutOfRangeException(nameof(trick)),
        });
        var spawn = Spawn();
        await RunToEndAsync(spawn, Op, brief);

        await Assert.ThrowsAsync<InvalidDataException>(() => Task.Run(() => spawn.Output(run, Op)).WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Codex_RunsWithExplicitSettings_NoRepositoryInstructions_ItsKeyInItsVariable_AndItsOutputIsItsFile()
    {
        var brief = Brief(Op, agent: new AgentPin(Harness.Codex, "pinned-model", "minimal"));
        var argv = Path.Combine(Path.GetDirectoryName(brief.OutputFile)!, "argv");
        Fake(Harness.Codex, $$$"""
            printf '%s\0' "$@" > '{{{argv}}}'
            [ "$CODEX_API_KEY" = "$(cat /run/softwarp/credential)" ] && echo "key ok"
            echo '{"pr_title":"codex","pr_body":"b"}' > '{{{brief.OutputFile}}}'
            """);
        var spawn = Spawn();

        var log = await RunToEndAsync(spawn, Op, brief);
        Assert.Contains("key ok", log);
        Assert.Equal("codex", Contracts.Read(spawn.Output(run, Op), Contracts.Default.CleanupOutput).PrTitle);
        Assert.Equal([
            "exec", "--dangerously-bypass-approvals-and-sandbox", "-C", workspace.Path, "-m", "pinned-model",
            "-c", "model_reasoning_effort=\"minimal\"", "-c", "project_doc_max_bytes=0", "-c", "skills.include_instructions=false",
            "--ignore-user-config", "--ignore-rules",
            "--ephemeral", "--json", "--output-schema", brief.SchemaFile, "-o", brief.OutputFile, brief.Prompt, "",
        ], File.ReadAllText(argv).Split('\0'));
    }

    /// <summary>The spec agent sees the holdouts, so it never gets the account the implementer runs on.</summary>
    [Theory]
    [InlineData(ActionKind.Spec)]
    [InlineData(ActionKind.Triage)]
    [InlineData(ActionKind.Implement)]
    [InlineData(ActionKind.Cleanup)]
    public async Task Agent_IsBoundTheCredentialItsRoleNeeds_TheSpecAgentsOwnAndTheOtherForEveryOtherRole(ActionKind role)
    {
        var op = new OperationId($"0003-{role.ToString().ToLowerInvariant()}");
        var brief = Brief(op, role);
        var (own, other) = role == ActionKind.Spec ? (specToken, token) : (token, specToken);
        Fake(Harness.Claude, $$"""
            [ "$(cat /run/softwarp/credential)" = '{{own}}' ] && [ "$CLAUDE_CODE_OAUTH_TOKEN" = '{{own}}' ] && echo "bound own"
            grep -qF '{{other}}' /run/softwarp/credential && echo "bound other"
            """);

        var log = await RunToEndAsync(Spawn(), op, brief);
        Assert.Contains("bound own", log);
        Assert.DoesNotContain("bound other", log);
    }

    /// <summary>A run pins its roles' harnesses at intake and the machine config may change after, so the spawn checks the
    /// field again, and a missing field never falls back to the other one.</summary>
    [Theory]
    [InlineData(ActionKind.Spec, "spec_credential")]
    [InlineData(ActionKind.Implement, "credential")]
    public async Task RunWhoseHarnessNoLongerHasTheFieldItsRoleNeeds_FailsClosedAndLaunchesNothing(ActionKind role, string field)
    {
        var op = new OperationId($"0003-{role.ToString().ToLowerInvariant()}");
        var brief = Brief(op, role);
        Fake(Harness.Claude, "exit 0");
        var install = role == ActionKind.Spec ? new HarnessInstall(ClaudeInstall, Credential) : new HarnessInstall(ClaudeInstall, SpecCredential: SpecCredential);
        var spawn = new AgentSpawn(new Sandbox(GateLimits.Default, GitRemote.Home), store, new Dictionary<Harness, HarnessInstall> { [Harness.Claude] = install });

        var message = (await Assert.ThrowsAsync<InvalidOperationException>(() => LaunchAsync(spawn, op, brief))).Message;
        Assert.Contains($"harnesses.claude.{field} is missing", message);
        Assert.IsType<AgentState.NotLaunched>(await spawn.InspectAsync(run, op, None));
    }

    [Fact]
    public async Task SpawnThatCannotStart_FailsClosedAndLeavesNoStamp()
    {
        var brief = Brief(Op);
        Fake(Harness.Claude, "exit 0");
        var spawn = Spawn("/usr/bin/false");

        Assert.Contains("could not start", (await Assert.ThrowsAsync<InvalidOperationException>(() => LaunchAsync(spawn, Op, brief))).Message);
        Assert.IsType<AgentState.NotLaunched>(await spawn.InspectAsync(run, Op, None));
    }

    /// <summary>The preflight binds what the agent binds, so a credential that is gone is found before the attempt is
    /// spent. The agent's own sandbox would start and fail at once.</summary>
    [Fact]
    public async Task AgentWhoseCredentialIsGone_IsNotStarted_AndSaysWhy()
    {
        var brief = Brief(Op);
        Fake(Harness.Claude, "exit 0");
        var spawn = Spawn();
        File.Delete(Credential);

        Assert.Contains("its sandbox cannot start here", (await Assert.ThrowsAsync<InvalidOperationException>(() => LaunchAsync(spawn, Op, brief))).Message);
        Assert.IsType<AgentState.NotLaunched>(await spawn.InspectAsync(run, Op, None));
    }

    /// <summary>Triage and spec need bwrap's overlay. Where bwrap lacks it, the agent must not start, so no attempt is
    /// spent on a sandbox that cannot run.</summary>
    [Theory]
    [InlineData(ActionKind.Triage)]
    [InlineData(ActionKind.Spec)]
    public async Task TriageOrSpecWhereBwrapHasNoOverlay_IsNotStarted_AndSaysWhy(ActionKind role)
    {
        var op = new OperationId($"0003-{role.ToString().ToLowerInvariant()}");
        var brief = Brief(op, role);
        Fake(Harness.Claude, "exit 0");
        var bwrap = Path.Combine(git.Root, "old-bwrap");
        GitRemote.Script(bwrap, """
            #!/bin/sh
            case " $* " in *" --overlay-src "*) echo "bwrap: Unknown option --overlay-src" >&2; exit 1;; esac
            exec /usr/bin/bwrap "$@"
            """);
        var spawn = new AgentSpawn(new Sandbox(GateLimits.Default, GitRemote.Home, bwrap: bwrap), store,
            new Dictionary<Harness, HarnessInstall> { [Harness.Claude] = new(ClaudeInstall, Credential, SpecCredential) });

        var message = (await Assert.ThrowsAsync<InvalidOperationException>(() => LaunchAsync(spawn, op, brief))).Message;
        Assert.Contains("overlay", message);
        Assert.Contains("Unknown option --overlay-src", message);
        Assert.IsType<AgentState.NotLaunched>(await spawn.InspectAsync(run, op, None));
    }

    [Fact]
    public void UnitNames_AreEscapedSoTwoRunsNeverShareOne() =>
        Assert.NotEqual(AgentSpawn.Unit(new RunId("a-b/c/1/n"), Op), AgentSpawn.Unit(new RunId("a/b-c/1/n"), Op));
}

/// <summary>bwrap's first process keeps the environment the service started it with, and every process in the sandbox
/// can read it at /proc/1/environ; the harness's own command line is readable too.</summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class AgentEnvironmentTests : SpawnFixture
{
    [Fact]
    public async Task NoProcessInTheSandboxShowsTheCallersEnvironment_AndNoCommandLineHoldsTheCredential()
    {
        var marker = $"planted-{Guid.NewGuid():N}";
        using var environment = GitRemote.Planted(new Dictionary<string, string> { ["SOFTWARP_PLANTED_TOKEN"] = marker });
        var op = new OperationId("0003-implement");
        var brief = Brief(op);
        // The script builds both values from halves, so its own command line holds neither whole.
        Fake(Harness.Claude, $$"""
            printf '%s%s' '{{marker[..12]}}' '{{marker[12..]}}' > /tmp/marker
            printf '%s%s' '{{token[..12]}}' '{{token[12..]}}' > /tmp/token
            read=0; leaks=0
            for f in /proc/[0-9]*/environ /proc/[0-9]*/cmdline; do
              if tr '\0' '\n' < "$f" > /tmp/seen 2>/dev/null; then
                read=$((read+1))
                if grep -qFf /tmp/marker /tmp/seen; then echo "LEAK marker $f"; leaks=1; fi
                case $f in */cmdline) if grep -qFf /tmp/token /tmp/seen; then echo "LEAK token $f"; leaks=1; fi;; esac
              fi
            done
            tr '\0' '\n' < /proc/1/environ > /dev/null && echo "read /proc/1/environ and $read files in all"
            echo "pid1 env: $(tr '\0' '\n' < /proc/1/environ | cut -d= -f1 | LC_ALL=C sort | tr '\n' ' ')"
            echo "own: $(tr '\0' '\n' < /proc/$$/environ | cut -d= -f1 | LC_ALL=C sort | tr '\n' ' ')"
            echo "exit $leaks"
            """);
        var spawn = Spawn();

        var handle = await LaunchAsync(spawn, op, brief);
        var properties = Systemctl("show", AgentSpawn.Unit(run.Run, op), "-p", "ExecStart", "-p", "ExecStartPre", "-p", "Environment");
        var log = await ExitAsync(spawn, op);

        Assert.Contains("read /proc/1/environ", log);
        Assert.Contains("exit 0", log);
        Assert.Contains("pid1 env: PATH \n", log);
        Assert.Contains("own: CLAUDE_CODE_OAUTH_TOKEN DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_GENERATE_ASPNET_CERTIFICATE DOTNET_NOLOGO " +
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE GIT_AUTHOR_EMAIL GIT_AUTHOR_NAME GIT_COMMITTER_EMAIL GIT_COMMITTER_NAME GIT_CONFIG_NOSYSTEM " +
            "GIT_TERMINAL_PROMPT HOME LANG PATH PWD PYTHONDONTWRITEBYTECODE TMPDIR USER \n", log);
        Assert.Contains("bwrap", properties);
        Assert.DoesNotContain(token, properties);
        Assert.DoesNotContain(marker, properties);
    }
}
