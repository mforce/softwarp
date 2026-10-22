using System.Security.Cryptography;
using Softwarp.Core;

namespace Softwarp.Bindings;

/// <summary>Two repositories per item. softwarp's trusted bare clone at `slices/owner/repo/trusted.git` alone talks to
/// the remote: it fetches trunk, reads WORKFLOW.md and pushes, with hardened git, explicit URLs and refspecs, and a config
/// it checks against a fixed baseline before each use. No sandbox binds it and no agent is handed it. The implementer's
/// repository at `slices/owner/repo/issue` is a standalone clone of it, with its own objects and no remote. The
/// implementer controls that repository, so the factory reads it only inside the <see cref="Sandbox"/>: it imports
/// commits by fetching into the trusted clone through an upload-pack that runs there.</summary>
/// <param name="remote">The URL of an item's repository: https, or a local path.</param>
/// <param name="timeout">How long one git command may run, on the host or in the sandbox, before it is stopped and the
/// call fails; five minutes when null.</param>
public sealed class GitIsolation(string slices, Func<ItemId, string> remote, Sandbox sandbox, TimeSpan? timeout = null) : IIsolation
{
    const string Baseline = "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = true\n";
    static readonly string[] Hardened = ["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "-c", "protocol.allow=never"];
    /// <summary>Prints `clean`, or `dirty: ` and a path, for the worktree $2 against commit $1, whose objects the
    /// trusted clone's object directory $3 holds. It reads nothing from the implementer's .git: a scratch git directory
    /// in the sandbox's own /tmp, with git's defaults and no system or global config, holds an index read from $1, so no
    /// filter, attributes file, excludes file, worktree setting, replace ref or index bit of the implementer's applies.
    /// Untracked files are listed with no excludes first: an untracked .gitignore or .gitattributes anywhere is dirt.
    /// Only then may the .gitignore files $1 tracks hide build output.</summary>
    const string CleanScript = """
        export GIT_NO_REPLACE_OBJECTS=1 GIT_ATTR_NOSYSTEM=1 GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null
        git init -q --bare /tmp/check.git && echo "$3" > /tmp/check.git/objects/info/alternates || exit 70
        export GIT_DIR=/tmp/check.git GIT_WORK_TREE="$2"
        git config core.bare false && git read-tree "$1" || exit 70
        git ls-files --others -z > /tmp/all || exit 70
        hidden=$(tr '\0' '\n' < /tmp/all | grep -E -m 1 '(^|/)[.]git(ignore|attributes)$')
        if [ -n "$hidden" ]; then echo "dirty: untracked $hidden"; exit 0; fi
        git ls-files --others --exclude-standard -z > /tmp/visible || exit 70
        if [ -s /tmp/visible ]; then echo "dirty: untracked $(tr '\0' '\n' < /tmp/visible | head -n 1)"; exit 0; fi
        git update-index -q --really-refresh > /dev/null; [ $? -le 1 ] || exit 70
        git diff-files --quiet
        case $? in 0) echo clean ;; 1) echo "dirty: changed $(git diff-files --name-only | head -n 1)" ;; *) exit 70 ;; esac
        """;
    const int MaxDirt = 200;

    readonly string slices = Path.GetFullPath(slices);
    // ponytail: one bound for local, sandboxed and forge calls alike; split it if a first fetch of a large repository needs longer.
    readonly TimeSpan timeout = timeout ?? TimeSpan.FromMinutes(5);

    /// <summary>Fetches the remote's default branch into the trusted clone and reads WORKFLOW.md at it.</summary>
    public async Task<TrunkSnapshot> ReadTrunkAsync(ItemId item, CancellationToken ct)
    {
        var (trusted, url) = (await TrustedAsync(item, ct), remote(item));
        var branch = Advertised(await Remote(url, ct, "-C", trusted, "ls-remote", "--symref", url, "HEAD")).Branch
            ?? throw new InvalidOperationException($"{url} names no default branch");
        await Remote(url, ct, "-C", trusted, "fetch", "--quiet", "--no-tags", "--no-write-fetch-head", url, $"+refs/heads/{branch}:{TrunkRef(item)}");
        var trunk = (await Git(ct, "-C", trusted, "rev-parse", "--verify", $"{TrunkRef(item)}^{{commit}}")).Trim();
        return new TrunkSnapshot(new CommitId(trunk), await Git(ct, "-C", trusted, "show", $"{trunk}:WORKFLOW.md"));
    }

    /// <summary>Makes the implementer's repository from the trusted clone, on the run branch at the pinned trunk or, when
    /// the branch was imported before, at that branch. It is built beside its final path and moved in, so a crash leaves
    /// no half-made repository.</summary>
    public async Task<Workspace> EnsureAsync(RunManifest run, CancellationToken ct)
    {
        var (item, worktree) = (run.Item.Id, Worktree(run.Item.Id));
        if (Directory.Exists(worktree)) return new Workspace(worktree);
        var trusted = await TrustedAsync(item, ct);
        var imported = (await TryGit(ct, "-C", trusted, "rev-parse", "--verify", "--quiet", BranchRef(run))).ExitCode == 0;
        var building = $"{worktree}.{RandomNumberGenerator.GetHexString(8, lowercase: true)}";
        await Git(ct, "-c", "init.defaultBranch=softwarp-unborn", "init", "--quiet", building);
        await Local(ct, "-C", building, "fetch", "--quiet", "--no-tags", "--no-write-fetch-head", trusted,
            $"{(imported ? BranchRef(run) : TrunkRef(item))}:{BranchRef(run)}");
        await Git(ct, "-C", building, "checkout", "--quiet", "-B", run.Branch, imported ? BranchRef(run) : run.Trunk.Value);
        Directory.Move(building, worktree);
        return new Workspace(worktree);
    }

    /// <summary>Imports the implementer's HEAD into the trusted clone, so branch, head and ancestry come from the trusted
    /// clone, then checks the tree in the sandbox against that head without reading the implementer's .git.</summary>
    public async Task<HeadObservation?> ObserveAsync(RunManifest run, CancellationToken ct)
    {
        var (item, worktree) = (run.Item.Id, Worktree(run.Item.Id));
        if (!Directory.Exists(worktree)) return null;
        var trusted = await TrustedAsync(item, ct);
        // One advertisement names HEAD's branch and its commit, and the fetch takes exactly that commit, so the two agree.
        var (branch, advertised) = Advertised(await Local(ct, "-C", trusted, "ls-remote", "--symref", UploadPack(worktree),
            Path.Combine(worktree, ".git"), "HEAD"));
        var observed = $"refs/softwarp/observe/{item.Number}";
        var commit = advertised ?? throw new InvalidOperationException($"{worktree} has no commit at HEAD");
        await ImportAsync(trusted, worktree, $"+{commit}:{observed}", ct);
        var head = (await Git(ct, "-C", trusted, "rev-parse", "--verify", $"{observed}^{{commit}}")).Trim();
        var descends = await TryGit(ct, "-C", trusted, "merge-base", "--is-ancestor", run.Trunk.Value, head) switch
        {
            { ExitCode: 0 } => true,
            { ExitCode: 1 } => false,
            var failed => throw Failed(failed, "merge-base"),
        };

        var objects = Path.Combine(trusted, "objects");
        var output = new Captured("the clean check");
        var check = output.Result(await sandbox.RunAsync(worktree, null,
            new CommandSpec("sh", ["-c", CleanScript, "clean", head, worktree, objects]), output, timeout, ct, readOnly: [objects]), timeout);
        var verdict = check.Output.TrimEnd('\n');
        if (check.ExitCode != 0 || verdict != "clean" && !verdict.StartsWith("dirty: ", StringComparison.Ordinal))
            throw new InvalidOperationException($"the clean check could not read {worktree} (exit {check.ExitCode}): {check.Error.Trim()}");
        var dirt = verdict == "clean" ? null : verdict["dirty: ".Length..];
        return new HeadObservation(branch ?? "HEAD", new CommitId(head),
            dirt is null ? null : dirt.Length <= MaxDirt ? dirt : $"{dirt[..MaxDirt]}...", descends);
    }

    public async Task<CommitId?> RemoteHeadAsync(RunManifest run, CancellationToken ct)
    {
        var (trusted, url) = (await TrustedAsync(run.Item.Id, ct), remote(run.Item.Id));
        var line = (await Remote(url, ct, "-C", trusted, "ls-remote", url, BranchRef(run))).Split('\n')
            .FirstOrDefault(l => l.EndsWith($"\t{BranchRef(run)}", StringComparison.Ordinal));
        return line is null ? null : new CommitId(line[..line.IndexOf('\t')]);
    }

    /// <summary>Imports the run branch from the implementer's repository into the trusted clone, checks it is the head the
    /// policy approved and descends from the pinned trunk, then pushes it from the trusted clone. The push names the URL
    /// and `refs/heads/branch:refs/heads/branch` with no `+` and no force, so no config can turn it into another push.
    /// git pushes nothing to a remote already at head, so a retry after a lost reply is a no-op.</summary>
    public async Task<bool> PushAsync(RunManifest run, CommitId head, OperationId operation, CancellationToken ct)
    {
        var (item, url) = (run.Item.Id, remote(run.Item.Id));
        var trusted = await TrustedAsync(item, ct);
        var staging = $"refs/softwarp/import/{item.Number}";
        await ImportAsync(trusted, Worktree(item), $"+{BranchRef(run)}:{staging}", ct);
        var imported = (await Git(ct, "-C", trusted, "rev-parse", "--verify", $"{staging}^{{commit}}")).Trim();
        if (imported != head.Value)
            throw new InvalidOperationException($"the worktree has {run.Branch} at {imported}, not at the approved head {head.Value}");
        if ((await TryGit(ct, "-C", trusted, "merge-base", "--is-ancestor", run.Trunk.Value, head.Value)).ExitCode != 0)
            throw new InvalidOperationException($"{head.Value} does not descend from the pinned trunk {run.Trunk.Value}");
        await Git(ct, "-C", trusted, "update-ref", BranchRef(run), head.Value);

        var push = await Run(Scheme(url), ct, "-C", trusted, "push", "--porcelain", "--no-verify", url, $"{BranchRef(run)}:{BranchRef(run)}");
        if (push.ExitCode == 0) return true;
        // --porcelain marks each ref the remote refused with a leading '!'.
        return push.Output.Split('\n').Any(line => line.StartsWith("!\t", StringComparison.Ordinal)) ? false : throw Failed(push, "push");
    }

    /// <summary>The files at the trunk commit whose name passes the filter, read from the item's trusted clone and never
    /// from the worktree.</summary>
    internal async Task<IReadOnlyList<(string Path, string Text)>> TrunkFilesAsync(ItemId item, CommitId trunk,
        Func<string, bool> wanted, CancellationToken ct)
    {
        var trusted = await TrustedAsync(item, ct);
        var files = new List<(string, string)>();
        foreach (var path in (await Git(ct, "-C", trusted, "ls-tree", "-r", "-z", "--name-only", trunk.Value))
                 .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(path => wanted(Path.GetFileName(path))))
            files.Add((path, await Git(ct, "-C", trusted, "show", $"{trunk.Value}:{path}")));
        return files;
    }

    /// <summary>Fetches from the implementer's repository into the trusted clone, which checks every object it receives.</summary>
    Task<string> ImportAsync(string trusted, string worktree, string refspec, CancellationToken ct) =>
        Local(ct, "-C", trusted, "-c", "transfer.fsckObjects=true", "fetch", "--quiet", "--no-tags", "--no-write-fetch-head",
            UploadPack(worktree), Path.Combine(worktree, ".git"), refspec);

    string UploadPack(string worktree) => $"--upload-pack={sandbox.UploadPack(worktree, timeout)}";

    /// <summary>HEAD's branch, null when HEAD is detached, and its commit, from `ls-remote --symref ... HEAD`.</summary>
    static (string? Branch, string? Head) Advertised(string listing)
    {
        var lines = listing.Split('\n').Where(line => line.EndsWith("\tHEAD", StringComparison.Ordinal)).ToList();
        return (lines.FirstOrDefault(line => line.StartsWith("ref: refs/heads/", StringComparison.Ordinal))?[16..^5],
            lines.FirstOrDefault(line => !line.StartsWith("ref: ", StringComparison.Ordinal))?[..^5]);
    }

    /// <summary>The trusted clone, made atomically on first use and refused once its config leaves the baseline or it
    /// borrows objects or history from elsewhere.</summary>
    async Task<string> TrustedAsync(ItemId item, CancellationToken ct)
    {
        var trusted = Path.Combine(Repo(item), "trusted.git");
        if (!Directory.Exists(trusted))
        {
            var building = $"{trusted}.{RandomNumberGenerator.GetHexString(8, lowercase: true)}";
            await Git(ct, "init", "--quiet", "--bare", building);
            await File.WriteAllTextAsync(Path.Combine(building, "config"), Baseline, ct);
            try
            {
                Directory.Move(building, trusted);
            }
            catch (IOException) when (Directory.Exists(trusted))
            {
                Directory.Delete(building, recursive: true); // another item of the same repository made it first
            }
        }
        if (await File.ReadAllTextAsync(Path.Combine(trusted, "config"), ct) != Baseline
            || File.Exists(Path.Combine(trusted, "objects", "info", "alternates")) || File.Exists(Path.Combine(trusted, "info", "grafts")))
            throw new InvalidOperationException($"{trusted} has a config other than softwarp's baseline, alternates or grafts; inspect it before softwarp uses it again");
        return trusted;
    }

    string Repo(ItemId item) => RunStore.IsSegment(item.Owner) && RunStore.IsSegment(item.Repo) && item.Number > 0
        ? Path.Combine(slices, item.Owner, item.Repo)
        : throw new ArgumentException($"'{item.Owner}/{item.Repo}#{item.Number}' does not name an issue", nameof(item));

    string Worktree(ItemId item) => Path.Combine(Repo(item), item.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    static string TrunkRef(ItemId item) => $"refs/softwarp/trunk/{item.Number}";
    static string BranchRef(RunManifest run) => $"refs/heads/{run.Branch}";

    /// <summary>The only transport a URL may use: https for the forge, file for a local path.</summary>
    static string Scheme(string url) => url.StartsWith('/') ? "file"
        : Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "file" ? uri.Scheme
        : throw new ArgumentException($"softwarp fetches and pushes only over https or a local path, not {url}", nameof(url));

    async Task<string> Remote(string url, CancellationToken ct, params string[] arguments) =>
        Checked(await Run(Scheme(url), ct, arguments), arguments);

    async Task<string> Local(CancellationToken ct, params string[] arguments) => Checked(await Run("file", ct, arguments), arguments);
    async Task<string> Git(CancellationToken ct, params string[] arguments) => Checked(await TryGit(ct, arguments), arguments);
    Task<ToolResult> TryGit(CancellationToken ct, params string[] arguments) => Tool.ReadAsync("git", [.. Hardened, .. arguments], timeout, ct);

    Task<ToolResult> Run(string protocol, CancellationToken ct, params string[] arguments) =>
        Tool.ReadAsync("git", [.. Hardened, "-c", $"protocol.{protocol}.allow=always", .. arguments], timeout, ct);

    static string Checked(ToolResult result, string[] arguments) =>
        result.ExitCode == 0 ? result.Output : throw Failed(result, string.Join(' ', arguments));

    static InvalidOperationException Failed(ToolResult result, string command) =>
        new($"git {command} exited {result.ExitCode}: {result.Error.Trim()}");
}
