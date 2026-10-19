using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;

namespace AstroJint.AspNetCore;

/// <summary>
/// Keeps the entries of the output cache as files, one per entry, so that a rendered page outlives the process that
/// rendered it, like the pages that <c>astro build</c> prerenders, and can be shared by the instances of a host that
/// see the same folder.
/// </summary>
/// <remarks>
/// A file is written next to its final name and moved into place, so a reader sees a whole entry or none. Its name is
/// a hash of the key, which is what the middleware builds from the method, the path, the query and the headers it
/// varies on. The expiration and the tags are in the file, so eviction by tag and the sweep read the files' heads.
/// </remarks>
internal sealed partial class FileOutputCacheStore : IOutputCacheBufferStore, IDisposable
{
    private const string Extension = ".cache";
    private const uint Magic = 0x434F4A41; // "AJOC"
    private const byte Version = 1;

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource? _sweeping;

    public FileOutputCacheStore(string directory, TimeSpan sweepInterval, ILogger<FileOutputCacheStore> logger, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(logger);
        _directory = Path.GetFullPath(directory);
        _logger = logger;
        _time = time ?? TimeProvider.System;
        System.IO.Directory.CreateDirectory(_directory);

        if (sweepInterval > TimeSpan.Zero)
        {
            _sweeping = new CancellationTokenSource();
            _ = SweepPeriodicallyAsync(sweepInterval, _sweeping.Token);
        }
    }

    /// <summary>The folder with the files.</summary>
    public string Directory => _directory;

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var entry = await ReadAsync(PathOf(key), cancellationToken).ConfigureAwait(false);
        return entry?.Body;
    }

    public async ValueTask<bool> TryGetAsync(string key, PipeWriter destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(destination);
        var entry = await ReadAsync(PathOf(key), cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return false;
        }

        await destination.WriteAsync(entry.Body, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return SetAsync(key, new ReadOnlySequence<byte>(value), tags ?? [], validFor, cancellationToken);
    }

    public async ValueTask SetAsync(string key, ReadOnlySequence<byte> value, ReadOnlyMemory<string> tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var path = PathOf(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var expires = _time.GetUtcNow() + validFor;

        try
        {
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                var head = WriteHead(expires, tags.Span, value.Length);
                await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
                foreach (var segment in value)
                {
                    await stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tag);
        foreach (var path in EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadHead(path, out var head) && head.Tags.Contains(tag, StringComparer.Ordinal))
            {
                TryDelete(path);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Deletes the files whose response has expired, and the ones that are not entries at all.</summary>
    public int Sweep()
    {
        var now = _time.GetUtcNow();
        var deleted = 0;
        foreach (var path in EnumerateFiles())
        {
            if (!TryReadHead(path, out var head) || head.Expires <= now)
            {
                deleted += TryDelete(path) ? 1 : 0;
            }
        }

        return deleted;
    }

    public void Dispose()
    {
        _sweeping?.Cancel();
        _sweeping?.Dispose();
    }

    private string PathOf(string key) =>
        Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + Extension);

    private IEnumerable<string> EnumerateFiles()
    {
        try
        {
            return System.IO.Directory.EnumerateFiles(_directory, "*" + Extension);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private async Task<Entry?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException ex)
        {
            // Being replaced right now, or locked by another process: a miss renders the page again.
            LogUnreadable(_logger, ex, path);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            LogUnreadable(_logger, ex, path);
            return null;
        }

        if (!TryParseHead(bytes, out var head, out var bodyOffset) || bytes.Length - bodyOffset != head.BodyLength)
        {
            LogCorrupt(_logger, path);
            TryDelete(path);
            return null;
        }

        if (head.Expires <= _time.GetUtcNow())
        {
            TryDelete(path);
            return null;
        }

        return new Entry(bytes.AsSpan(bodyOffset).ToArray());
    }

    private static bool TryReadHead(string path, out Head head)
    {
        head = default;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: false);
            var buffer = new byte[Math.Min(stream.Length, 64 * 1024)];
            stream.ReadExactly(buffer);
            return TryParseHead(buffer, out head, out _);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] WriteHead(DateTimeOffset expires, ReadOnlySpan<string> tags, long bodyLength)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(expires.UtcTicks);
        writer.Write(tags.Length);
        foreach (var tag in tags)
        {
            writer.Write(tag);
        }

        writer.Write(bodyLength);
        writer.Flush();
        return buffer.ToArray();
    }

    private static bool TryParseHead(byte[] bytes, out Head head, out int bodyOffset)
    {
        head = default;
        bodyOffset = 0;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic || reader.ReadByte() != Version)
            {
                return false;
            }

            var expires = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var count = reader.ReadInt32();
            if (count is < 0 or > 10_000)
            {
                return false;
            }

            var tags = new string[count];
            for (var i = 0; i < count; i++)
            {
                tags[i] = reader.ReadString();
            }

            var bodyLength = reader.ReadInt64();
            if (bodyLength < 0)
            {
                return false;
            }

            head = new Head(expires, tags, bodyLength);
            bodyOffset = (int)stream.Position;
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // A tag whose UTF-8 is not valid, or a length that points past the file.
            return false;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task SweepPeriodicallyAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var deleted = Sweep();
                    if (deleted > 0)
                    {
                        LogSwept(_logger, deleted);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogSweepFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly record struct Head(DateTimeOffset Expires, string[] Tags, long BodyLength);

    private sealed record Entry(byte[] Body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The cached response '{Path}' could not be read.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The cached response '{Path}' is not a valid entry and was deleted.")]
    private static partial void LogCorrupt(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleted {Count} expired cached responses.")]
    private static partial void LogSwept(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sweeping the expired cached responses failed.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
