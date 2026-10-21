using System.Security.Cryptography;
using System.Text;

namespace Softwarp.Core;

/// <summary>Reads files that agents or gate commands wrote, without trusting them. A link is never followed, a file of
/// size 0 is never opened (on Linux every FIFO, socket and device node reports size 0, so nothing can block the open),
/// and every read is bounded.</summary>
/// <remarks>ponytail: the checks and the open are two steps, so a process that outlives its agent could swap the file
/// in between. The spawn binding must end an agent's processes before its output is read.</remarks>
public static class UntrustedFile
{
    /// <summary>The file's bytes, read once through one stream into a buffer one byte longer than the file was when opened
    /// (at most maxBytes + 1), so a file that grows during the read fills it and is refused, not cut short.</summary>
    /// <exception cref="InvalidDataException">The file is a link, sits in a linked directory, is missing, empty, not a
    /// regular file or not openable, or holds more than maxBytes or grew during the read.</exception>
    public static byte[] Read(string path, int maxBytes)
    {
        using var stream = Open(path)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} is missing, empty or not a regular file");
        var buffer = new byte[Math.Min(maxBytes, stream.Length) + 1];
        var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return length < buffer.Length ? buffer[..length] : throw new InvalidDataException($"{Path.GetFileName(path)} is over {maxBytes} bytes or grew while read");
    }

    /// <summary>The last maxBytes of the file, decoded as UTF-8; empty when the file is missing or empty.</summary>
    /// <exception cref="InvalidDataException">The file or its directory is a link, or the file is not openable.</exception>
    public static string Tail(string path, int maxBytes)
    {
        using var stream = Open(path);
        if (stream is null) return "";
        stream.Seek(Math.Max(0, stream.Length - maxBytes), SeekOrigin.Begin);
        var buffer = new byte[maxBytes];
        return Encoding.UTF8.GetString(buffer, 0, stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false));
    }

    /// <summary>SHA-256 of the file, streamed; the digest of no bytes when the file is missing or empty.</summary>
    /// <exception cref="InvalidDataException">The file or its directory is a link, the file is not openable, or it holds
    /// more than maxBytes.</exception>
    public static Digest Hash(string path, long maxBytes)
    {
        using var stream = Open(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        for (int read; stream is not null && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes + 1 - total))) > 0;)
        {
            sha.AppendData(buffer, 0, read);
            if ((total += read) > maxBytes) throw new InvalidDataException($"{Path.GetFileName(path)} is over {maxBytes} bytes");
        }
        return new Digest(Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    /// <summary>Null when the file is missing or has size 0. A file the factory may not open is refused like a link, so
    /// an unreadable output is an invalid artifact and an unreadable gate log fails its gate.</summary>
    static FileStream? Open(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Directory?.LinkTarget is not null)
            throw new InvalidDataException($"{path} or its directory is a link");
        try
        {
            return file.Exists && file.Length > 0 ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be opened: {e.Message}", e);
        }
    }
}
