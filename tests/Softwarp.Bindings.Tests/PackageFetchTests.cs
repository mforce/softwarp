using Softwarp.Core;

namespace Softwarp.Bindings.Tests;

/// <summary>The fetch's one package source is a local feed made of packages already in the maintainer's cache, so no test
/// needs the network. The trunk is a local remote whose WORKFLOW.md commit may carry a lock file.</summary>
public sealed class PackageFetchTests : IAsyncLifetime
{
    readonly GitRemote git = new();
    static readonly CancellationToken None = CancellationToken.None;
    RunManifest run = null!;
    Workspace workspace = null!;

    string Root => git.Root;
    string Feed => Path.Combine(Root, "feed");
    string Home => Path.Combine(Root, "home");
    Sandbox Sandbox => new(GateLimits.Default, Home);
    string Cache => Sandbox.Packages;
    string Scratch => Path.Combine(Root, "scratch");
    string Details => Path.Combine(Root, "Build.fetch.log");
    string Admissions => Path.Combine(Root, "Build.packages");
    string[] Recorded => File.Exists(Admissions) ? File.ReadAllLines(Admissions) : [];

    const string Abstractions = "xunit.abstractions", Sqlite = "sqlitepclraw.bundle_e_sqlite3";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Feed);
        foreach (var (id, version) in new[] { (Abstractions, "2.0.3"), (Sqlite, "2.1.12"), (Sqlite, "2.1.13") })
            File.Copy(Path.Combine(GitRemote.Home, ".nuget", "packages", id, version, $"{id}.{version}.nupkg"),
                Path.Combine(Feed, $"{id}.{version}.nupkg"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        git.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Puts the lock file on trunk, then makes the worktree from it.</summary>
    async Task TrunkAsync(params (string Id, string Version)[] packages)
    {
        if (packages.Length > 0) git.Commit("packages.lock.json", LockFile(packages));
        (run, workspace) = await git.WorktreeAsync();
    }

    PackageFetch Fetch(int cap = 20, string dotnet = "/usr/bin/dotnet") => new(git.Isolation, Sandbox, Scratch, cap, Feed, dotnet);

    GateRequest Request(string log, params string[] admitted) =>
        new(workspace, run.Item.Id, run.Trunk, GateKind.Build, new CommandSpec("true", []), TestReport.Trx, [.. admitted], log, Admissions);

    Task<string?> EnsureAsync(PackageFetch? fetch = null, params string[] admitted) =>
        (fetch ?? Fetch()).EnsureAsync(Request(Path.Combine(Root, "Build.log"), admitted), Details, TimeSpan.FromMinutes(5), None);

    static string LockFile(params (string Id, string Version)[] packages) => $$"""
        { "version": 1, "dependencies": { "net10.0": {
          {{string.Join(",\n", packages.Select(p => $$"""
            "{{System.Text.Json.JsonEncodedText.Encode(p.Id)}}": { "type": "Direct", "requested": "[{{p.Version}}, )", "resolved": "{{System.Text.Json.JsonEncodedText.Encode(p.Version)}}", "contentHash": "x" }
            """))}}{{(packages.Length > 0 ? "," : "")}}
          "Repo.Library": { "type": "Project" } } } }
        """;

    void Write(string relative, string text)
    {
        var path = Path.Combine(workspace.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    bool Cached(string id, string version) => File.Exists(Path.Combine(Cache, id, version, ".nupkg.metadata"));

    [Fact]
    public async Task Wanted_ReadsEveryLockFileAndVersionedSdk_SkippingProjectsHiddenDirectoriesAndLinks()
    {
        await TrunkAsync();
        Write("src/A/packages.lock.json", LockFile((Abstractions, "2.0.3"), ("SQLitePCLRaw.bundle_e_sqlite3", "2.1.12")));
        Write("tests/B/packages.lock.json", LockFile((Sqlite, "2.1.13")));
        Write("src/Host/Host.csproj", """<Project Sdk="Microsoft.NET.Sdk"><Sdk Name="Ignored" /></Project>""");
        Write("src/App/App.csproj", """<Project Sdk = "Aspire.AppHost.Sdk/13.5.0; Microsoft.NET.Sdk"></Project>""");
        Write(".hidden/packages.lock.json", LockFile(("Hidden.Package", "1.0.0")));
        Directory.CreateDirectory(Path.Combine(Root, "outside"));
        File.WriteAllText(Path.Combine(Root, "outside", "packages.lock.json"), LockFile(("Linked.Package", "1.0.0")));
        Directory.CreateSymbolicLink(Path.Combine(workspace.Path, "linked"), Path.Combine(Root, "outside"));

        Assert.Equal(new[] { ("aspire.apphost.sdk", "13.5.0"), (Sqlite, "2.1.12"), (Sqlite, "2.1.13"), (Abstractions, "2.0.3") },
            PackageFetch.Locked(PackageFetch.WorktreeFiles(workspace.Path)).Packages.Order());
    }

    [Theory]
    [InlineData("A\" /><Exec Command=\"x", "1.0.0")]
    [InlineData("A B", "1.0.0")]
    [InlineData("Pаckage", "1.0.0")] // a Cyrillic 'а'
    [InlineData("A", "1.0.0]; [2")]
    [InlineData("A", "1..0")]
    [InlineData("A", "1")]
    [InlineData("A", "1.0.0-beta_1")]
    public async Task IdOrVersionOutsideNuGetsGrammar_IsRefused(string id, string version)
    {
        await TrunkAsync();
        Write("packages.lock.json", LockFile((id, version)));
        Assert.Contains("is not a NuGet package id and version", (await Assert.ThrowsAsync<FetchRefusedException>(() => EnsureAsync())).Message);
        Assert.False(Directory.Exists(Cache));
    }

    static string RuntimeLockFile(string target) => $$"""
        { "version": 1, "dependencies": { "net10.0": { "{{Abstractions}}": { "type": "Direct", "resolved": "2.0.3" } },
          "{{System.Text.Json.JsonEncodedText.Encode(target)}}": {} } }
        """;

    [Fact]
    public void Runtimes_AreTheLockFilesFrameworkAndRuntimeTargets()
    {
        string[] files = [RuntimeLockFile("net10.0/linux-x64"), RuntimeLockFile("netstandard2.0/linux-musl-arm64")];
        Assert.Equal(new[] { "net10.0/linux-x64", "netstandard2.0/linux-musl-arm64" },
            PackageFetch.Locked(files.Select(text => ("packages.lock.json", text))).Runtimes.Order());
    }

    [Theory]
    [InlineData("net10.0/linux-x64-c2VjcmV0")]
    [InlineData("net10.0/Linux-X64")]
    [InlineData("net10.0/linux")]
    [InlineData("anything/linux-x64")]
    [InlineData("net10.0/linux-x64/extra")]
    public async Task RuntimeTargetOutsideTheGrammar_IsRefused(string target)
    {
        await TrunkAsync();
        Write("packages.lock.json", RuntimeLockFile(target));
        Assert.Contains("is not a framework and runtime identifier", (await Assert.ThrowsAsync<FetchRefusedException>(() => EnsureAsync())).Message);
    }

    [Fact]
    public async Task RuntimeTargetThePinnedTrunkDoesNotRestoreFor_IsRefusedBeforeAnythingIsRecorded()
    {
        await TrunkAsync((Abstractions, "2.0.3"));
        Write("packages.lock.json", RuntimeLockFile("net10.0/linux-arm64"));
        Assert.Contains("net10.0/linux-arm64, which the pinned trunk's lock files do not",
            (await Assert.ThrowsAsync<FetchRefusedException>(() => EnsureAsync())).Message);
        Assert.Empty(Recorded);
        Assert.False(Directory.Exists(Cache));
    }

    [Theory]
    [InlineData("""{ "version": 1, "dependencies": { "net10.0": { "A": { "type": "Direct" } } } }""")]
    [InlineData("""{ "version": 1, "dependencies": [] }""")]
    [InlineData("""not json""")]
    public async Task MalformedLockFile_IsRefused(string lockFile)
    {
        await TrunkAsync();
        Write("packages.lock.json", lockFile);
        Assert.Contains("packages.lock.json", (await Assert.ThrowsAsync<FetchRefusedException>(() => EnsureAsync())).Message);
    }

    [Fact]
    public async Task NewPackages_AreCountedAgainstThePinnedTrunk_NotTheTrunkSoftwarpReadLast()
    {
        await TrunkAsync((Abstractions, "2.0.3"));
        // Trunk moves on and softwarp reads it again, for another run of the same repository; this run stays pinned.
        git.Commit("packages.lock.json", LockFile((Abstractions, "2.0.3"), (Sqlite, "2.1.13")));
        await git.Isolation.ReadTrunkAsync(GitRemote.Item, None);
        Write("packages.lock.json", LockFile((Abstractions, "2.0.3"), (Sqlite, "2.1.13")));

        Assert.Contains("over the cap of 0", (await Assert.ThrowsAsync<FetchRefusedException>(() => EnsureAsync(Fetch(cap: 0)))).Message);
        Assert.False(Cached(Sqlite, "2.1.13"));
    }

    [Fact]
    public async Task TrunkPackages_AreFetchedAndAreNotNew()
    {
        await TrunkAsync((Abstractions, "2.0.3"));
        await EnsureAsync(Fetch(cap: 0));
        Assert.Empty(Recorded);
        Assert.True(Cached(Abstractions, "2.0.3"));
    }

    [Fact]
    public async Task NewPackagesUnderTheCap_AreFetchedAndRecorded_AndACachedRunNeedsNoRestore()
    {
        await TrunkAsync((Abstractions, "2.0.3"));
        Write("packages.lock.json", LockFile((Abstractions, "2.0.3"), (Sqlite, "2.1.12"), (Sqlite, "2.1.13")));

        await EnsureAsync(Fetch(cap: 2));
        Assert.Equal([$"{Sqlite}@2.1.12", $"{Sqlite}@2.1.13"], Recorded);
        Assert.True(Cached(Sqlite, "2.1.12") && Cached(Sqlite, "2.1.13"));
        Assert.StartsWith("the NuGet cache holds every package", await EnsureAsync(Fetch(cap: 2, dotnet: "/nonexistent/dotnet")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task NewPackagesOverTheCapForTheRun_FailClosedFetchingNothing(int alreadyAdmitted)
    {
        await TrunkAsync((Abstractions, "2.0.3"));
        Write("packages.lock.json", LockFile((Sqlite, "2.1.12"), (Sqlite, "2.1.13")));
        string[] admitted = alreadyAdmitted == 1 ? ["earlier.package@1.0.0"] : [];

        // Two new packages: over a cap of 1 alone, or over a cap of 2 with the one an earlier gate let in.
        var refused = await Assert.ThrowsAsync<FetchRefusedException>(() => EnsureAsync(Fetch(cap: 1 + alreadyAdmitted), admitted));
        Assert.Contains("over the cap", refused.Message);
        Assert.False(Cached(Sqlite, "2.1.12") || Cached(Sqlite, "2.1.13"));
        Assert.Empty(Recorded);
    }

    [Fact]
    public async Task ARepositoryNuGetConfigWithAPrivateFeed_IsNeverRead()
    {
        await TrunkAsync();
        var privateFeed = Path.Combine(Root, "private-feed");
        Directory.CreateDirectory(privateFeed);
        File.Move(Path.Combine(Feed, $"{Sqlite}.2.1.12.nupkg"), Path.Combine(privateFeed, $"{Sqlite}.2.1.12.nupkg"));
        Write("nuget.config", $"""
            <configuration><packageSources><add key="private" value="{privateFeed}" /></packageSources>
            <packageSourceCredentials><private><add key="Username" value="me" /><add key="ClearTextPassword" value="secret" /></private></packageSourceCredentials>
            </configuration>
            """);
        Write("packages.lock.json", LockFile((Sqlite, "2.1.12")));

        await Assert.ThrowsAsync<FetchRefusedException>(() => EnsureAsync());
        Assert.False(Cached(Sqlite, "2.1.12"));
    }

    [Fact]
    public async Task ReadsNoBuildFileOrResponseFileAboveItsProject()
    {
        await TrunkAsync((Abstractions, "2.0.3"));
        var marker = Path.Combine(Root, "ran");
        Directory.CreateDirectory(Scratch);
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
            File.WriteAllText(Path.Combine(Scratch, name), $"""
                <Project><Target Name="Evil" BeforeTargets="Restore;_GenerateRestoreGraph;CollectPackageDownloads">
                <Exec Command="touch {marker}" /></Target></Project>
                """);
        File.WriteAllText(Path.Combine(Scratch, "Directory.Build.rsp"), "-bogusSwitch");

        await EnsureAsync();
        Assert.True(Cached(Abstractions, "2.0.3"));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task WithNoLockFile_DoesNothing()
    {
        await TrunkAsync();
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        Assert.Null(await EnsureAsync(Fetch(dotnet: "/nonexistent/dotnet")));
        Assert.Empty(Recorded);
    }

    GateRunner Runner(int cap = 20) => new(Sandbox, Scratch, Fetch(cap));

    async Task<(GateRun Run, string Log)> GateAsync(GateRunner runner, CommandSpec command, CancellationToken? ct = null)
    {
        var log = Path.Combine(Root, "Build.log");
        new FileStream(log, FileMode.Create).Dispose();
        var gate = await runner.RunAsync(Request(log) with { Command = command }, ct ?? None);
        return (gate, File.ReadAllText(log));
    }

    [Fact]
    public async Task Gate_RestoresFromWhatTheFetchAddedBeforeIt_ReadOnly_AndRecordsWhatIsNew()
    {
        await TrunkAsync((Abstractions, "2.0.3"));
        Write("packages.lock.json", LockFile((Abstractions, "2.0.3"), (Sqlite, "2.1.13")));

        var (gate, log) = await GateAsync(Runner(), new CommandSpec("bash", ["-c",
            $"ls \"$HOME/.nuget/packages/{Abstractions}/2.0.3\" \"$HOME/.nuget/packages/{Sqlite}/2.1.13\" && ! touch \"$HOME/.nuget/packages/poisoned\""]));

        Assert.True(gate.ExitCode == 0, log + (File.Exists(Details) ? File.ReadAllText(Details) : ""));
        Assert.Equal([$"{Sqlite}@2.1.13"], Recorded);
        Assert.Contains("fetched 2 locked packages", log);
        Assert.Contains($"{Abstractions}.2.0.3.nupkg", log);
    }

    [Fact]
    public async Task GateWhoseFetchFails_IsUnavailable_AndRestoreOutputNeverReachesTheGateLog()
    {
        await TrunkAsync();
        Write("packages.lock.json", LockFile(("Not.In.The.Feed", "1.0.0")));

        var (gate, log) = await GateAsync(Runner(), new CommandSpec("true", []));
        Assert.Equal((75, (int?)null), (gate.ExitCode, gate.TestsExecuted));
        Assert.Contains("could not get not.in.the.feed@1.0.0", log);
        Assert.DoesNotContain("NU1101", log);
        Assert.DoesNotContain(Feed, log);
        Assert.Contains("NU1101", File.ReadAllText(Details));
    }

    [Fact]
    public async Task FetchThatFailsPartway_StillRecordsWhatItAdmitted()
    {
        await TrunkAsync();
        Write("packages.lock.json", LockFile((Sqlite, "2.1.12"), ("Not.In.The.Feed", "1.0.0")));

        await Assert.ThrowsAsync<FetchRefusedException>(() => EnsureAsync());
        Assert.Equal([$"not.in.the.feed@1.0.0", $"{Sqlite}@2.1.12"], Recorded);
    }

    [Fact]
    public async Task GateCutOffAfterTheFetch_StillLeavesTheRecord()
    {
        await TrunkAsync();
        Write("packages.lock.json", LockFile((Sqlite, "2.1.13")));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GateAsync(Runner(), new CommandSpec("sleep", ["60"]), cancel.Token));
        Assert.Equal([$"{Sqlite}@2.1.13"], Recorded);
        Assert.True(Cached(Sqlite, "2.1.13"));
    }

    [Fact]
    public async Task GateOverTheCap_IsUnavailable()
    {
        await TrunkAsync();
        Write("packages.lock.json", LockFile((Abstractions, "2.0.3")));

        var (gate, log) = await GateAsync(Runner(cap: 0), new CommandSpec("true", []));
        Assert.Equal(75, gate.ExitCode);
        Assert.Contains("over the cap of 0", log);
        Assert.False(Cached(Abstractions, "2.0.3"));
    }
}
