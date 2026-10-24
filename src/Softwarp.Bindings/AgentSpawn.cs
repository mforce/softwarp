using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Softwarp.Core;

namespace Softwarp.Bindings;

/// <summary>A harness on this machine.</summary>
/// <param name="Install">What every agent of the harness is shown, read-only at its own path: Claude's native binary, or
/// Codex's vendor directory for this platform, which holds bin/codex and the tools Codex runs.</param>
/// <param name="Credential">The file with the dedicated, revocable credential of the triage, implement and cleanup agents on
/// this harness: a token from `claude setup-token`, or an OpenAI API key with a spend limit. Never the maintainer's own
/// login. Null when no such role runs on the harness.</param>
/// <param name="SpecCredential">The same for the spec agents, in a file of its own and with content no other credential
/// has, so the holdouts the spec agent sees have no shared credential to reach the implementer by. Bind compares files,
/// not accounts. Null when no spec role runs on the harness.</param>
public sealed record HarnessInstall(string Install, string? Credential = null, string? SpecCredential = null)
{
    /// <summary>The machine config field and file of the credential the role's agent gets; there is no fallback from one
    /// field to the other. Every role is named, so a role added later fails at startup until it is given a field.</summary>
    internal (string Field, string? File) CredentialFor(ActionKind role) => role switch
    {
        ActionKind.Spec => ("spec_credential", SpecCredential),
        ActionKind.Triage or ActionKind.Implement or ActionKind.Cleanup => ("credential", Credential),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

/// <summary>What softwarp knows about one harness. Each harness's command line sets the model, effort and permission
/// mode explicitly; turns its own sandbox off, since the service's sandbox is the boundary; loads no user or repository
/// settings, hooks, MCP servers, skills or instruction files; and saves no session.</summary>
/// <param name="CredentialVariable">The one variable the harness reads its credential from.</param>
/// <param name="InstallIsFile">The install is the executable itself, not a directory that holds it.</param>
/// <param name="Efforts">The reasoning efforts the harness takes.</param>
/// <param name="Executable">The executable inside the install.</param>
/// <param name="Arguments">The harness's arguments for a brief in a worktree.</param>
/// <param name="OutputInLog">The harness prints its structured output as the last `result` event in its log, where
/// softwarp reads it; otherwise softwarp reads the output file the harness writes.</param>
sealed record HarnessProfile(string CredentialVariable, bool InstallIsFile, ImmutableArray<string> Efforts,
    Func<string, string> Executable, Func<Brief, Workspace, IEnumerable<string>> Arguments, bool OutputInLog)
{
    public static readonly ImmutableDictionary<Harness, HarnessProfile> All = new Dictionary<Harness, HarnessProfile>
    {
        [Harness.Claude] = new("CLAUDE_CODE_OAUTH_TOKEN", InstallIsFile: true, ["low", "medium", "high", "xhigh", "max"], install => install,
            (brief, _) =>
            [
                "-p", brief.Prompt, "--model", brief.Agent.Model, "--effort", brief.Agent.Effort, "--permission-mode", "bypassPermissions",
                "--setting-sources", "", "--strict-mcp-config", "--no-session-persistence", "--output-format", "stream-json", "--verbose",
                "--json-schema", Contracts.OutputSchema(brief.Role).ToJsonString(),
            ],
            OutputInLog: true),
        [Harness.Codex] = new("CODEX_API_KEY", InstallIsFile: false, ["minimal", "low", "medium", "high", "xhigh", "max"],
            install => Path.Combine(install, "bin", "codex"),
            (brief, workspace) =>
            [
                "exec", "--dangerously-bypass-approvals-and-sandbox", "-C", workspace.Path, "-m", brief.Agent.Model,
                "-c", $"model_reasoning_effort=\"{brief.Agent.Effort}\"", "-c", "project_doc_max_bytes=0",
                "-c", "skills.include_instructions=false", "--ignore-user-config", "--ignore-rules", "--ephemeral", "--json",
                "--output-schema", brief.SchemaFile, "-o", brief.OutputFile, brief.Prompt,
            ],
            OutputInLog: false),
    }.ToImmutableDictionary();
}

/// <summary>Starts each agent as a transient systemd user service named from its run and operation, so the agent
/// outlives the CLI that started it and systemd refuses a second start under the same name. The service runs
/// softwarp's sandbox profile with the network on (<see cref="Sandbox.StartAsync"/>), around the role's harness with the
/// harness's own sandbox off. The agent is shown its worktree with the real .git (writable for the implement and
/// cleanup agents, a throwaway overlay for triage and spec, <see cref="Mount"/>), its own io directory and, read-only,
/// its harness and the credential its role needs (<see cref="HarnessInstall.CredentialFor"/>). Its log lies outside the
/// sandbox. An ExecStartPre stamp in the operation's directory, which no agent sees, tells an agent that exited from one
/// that never started.</summary>
/// <remarks>The credential reaches the harness through the one variable the harness reads it from, set by a shell in
/// the sandbox, so it is on no command line and in no environment of softwarp's. A hijacked agent can still read it, from
/// the bound file or from the harness's /proc entry, so each credential is dedicated and revocable.</remarks>
/// <param name="harnesses">The harnesses on this machine; a run's pins name which one each role uses.</param>
public sealed class AgentSpawn(Sandbox sandbox, RunStore store, IReadOnlyDictionary<Harness, HarnessInstall> harnesses) : ISpawn
{
    const string Credential = "/run/softwarp/credential";
    const string Started = "agent.started";
    /// <summary>How much of the log's end is searched for the harness's last result event, and how many JSON lines in it
    /// are parsed: the result is the harness's last event, and the cap keeps a log of junk from costing the host
    /// minutes under the run's lock.</summary>
    const int ResultTailBytes = 4 << 20, ResultCandidates = 64;
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    /// <summary>Agents commit in their own repository as softwarp until the GitHub App exists, and git asks nothing.</summary>
    static readonly (string Name, string Value)[] Identity =
    [
        ("GIT_AUTHOR_NAME", "softwarp implementer"), ("GIT_AUTHOR_EMAIL", "softwarp-implementer@invalid"),
        ("GIT_COMMITTER_NAME", "softwarp implementer"), ("GIT_COMMITTER_EMAIL", "softwarp-implementer@invalid"),
        ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_TERMINAL_PROMPT", "0"),
    ];

    /// <summary>Runs in the sandbox: puts the credential into the variable $1 and runs the harness in its place.</summary>
    const string Launch = $$"""
        key=$1; shift
        secret=$(cat {{Credential}}) || exit 70
        export "$key=$secret"
        exec "$@"
        """;

    /// <summary>Starts the agent unless its stamp shows it started once already. A start that fails throws; while the
    /// unit is still starting that means another call started it, and the next call adopts it through
    /// <see cref="InspectAsync"/>.</summary>
    public async Task<AgentHandle> EnsureAsync(RunManifest run, OperationId operation, Brief brief, Workspace workspace,
        CancellationToken ct)
    {
        var (unit, stamp) = (Unit(run.Run, operation), store.OperationFile(run.Item.Id, operation, Started));
        if (File.Exists(stamp)) return Handle(unit, brief);
        var install = harnesses.TryGetValue(brief.Agent.Harness, out var configured) ? configured
            : throw new InvalidOperationException($"the run pins {brief.Agent.Harness} for {brief.Role}, and the machine config has no such harness");
        var (field, credential) = install.CredentialFor(brief.Role);
        if (credential is null)
            throw new InvalidOperationException(
                $"the run pins {brief.Agent.Harness} for {brief.Role}, and harnesses.{Composition.Name(brief.Agent.Harness)}.{field} is missing from the machine config");
        var harness = HarnessProfile.All[brief.Agent.Harness];
        var io = Path.GetDirectoryName(brief.OutputFile)!;
        var profile = new Profile(workspace.Path, Mount(brief.Role),
            new CommandSpec("/bin/sh", ["-c", Launch, "softwarp-agent", harness.CredentialVariable, harness.Executable(install.Install),
                .. harness.Arguments(brief, workspace)]),
            Identity, [new PathBind.ReadOnly(install.Install, install.Install), new PathBind.ReadOnly(credential, Credential), new PathBind.Writable(io)]);
        // A sandbox this machine cannot make (no bwrap, a worktree, harness, credential or io directory that cannot be bound,
        // or an overlay that needs bwrap 0.10 or later and overlayfs in user namespaces) would start, fail at once and spend
        // the attempt, so the agent's own profile is tried first.
        if (await sandbox.ProblemAsync(profile, ct) is { } problem)
            throw new InvalidOperationException($"the {brief.Role} agent was not started: its sandbox cannot start here: {problem}");
        // An agent gets twice its expected time, then systemd stops it, and kills what ignores the stop.
        var start = await sandbox.StartAsync(new Service(unit, $"softwarp {brief.Role} agent", 2 * brief.ExpectedDuration, brief.LogFile, stamp),
            profile, ct);
        return start.ExitCode == 0
            ? Handle(unit, brief)
            : throw new InvalidOperationException($"systemd-run could not start {unit} (exit {start.ExitCode}): {start.Error.Trim()}");
    }

    public async Task<AgentState> InspectAsync(RunManifest run, OperationId operation, CancellationToken ct)
    {
        var unit = Unit(run.Run, operation);
        if (await sandbox.RunningAsync(unit, ct)) return new AgentState.Running(Handle(unit, store.Brief(run.Item.Id, operation)));
        return File.Exists(store.OperationFile(run.Item.Id, operation, Started)) ? new AgentState.Exited() : new AgentState.NotLaunched();
    }

    /// <summary>True only once systemd shows the unit inactive: every process of the agent is gone, so none can still
    /// write what <see cref="Output"/> reads.</summary>
    public async Task<bool> WaitAsync(RunManifest run, OperationId operation, DateTimeOffset deadline, CancellationToken ct)
    {
        var unit = Unit(run.Run, operation);
        // ponytail: polls systemctl once a second; subscribe to the unit's D-Bus signals if many agents run at once.
        while (await sandbox.RunningAsync(unit, ct))
        {
            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return false;
            await Task.Delay(left < Poll ? left : Poll, ct);
        }
        return true;
    }

    /// <summary>Codex's output is the file its -o wrote. Claude's is the structured output of the last `result` event in
    /// its log, which lies outside the sandbox and is read here on the host; no file the agent wrote ever stands in for it.
    /// The log tail bounds its size, and the factory checks that against its cap. Nothing is written.</summary>
    /// <remarks>The harness's stdout and stderr are open on the log, so the agent can still append to or truncate it: like
    /// the output file, what it holds is the agent's to choose, and the role's contract is the check.</remarks>
    public byte[] Output(RunManifest run, OperationId operation)
    {
        var brief = store.Brief(run.Item.Id, operation);
        if (!HarnessProfile.All[brief.Agent.Harness].OutputInLog) return UntrustedFile.Read(brief.OutputFile, Factory.MaxOutputBytes);
        return Encoding.UTF8.GetBytes(LastResult(UntrustedFile.Tail(brief.LogFile, ResultTailBytes))
            ?? throw new InvalidDataException($"no result event with structured output among the last {ResultCandidates} JSON lines of the harness's log"));
    }

    /// <summary>The implementer and the cleanup agent work in commits. Triage writes nothing it keeps, and the spec agent
    /// sees holdouts, which must never reach the repository the implementer reads, so both get a throwaway overlay.</summary>
    static WorktreeMount Mount(ActionKind role) => role switch
    {
        ActionKind.Implement or ActionKind.Cleanup => new WorktreeMount.Writable(),
        ActionKind.Triage or ActionKind.Spec => new WorktreeMount.Overlay(),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>The structured output of the last `result` event among the log's last JSON lines; null when that event
    /// has none or there is no such event. Other lines (stderr, other events) are skipped.</summary>
    static string? LastResult(string log)
    {
        foreach (var line in log.Split('\n').Reverse().Where(line => line.StartsWith('{')).Take(ResultCandidates))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String && type.GetString() == "result")
                    return root.TryGetProperty("structured_output", out var output) && output.ValueKind == JsonValueKind.Object
                        ? output.GetRawText()
                        : null;
            }
            catch (JsonException)
            {
                // not a JSON line
            }
        }
        return null;
    }

    /// <summary>`softwarp-agent-{run}-{operation}.service`, each part escaped as systemd-escape does, so two runs never
    /// share a name.</summary>
    /// <remarks>ponytail: a run id over about 200 escaped characters makes a name systemd refuses, and the start fails
    /// closed; shorten to the run's nonce if a real repository name gets there.</remarks>
    internal static string Unit(RunId run, OperationId operation) => $"softwarp-agent-{Escape(run.Value)}-{Escape(operation.Value)}.service";

    static string Escape(string text) => string.Concat(text.Select((c, i) =>
        c == '/' ? "-" : char.IsAsciiLetterOrDigit(c) || c is ':' or '_' || c == '.' && i > 0 ? c.ToString() : $"\\x{(int)c:x2}"));

    /// <summary>The agent's harness writes the log, so the watch command shows it through `cat -v`, which prints control
    /// bytes as text and never sends them to the maintainer's terminal.</summary>
    static AgentHandle Handle(string unit, Brief brief) => new(unit, brief.LogFile,
        $"systemctl --user list-units --no-pager 'softwarp-agent-*'; tail -f {Sandbox.Quoted(brief.LogFile)} | cat -v");
}
