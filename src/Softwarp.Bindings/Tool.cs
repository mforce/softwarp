using System.Diagnostics;
using System.Text;

namespace Softwarp.Bindings;

/// <summary>What a trusted tool printed and how it exited.</summary>
public sealed record ToolResult(int ExitCode, string Output, string Error);

/// <summary>Where a child's output goes, a chunk at a time, from the stdout and stderr readers at once. A sink stops the
/// child by throwing <see cref="InvalidOperationException"/>.</summary>
interface IOutputSink
{
    void Write(ReadOnlySpan<byte> data, bool error);
}

/// <summary>Starts every child process softwarp runs: as argv, never through a shell, from an environment the caller
/// spells out in full, with stdin closed and both pipes read into a sink. It owns the timeout and kills the whole process
/// tree at it.</summary>
static class Tool
{
    public const int MaxOutputBytes = 4 << 20;

    /// <summary>The whole environment of a trusted tool (git, dotnet). The caller's own may come from an agent and holds
    /// session tokens, and a `DOTNET_STARTUP_HOOKS`, `MSBUILD*` or `GIT_*` variable would change what a trusted tool runs,
    /// so nothing is taken from it but the home. git reads no system or global config and no replace refs.</summary>
    public static IEnumerable<(string Name, string Value)> Trusted =>
    [
        ("PATH", "/usr/local/bin:/usr/bin:/bin"), ("HOME", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        ("LANG", "C.UTF-8"), ("LC_ALL", "C"),
        ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_CONFIG_GLOBAL", "/dev/null"), ("GIT_NO_REPLACE_OBJECTS", "1"), ("GIT_TERMINAL_PROMPT", "0"),
        ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"), ("DOTNET_NOLOGO", "1"), ("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1"),
    ];

    /// <summary>Runs a trusted tool to its end and returns what it printed.</summary>
    /// <exception cref="InvalidOperationException">It ran past the timeout or wrote more than
    /// <see cref="MaxOutputBytes"/> to one stream, and was stopped.</exception>
    public static async Task<ToolResult> ReadAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken ct, string? directory = null)
    {
        var output = new Captured(Path.GetFileName(executable));
        return output.Result(await RunAsync(executable, arguments, Trusted, output, timeout, ct, directory), timeout);
    }

    /// <summary>Runs the child to its end and returns its exit code, or null when it ran past the timeout and was killed
    /// with every descendant. A cancelled token kills the tree the same way, then throws.</summary>
    public static async Task<int?> RunAsync(string executable, IEnumerable<string> arguments,
        IEnumerable<(string Name, string Value)> environment, IOutputSink sink, TimeSpan timeout, CancellationToken ct,
        string? directory = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory ?? "",
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = await StartAsync(start);
        process.StandardInput.Close();
        var pumps = Task.WhenAll(PumpAsync(process, process.StandardOutput.BaseStream, sink, error: false),
            PumpAsync(process, process.StandardError.BaseStream, sink, error: true));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await pumps;
            ct.ThrowIfCancellationRequested();
            return null;
        }
        await pumps;
        return process.ExitCode;
    }

    /// <summary>Starts the process on a thread of its own that lives until the process exits. bwrap's --die-with-parent
    /// sets PR_SET_PDEATHSIG, which Linux fires when the forking thread exits, not the process; a thread-pool thread
    /// retires after about 20 idle seconds, and a sandbox forked from one was killed mid-build (exit 137).</summary>
    static Task<Process> StartAsync(ProcessStartInfo start)
    {
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                var process = Process.Start(start)!;
                started.SetResult(process);
                process.WaitForExit();
            }
            catch (Exception e)
            {
                started.TrySetException(e); // also absorbs the caller disposing the process as it exits
            }
        }) { IsBackground = true, Name = "softwarp child" }.Start();
        return started.Task;
    }

    static async Task PumpAsync(Process process, Stream stream, IOutputSink sink, bool error)
    {
        var buffer = new byte[81920];
        try
        {
            for (int read; (read = await stream.ReadAsync(buffer)) > 0;) sink.Write(buffer.AsSpan(0, read), error);
        }
        catch (InvalidOperationException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}

/// <summary>Each stream up to <see cref="Tool.MaxOutputBytes"/>, so a hostile repository cannot flood the factory's
/// memory through a sandboxed git; past that the child is stopped.</summary>
sealed class Captured(string name) : IOutputSink
{
    readonly MemoryStream output = new(), error = new();

    public void Write(ReadOnlySpan<byte> data, bool isError)
    {
        var stream = isError ? error : output;
        if (stream.Length + data.Length > Tool.MaxOutputBytes)
            throw new InvalidOperationException($"{name} wrote more than {Tool.MaxOutputBytes} bytes to one stream, so softwarp stopped it");
        stream.Write(data);
    }

    /// <exception cref="InvalidOperationException">The exit is null: the child ran past the timeout.</exception>
    public ToolResult Result(int? exit, TimeSpan timeout) => exit is { } code
        ? new ToolResult(code, Text(output), Text(error))
        : throw new InvalidOperationException($"{name} ran past its {timeout} timeout and was stopped");

    static string Text(MemoryStream stream) => Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
}
