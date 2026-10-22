using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Softwarp.Core;

namespace Softwarp.Bindings;

/// <summary>Limits for the sandbox, from the machine config.</summary>
/// <param name="MemoryMax">systemd's MemoryMax for each sandbox scope, for example "8G".</param>
/// <param name="LogCapBytes">The most a gate log may hold, from 64 KiB to <see cref="Factory.MaxLogBytes"/>; the
/// machine config checks it.</param>
/// <param name="Toolchains">Directories under the hidden home that gates need, bound back read-only at their own path
/// (for example ~/.nvm/versions/node/v26.9.0).</param>
/// <param name="MaxNewPackages">How many packages from outside the pinned trunk's lock files the trusted fetch lets in for
/// one run.</param>
public sealed record GateLimits(TimeSpan BuildTimeout, TimeSpan TargetedTestsTimeout, string MemoryMax, int TasksMax,
    long LogCapBytes, ImmutableArray<string> Toolchains, int MaxNewPackages)
{
    public static readonly GateLimits Default = new(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), "8G", 4096, 8 << 20, [], 20);

    public TimeSpan Timeout(GateKind kind) => kind switch
    {
        GateKind.Build => BuildTimeout,
        GateKind.TargetedTests => TargetedTestsTimeout,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>How a sandbox shows the worktree.</summary>
internal abstract record WorktreeMount
{
    private WorktreeMount() { }

    /// <summary>Read-only: softwarp's own reads of the implementer's repository.</summary>
    public sealed record ReadOnly : WorktreeMount;

    /// <summary>A gate: the tree writable, its .git read-only, and a fresh report directory at /softwarp/out.</summary>
    public sealed record Gate(string Reports) : WorktreeMount;

    /// <summary>The whole repository writable, .git included: softwarp's checkout while it makes the repository, and the
    /// agents whose work is commits.</summary>
    public sealed record Writable : WorktreeMount;

    /// <summary>A writable view whose writes, .git included, live on a tmpfs and vanish with the sandbox: agents whose work
    /// is not commits, so nothing they write reaches the repository.</summary>
    public sealed record Overlay : WorktreeMount;

    internal IEnumerable<string> Binds(string worktree)
    {
        var dotGit = Path.Combine(worktree, ".git");
        return this switch
        {
            ReadOnly => ["--ro-bind", worktree, worktree],
            Gate gate => ["--bind", worktree, worktree, "--ro-bind", dotGit, dotGit, "--bind", gate.Reports, Sandbox.ReportDirectory],
            Writable => ["--bind", worktree, worktree],
            Overlay => ["--overlay-src", worktree, "--tmp-overlay", worktree],
            _ => throw new UnreachableException($"no binds for {this}"),
        };
    }
}

/// <summary>One sandbox: the worktree as the mount shows it, the command run there, and what else it gets. The launch
/// decides the network: a scope is always offline, an agent's service always online.</summary>
/// <param name="Environment">Variables on top of the sandbox's own.</param>
/// <param name="Binds">bwrap bind arguments on top of the worktree's.</param>
internal sealed record Profile(string Worktree, WorktreeMount Mount, CommandSpec Command,
    IReadOnlyList<(string Name, string Value)>? Environment = null, IReadOnlyList<string>? Binds = null);

/// <summary>A transient user service that outlives softwarp: its unit name, its description in `systemctl list-units`,
/// the time systemd lets it run, the log its stdout and stderr append to, and the stamp its ExecStartPre touches.</summary>
internal sealed record Service(string Unit, string Description, TimeSpan RuntimeMax, string Log, string Stamp);

/// <summary>Where every command that reads or runs the implementer's repository runs: a bubblewrap sandbox in a systemd
/// user unit, with the environment cleared, systemd-run's own included. Gates and softwarp's own git run offline in a
/// scope that ends with the call; agents run online in a service that outlives it. Every sandbox sees /usr and /etc
/// read-only, an empty home with the configured toolchains bound back read-only, and the worktree as its
/// <see cref="WorktreeMount"/> shows it. The offline ones also see softwarp's own package cache at the NuGet cache path. No
/// sandbox sees the run store, softwarp's trusted clone, the session bus or any other secret.</summary>
/// <param name="home">The maintainer's home, hidden behind an empty tmpfs inside the sandbox.</param>
public sealed class Sandbox(GateLimits limits, string home, string systemdRun = "/usr/bin/systemd-run",
    string bwrap = "/usr/bin/bwrap")
{
    internal const string ReportDirectory = "/softwarp/out";
    const string Systemctl = "/usr/bin/systemctl";
    const long TmpBytes = 2L << 30, HomeBytes = 1L << 30;
    const int StopSeconds = 5;
    // The scope's own runtime cap only backs up the caller's timeout, so it fires after it.
    static readonly TimeSpan Backstop = TimeSpan.FromMinutes(1);
    static readonly TimeSpan PreflightTimeout = TimeSpan.FromMinutes(1), Call = TimeSpan.FromSeconds(30);

    public GateLimits Limits => limits;

    /// <summary>softwarp's own package cache, a NuGet global packages folder: the trusted fetch fills it, and the sandbox
    /// binds it read-only at ~/.nuget/packages, where dotnet looks. The maintainer's own ~/.nuget/packages is never
    /// bound.</summary>
    public string Packages => Path.Combine(home, ".softwarp", "packages");

    /// <summary>bwrap resolves a bind source on the host, so a worktree or .git the implementer turned into a link would
    /// bind whatever it points at, and a .git file would send git to a directory the sandbox does not show.</summary>
    internal static string? BindProblem(string worktree)
    {
        var dotGit = new DirectoryInfo(Path.Combine(worktree, ".git"));
        return new DirectoryInfo(worktree).LinkTarget is null && dotGit.LinkTarget is null && dotGit.Exists
            ? null
            : $"{dotGit.FullName} is not a directory in a worktree that is not a link, so the sandbox will not bind it";
    }

    /// <summary>Starts a sandbox with the mount around `true`, so a missing bwrap, a missing user systemd manager, a bad
    /// bind or a mount this machine's bwrap cannot make is told apart from the command failing.</summary>
    internal async Task<string?> ProblemAsync(string worktree, WorktreeMount mount, CancellationToken ct)
    {
        if (BindProblem(worktree) is { } problem) return problem;
        var output = new Captured("the sandbox");
        try
        {
            return await RunAsync(new Profile(worktree, mount, new CommandSpec("/usr/bin/true", [])), output, PreflightTimeout, ct) switch
            {
                0 => null,
                null => $"the sandbox did not start within {PreflightTimeout}",
                var exit => $"the sandbox could not start (exit {exit}): {output.Result(exit, PreflightTimeout).Error.Trim()}",
            };
        }
        catch (Win32Exception e)
        {
            return $"the sandbox could not start: {systemdRun}: {e.Message}";
        }
    }

    /// <summary>Runs the profile offline in a scope with its caps and returns the command's exit code, or null when it
    /// ran past the timeout and was killed.</summary>
    /// <exception cref="InvalidOperationException">The worktree cannot be bound safely.</exception>
    internal Task<int?> RunAsync(Profile profile, IOutputSink sink, TimeSpan timeout, CancellationToken ct) =>
        Tool.RunAsync(systemdRun, Scope(profile, timeout), Launcher, sink, timeout, ct);

    /// <summary>A command for git's --upload-pack that serves the implementer's repository from inside the read-only
    /// sandbox: every path that repository names (a .git file, a linked objects directory or ref, a FIFO) resolves inside
    /// it, not on the host, and the scope caps its memory and time. --strict stops git from trying `.git/.git`. git runs
    /// it through a shell and appends the repository path.</summary>
    /// <exception cref="InvalidOperationException">The worktree cannot be bound safely.</exception>
    internal string UploadPack(string worktree, TimeSpan timeout) => string.Join(' ', new[] { "/usr/bin/env", "-i" }
        .Concat(Launcher.Select(variable => $"{variable.Name}={variable.Value}"))
        .Append(systemdRun)
        .Concat(Scope(new Profile(worktree, new WorktreeMount.ReadOnly(), new CommandSpec("git-upload-pack", ["--strict"])), timeout))
        .Select(Quoted));

    /// <summary>Starts the profile in a transient user service with the scope's caps, systemd's own runtime cap, a short
    /// stop timeout, stdin closed and its output appended to the log, and returns systemd-run's result. systemd refuses a
    /// second start under a loaded name. With --service-type=exec, systemd-run returns once the stamp exists and the
    /// command has started.</summary>
    /// <exception cref="InvalidOperationException">The worktree cannot be bound safely, or systemd-run ran past its timeout.</exception>
    internal Task<ToolResult> StartAsync(Service service, Profile profile, CancellationToken ct) => Tool.ReadAsync(systemdRun,
    [
        "--user", $"--unit={service.Unit}", $"--description={service.Description}", "--quiet", "--collect",
        "--expand-environment=no", "--service-type=exec",
        "-p", $"RuntimeMaxSec={(long)service.RuntimeMax.TotalSeconds}", "-p", $"TimeoutStopSec={StopSeconds}", .. Caps,
        "-p", "StandardInput=null", "-p", $"StandardOutput=append:{service.Log}", "-p", $"StandardError=append:{service.Log}",
        // ':' turns off systemd's $-expansion; in a transient unit % is literal already.
        "-p", $"ExecStartPre=:/usr/bin/touch \"{service.Stamp.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
        // The user manager's environment would reach every process in the sandbox through /proc/1/environ.
        "/usr/bin/env", "-i", "PATH=/usr/bin:/bin",
        // The host network is shared for part B1 only, a HIGH finding the maintainer accepted: agents reach the host's
        // loopback services and abstract sockets. Part B2 moves them into a private network namespace (passt) before the
        // pilot and before any agent works on a real repository.
        .. Bwrap(profile, network: true),
    ], Call, ct, environment: Launcher);

    /// <summary>Whether the unit may still have processes: every ActiveState but inactive and failed, including one that
    /// is still stopping. An unknown unit shows as inactive.</summary>
    internal async Task<bool> RunningAsync(string unit, CancellationToken ct)
    {
        var show = await Tool.ReadAsync(Systemctl, ["--user", "show", unit, "-p", "ActiveState", "--value"], Call, ct, environment: Launcher);
        return show.ExitCode == 0
            ? show.Output.Trim() is not ("inactive" or "failed")
            : throw new InvalidOperationException($"systemctl could not read {unit} (exit {show.ExitCode}): {show.Error.Trim()}");
    }

    /// <summary>The word in single quotes, for a POSIX shell.</summary>
    internal static string Quoted(string word) => $"'{word.Replace("'", "'\\''")}'";

    /// <summary>The whole environment systemd-run and systemctl start with: the session's runtime directory and bus,
    /// which they need to reach the user manager. bwrap's first process inside a scope keeps this environment, and every
    /// process in the sandbox can read it at /proc/1/environ, so it holds nothing else of the caller's.</summary>
    static IEnumerable<(string Name, string Value)> Launcher =>
    [
        ("PATH", "/usr/local/bin:/usr/bin:/bin"),
        .. new[] { "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS" }
            .Where(name => System.Environment.GetEnvironmentVariable(name) is { Length: > 0 })
            .Select(name => (name, System.Environment.GetEnvironmentVariable(name)!)),
    ];

    string[] Caps => ["-p", $"TasksMax={limits.TasksMax}", "-p", $"MemoryMax={limits.MemoryMax}", "-p", "MemorySwapMax=0"];

    /// <summary>systemd-run's arguments: a scope with its caps around the offline sandbox.</summary>
    IEnumerable<string> Scope(Profile profile, TimeSpan timeout) =>
    [
        "--user", "--scope", "--quiet", "--collect", "--expand-environment=no",
        $"--unit=softwarp-sandbox-{RandomNumberGenerator.GetHexString(12, lowercase: true)}", .. Caps,
        "-p", $"RuntimeMaxSec={(long)(timeout + Backstop).TotalSeconds}",
        .. Bwrap(profile, network: false),
    ];

    /// <summary>bwrap's arguments, the one profile every sandbox shares: every namespace new, the network's too unless the
    /// launch shares it, and no user namespace inside; the environment cleared to <see cref="Environment"/> and the
    /// profile's variables; /usr and /etc read-only; /tmp, /run and the home on tmpfs; with the network, the host's DNS
    /// stub resolver; offline, softwarp's package cache; the configured toolchains read-only; then the worktree's mount,
    /// the profile's binds and the command, run in the worktree.</summary>
    /// <exception cref="InvalidOperationException">The worktree cannot be bound safely.</exception>
    IEnumerable<string> Bwrap(Profile profile, bool network)
    {
        if (BindProblem(profile.Worktree) is { } problem) throw new InvalidOperationException(problem);
        return
        [
            bwrap, "--unshare-all", .. network ? ["--share-net"] : Array.Empty<string>(), "--unshare-user", "--disable-userns",
            "--die-with-parent", "--new-session", "--clearenv",
            .. Environment(network).Concat(profile.Environment ?? []).SelectMany(variable => new[] { "--setenv", variable.Name, variable.Value }),
            "--ro-bind", "/usr", "/usr", "--symlink", "usr/bin", "/bin", "--symlink", "usr/lib", "/lib",
            "--symlink", "usr/lib64", "/lib64", "--symlink", "usr/sbin", "/sbin", "--ro-bind", "/etc", "/etc",
            "--proc", "/proc", "--dev", "/dev",
            "--size", $"{TmpBytes}", "--tmpfs", "/tmp", "--tmpfs", "/run",
            .. network ? ["--ro-bind-try", "/run/systemd/resolve", "/run/systemd/resolve"] : Array.Empty<string>(),
            "--size", $"{HomeBytes}", "--tmpfs", home,
            .. network ? Array.Empty<string>() : ["--ro-bind-try", Packages, Path.Combine(home, ".nuget", "packages")],
            .. limits.Toolchains.SelectMany(toolchain => new[] { "--ro-bind", toolchain, toolchain }),
            .. profile.Mount.Binds(profile.Worktree),
            .. profile.Binds ?? [],
            "--chdir", profile.Worktree, "--", profile.Command.Executable, .. profile.Command.Arguments,
        ];
    }

    /// <summary>The environment every sandboxed command gets; the caller's own, which may hold session tokens, is
    /// cleared.</summary>
    IEnumerable<(string Name, string Value)> Environment(bool network) =>
    [
        ("PATH", "/usr/local/bin:/usr/bin:/bin"), ("HOME", home), ("USER", System.Environment.UserName),
        ("LANG", "C.UTF-8"), ("TMPDIR", "/tmp"),
        ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"), ("DOTNET_NOLOGO", "1"), ("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1"),
        ("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false"),
        // Audit needs the network, which gates lack; CI runs it.
        .. network ? Array.Empty<(string, string)>() : [("NuGetAudit", "false")],
        // A __pycache__ left in the worktree would make the tree dirty and the receipt untrusted.
        ("PYTHONDONTWRITEBYTECODE", "1"),
    ];
}
