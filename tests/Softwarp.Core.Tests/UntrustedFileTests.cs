using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Softwarp.Core.Tests;

public sealed class UntrustedFileTests : IDisposable
{
    readonly TempRoot root = new();
    readonly string dir;

    public UntrustedFileTests() => Directory.CreateDirectory(dir = Path.Combine(root.Path, "ops", "0002-triage"));

    public void Dispose() => root.Dispose();

    string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    string Fifo(string name)
    {
        var path = Path.Combine(dir, name);
        using var mkfifo = Process.Start("mkfifo", path);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
        return path;
    }

    /// <summary>A read that blocks fails the test after 10 seconds instead of hanging it.</summary>
    static Task<T> Bounded<T>(Func<T> read) => Task.Run(read).WaitAsync(TimeSpan.FromSeconds(10));

    static async Task<string> Refused(Func<object> read) =>
        (await Assert.ThrowsAsync<InvalidDataException>(() => Bounded(read))).Message;

    [Fact]
    public void FileWithinTheBound_IsReadWhole()
    {
        Assert.Equal("{}"u8.ToArray(), UntrustedFile.Read(Write("output.json", "{}"u8.ToArray()), 2));
    }

    [Fact]
    public async Task FileOverTheBound_IsRefused() =>
        Assert.Contains("over 1024 bytes", await Refused(() => UntrustedFile.Read(Write("output.json", new byte[1025]), 1024)));

    [Fact]
    public async Task FileThatGrowsDuringTheRead_IsRefusedNotCutShort()
    {
        var path = Write("output.json", "{}"u8.ToArray());
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
            while (!stop.IsCancellationRequested) file.WriteByte((byte)' ');
        });
        try
        {
            var refusal = await Task.Run(() =>
            {
                for (var timer = Stopwatch.StartNew(); timer.Elapsed < TimeSpan.FromSeconds(5);)
                {
                    try { UntrustedFile.Read(path, 16 << 20); }
                    catch (InvalidDataException e) { return e.Message; }
                }
                return null;
            });
            Assert.Contains("grew while read", refusal);
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
    }

    [Theory]
    [InlineData("/dev/zero")]
    [InlineData("outside")]
    public async Task Symlink_IsRefusedNotFollowed(string target)
    {
        var outside = Path.Combine(root.Path, "outside.json");
        File.WriteAllText(outside, "{}");
        var link = Path.Combine(dir, "output.json");
        File.CreateSymbolicLink(link, target == "outside" ? outside : target);
        Assert.Contains("link", await Refused(() => UntrustedFile.Read(link, 1024)));
        Assert.Contains("link", await Refused(() => UntrustedFile.Tail(link, 1024)));
        Assert.Contains("link", await Refused(() => UntrustedFile.Hash(link, 1024)));
    }

    [Fact]
    public async Task FileInALinkedDirectory_IsRefused()
    {
        var outside = Path.Combine(root.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "output.json"), "{}");
        var linked = Path.Combine(root.Path, "ops", "0003-triage");
        Directory.CreateSymbolicLink(linked, outside);
        Assert.Contains("link", await Refused(() => UntrustedFile.Read(Path.Combine(linked, "output.json"), 1024)));
    }

    [Fact]
    public async Task Fifo_IsNeverOpened()
    {
        var fifo = Fifo("output.json");
        Assert.Contains("not a regular file", await Refused(() => UntrustedFile.Read(fifo, 1024)));
        Assert.Equal("", await Bounded(() => UntrustedFile.Tail(fifo, 1024)));
        Assert.Equal(new Digest(Convert.ToHexStringLower(SHA256.HashData([]))), await Bounded(() => UntrustedFile.Hash(fifo, 1024)));
    }

    [Fact]
    public async Task FileTheFactoryCannotOpen_IsRefused()
    {
        var path = Write("output.json", "{}"u8.ToArray());
        File.SetUnixFileMode(path, UnixFileMode.None);
        Assert.Contains("could not be opened", await Refused(() => UntrustedFile.Read(path, 1024)));
        Assert.Contains("could not be opened", await Refused(() => UntrustedFile.Tail(path, 1024)));
        Assert.Contains("could not be opened", await Refused(() => UntrustedFile.Hash(path, 1024)));
    }

    [Fact]
    public async Task MissingOrEmptyFile_IsRefusedForOutputAndEmptyForLogs()
    {
        Assert.Contains("missing", await Refused(() => UntrustedFile.Read(Path.Combine(dir, "output.json"), 1024)));
        Assert.Contains("empty", await Refused(() => UntrustedFile.Read(Write("empty.json", []), 1024)));
        Assert.Equal("", UntrustedFile.Tail(Path.Combine(dir, "none.log"), 1024));
    }

    [Fact]
    public void LargeLog_IsTailedAndHashedWithoutLoadingIt()
    {
        var content = new byte[64 << 20];
        content.AsSpan().Fill((byte)'x');
        "\nAssert.Equal failed\n"u8.CopyTo(content.AsSpan(content.Length - 21));
        var log = Write("TargetedTests.log", content);
        var expected = Convert.ToHexStringLower(SHA256.HashData(content));
        content = null;
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var tail = UntrustedFile.Tail(log, 4096);
        var digest = UntrustedFile.Hash(log, 64 << 20);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(4096, Encoding.UTF8.GetByteCount(tail));
        Assert.EndsWith("x\nAssert.Equal failed\n", tail);
        Assert.Equal(expected, digest.Value);
        Assert.InRange(allocated, 0, 1 << 20);
    }

    [Fact]
    public async Task LogOverTheHashCap_IsRefused()
    {
        var log = Write("TargetedTests.log", new byte[1025]);
        Assert.Contains("over 1024 bytes", await Refused(() => UntrustedFile.Hash(log, 1024)));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[1025])), UntrustedFile.Hash(log, 1025).Value);
    }

    [Fact]
    public void ShortLog_IsTailedWhole() =>
        Assert.Equal("exit 1\n", UntrustedFile.Tail(Write("Build.log", "exit 1\n"u8.ToArray()), 4096));
}
