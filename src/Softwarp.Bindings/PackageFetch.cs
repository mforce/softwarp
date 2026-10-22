using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Softwarp.Core;

namespace Softwarp.Bindings;

/// <summary>The fetch refused or failed for a reason whose message is written for the gate log, which the implementer
/// reads: it names packages and lock files, never raw restore output or a host path outside the worktree.</summary>
public sealed class FetchRefusedException(string message) : Exception(message);

/// <summary>Fills softwarp's package cache, which gates see read-only, before a gate and outside the sandbox, so gates
/// can restore with the network off. It reads packages.lock.json files and project `Sdk="Name/Version"` references as
/// data, and restores what the cache lacks through a synthetic project of PackageDownload items in a runner-owned
/// directory, so no repository code runs while the network is on. The restore reads only a NuGet.Config the fetch
/// writes itself, with one source and no credentials, never the maintainer's or the repository's. The implementer
/// chooses the worktree's lock files, so packages the pinned trunk's lock files do not name are capped per run and
/// reported.</summary>
/// <param name="sandbox">The sandbox gates run in. The fetch fills its <see cref="Sandbox.Packages"/>, the one cache the
/// sandbox binds.</param>
/// <param name="scratch">A directory only the runner writes.</param>
/// <param name="source">The one package source the restore may use.</param>
public sealed partial class PackageFetch(GitIsolation git, Sandbox sandbox, string scratch, int maxNewPackages,
    string source = PackageFetch.NuGetOrg, string dotnet = "/usr/bin/dotnet")
{
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";
    const int MaxFileBytes = 16 << 20, MaxNamed = 20;
    const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Restores what the cache lacks for the request's worktree and returns a line for the gate log. Before
    /// anything is fetched it records the packages the request's pinned trunk does not name in its PackagesFile, flushed
    /// to disk, so a failure or a crash after that point still leaves the record; the request's Admitted packages, from
    /// earlier gate runs of the run, count toward the cap. Its messages are safe for the gate log, which the implementer
    /// reads: raw restore output goes only to <paramref name="details"/>, a runner-only file.</summary>
    /// <param name="timeout">How long the restore may run.</param>
    /// <exception cref="FetchRefusedException">A lock file or project is unreadable or names something that is not a
    /// NuGet id and version, the run would pass its cap of new packages, or the restore failed.</exception>
    public async Task<string?> EnsureAsync(GateRequest request, string details, TimeSpan timeout, CancellationToken ct)
    {
        var (wanted, runtimes) = Locked(WorktreeFiles(request.Workspace.Path));
        if (wanted.Count == 0) return null;
        var (atTrunk, trunkRuntimes) = Locked(await git.TrunkFilesAsync(request.Item, request.Trunk, IsPackageFile, ct));
        if (runtimes.Except(trunkRuntimes).FirstOrDefault() is { } unknown)
            throw new FetchRefusedException($"the lock files restore for {unknown}, which the pinned trunk's lock files do not");
        var fresh = wanted.Except(atTrunk).Select(Name).Order(StringComparer.Ordinal).ToList();
        var run = request.Admitted.Union(fresh, StringComparer.OrdinalIgnoreCase).ToList();
        if (run.Count > maxNewPackages)
            throw new FetchRefusedException(
                $"this run's lock files name {run.Count} packages its pinned trunk's do not, over the cap of {maxNewPackages}: {List(run)}");
        if (fresh.Count > 0)
            await using (var record = new FileStream(request.PackagesFile, FileMode.Create, FileAccess.Write))
            {
                await record.WriteAsync(System.Text.Encoding.UTF8.GetBytes(string.Concat(fresh.Select(name => name + "\n"))), ct);
                record.Flush(flushToDisk: true);
            }

        var missing = wanted.Where(package => !Cached(package)).ToList();
        // Runtime packs for a runtime identifier are downloads the SDK adds itself, and lock files never list them, so a
        // repository that restores for one always gets the synthetic restore, a no-op once the cache holds them.
        if (missing.Count == 0 && runtimes.Count == 0) return $"the NuGet cache holds every package the lock files name ({wanted.Count})";

        Directory.CreateDirectory(scratch, Private);
        var directory = Directory.CreateDirectory(Path.Combine(scratch, $"fetch-{RandomNumberGenerator.GetHexString(16, lowercase: true)}"), Private).FullName;
        try
        {
            var (project, config) = (Path.Combine(directory, "fetch.csproj"), Path.Combine(directory, "NuGet.Config"));
            new XElement("configuration",
                    new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "softwarp"), new XAttribute("value", source))),
                    new XElement("fallbackPackageFolders", new XElement("clear")))
                .Save(config);
            // Restoring for the same frameworks and runtime identifiers, with PublishAot when the repository locks the
            // ILCompiler, makes the SDK add the runtime packs the repository's own restore will want.
            new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                    new XElement("PropertyGroup",
                        new XElement("TargetFrameworks", runtimes.Count == 0
                            ? $"net{Environment.Version.Major}.0"
                            : string.Join(';', runtimes.Select(runtime => runtime.Split('/')[0]).Distinct())),
                        new XElement("RuntimeIdentifiers", string.Join(';', runtimes.Select(runtime => runtime.Split('/')[1]).Distinct())),
                        new XElement("PublishAot", wanted.Any(package => package.Id == "microsoft.dotnet.ilcompiler"))),
                    new XElement("ItemGroup", missing.GroupBy(package => package.Id).Select(id => new XElement("PackageDownload",
                        new XAttribute("Include", id.Key), new XAttribute("Version", string.Join(';', id.Select(package => $"[{package.Version}]")))))))
                .Save(project);
            // The directory is the runner's, but MSBuild would still import Directory.Build.* files and a response file
            // from any parent directory, and attach to build nodes another process left running; it does none of that.
            var restore = await Tool.ReadAsync(dotnet,
            [
                "restore", project, "--packages", sandbox.Packages, "--configfile", config, "-noAutoResponse", "--disable-build-servers",
                "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false",
                "-p:ImportDirectoryPackagesProps=false", "-p:NuGetAudit=false",
            ], timeout, ct, directory);
            await File.AppendAllTextAsync(details, $"$ dotnet restore (exit {restore.ExitCode})\n{restore.Output}{restore.Error}\n", ct);
            var failed = missing.Where(package => !Cached(package)).Select(Name).ToList();
            return restore.ExitCode == 0 && failed.Count == 0
                ? $"fetched {missing.Count} locked packages into the NuGet cache" + (runtimes.Count > 0 ? $" and checked the runtime packs for {string.Join(", ", runtimes)}" : "")
                : throw new FetchRefusedException($"the trusted fetch could not get " +
                    $"{(failed.Count > 0 ? List(failed) : $"the runtime packs for {string.Join(", ", runtimes)}")} from its one package source; private feeds are not supported");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Lock files and projects in the worktree, read without following a link and skipping hidden directories
    /// (.git among them).</summary>
    internal static IEnumerable<(string Path, string Text)> WorktreeFiles(string worktree) =>
        Directory.EnumerateFiles(worktree, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden, IgnoreInaccessible = true,
            })
            .Where(file => IsPackageFile(Path.GetFileName(file)))
            .Select(file => (Path.GetRelativePath(worktree, file), Read(worktree, file)));

    static string Read(string worktree, string file)
    {
        try
        {
            return System.Text.Encoding.UTF8.GetString(UntrustedFile.Read(file, MaxFileBytes));
        }
        catch (InvalidDataException)
        {
            throw new FetchRefusedException($"softwarp will not fetch packages for {Path.GetRelativePath(worktree, file)}: it is a link, empty, unreadable or over {MaxFileBytes} bytes");
        }
    }

    static bool IsPackageFile(string name) => name == "packages.lock.json" || Path.GetExtension(name) is ".csproj" or ".fsproj" or ".vbproj";

    /// <summary>Every package the files lock, ids and versions lowercased as the cache stores them, each checked against
    /// NuGet's grammar before it can reach the synthetic project; and every `framework/runtime-identifier` target the lock
    /// files restore for, checked against the grammar of both, since a runtime identifier becomes part of a package id the
    /// SDK downloads.</summary>
    internal static (HashSet<(string Id, string Version)> Packages, HashSet<string> Runtimes) Locked(
        IEnumerable<(string Path, string Text)> files)
    {
        var (wanted, runtimes) = (new HashSet<(string, string)>(), new HashSet<string>(StringComparer.Ordinal));
        foreach (var (path, text) in files)
        {
            void Add(string id, string version)
            {
                if (id.Length > 100 || !Id().IsMatch(id) || !Version().IsMatch(version))
                    throw new InvalidDataException($"'{Cut(id)}' '{Cut(version)}' is not a NuGet package id and version");
                wanted.Add((id.ToLowerInvariant(), version.ToLowerInvariant()));
            }

            try
            {
                if (Path.GetFileName(path) == "packages.lock.json")
                {
                    using var lockFile = JsonDocument.Parse(text);
                    foreach (var target in lockFile.RootElement.GetProperty("dependencies").EnumerateObject())
                    {
                        if (target.Name.Contains('/'))
                            runtimes.Add(RuntimeTarget().IsMatch(target.Name)
                                ? target.Name
                                : throw new InvalidDataException($"'{Cut(target.Name)}' is not a framework and runtime identifier"));
                        foreach (var package in target.Value.EnumerateObject())
                            if (package.Value.GetProperty("type").GetString() != "Project")
                                Add(package.Name, package.Value.GetProperty("resolved").GetString() ?? "");
                    }
                }
                else
                {
                    foreach (Match sdk in SdkAttribute().Matches(text))
                    foreach (var reference in sdk.Groups[1].Value.Split(';', StringSplitOptions.TrimEntries))
                        if (reference.Split('/') is [var id, var version]) Add(id, version);
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
            {
                throw new FetchRefusedException($"softwarp will not fetch packages for {path}: {e.Message}");
            }
        }
        return (wanted, runtimes);
    }

    /// <summary>NuGet marks a package complete in the global packages folder with this file.</summary>
    bool Cached((string Id, string Version) package) => File.Exists(Path.Combine(sandbox.Packages, package.Id, package.Version, ".nupkg.metadata"));

    static string Name((string Id, string Version) package) => $"{package.Id}@{package.Version}";

    static string List(IEnumerable<string> names)
    {
        var all = names.ToList();
        return string.Join(", ", all.Take(MaxNamed)) + (all.Count > MaxNamed ? $" and {all.Count - MaxNamed} more" : "");
    }

    static string Cut(string text) => text.Length <= 60 ? text : $"{text[..60]}...";

    /// <summary>NuGet's package id grammar, in ASCII.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_]+([.-][A-Za-z0-9_]+)*$")]
    private static partial Regex Id();

    /// <summary>NuGet's version grammar: two to four numeric parts, then optional SemVer 2 release and build labels.</summary>
    [GeneratedRegex(@"^[0-9]+(\.[0-9]+){1,3}(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex Version();

    /// <summary>A target framework moniker such as net10.0, then a runtime identifier such as linux-x64.</summary>
    [GeneratedRegex(@"^net[a-z]*[0-9][0-9.]*(-[a-z0-9.]+)?/[a-z]+(-[a-z0-9]+)+$")]
    private static partial Regex RuntimeTarget();

    [GeneratedRegex(@"\bSdk\s*=\s*""([^""]*)""")]
    private static partial Regex SdkAttribute();
}
