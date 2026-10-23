using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Softwarp.Core;

namespace Softwarp.Bindings;

/// <summary>This machine's settings: who the maintainer is, how each agent role runs and the harnesses they run in,
/// where softwarp keeps its state, and the sandbox limits. Paths must be absolute.</summary>
/// <param name="Agents">Each role's harness, model and effort; intake pins them into the run's manifest.</param>
/// <param name="Runs">The run store; ~/.softwarp/runs when null.</param>
/// <param name="Slices">The trusted clones and implementer repositories; ~/.softwarp-slices when null.</param>
/// <param name="Gates">The sandbox limits; <see cref="GateLimits.Default"/> when null.</param>
public sealed record MachineConfig(string MaintainerAccountId, AgentPins Agents,
    ImmutableDictionary<Harness, HarnessInstall> Harnesses, string? Runs = null, string? Slices = null, GateLimits? Gates = null);

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

    /// <summary>Fails closed, naming the binding this build lacks: the tracker arrives in slice 2b-3. It still reads and
    /// checks the machine config and builds the bindings that exist, so a broken config is reported first.</summary>
    /// <exception cref="InvalidOperationException">The config is missing or invalid, or a binding is missing.</exception>
    public static Factory Create(string configPath)
    {
        _ = Bind(configPath);
        throw new InvalidOperationException("this softwarp build has no tracker binding (slice 2b-3), so it cannot run a factory step yet");
    }

    /// <summary>The run store, git isolation, gate runner and agent spawn the machine config describes.</summary>
    /// <exception cref="InvalidOperationException">The config is missing or invalid.</exception>
    public static (MachineConfig Config, RunStore Store, GitIsolation Isolation, GateRunner Gates, AgentSpawn Spawn) Bind(string configPath)
    {
        var config = Load(configPath);
        var home = Home;
        var limits = config.Gates ?? GateLimits.Default;
        var (runs, slices) = (config.Runs ?? Path.Combine(home, ".softwarp", "runs"), config.Slices ?? Path.Combine(home, ".softwarp-slices"));
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        if (Path.GetFileName(configDirectory) != "softwarp")
            throw new InvalidOperationException($"{configPath}: the machine config must sit in a directory named softwarp, so the credentials beside it are not a login's or key's own directory (~/.claude, ~/.ssh)");
        string[] guarded = [runs, slices, configDirectory];
        // Agents write the worktrees under the slices root.
        foreach (var directory in new[] { runs, configDirectory })
            if (Overlaps(directory, slices))
                throw new InvalidOperationException($"{configPath}: '{directory}' overlaps the slices root {slices}, whose worktrees agents write");
        foreach (var toolchain in limits.Toolchains)
            if (BoundPathProblem(toolchain, home, guarded) is { } problem)
                throw new InvalidOperationException($"{configPath}: toolchain '{toolchain}' {problem}, so no gate may see it");
        var sandbox = new Sandbox(limits, home);
        var scratch = Path.Combine(home, ".softwarp", "gates");
        // Gates are shown the package cache and their own directory under the scratch, so no store may overlap either.
        foreach (var directory in guarded)
            if (new[] { sandbox.Packages, scratch }.FirstOrDefault(shared => Overlaps(directory, shared)) is { } shared)
                throw new InvalidOperationException($"{configPath}: '{directory}' overlaps {shared}, which gate sandboxes are shown");
        if (HarnessesProblem(config, home, configDirectory, guarded, [runs, Path.Combine(home, ".softwarp")]) is { } harnesses)
            throw new InvalidOperationException($"{configPath}: {harnesses}");
        var isolation = new GitIsolation(slices, item => $"https://github.com/{item.Owner}/{item.Repo}.git", sandbox);
        var gates = new GateRunner(sandbox, scratch, new PackageFetch(isolation, sandbox, scratch, limits.MaxNewPackages));
        var store = new RunStore(runs);
        return (config, store, isolation, gates, new AgentSpawn(sandbox, store, config.Harnesses));
    }

    /// <summary>Why the harnesses cannot run the agents; null when they can. Every role's harness must be configured, with
    /// the credential field the role needs (<see cref="HarnessInstall.CredentialFor"/>). Each configured harness, since a run
    /// may pin one the roles no longer name, needs an install that may be bound into its agents
    /// (<see cref="BoundPathProblem"/>) and holds the executable, and each credential it names must be one that no sandbox
    /// can see but its own (<see cref="CredentialProblem"/>). No two credentials may be the same file or content
    /// (<see cref="DuplicateProblem"/>).</summary>
    /// <param name="bound">The directories some sandbox binds that the machine config's directory can still hold or sit in:
    /// the run store and ~/.softwarp. Bind refuses a toolchain, an install or the slices root that overlaps that directory.</param>
    static string? HarnessesProblem(MachineConfig config, string home, string configDirectory, string[] guarded, string[] bound)
    {
        foreach (var role in Enum.GetValues<ActionKind>())
        {
            var used = config.Agents.For(role).Harness;
            if (!config.Harnesses.TryGetValue(used, out var configured)) return $"a role runs on {Name(used)}, and harnesses.{Name(used)} is missing";
            if (configured.CredentialFor(role) is (var field, null))
                return $"the {Name(role)} role runs on {Name(used)}, and harnesses.{Name(used)}.{field} is missing";
        }
        List<(string Name, string Hash)> credentials = [];
        foreach (var (harness, install) in config.Harnesses)
        {
            var profile = HarnessProfile.All[harness];
            if ((BoundPathProblem(install.Install, home, guarded, profile.InstallIsFile)
                 ?? (File.Exists(profile.Executable(install.Install)) ? null : "holds no executable")) is { } problem)
                return $"the {Name(harness)} install '{install.Install}' {problem}, so no agent may see it";
            foreach (var (field, file) in Enum.GetValues<ActionKind>().Select(install.CredentialFor).Distinct())
            {
                if (file is null) continue;
                if (CredentialProblem(file, home, configDirectory, bound, out var hash) is { } credential)
                    return $"the {Name(harness)} {field} '{file}' {credential}";
                credentials.Add(($"harnesses.{Name(harness)}.{field}", hash));
            }
        }
        return DuplicateProblem(credentials);
    }

    internal static string Name(Enum value) => value.ToString().ToLowerInvariant();

    /// <summary>Why two credentials are the same file or content; null when no two are. Entries are compared by the
    /// SHA-256 of the content with surrounding whitespace cut (the agent's launch script reads the file through
    /// `$(cat ...)`, which cuts a trailing newline), so two entries that name one file match too. A spec agent and an
    /// implementer with one credential could pass the holdouts through it. The message names the entries, never the content.</summary>
    static string? DuplicateProblem(List<(string Name, string Hash)> credentials) =>
        credentials.GroupBy(credential => credential.Hash).FirstOrDefault(same => same.Count() > 1) is { } twins
            ? $"{string.Join(" and ", twins.Select(credential => credential.Name))} hold the same credential"
            : null;

    /// <summary>A token from `claude setup-token` or an OpenAI API key is a few hundred bytes.</summary>
    const int MaxCredentialBytes = 4096;

    /// <summary>Why a credential file may not be handed to a harness; null when it may. It must be a regular file with
    /// content and mode 0600, which only its owner can read, so it is this user's; inside the machine config's directory,
    /// which no sandbox binds and Bind requires to be named softwarp, so the maintainer's own logins (~/.claude, ~/.codex)
    /// are refused by place; one token, which refuses a copy or hard link of a JSON login by content; with no link on the
    /// way; and outside every directory a sandbox binds, so only the agents it is bound into read it. The file is read once,
    /// through <see cref="UntrustedFile"/> and within <see cref="MaxCredentialBytes"/>.</summary>
    /// <param name="hash">The SHA-256 of the content with surrounding whitespace cut; "" when the file is refused first.</param>
    internal static string? CredentialProblem(string credential, string home, string configDirectory, IEnumerable<string> bound,
        out string hash)
    {
        hash = "";
        if (LocationProblem(credential, home) is { } problem) return problem;
        if (!Inside(Normal(credential), Normal(configDirectory))) return $"is not inside the machine config's directory {configDirectory}";
        var file = new FileInfo(credential);
        // On Linux every FIFO, socket and device node reports size 0.
        if (!file.Exists || file.Length == 0) return "is not a regular file with content";
        if (file.UnixFileMode != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) return "does not have mode 0600";
        string token;
        try
        {
            token = Encoding.UTF8.GetString(UntrustedFile.Read(credential, MaxCredentialBytes)).Trim();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return $"cannot be read as a credential (unreadable, or over {MaxCredentialBytes} bytes)";
        }
        if (token.Length == 0 || token.Any(char.IsWhiteSpace) || token.Contains('{'))
            return "is not one token: it is empty, has whitespace inside or holds JSON, as a login or key file does";
        hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        return bound.FirstOrDefault(directory => Overlaps(credential, directory)) is { } inside ? $"sits inside {inside}, which a sandbox binds" : null;
    }

    /// <summary>Why a path from the maintainer's config may not be bound read-only into every sandbox of a kind (a
    /// toolchain directory into every gate, a harness install into every agent of the harness); null when it may. It
    /// guards against a slip in the config: a path outside the home (outside it, the sandbox shows /usr and /etc already
    /// and hides the rest on purpose, /run's sockets among them), a link, or a path that overlaps softwarp's own state or
    /// a credential location.</summary>
    /// <param name="guarded">The directories the machine config can move: the run store, the slices root and the machine
    /// config's directory. Everything else of softwarp's lives under ~/.softwarp, which is refused whole.</param>
    /// <param name="file">The path names a file, not a directory.</param>
    internal static string? BoundPathProblem(string bound, string home, IEnumerable<string> guarded, bool file = false)
    {
        if (LocationProblem(bound, home) is { } problem) return problem;
        var path = Normal(bound);
        home = Normal(home);
        if (file ? !File.Exists(path) : !Directory.Exists(path)) return file ? "is not a file" : "is not a directory";
        string[] secrets = [".ssh", ".gnupg", ".config", ".codex", ".paseo", ".docker", ".nuget", ".softwarp"];
        if (guarded.Concat(secrets.Select(secret => Path.Combine(home, secret))).Select(Normal)
                .FirstOrDefault(other => Overlaps(path, other)) is { } overlap)
            return $"overlaps {overlap}";
        return Path.GetRelativePath(home, path).StartsWith(".claude", StringComparison.Ordinal)
            ? "overlaps the ~/.claude files"
            : null;
    }

    /// <summary>Why the path is not an absolute path inside the home with no link on the way; null when it is.</summary>
    static string? LocationProblem(string location, string home)
    {
        if (!Path.IsPathFullyQualified(location) || location.Split('/').Contains("..")) return "is not an absolute path without '..'";
        var path = Normal(location);
        if (!Inside(path, Normal(home))) return "is not inside the home";
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Select((_, i) => "/" + string.Join('/', parts[..(i + 1)])).Any(prefix => new FileInfo(prefix).LinkTarget is not null)
            ? "resolves through a link"
            : null;
    }

    static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    static bool Overlaps(string path, string other) => (Normal(path), Normal(other)) is var (a, b) && (a == b || Inside(a, b) || Inside(b, a));
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
        (bool Ok, string Rule)[] rules =
        [
            (paths.All(Path.IsPathFullyQualified), "every path must be absolute"),
            (config.MaintainerAccountId.Length > 0, "the maintainer account id must not be empty"),
            (gates.BuildTimeout > TimeSpan.Zero && gates.TargetedTestsTimeout > TimeSpan.Zero,
                "build_timeout and targeted_tests_timeout must be positive"),
            (gates.TasksMax > 0, "tasks_max must be positive"),
            (gates.MaxNewPackages >= 0, "max_new_packages must not be negative"),
            (gates.MemoryMax.Length > 0 && gates.MemoryMax.All(char.IsAsciiLetterOrDigit), "memory_max must be a systemd size such as 8G"),
            (gates.LogCapBytes is >= 64 << 10 and <= Factory.MaxLogBytes, $"log_cap_bytes must be between 65536 and {Factory.MaxLogBytes}"),
            .. Enum.GetValues<ActionKind>().SelectMany(AgentRules),
        ];
        var broken = rules.Where(rule => !rule.Ok).Select(rule => rule.Rule).ToList();
        return broken.Count == 0 ? config : throw new InvalidOperationException($"{path}: {string.Join("; ", broken)}");

        IEnumerable<(bool, string)> AgentRules(ActionKind role)
        {
            var (agent, name) = (config.Agents.For(role), Name(role));
            var efforts = HarnessProfile.All[agent.Harness].Efforts;
            yield return (efforts.Contains(agent.Effort), $"agents.{name}.effort must be one {Name(agent.Harness)} takes: {string.Join(", ", efforts)}");
            yield return (agent.Model.Length > 0 && agent.Model[0] != '-' && !agent.Model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)),
                $"agents.{name}.model must not be empty, start with '-' or hold a space");
        }
    }
}
