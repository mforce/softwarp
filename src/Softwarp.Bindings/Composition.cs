using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Softwarp.Core;

namespace Softwarp.Bindings;

/// <summary>This machine's settings: who the maintainer is, how each agent role runs and the harnesses they run in,
/// where softwarp keeps its state, and the sandbox limits. Paths must be absolute and in normal form.</summary>
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
        // The overlap rules compare path strings, so a link on the way to a state directory could hide an overlap.
        foreach (var directory in new[] { runs, slices, sandbox.Packages, scratch })
            if (LinkOn(directory) is { } link)
                throw new InvalidOperationException($"{configPath}: '{directory}' resolves through the link {link}; softwarp's state directories may not be links");
        // Gates are shown the package cache and their own directory under the scratch, so no store may overlap either.
        foreach (var directory in guarded)
            if (new[] { sandbox.Packages, scratch }.FirstOrDefault(shared => Overlaps(directory, shared)) is { } shared)
                throw new InvalidOperationException($"{configPath}: '{directory}' overlaps {shared}, which gate sandboxes are shown");
        // The credentials sit in the config's directory, and sandboxes are bound to the run store's io directories and
        // ~/.softwarp's package cache and scratch, so none may overlap it.
        if (new[] { runs, Path.Combine(home, ".softwarp") }.FirstOrDefault(state => Overlaps(configDirectory, state)) is { } state)
            throw new InvalidOperationException($"{configPath}: the machine config's directory '{configDirectory}' overlaps {state}, which a sandbox binds");
        if (HarnessesProblem(config, home, configDirectory, guarded) is { } harnesses)
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
    static string? HarnessesProblem(MachineConfig config, string home, string configDirectory, string[] guarded)
    {
        foreach (var role in Enum.GetValues<ActionKind>())
        {
            var used = config.Agents.For(role).Harness;
            if (!config.Harnesses.TryGetValue(used, out var configured)) return $"a role runs on {Name(used)}, and harnesses.{Name(used)} is missing";
            if (configured.CredentialFor(role) is (var field, null))
                return $"the {Name(role)} role runs on {Name(used)}, and harnesses.{Name(used)}.{field} is missing";
        }
        List<(string Name, string Token)> credentials = [];
        foreach (var (harness, install) in config.Harnesses)
        {
            var profile = HarnessProfile.All[harness];
            if ((BoundPathProblem(install.Install, home, guarded, profile.InstallIsFile)
                 ?? (File.Exists(profile.Executable(install.Install)) ? null : "holds no executable")) is { } problem)
                return $"the {Name(harness)} install '{install.Install}' {problem}, so no agent may see it";
            foreach (var (field, file) in Enum.GetValues<ActionKind>().Select(install.CredentialFor).Distinct())
            {
                if (file is null) continue;
                if (CredentialProblem(file, home, configDirectory, out var token) is { } credential)
                    return $"the {Name(harness)} {field} '{file}' {credential}";
                credentials.Add(($"harnesses.{Name(harness)}.{field}", token));
            }
        }
        return DuplicateProblem(credentials);
    }

    internal static string Name(Enum value) => value.ToString().ToLowerInvariant();

    /// <summary>Why two credentials are the same file or content; null when no two are. Entries are compared by their
    /// token (see <see cref="CredentialProblem"/>), so two entries that name one file match too. A spec agent and an
    /// implementer with one credential could pass the holdouts through it. The message names the entries, never the
    /// content.</summary>
    static string? DuplicateProblem(List<(string Name, string Token)> credentials) =>
        credentials.GroupBy(credential => credential.Token).FirstOrDefault(same => same.Count() > 1) is { } twins
            ? $"{string.Join(" and ", twins.Select(credential => credential.Name))} hold the same credential"
            : null;

    /// <summary>A token from `claude setup-token` or an OpenAI API key is a few hundred bytes.</summary>
    const int MaxCredentialBytes = 4096;

    /// <summary>Why a credential file may not be handed to a harness; null when it may. It must be a regular file with
    /// content and mode 0600, which only its owner can read, so it is this user's; inside the machine config's directory,
    /// which Bind requires to be named softwarp and to overlap no directory a sandbox binds, so the maintainer's own logins
    /// (~/.claude, ~/.codex) are refused by place; and one token of ASCII letters, digits, '-' and '_', which refuses a copy
    /// or hard link of a JSON, key or URL login by content, and a byte the launch script's shell drops; with no link on the
    /// way. The file is read once, through <see cref="UntrustedFile"/> and within <see cref="MaxCredentialBytes"/>.</summary>
    /// <param name="token">The content with trailing newlines cut, as the launch script's `$(cat ...)` does, so the token
    /// checked is the one the harness gets; "" when the file is refused first.</param>
    internal static string? CredentialProblem(string credential, string home, string configDirectory, out string token)
    {
        token = "";
        if (LocationProblem(credential, home) is { } problem) return problem;
        if (!Inside(Normal(credential), Normal(configDirectory))) return $"is not inside the machine config's directory {configDirectory}";
        try
        {
            token = Encoding.UTF8.GetString(UntrustedFile.Read(credential, MaxCredentialBytes)).TrimEnd('\n');
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return $"cannot be read as a credential (not a regular file with content, or over {MaxCredentialBytes} bytes)";
        }
        if (new FileInfo(credential).UnixFileMode != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) return "does not have mode 0600";
        return token.Length > 0 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? null
            : "is not one token: it must be non-empty and hold only ASCII letters, digits, '-' and '_', as a pasted token does";
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
        if (!Inside(Normal(location), Normal(home))) return "is not inside the home";
        return LinkOn(location) is null ? null : "resolves through a link";
    }

    /// <summary>The first directory or file on the way to the path, from the root down, that is a link; null when none is.</summary>
    static string? LinkOn(string path)
    {
        var parts = Normal(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Select((_, i) => "/" + string.Join('/', parts[..(i + 1)])).FirstOrDefault(prefix => new FileInfo(prefix).LinkTarget is not null);
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
        string[] paths = [config.Runs ?? "/", config.Slices ?? "/", .. config.Gates?.Toolchains ?? [],
            .. config.Harnesses.Values.SelectMany(harness => new[] { harness.Install, harness.Credential, harness.SpecCredential }).OfType<string>()];
        var gates = config.Gates ?? GateLimits.Default;
        (bool Ok, string Rule)[] rules =
        [
            (paths.All(Path.IsPathFullyQualified), "every path must be absolute"),
            (paths.All(path => !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) == path),
                "every path must be in normal form, with no '.', '..' or '//'"),
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
