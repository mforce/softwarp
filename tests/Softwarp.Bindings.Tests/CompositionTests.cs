namespace Softwarp.Bindings.Tests;

[Collection(nameof(ProcessEnvironment))]
public sealed class CompositionTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("softwarp-config-").FullName;
    string Config => Path.Combine(root, "machine.json");

    public void Dispose() => Directory.Delete(root, recursive: true);

    const string Valid = """
        {
          "maintainer_account_id": "12345",
          "models": { "triage": "glm-5.3", "spec": "claude-opus-5-5", "implement": "claude-sonnet-5-5", "cleanup": "claude-haiku-5" },
          "runs": "/var/softwarp/runs",
          "gates": {
            "build_timeout": "00:30:00", "targeted_tests_timeout": "00:20:00", "memory_max": "8G", "tasks_max": 4096,
            "log_cap_bytes": 8388608, "toolchains": [], "max_new_packages": 20
          }
        }
        """;

    string Refused(string json)
    {
        File.WriteAllText(Config, json);
        return Assert.Throws<InvalidOperationException>(() => Composition.Bind(Config)).Message;
    }

    [Fact]
    public void ValidConfig_BindsTheStoreIsolationAndGates()
    {
        File.WriteAllText(Config, Valid);
        var (config, _, _, _) = Composition.Bind(Config);
        Assert.Equal(("12345", "claude-opus-5-5", TimeSpan.FromMinutes(20)),
            (config.MaintainerAccountId, config.Models.Spec, config.Gates!.TargetedTestsTimeout));
    }

    [Fact]
    public void Create_FailsClosedNamingTheMissingBindings()
    {
        File.WriteAllText(Config, Valid);
        var message = Assert.Throws<InvalidOperationException>(() => Composition.Create(Config)).Message;
        Assert.Contains("no tracker binding (slice 2b-2)", message);
        Assert.Contains("no agent spawn binding (slice 2b-3)", message);
    }

    [Fact]
    public void HomeThatDoesNotExist_FailsClosedBeforeAnyPathIsBuilt()
    {
        File.WriteAllText(Config, Valid);
        using var environment = GitRemote.Planted(new Dictionary<string, string> { ["HOME"] = Path.Combine(root, "no-such-home") });
        Assert.Contains("home directory", Assert.Throws<InvalidOperationException>(() => Composition.DefaultPath).Message);
        Assert.Contains("home directory", Assert.Throws<InvalidOperationException>(() => Composition.Bind(Config)).Message);
    }

    [Fact]
    public void MissingConfig_NamesItsPath() =>
        Assert.Contains(Config, Assert.Throws<InvalidOperationException>(() => Composition.Create(Config)).Message);

    [Theory]
    [InlineData("""{ "maintainer_account_id": "1" }""")]
    [InlineData("not json")]
    public void ConfigThatIsNotAMachineConfig_IsRefused(string json) => Assert.Contains("not a valid machine config", Refused(json));

    [Fact]
    public void UnknownKey_IsRefused() => Assert.Contains("not a valid machine config", Refused(Valid.Replace("\"runs\"", "\"run_store\"")));

    [Theory]
    [InlineData("\"runs\": \"/var/softwarp/runs\"", "\"runs\": \"relative/runs\"")]
    [InlineData("\"maintainer_account_id\": \"12345\"", "\"maintainer_account_id\": \"\"")]
    [InlineData("\"build_timeout\": \"00:30:00\"", "\"build_timeout\": \"00:00:00\"")]
    [InlineData("\"memory_max\": \"8G\"", "\"memory_max\": \"8G -p Delegate=yes\"")]
    [InlineData("\"tasks_max\": 4096", "\"tasks_max\": 0")]
    [InlineData("\"toolchains\": []", "\"toolchains\": [\"nvm/node\"]")]
    public void ConfigWithAValueTheBindingsCannotUse_IsRefused(string from, string to) =>
        Assert.Contains("must be absolute", Refused(Valid.Replace(from, to)));

    [Theory]
    [InlineData(68719476736)]
    [InlineData(1024)]
    public void LogCapAboveWhatTheFactoryHashesOrTooSmallForAHeadAndATail_IsRefused(long cap) =>
        Assert.Contains("log_cap_bytes must be between", Refused(Valid.Replace("\"log_cap_bytes\": 8388608", $"\"log_cap_bytes\": {cap}")));
}

