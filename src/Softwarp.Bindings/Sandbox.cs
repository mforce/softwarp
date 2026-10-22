using System.Collections.Immutable;
using System.ComponentModel;
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

/// <summary>Where every command that reads or runs the implementer's repository runs: a bubblewrap sandbox inside a
/// systemd user scope, with the network off and the environment cleared, systemd-run's own included. It sees /usr and
/// /etc read-only; an empty home with the NuGet cache and the configured toolchains bound back read-only; the worktree,
/// with its .git directory read-only; and, for a gate, a fresh report directory at /softwarp/out. It never sees the run
/// store, softwarp's trusted clone, the session bus or any other secret.</summary>
/// <param name="home">The maintainer's home, hidden behind an empty tmpfs inside the sandbox.</param>
public sealed class Sandbox(GateLimits limits, string home, string systemdRun = "/usr/bin/systemd-run",
    string bwrap = "/usr/bin/bwrap")
{
    const string ReportDirectory = "/softwarp/out";
    const long TmpBytes = 2L << 30, HomeBytes = 1L << 30;
    // The scope's own runtime cap only backs up the caller's timeout, so it fires after it.
    static readonly TimeSpan Backstop = TimeSpan.FromMinutes(1);
    static readonly TimeSpan PreflightTimeout = TimeSpan.FromMinutes(1);

    public GateLimits Limits => limits;

    /// <summary>The NuGet global packages folder: the trusted fetch fills it, and the sandbox binds it read-only.</summary>
    public string Packages => Path.Combine(home, ".nuget", "packages");

    /// <summary>bwrap resolves a bind source on the host, so a worktree or .git the implementer turned into a link would
    /// bind whatever it points at, and a .git file would send git to a directory the sandbox does not show.</summary>
    internal static string? BindProblem(string worktree)
    {
        var dotGit = new DirectoryInfo(Path.Combine(worktree, ".git"));
        return new DirectoryInfo(worktree).LinkTarget is null && dotGit.LinkTarget is null && dotGit.Exists
            ? null
            : $"{dotGit.FullName} is not a directory in a worktree that is not a link, so the sandbox will not bind it";
    }

    /// <summary>Starts the same sandbox around `true`, so a missing bwrap, a missing user systemd manager or a bad bind is
    /// told apart from the command failing.</summary>
    internal async Task<string?> ProblemAsync(string worktree, string reports, CancellationToken ct)
    {
        if (BindProblem(worktree) is { } problem) return problem;
        var output = new Captured("the sandbox");
        try
        {
            return await RunAsync(worktree, reports, new CommandSpec("/usr/bin/true", []), output, PreflightTimeout, ct) switch
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

    /// <summary>Runs the command in the sandbox and returns its exit code, or null when it ran past the timeout and was
    /// killed. The factory's own directories in readOnly are bound read-only at their own path.</summary>
    /// <param name="reports">The directory bound at <see cref="ReportDirectory"/>; when set, the worktree is writable,
    /// as a gate needs; when null, the worktree is read-only.</param>
    /// <exception cref="InvalidOperationException">The worktree cannot be bound safely.</exception>
    internal Task<int?> RunAsync(string worktree, string? reports, CommandSpec command, IOutputSink sink, TimeSpan timeout,
        CancellationToken ct, IEnumerable<string>? readOnly = null) =>
        Tool.RunAsync(systemdRun, Argv(worktree, reports, timeout, command, readOnly ?? []), Launcher, sink, timeout, ct);

    /// <summary>A command for git's --upload-pack that serves the implementer's repository from inside the read-only
    /// sandbox: every path that repository names (a .git file, a linked objects directory or ref, a FIFO) resolves inside
    /// it, not on the host, and the scope caps its memory and time. --strict stops git from trying `.git/.git`. git runs
    /// it through a shell and appends the repository path.</summary>
    /// <exception cref="InvalidOperationException">The worktree cannot be bound safely.</exception>
    internal string UploadPack(string worktree, TimeSpan timeout) => string.Join(' ', new[] { "/usr/bin/env", "-i" }
        .Concat(Launcher.Select(variable => $"{variable.Name}={variable.Value}"))
        .Append(systemdRun)
        .Concat(Argv(worktree, null, timeout, new CommandSpec("git-upload-pack", ["--strict"]), []))
        .Select(word => $"'{word.Replace("'", "'\\''")}'"));

    /// <summary>The whole environment systemd-run starts with: the session's runtime directory and bus, which it needs to
    /// reach the user manager. bwrap's first process inside the sandbox keeps this environment, and every process in the
    /// sandbox can read it at /proc/1/environ, so it holds nothing else of the caller's.</summary>
    static IEnumerable<(string Name, string Value)> Launcher =>
    [
        ("PATH", "/usr/local/bin:/usr/bin:/bin"),
        .. new[] { "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS" }
            .Where(name => System.Environment.GetEnvironmentVariable(name) is { Length: > 0 })
            .Select(name => (name, System.Environment.GetEnvironmentVariable(name)!)),
    ];

    /// <summary>systemd-run's arguments: a scope with its caps around bwrap around the command.</summary>
    IEnumerable<string> Argv(string worktree, string? reports, TimeSpan timeout, CommandSpec command, IEnumerable<string> readOnly)
    {
        if (BindProblem(worktree) is { } problem) throw new InvalidOperationException(problem);
        var dotGit = Path.Combine(worktree, ".git");
        return
        [
            "--user", "--scope", "--quiet", "--collect", "--expand-environment=no",
            $"--unit=softwarp-{(reports is null ? "read" : "gate")}-{RandomNumberGenerator.GetHexString(12, lowercase: true)}",
            "-p", $"TasksMax={limits.TasksMax}", "-p", $"MemoryMax={limits.MemoryMax}", "-p", "MemorySwapMax=0",
            "-p", $"RuntimeMaxSec={(long)(timeout + Backstop).TotalSeconds}",
            bwrap, "--unshare-all", "--unshare-user", "--disable-userns", "--die-with-parent", "--new-session",
            "--clearenv",
            .. Environment().SelectMany(variable => new[] { "--setenv", variable.Name, variable.Value }),
            "--ro-bind", "/usr", "/usr", "--symlink", "usr/bin", "/bin", "--symlink", "usr/lib", "/lib",
            "--symlink", "usr/lib64", "/lib64", "--symlink", "usr/sbin", "/sbin", "--ro-bind", "/etc", "/etc",
            "--proc", "/proc", "--dev", "/dev",
            "--size", $"{TmpBytes}", "--tmpfs", "/tmp", "--tmpfs", "/run", "--size", $"{HomeBytes}", "--tmpfs", home,
            "--ro-bind-try", Packages, Packages,
            .. limits.Toolchains.SelectMany(toolchain => new[] { "--ro-bind", toolchain, toolchain }),
            reports is null ? "--ro-bind" : "--bind", worktree, worktree, "--ro-bind", dotGit, dotGit,
            .. reports is null ? Array.Empty<string>() : ["--bind", reports, ReportDirectory],
            .. readOnly.SelectMany(directory => new[] { "--ro-bind", directory, directory }),
            "--chdir", worktree, "--", command.Executable, .. command.Arguments,
        ];
    }

    /// <summary>The whole environment a sandboxed command gets; the caller's own, which may hold session tokens, is
    /// cleared.</summary>
    IEnumerable<(string Name, string Value)> Environment() =>
    [
        ("PATH", "/usr/local/bin:/usr/bin:/bin"), ("HOME", home), ("USER", System.Environment.UserName),
        ("LANG", "C.UTF-8"), ("TMPDIR", "/tmp"),
        ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"), ("DOTNET_NOLOGO", "1"), ("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1"),
        ("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false"),
        // Audit needs the network, which gates lack; CI runs it.
        ("NuGetAudit", "false"),
        // A __pycache__ left in the worktree would make the tree dirty and the receipt untrusted.
        ("PYTHONDONTWRITEBYTECODE", "1"),
    ];
}
