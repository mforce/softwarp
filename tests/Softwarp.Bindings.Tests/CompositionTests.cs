using System.Diagnostics;
using Softwarp.Core;

namespace Softwarp.Bindings.Tests;

/// <summary>Each test runs in a home of its own that holds both harnesses and their credentials.</summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class CompositionTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("softwarp-config-").FullName;
    readonly IDisposable home;
    string Config => Path.Combine(root, "cfg", "machine.json");
    string ClaudeInstall => Path.Combine(root, ".local", "share", "claude", "versions", "9.9.9");
    string CodexInstall => Path.Combine(root, ".npm-global", "codex", "vendor", "x86_64-unknown-linux-musl");
    string ClaudeToken => Path.Combine(root, ".config", "softwarp", "claude-token");
    string CodexKey => Path.Combine(root, ".config", "softwarp", "codex-key");

    public CompositionTests()
    {
        home = GitRemote.Planted(new Dictionary<string, string> { ["HOME"] = root });
        Directory.CreateDirectory(Path.Combine(root, "cfg"));
        foreach (var executable in new[] { ClaudeInstall, HarnessProfile.All[Harness.Codex].Executable(CodexInstall) })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            GitRemote.Script(executable, "#!/bin/sh\n");
        }
        Credential(ClaudeToken);
        Credential(CodexKey);
    }

    public void Dispose()
    {
        home.Dispose();
        Directory.Delete(root, recursive: true);
    }

    string Valid => $$"""
        {
          "maintainer_account_id": "12345",
          "agents": {
            "triage": { "harness": "codex", "model": "gpt-6-astra", "effort": "low" },
            "spec": { "harness": "claude", "model": "claude-opus-5-5", "effort": "high" },
            "implement": { "harness": "claude", "model": "claude-sonnet-5-5", "effort": "medium" },
            "cleanup": { "harness": "claude", "model": "claude-haiku-5", "effort": "low" }
          },
          "harnesses": {
            "claude": { "install": "{{ClaudeInstall}}", "credential": "{{ClaudeToken}}" },
            "codex": { "install": "{{CodexInstall}}", "credential": "{{CodexKey}}" }
          },
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
    public void ValidConfig_BindsTheStoreIsolationGatesAndSpawn()
    {
        File.WriteAllText(Config, Valid);
        var (config, _, _, _, _) = Composition.Bind(Config);
        Assert.Equal(("12345", TimeSpan.FromMinutes(20)), (config.MaintainerAccountId, config.Gates!.TargetedTestsTimeout));
        Assert.Equal(new AgentPin(Harness.Codex, "gpt-6-astra", "low"), config.Agents.Triage);
        Assert.Equal(new AgentPin(Harness.Claude, "claude-opus-5-5", "high"), config.Agents.Spec);
        Assert.Equal([Harness.Claude, Harness.Codex], config.Harnesses.Keys.Order());
    }

    [Fact]
    public void Create_BuildsTheSpawnAndFailsClosedNamingTheMissingTracker()
    {
        File.WriteAllText(Config, Valid);
        var message = Assert.Throws<InvalidOperationException>(() => Composition.Create(Config)).Message;
        Assert.Equal("this softwarp build has no tracker binding (slice 2b-3), so it cannot run a factory step yet", message);
    }

    [Theory]
    [InlineData("/no-such-softwarp-home")]
    [InlineData(".")] // a directory that exists, named relative to the working directory
    public void HomeThatIsMissingOrRelative_FailsClosedBeforeAnyPathIsBuilt(string home)
    {
        File.WriteAllText(Config, Valid);
        using var environment = GitRemote.Planted(new Dictionary<string, string> { ["HOME"] = home });
        Assert.Contains("home directory", Assert.Throws<InvalidOperationException>(() => Composition.DefaultPath).Message);
        Assert.Contains("home directory", Assert.Throws<InvalidOperationException>(() => Composition.Bind(Config)).Message);
    }

    [Fact]
    public void ToolchainThatOverlapsSoftwarpsPackageCache_IsRefusedByBind()
    {
        // The home sits outside the config's directory, so the toolchain can overlap only ~/.softwarp.
        var home = Directory.CreateTempSubdirectory("softwarp-home-").FullName;
        try
        {
            var cache = Directory.CreateDirectory(Path.Combine(home, ".softwarp", "packages")).FullName;
            using var environment = GitRemote.Planted(new Dictionary<string, string> { ["HOME"] = home });
            Assert.Contains($"toolchain '{cache}' overlaps {home}/.softwarp,", Refused(Valid.Replace("\"toolchains\": []", $"\"toolchains\": [\"{cache}\"]")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Theory]
    [InlineData("data/runs")]
    [InlineData("data/slices")]
    [InlineData("cfg")]
    public void ToolchainAtAConfiguredStateDirectory_IsRefusedByBind(string directory)
    {
        var home = Directory.CreateTempSubdirectory("softwarp-home-").FullName;
        try
        {
            foreach (var d in new[] { "data/runs", "data/slices", "cfg" }) Directory.CreateDirectory(Path.Combine(home, d));
            using var environment = GitRemote.Planted(new Dictionary<string, string> { ["HOME"] = home });
            var config = Path.Combine(home, "cfg", "machine.json");
            File.WriteAllText(config, Valid.Replace("\"/var/softwarp/runs\"", $"\"{home}/data/runs\", \"slices\": \"{home}/data/slices\"")
                .Replace("\"toolchains\": []", $"\"toolchains\": [\"{home}/{directory}\"]"));
            Assert.Contains($"toolchain '{home}/{directory}' overlaps {home}/{directory},", Assert.Throws<InvalidOperationException>(() => Composition.Bind(config)).Message);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>Gates are shown the package cache, and a run store inside it would show them every run and its holdouts.</summary>
    [Theory]
    [InlineData("runs", ".softwarp/packages/runs", ".softwarp/packages")]
    [InlineData("runs", ".softwarp", ".softwarp/packages")]
    [InlineData("runs", ".softwarp/gates", ".softwarp/gates")]
    [InlineData("slices", ".softwarp/packages", ".softwarp/packages")]
    [InlineData("slices", ".softwarp/gates/slices", ".softwarp/gates")]
    [InlineData("config", ".softwarp/packages/cfg", ".softwarp/packages")]
    public void StateDirectoryThatOverlapsThePackageCacheOrTheGateScratch_IsRefusedByBind(string key, string directory, string shared)
    {
        var home = Directory.CreateTempSubdirectory("softwarp-home-").FullName;
        try
        {
            using var environment = GitRemote.Planted(new Dictionary<string, string> { ["HOME"] = home });
            var path = Path.Combine(home, directory);
            var config = Path.Combine(key == "config" ? path : Path.Combine(home, "cfg"), "machine.json");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, key == "config" ? Valid : Valid.Replace("\"runs\": \"/var/softwarp/runs\"", $"\"{key}\": \"{path}\""));
            Assert.Contains($"'{path}' overlaps {Path.Combine(home, shared)}",
                Assert.Throws<InvalidOperationException>(() => Composition.Bind(config)).Message);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>Agents write the worktrees under the slices root, so neither the run store nor the config may sit there.</summary>
    [Theory]
    [InlineData("runs", "data/slices/runs")]
    [InlineData("runs", "data")]
    [InlineData("config", "data/slices/cfg")]
    public void RunStoreOrConfigThatOverlapsTheSlicesRoot_IsRefusedByBind(string key, string directory)
    {
        var path = Path.Combine(root, directory);
        var slices = Path.Combine(root, "data", "slices");
        var config = Path.Combine(key == "config" ? path : Path.Combine(root, "cfg"), "machine.json");
        var runs = key == "runs" ? path : "/var/softwarp/runs";
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, Valid.Replace("\"runs\": \"/var/softwarp/runs\"", $"\"runs\": \"{runs}\", \"slices\": \"{slices}\""));
        Assert.Contains($"'{path}' overlaps the slices root {slices}",
            Assert.Throws<InvalidOperationException>(() => Composition.Bind(config)).Message);
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
    [InlineData("\"runs\": \"/var/softwarp/runs\"", "\"runs\": \"relative/runs\"", "every path must be absolute")]
    [InlineData("\"toolchains\": []", "\"toolchains\": [\"nvm/node\"]", "every path must be absolute")]
    [InlineData("\"maintainer_account_id\": \"12345\"", "\"maintainer_account_id\": \"\"", "the maintainer account id must not be empty")]
    [InlineData("\"build_timeout\": \"00:30:00\"", "\"build_timeout\": \"00:00:00\"", "build_timeout and targeted_tests_timeout must be positive")]
    [InlineData("\"memory_max\": \"8G\"", "\"memory_max\": \"8G -p Delegate=yes\"", "memory_max must be a systemd size such as 8G")]
    [InlineData("\"tasks_max\": 4096", "\"tasks_max\": 0", "tasks_max must be positive")]
    [InlineData("\"max_new_packages\": 20", "\"max_new_packages\": -1", "max_new_packages must not be negative")]
    [InlineData("\"effort\": \"high\"", "\"effort\": \"minimal\"", "agents.spec.effort must be one claude takes: low, medium, high, xhigh, max")]
    [InlineData("\"effort\": \"low\" },\n    \"spec\"", "\"effort\": \"max\\\" -c x=\\\"y\" },\n    \"spec\"",
        "agents.triage.effort must be one codex takes: minimal, low, medium, high, xhigh, max")]
    [InlineData("\"model\": \"claude-opus-5-5\"", "\"model\": \"--dangerously-skip-permissions\"", "agents.spec.model must not be empty, start with '-' or hold a space")]
    [InlineData("\"model\": \"claude-opus-5-5\"", "\"model\": \"opus 5\"", "agents.spec.model must not be empty, start with '-' or hold a space")]
    [InlineData("\"model\": \"claude-opus-5-5\"", "\"model\": \"\"", "agents.spec.model must not be empty, start with '-' or hold a space")]
    public void ConfigWithAValueTheBindingsCannotUse_IsRefusedNamingOnlyTheRuleItBreaks(string from, string to, string rule)
    {
        Assert.Contains(from, Valid);
        Assert.Equal($"{Config}: {rule}", Refused(Valid.Replace(from, to)));
    }

    [Fact]
    public void ConfigThatBreaksSeveralRules_NamesEach() =>
        Assert.Equal($"{Config}: tasks_max must be positive; agents.spec.effort must be one claude takes: low, medium, high, xhigh, max",
            Refused(Valid.Replace("\"tasks_max\": 4096", "\"tasks_max\": 0").Replace("\"effort\": \"high\"", "\"effort\": \"minimal\"")));

    [Theory]
    [InlineData("\"harness\": \"codex\"", "\"harness\": \"pi\"")]
    [InlineData("\"harness\": \"codex\"", "\"harness\": 1")]
    public void HarnessThatIsNotOneThisBuildRuns_IsRefused(string from, string to) =>
        Assert.Contains("not a valid machine config", Refused(Valid.Replace(from, to)));

    [Fact]
    public void RoleOnAHarnessWithNoSection_IsRefusedByBind() =>
        Assert.Contains("a role runs on codex, and harnesses.codex is missing", Refused(Valid.Replace(CodexSection, "")));

    [Fact]
    public void HarnessNoRoleRuns_NeedsNoSection()
    {
        File.WriteAllText(Config, Valid.Replace("\"harness\": \"codex\"", "\"harness\": \"claude\"").Replace(CodexSection, ""));
        Assert.Equal([Harness.Claude], Composition.Bind(Config).Config.Harnesses.Keys);
    }

    /// <summary>A run pins its harnesses at intake, so a section no role names today may still serve a run.</summary>
    [Fact]
    public void HarnessNoRoleRuns_IsStillChecked() =>
        Assert.Contains($"the codex credential '/nowhere' is not inside the home", Refused(Valid.Replace("\"harness\": \"codex\"", "\"harness\": \"claude\"")
            .Replace($"\"credential\": \"{CodexKey}\"", "\"credential\": \"/nowhere\"")));

    [Fact]
    public void HarnessNoRoleRuns_HasItsInstallCheckedToo() =>
        Assert.Contains("the codex install '/nowhere' is not inside the home", Refused(Valid.Replace("\"harness\": \"codex\"", "\"harness\": \"claude\"")
            .Replace($"\"install\": \"{CodexInstall}\"", "\"install\": \"/nowhere\"")));

    string CodexSection => $",\n    \"codex\": {{ \"install\": \"{CodexInstall}\", \"credential\": \"{CodexKey}\" }}";

    [Theory]
    [InlineData("claude", "link", "resolves through a link")]
    [InlineData("claude", "directory", "is not a file")]
    [InlineData("codex", "file", "is not a directory")]
    [InlineData("codex", "directory", "holds no executable")]
    [InlineData("codex", "secret", "overlaps")]
    [InlineData("claude", "outside", "is not inside the home")]
    public void InstallThatAnAgentMayNotBeShown_IsRefusedByBind(string harness, string kind, string reason)
    {
        var path = Path.Combine(root, "install");
        switch (kind)
        {
            case "link": File.CreateSymbolicLink(path, ClaudeInstall); break;
            case "directory": Directory.CreateDirectory(path); break;
            case "file": File.WriteAllText(path, ""); break;
            case "secret": path = Directory.CreateDirectory(Path.Combine(root, ".ssh", "codex")).FullName; break;
            case "outside": path = "/usr/bin/true"; break;
        }
        var install = harness == "claude" ? ClaudeInstall : CodexInstall;
        Assert.Contains($"install '{path}' {reason}", Refused(Valid.Replace($"\"install\": \"{install}\"", $"\"install\": \"{path}\"")));
    }

    [Theory]
    [InlineData("mode", "does not have mode 0600")]
    [InlineData("empty", "is not a regular file with content")]
    [InlineData("fifo", "is not a regular file with content")]
    [InlineData("link", "resolves through a link")]
    [InlineData("outside", "is not inside the home")]
    [InlineData("runs", "sits inside")]
    [InlineData("toolchain", "sits inside")]
    [InlineData("install", "sits inside")]
    [InlineData("softwarp", "sits inside")]
    public void CredentialThatASandboxCouldShowOrAnotherUserCouldPlant_IsRefusedByBind(string kind, string reason)
    {
        var path = Path.Combine(root, "credential");
        var config = Valid.Replace("\"runs\": \"/var/softwarp/runs\"", $"\"runs\": \"{root}/runs\"")
            .Replace("\"toolchains\": []", $"\"toolchains\": [\"{root}/tools\"]");
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        switch (kind)
        {
            case "mode": File.WriteAllText(path, "secret"); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); break;
            case "empty": File.WriteAllText(path, ""); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); break;
            case "fifo": using (var mkfifo = Process.Start("mkfifo", ["-m", "600", path])) mkfifo.WaitForExit(); break;
            case "link": File.CreateSymbolicLink(path, ClaudeToken); break;
            case "outside": path = "/etc/hostname"; break;
            case "runs": path = Credential(Path.Combine(root, "runs", "mforce", "credential")); break;
            case "toolchain": path = Credential(Path.Combine(root, "tools", "credential")); break;
            case "install": path = Credential(Path.Combine(CodexInstall, "credential")); break;
            case "softwarp": path = Credential(Path.Combine(root, ".softwarp", "credential")); break;
        }
        Assert.Contains($"credential '{path}' {reason}", Refused(config.Replace($"\"credential\": \"{ClaudeToken}\"", $"\"credential\": \"{path}\"")));
    }

    static string Credential(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "secret");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

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
    string Runs => Path.Combine(Home, "data", "runs");
    string Slices => Path.Combine(Home, "data", "slices");

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

    string? Problem(string toolchain) => Composition.BoundPathProblem(toolchain.Replace("~", Home), Home,
        [Runs, Slices, Path.Combine(Home, ".config", "softwarp")]);

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
    [InlineData("~/.nuget/packages", "overlaps")]
    [InlineData("~/data", "overlaps")]
    [InlineData("~/data/runs/mforce", "overlaps")]
    [InlineData("~/data/slices", "overlaps")]
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
              "agents": {
                "triage": { "harness": "claude", "model": "a", "effort": "low" }, "spec": { "harness": "claude", "model": "b", "effort": "low" },
                "implement": { "harness": "claude", "model": "c", "effort": "low" }, "cleanup": { "harness": "claude", "model": "d", "effort": "low" }
              },
              "harnesses": {},
              "gates": {
                "build_timeout": "00:30:00", "targeted_tests_timeout": "00:30:00", "memory_max": "8G", "tasks_max": 4096,
                "log_cap_bytes": 8388608, "toolchains": ["{{home}}"], "max_new_packages": 20
              }
            }
            """);
        Assert.Contains($"toolchain '{home}' is not inside the home", Assert.Throws<InvalidOperationException>(() => Composition.Bind(config)).Message);
    }
}
