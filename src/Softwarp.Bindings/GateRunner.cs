using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Softwarp.Core;

namespace Softwarp.Bindings;

/// <summary>Runs each gate command in the <see cref="Sandbox"/> with the worktree writable and a fresh report directory,
/// reading its output through pipes into a capped log. First the trusted fetch fills the package cache, since the
/// sandbox has no network. A sandbox that cannot start, or a fetch that fails, gives exit 75.</summary>
/// <param name="scratch">A directory only the runner writes, for the report directories.</param>
public sealed class GateRunner(Sandbox sandbox, string scratch, PackageFetch? fetch = null) : IGates
{
    const int Unavailable = 75;
    const int MaxReportBytes = 64 << 20;
    const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public async Task<GateRun> RunAsync(GateRequest request, CancellationToken ct)
    {
        var (workspace, kind, command, report, logFile) = (request.Workspace, request.Kind, request.Command, request.Report, request.LogFile);
        await using var file = new FileStream(logFile, FileMode.Truncate, FileAccess.Write);
        var log = new CappedLog(file, sandbox.Limits.LogCapBytes);
        Directory.CreateDirectory(scratch, Private);
        var output = Directory.CreateDirectory(Path.Combine(scratch, RandomNumberGenerator.GetHexString(16, lowercase: true)), Private).FullName;
        try
        {
            log.Note($"$ {command.Executable} {string.Join(' ', command.Arguments)}");
            var timeout = sandbox.Limits.Timeout(kind);
            var profile = new Profile(workspace.Path, new WorktreeMount.Gate(output), command);
            if ((await sandbox.ProblemAsync(profile, ct) ?? await FetchProblemAsync(request, log, timeout, ct)) is { } problem)
            {
                log.Note(problem);
                return new GateRun(Unavailable, null);
            }
            // stdout and stderr reach the log through pipes, so the gate never holds a descriptor for it. bwrap's pid
            // namespace takes every descendant with it at the timeout, setsid daemons included, so both pipes close.
            if (await sandbox.RunAsync(profile, log, timeout, ct) is { } code)
                return new GateRun(code, kind == GateKind.TargetedTests ? Count(output, report) : null);
            log.Note($"the gate ran past its {timeout} timeout and was stopped");
            return new GateRun(GateReceipt.TimedOut, null);
        }
        finally
        {
            log.Finish();
            try
            {
                File.SetUnixFileMode(output, Private); // a gate may have locked its own report directory
                Directory.Delete(output, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // ponytail: a gate can chmod a report subdirectory so it stays behind under scratch; it holds no secret.
            }
        }
    }

    /// <summary>Raw restore output goes to a runner-only file beside the log, among the factory's records; the gate log,
    /// which the implementer reads, gets only the fetch's own line.</summary>
    async Task<string?> FetchProblemAsync(GateRequest request, CappedLog log, TimeSpan timeout, CancellationToken ct)
    {
        if (fetch is null) return null;
        var details = Path.ChangeExtension(request.LogFile, ".fetch.log");
        try
        {
            if (await fetch.EnsureAsync(request, details, timeout, ct) is { } note)
                log.Note(note);
            return null;
        }
        catch (FetchRefusedException e)
        {
            return e.Message;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException)
        {
            await File.AppendAllTextAsync(details, $"{e}\n", CancellationToken.None);
            return "the trusted package fetch failed; the details are in the runner's own log";
        }
    }

    /// <summary>Tests executed, summed over the reports the gate wrote at the top of its report directory; null when there
    /// is none or one cannot be read, so the verdict fails. TRX: Counters/@executed. JUnit: every testcase, wherever it
    /// sits, without a skipped child.</summary>
    /// <remarks>Test code can write the report too, so the count catches honest mistakes (a filter that matched nothing),
    /// not a hostile implementer.</remarks>
    internal static int? Count(string directory, TestReport report)
    {
        try
        {
            int? total = null;
            foreach (var file in Directory.EnumerateFiles(directory, report == TestReport.Trx ? "*.trx" : "*.xml"))
            {
                using var reader = XmlReader.Create(new MemoryStream(UntrustedFile.Read(file, MaxReportBytes)),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                var root = XDocument.Load(reader).Root!;
                int? executed = report == TestReport.Trx
                    ? int.TryParse(root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters")?.Attribute("executed")?.Value,
                        NumberStyles.None, CultureInfo.InvariantCulture, out var counted) ? counted : null
                    : root.DescendantsAndSelf().Count(e => e.Name.LocalName == "testcase"
                        && !e.Elements().Any(child => child.Name.LocalName == "skipped"));
                if (executed is null) return null;
                total = (total ?? 0) + executed;
            }
            return total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>A gate log that never exceeds its cap: the output up to the head budget, then a marker naming how many
/// bytes were cut, then the last tail bytes, which the repair brief quotes.</summary>
sealed class CappedLog(Stream file, long cap) : IOutputSink
{
    const int MarkerReserve = 80;
    readonly long tailBytes = Math.Min(cap / 4, 1 << 20);
    readonly Queue<byte[]> tail = new();
    long head, tailLength, dropped;

    long HeadCap => cap - tailBytes - MarkerReserve;

    public void Write(ReadOnlySpan<byte> data, bool error = false)
    {
        lock (tail)
        {
            var toHead = (int)Math.Min(data.Length, Math.Max(0, HeadCap - head));
            file.Write(data[..toHead]);
            head += toHead;
            if (toHead == data.Length) return;
            tail.Enqueue(data[toHead..].ToArray());
            tailLength += data.Length - toHead;
            while (tailLength - tail.Peek().Length >= tailBytes)
            {
                dropped += tail.Peek().Length;
                tailLength -= tail.Dequeue().Length;
            }
        }
    }

    public void Note(string text) => Write(Encoding.UTF8.GetBytes($"[softwarp] {text}\n"));

    public void Finish()
    {
        lock (tail)
        {
            var rest = tail.SelectMany(chunk => chunk).ToArray();
            var keep = (int)Math.Min(rest.Length, tailBytes);
            dropped += rest.Length - keep;
            if (dropped > 0) file.Write(Encoding.UTF8.GetBytes($"\n[softwarp: {dropped} bytes cut here]\n"));
            file.Write(rest.AsSpan(rest.Length - keep));
            file.Flush();
        }
    }
}