/// <summary>A toolchain directory is bound into every gate, so a slip in the config must not expose a secret.</summary>
public sealed class ToolchainTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("softwarp-home-").FullName;
    string Home => Path.Combine(root, "home", "maintainer");
    string Runs => Path.Combine(root, "state", "runs");
    string Slices => Path.Combine(root, "state", "slices");

    public ToolchainTests()
    {
        foreach (var directory in new[]
                 {
                     ".nvm/versions/node/v26.9.0/bin", ".ssh", ".gnupg", ".config/gh", ".config/softwarp", ".codex", ".paseo",
                     ".docker", ".nuget/NuGet", ".nuget/packages", ".claude/projects", ".claude-code/cache", "tools/go", ".softwarp/gates",
                 })
            Directory.CreateDirectory(Path.Combine(Home, directory));
        Directory.CreateDirectory(Path.Combine(Runs, "mforce"));
        Directory.CreateDirectory(Path.Combine(Slices, "mforce"));
        Directory.CreateSymbolicLink(Path.Combine(Home, "node"), Path.Combine(Home, ".ssh"));
        Directory.CreateSymbolicLink(Path.Combine(Home, "linked"), Path.Combine(Home, "tools"));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    string? Problem(string toolchain) => Composition.ToolchainProblem(toolchain.Replace("~", Home), Home,
        [Runs, Slices, Path.Combine(Home, ".softwarp", "gates"), Path.Combine(Home, ".config", "softwarp")]);

    [Theory]
    [InlineData("~/.nvm/versions/node/v26.9.0")]
    [InlineData("~/.nvm/versions/node/v26.9.0/")]
    [InlineData("~/tools/go")]
    public void ToolchainUnderHomeAwayFromSecrets_IsAccepted(string toolchain) => Assert.Null(Problem(toolchain));

    [Theory]
    [InlineData("~", "is not inside the home")]
    [InlineData("/", "is not inside the home")]
    [InlineData("/run", "is not inside the home")]
    [InlineData("~/..", "without '..'")]
    [InlineData("~/.nvm/../.ssh", "without '..'")]
    [InlineData("nvm/versions/node", "is not an absolute path")]
    [InlineData("~/node", "resolves through a link")]
    [InlineData("~/linked/go", "resolves through a link")]
    [InlineData("~/.nvm/versions/node/v0.0.0", "is not a directory")]
    [InlineData("~/.ssh", "overlaps")]
    [InlineData("~/.gnupg", "overlaps")]
    [InlineData("~/.config/gh", "overlaps")]
    [InlineData("~/.codex", "overlaps")]
    [InlineData("~/.paseo", "overlaps")]
    [InlineData("~/.docker", "overlaps")]
    [InlineData("~/.nuget", "overlaps")]
    [InlineData("~/.nuget/NuGet", "overlaps")]
    [InlineData("~/.softwarp", "overlaps")]
    [InlineData("~/.softwarp/gates", "overlaps")]
    [InlineData("~/.claude/projects", "~/.claude")]
    [InlineData("~/.claude-code/cache", "~/.claude")]
    public void ToolchainThatWouldExposeTheHomeOrASecret_IsRefused(string toolchain, string reason) =>
        Assert.Contains(reason, Problem(toolchain));

    [Fact]
    public void BindRefusesAConfigWhoseToolchainIsASecretLocation_NamingTheEntry()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Path.Combine(root, "machine.json");
        File.WriteAllText(config, $$"""
            {
              "maintainer_account_id": "12345",
              "models": { "triage": "a", "spec": "b", "implement": "c", "cleanup": "d" },
              "gates": {
                "build_timeout": "00:30:00", "targeted_tests_timeout": "00:30:00", "memory_max": "8G", "tasks_max": 4096,
                "log_cap_bytes": 8388608, "toolchains": ["{{home}}"], "max_new_packages": 20
              }
            }
            """);
        Assert.Contains($"toolchain '{home}' is not inside the home", Assert.Throws<InvalidOperationException>(() => Composition.Bind(config)).Message);
    }
}
