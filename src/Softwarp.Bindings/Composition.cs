using System.Text.Json;
using System.Text.Json.Serialization;
using Softwarp.Core;

namespace Softwarp.Bindings;

/// <summary>This machine's settings: who the maintainer is, the model each agent role runs on, where softwarp keeps its
/// state, and the sandbox limits. Paths must be absolute.</summary>
/// <param name="Runs">The run store; ~/.softwarp/runs when null.</param>
/// <param name="Slices">The trusted clones and implementer repositories; ~/.softwarp-slices when null.</param>
/// <param name="Gates">The sandbox limits; <see cref="GateLimits.Default"/> when null.</param>
public sealed record MachineConfig(string MaintainerAccountId, ModelPins Models, string? Runs = null, string? Slices = null,
    GateLimits? Gates = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(MachineConfig))]
sealed partial class MachineJson : JsonSerializerContext;

/// <summary>The composition root: it builds the factory from the machine config, with the bindings this build has.</summary>
public static class Composition
{
    /// <summary>A home that does not exist reads as "", and every path built from it would be relative to the working
    /// directory.</summary>
    static string Home
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.IsPathFullyQualified(home)
                ? home
                : throw new InvalidOperationException($"the home directory '{home}' is not an absolute path to an existing directory; set HOME");
        }
    }

    /// <summary>~/.config/softwarp/machine.json. Its directory is never bound into a sandbox; the toolchain guard refuses it.</summary>
    public static string DefaultPath => Path.Combine(Home, ".config", "softwarp", "machine.json");

    /// <summary>Fails closed, naming the bindings this build lacks: the agent spawn arrives in slice 2b-2 and the tracker
    /// in slice 2b-3. It still reads and checks the machine config and builds the bindings that exist, so a broken config
    /// is reported first.</summary>
    /// <exception cref="InvalidOperationException">The config is missing or invalid, or a binding is missing.</exception>
    public static Factory Create(string configPath)
    {
        _ = Bind(configPath);
        throw new InvalidOperationException(
            "this softwarp build has no agent spawn binding (slice 2b-2) and no tracker binding (slice 2b-3), so it cannot run a factory step yet");
    }

    /// <summary>The run store, git isolation and gate runner the machine config describes.</summary>
    /// <exception cref="InvalidOperationException">The config is missing or invalid.</exception>
    public static (MachineConfig Config, RunStore Store, GitIsolation Isolation, GateRunner Gates) Bind(string configPath)
    {
        var config = Load(configPath);
        var home = Home;
        var limits = config.Gates ?? GateLimits.Default;
        var (runs, slices) = (config.Runs ?? Path.Combine(home, ".softwarp", "runs"), config.Slices ?? Path.Combine(home, ".softwarp-slices"));
        string[] guarded = [runs, slices, Path.GetDirectoryName(Path.GetFullPath(configPath))!];
        foreach (var toolchain in limits.Toolchains)
            if (ToolchainProblem(toolchain, home, guarded) is { } problem)
                throw new InvalidOperationException($"{configPath}: toolchain '{toolchain}' {problem}, so no gate may see it");
        var sandbox = new Sandbox(limits, home);
        var isolation = new GitIsolation(slices, item => $"https://github.com/{item.Owner}/{item.Repo}.git", sandbox);
        var scratch = Path.Combine(home, ".softwarp", "gates");
        var gates = new GateRunner(sandbox, scratch, new PackageFetch(isolation, sandbox, scratch, limits.MaxNewPackages));
        return (config, new RunStore(runs), isolation, gates);
    }

    /// <summary>Why a toolchain directory from the maintainer's config may not be bound into every gate sandbox; null
    /// when it may. It guards against a slip in the config: a directory outside the home (outside it, the sandbox shows
    /// /usr and /etc already and hides the rest on purpose, /run's sockets among them), a link, or a directory that
    /// overlaps softwarp's own state or a credential location.</summary>
    /// <param name="guarded">The directories the machine config can move: the run store, the slices root and the machine
    /// config's directory. Everything else of softwarp's lives under ~/.softwarp, which is refused whole.</param>
    internal static string? ToolchainProblem(string toolchain, string home, IEnumerable<string> guarded)
    {
        if (!Path.IsPathFullyQualified(toolchain) || toolchain.Split('/').Contains("..")) return "is not an absolute path without '..'";
        var path = Normal(toolchain);
        home = Normal(home);
        if (!Inside(path, home)) return "is not inside the home";
        var prefixes = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select((_, i) => "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries)[..(i + 1)]));
        if (prefixes.Any(prefix => new FileInfo(prefix).LinkTarget is not null)) return "resolves through a link";
        if (!Directory.Exists(path)) return "is not a directory";
        string[] secrets = [".ssh", ".gnupg", ".config", ".codex", ".paseo", ".docker", ".nuget", ".softwarp"];
        if (guarded.Concat(secrets.Select(secret => Path.Combine(home, secret))).Select(Normal)
                .FirstOrDefault(other => path == other || Inside(path, other) || Inside(other, path)) is { } overlap)
            return $"overlaps {overlap}";
        return Path.GetRelativePath(home, path).StartsWith(".claude", StringComparison.Ordinal)
            ? "overlaps the ~/.claude files"
            : null;
    }

    static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    static bool Inside(string path, string directory) => path.StartsWith(directory == "/" ? "/" : directory + "/", StringComparison.Ordinal);

    static MachineConfig Load(string path)
    {
        MachineConfig config;
        try
        {
            config = JsonSerializer.Deserialize(File.ReadAllBytes(path), MachineJson.Default.MachineConfig)
                     ?? throw new JsonException("the config is null");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"no machine config at {path}: {e.Message}", e);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{path} is not a valid machine config: {e.Message}", e);
        }
        string[] paths = [config.Runs ?? "/", config.Slices ?? "/", .. config.Gates?.Toolchains ?? []];
        var gates = config.Gates ?? GateLimits.Default;
        return paths.All(Path.IsPathFullyQualified) && config.MaintainerAccountId.Length > 0
               && gates.BuildTimeout > TimeSpan.Zero && gates.TargetedTestsTimeout > TimeSpan.Zero && gates.TasksMax > 0
               && gates.MaxNewPackages >= 0 && gates.MemoryMax.Length > 0 && gates.MemoryMax.All(char.IsAsciiLetterOrDigit)
               && gates.LogCapBytes is >= 64 << 10 and <= Factory.MaxLogBytes
            ? config
            : throw new InvalidOperationException($"{path}: every path must be absolute, the maintainer account id must not be " +
                "empty, timeouts and tasks_max must be positive, max_new_packages must not be negative, memory_max must " +
                $"be a systemd size such as 8G, and log_cap_bytes must be between 65536 and {Factory.MaxLogBytes}");
    }
}
