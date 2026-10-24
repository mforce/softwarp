using System.Diagnostics;
using Softwarp.Core;

namespace Softwarp.Bindings.Tests;

/// <summary>Each test runs in a home of its own that holds both harnesses and their credentials.</summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class CompositionTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("softwarp-config-").FullName;
    readonly IDisposable home;
    string Config => Path.Combine(root, "softwarp", "machine.json");
    string ClaudeInstall => Path.Combine(root, ".local", "share", "claude", "versions", "9.9.9");
    string CodexInstall => Path.Combine(root, ".npm-global", "codex", "vendor", "x86_64-unknown-linux-musl");
    string Secrets => Path.GetDirectoryName(Config)!;
    string ClaudeToken => Path.Combine(Secrets, "claude-token");
    string ClaudeSpecToken => Path.Combine(Secrets, "claude-spec-token");
    string CodexKey => Path.Combine(Secrets, "codex-key");

    public CompositionTests()
    {
        home = GitRemote.Planted(new Dictionary<string, string> { ["HOME"] = root });
        Directory.CreateDirectory(Path.Combine(root, "softwarp"));
        foreach (var executable in new[] { ClaudeInstall, HarnessProfile.All[Harness.Codex].Executable(CodexInstall) })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            GitRemote.Script(executable, "#!/bin/sh\n");
        }
        Credential(ClaudeToken);
        Credential(ClaudeSpecToken);
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
            "claude": { "install": "{{ClaudeInstall}}", "credential": "{{ClaudeToken}}", "spec_credential": "{{ClaudeSpecToken}}" },
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
        Assert.Equal((ClaudeToken, ClaudeSpecToken), (config.Harnesses[Harness.Claude].Credential, config.Harnesses[Harness.Claude].SpecCredential));
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
    [InlineData("softwarp")]
    public void ToolchainAtAConfiguredStateDirectory_IsRefusedByBind(string directory)
    {
        var home = Directory.CreateTempSubdirectory("softwarp-home-").FullName;
        try
        {
            foreach (var d in new[] { "data/runs", "data/slices", "softwarp" }) Directory.CreateDirectory(Path.Combine(home, d));
            using var environment = GitRemote.Planted(new Dictionary<string, string> { ["HOME"] = home });
            var config = Path.Combine(home, "softwarp", "machine.json");
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
    [InlineData("config", ".softwarp/packages/softwarp", ".softwarp/packages")]
    public void StateDirectoryThatOverlapsThePackageCacheOrTheGateScratch_IsRefusedByBind(string key, string directory, string shared)
    {
        var home = Directory.CreateTempSubdirectory("softwarp-home-").FullName;
        try
        {
            using var environment = GitRemote.Planted(new Dictionary<string, string> { ["HOME"] = home });
            var path = Path.Combine(home, directory);
            var config = Path.Combine(key == "config" ? path : Path.Combine(home, "softwarp"), "machine.json");
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
    [InlineData("config", "data/slices/softwarp")]
    public void RunStoreOrConfigThatOverlapsTheSlicesRoot_IsRefusedByBind(string key, string directory)
    {
        var path = Path.Combine(root, directory);
        var slices = Path.Combine(root, "data", "slices");
        var config = Path.Combine(key == "config" ? path : Path.Combine(root, "softwarp"), "machine.json");
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
    // A '..' after a link would hide the link from the link walk, which reads the path as text.
    [InlineData("\"runs\": \"/var/softwarp/runs\"", "\"runs\": \"/var/link/../runs\"", NormalForm)]
    [InlineData("\"runs\": \"/var/softwarp/runs\"", "\"runs\": \"/var/./runs\"", NormalForm)]
    [InlineData("\"runs\": \"/var/softwarp/runs\"", "\"runs\": \"/var//runs\"", NormalForm)]
    [InlineData("\"claude\": { \"install\": \"/", "\"claude\": { \"install\": \"//", NormalForm)]
    [InlineData("\"credential\": \"/", "\"credential\": \"//", NormalForm)]
    [InlineData("\"spec_credential\": \"/", "\"spec_credential\": \"//", NormalForm)]
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

    const string NormalForm = "every path must be in normal form, with no '.', '..' or '//'";

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

    /// <summary>The spec agent sees the holdouts, so a role gets only the field made for it, and none stands in for another.</summary>
    [Theory]
    [InlineData("spec_credential", "the spec role runs on claude")]
    [InlineData("credential", "the implement role runs on claude")]
    public void RoleOnAHarnessWithoutTheCredentialFieldItNeeds_IsRefusedNamingTheHarnessAndTheField(string field, string role)
    {
        var file = field == "credential" ? ClaudeToken : ClaudeSpecToken;
        Assert.Contains($"{role}, and harnesses.claude.{field} is missing", Refused(Valid.Replace($", \"{field}\": \"{file}\"", "")));
    }

    [Fact]
    public void SpecOnCodexAndTheRestOnClaude_EachWithOnlyTheFieldItNeeds_IsAccepted()
    {
        File.WriteAllText(Config, Valid.Replace("\"triage\": { \"harness\": \"codex\"", "\"triage\": { \"harness\": \"claude\"")
            .Replace("\"spec\": { \"harness\": \"claude\"", "\"spec\": { \"harness\": \"codex\"")
            .Replace($", \"spec_credential\": \"{ClaudeSpecToken}\"", "").Replace($"\"credential\": \"{CodexKey}\"", $"\"spec_credential\": \"{CodexKey}\""));
        var harnesses = Composition.Bind(Config).Config.Harnesses;
        Assert.Equal(ClaudeToken, harnesses[Harness.Claude].Credential);
        Assert.Null(harnesses[Harness.Claude].SpecCredential);
        Assert.Null(harnesses[Harness.Codex].Credential);
        Assert.Equal(CodexKey, harnesses[Harness.Codex].SpecCredential);
    }

    /// <summary>A role added later has to be given a field, or Bind fails for it at startup; it never gets the implementer's.</summary>
    [Fact]
    public void RoleWithNoCredentialField_ThrowsInsteadOfGettingTheImplementersCredential() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HarnessInstall(ClaudeInstall, ClaudeToken, ClaudeSpecToken).CredentialFor((ActionKind)99));

    /// <summary>The same file or content for the spec agent and another agent would carry the holdouts from one to the
    /// other. The message names both entries and never the content.</summary>
    [Fact]
    public void SpecCredentialThatIsTheCredentialFile_IsRefusedNamingBothEntries() =>
        Assert.Contains("harnesses.claude.credential and harnesses.claude.spec_credential hold the same credential",
            Refused(Valid.Replace($"\"spec_credential\": \"{ClaudeSpecToken}\"", $"\"spec_credential\": \"{ClaudeToken}\"")));

    /// <summary>The launch script reads the file through `$(cat ...)`, which cuts a trailing newline, so a copy with one has
    /// the same content.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public void CredentialInASecondFileWithTheSameContent_IsRefusedNamingBothEntries(string ending)
    {
        var content = File.ReadAllText(ClaudeSpecToken);
        var copy = Credential(Path.Combine(Secrets, "copy"), content + ending);
        var message = Refused(Valid.Replace($"\"credential\": \"{CodexKey}\"", $"\"credential\": \"{copy}\""));
        Assert.Contains("harnesses.claude.spec_credential", message);
        Assert.Contains("harnesses.codex.credential", message);
        Assert.Contains("hold the same credential", message);
        Assert.DoesNotContain(content, message);
    }

    /// <summary>The spec agent's credential on one harness against the implementer's on another: the entries sit apart in
    /// the config, so the check compares every pair.</summary>
    [Fact]
    public void CredentialOfOneHarnessCopiedAsTheSpecCredentialOfAnother_IsRefusedNamingBothEntries()
    {
        var copy = Credential(Path.Combine(Secrets, "copy"), File.ReadAllText(ClaudeToken));
        Assert.Contains("harnesses.claude.credential and harnesses.codex.spec_credential hold the same credential",
            Refused(Valid.Replace($"\"credential\": \"{CodexKey}\"", $"\"credential\": \"{CodexKey}\", \"spec_credential\": \"{copy}\"")));
    }

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
    [InlineData("empty", "cannot be read as a credential")]
    [InlineData("fifo", "cannot be read as a credential")]
    [InlineData("link", "resolves through a link")]
    [InlineData("large", "cannot be read as a credential")]
    [InlineData("json", "is not one token")]
    [InlineData("words", "is not one token")]
    [InlineData("blank", "is not one token")]
    [InlineData("nul", "is not one token")]
    [InlineData("url", "is not one token")]
    [InlineData("leading", "is not one token")]
    [InlineData("crlf", "is not one token")]
    [InlineData("jwt", "is not one token")]
    [InlineData("outside", "is not inside the home")]
    [InlineData("login", "is not inside the machine config's directory")]
    public void CredentialThatASandboxCouldShowOrAnotherUserCouldPlant_IsRefusedByBind(string kind, string reason)
    {
        var path = Path.Combine(Secrets, "credential");
        switch (kind)
        {
            case "mode": File.WriteAllText(path, "secret"); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); break;
            case "empty": File.WriteAllText(path, ""); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); break;
            case "fifo": using (var mkfifo = Process.Start("mkfifo", ["-m", "600", path])) mkfifo.WaitForExit(); break;
            case "link": File.CreateSymbolicLink(path, ClaudeToken); break;
            case "large": Credential(path, new string('a', 5000)); break;
            case "json": Credential(path, """{"claudeAiOauth":{"accessToken":"fake"}}"""); break;
            case "words": Credential(path, "two words"); break;
            case "blank": Credential(path, " \n"); break;
            case "nul": Credential(path, "sk-fake\0"); break; // the launch script's shell drops the NUL, so it would match a token without it
            case "url": Credential(path, "https://user:ghp_fake@github.com"); break; // a ~/.git-credentials entry
            case "leading": Credential(path, " sk-fake"); break; // `$(cat ...)` keeps it, so the harness would get a token that is not the one checked
            case "crlf": Credential(path, "sk-fake\r\n"); break; // `$(cat ...)` cuts the newline only
            case "jwt": Credential(path, "a.b/c+d="); break; // a JWT or base64 value: characters the allow-list leaves out
            case "outside": path = "/etc/hostname"; break;
            case "login": path = Credential(Path.Combine(root, ".claude", ".credentials.json")); break;
        }
        foreach (var (field, file) in new[] { ("credential", ClaudeToken), ("spec_credential", ClaudeSpecToken) })
            Assert.Contains($"claude {field} '{path}' {reason}", Refused(Valid.Replace($"\"{field}\": \"{file}\"", $"\"{field}\": \"{path}\"")));
    }

    /// <summary>Sandboxes are bound to ~/.softwarp's package cache and scratch, so no credential may sit in it.</summary>
    [Fact]
    public void ConfigDirectoryInsideSoftwarpsState_IsRefusedByBind()
    {
        var directory = Path.Combine(root, ".softwarp", "softwarp");
        Assert.Contains($"the machine config's directory '{directory}' overlaps {root}/.softwarp, which a sandbox binds",
            Assert.Throws<InvalidOperationException>(() => Composition.Bind(ConfigWithCredentialsIn(directory))).Message);
    }

    /// <summary>Sandboxes are bound to the run store's io directories, so no credential may sit in it, and the run store may
    /// not sit in the config's directory.</summary>
    [Theory]
    [InlineData("softwarp/runs")]
    [InlineData("softwarp")]
    public void RunStoreThatOverlapsTheConfigDirectory_IsRefusedByBind(string directory)
    {
        var runs = Path.Combine(root, directory);
        Assert.Contains($"the machine config's directory '{Secrets}' overlaps {runs}, which a sandbox binds",
            Refused(Valid.Replace("\"/var/softwarp/runs\"", $"\"{runs}\"")));
    }

    /// <summary>The overlap rules compare path strings, so a link would hide an overlap: a sandbox could bind the config's
    /// directory, and every credential in it, through a state directory. The rows link the package cache, the scratch,
    /// ~/.softwarp, and a directory that holds the run store or the slices root.</summary>
    [Theory]
    [InlineData(".softwarp/packages", ".softwarp/packages")]
    [InlineData(".softwarp/gates", ".softwarp/gates")]
    [InlineData(".softwarp", ".softwarp/packages")]
    [InlineData("state", "state/runs")]
    [InlineData("state", "state/slices")]
    public void StateDirectoryThroughALink_IsRefusedByBind(string link, string directory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, link))!);
        File.CreateSymbolicLink(Path.Combine(root, link), Secrets);
        var path = Path.Combine(root, directory);
        var json = Path.GetFileName(directory) switch
        {
            "runs" => Valid.Replace("/var/softwarp/runs", path),
            "slices" => Valid.Replace("\"runs\":", $"\"slices\": \"{path}\", \"runs\":"),
            _ => Valid,
        };
        Assert.Contains($"'{path}' resolves through the link {Path.Combine(root, link)}; softwarp's state directories may not be links", Refused(json));
    }

    /// <summary>A credential must sit in the config directory, so the directory must be softwarp's own: a machine.json in a
    /// login's directory would admit the login beside it.</summary>
    [Theory]
    [InlineData(".claude")]
    [InlineData(".codex")]
    [InlineData(".config")]
    public void ConfigInADirectoryNotNamedSoftwarp_IsRefusedWithItsCredentials(string directory) =>
        Assert.Contains("must sit in a directory named softwarp",
            Assert.Throws<InvalidOperationException>(() => Composition.Bind(ConfigWithCredentialsIn(Path.Combine(root, directory)))).Message);

    /// <summary>Writes the valid config as machine.json in the directory, with its three credentials moved beside it, and returns its path.</summary>
    string ConfigWithCredentialsIn(string directory)
    {
        var json = new[] { ClaudeToken, ClaudeSpecToken, CodexKey }.Aggregate(Valid, (text, file) => text.Replace(file, Credential(Path.Combine(directory, Path.GetFileName(file)))));
        var config = Path.Combine(directory, "machine.json");
        File.WriteAllText(config, json);
        return config;
    }

    /// <summary>A credential file whose content is a random token, so two files differ in content.</summary>
    static string Credential(string path, string? content = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content ?? $"sk-{Guid.NewGuid():N}");
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
        var config = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "softwarp")).FullName, "machine.json");
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
