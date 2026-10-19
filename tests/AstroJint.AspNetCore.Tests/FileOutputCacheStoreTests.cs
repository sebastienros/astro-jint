using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace AstroJint.AspNetCore.Tests;

public class FileOutputCacheStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "astro-jint-tests", "cache-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTime _time = new();
    private static readonly string[] Tags = ["a", "b"];

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private FileOutputCacheStore CreateStore(TimeSpan? sweep = null) =>
        new(_directory, sweep ?? TimeSpan.Zero, NullLogger<FileOutputCacheStore>.Instance, _time);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Set_ThenGet_RoundTripsTheBytes()
    {
        using var store = CreateStore();

        await store.SetAsync("key", Bytes("hello"), ["tag"], TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(Bytes("hello"), await store.GetAsync("key", CancellationToken.None));
        Assert.Null(await store.GetAsync("other", CancellationToken.None));
    }

    [Fact]
    public async Task TheBufferApi_RoundTripsASegmentedSequence()
    {
        using var store = CreateStore();
        var first = new Segment(Bytes("hel"));
        var last = first.Append(Bytes("lo"));
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        var pipe = new Pipe();

        await store.SetAsync("key", sequence, Tags, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(await store.TryGetAsync("key", pipe.Writer, CancellationToken.None));
        await pipe.Writer.CompleteAsync();
        var read = await pipe.Reader.ReadAsync();

        Assert.Equal("hello", Encoding.UTF8.GetString(read.Buffer.ToArray()));
        Assert.False(await store.TryGetAsync("missing", pipe.Writer, CancellationToken.None));
    }

    [Fact]
    public async Task Set_ReplacesAnExistingEntry()
    {
        using var store = CreateStore();

        await store.SetAsync("key", Bytes("one"), null, TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.SetAsync("key", Bytes("two"), null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(Bytes("two"), await store.GetAsync("key", CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Get_ForgetsAnExpiredEntry()
    {
        using var store = CreateStore();
        await store.SetAsync("key", Bytes("hello"), null, TimeSpan.FromMinutes(1), CancellationToken.None);

        _time.Now += TimeSpan.FromSeconds(59);
        Assert.NotNull(await store.GetAsync("key", CancellationToken.None));

        _time.Now += TimeSpan.FromSeconds(2);
        Assert.Null(await store.GetAsync("key", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task EvictByTag_DeletesTheEntriesWithTheTagOnly()
    {
        using var store = CreateStore();
        await store.SetAsync("a", Bytes("a"), ["products", "all"], TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.SetAsync("b", Bytes("b"), ["blog", "all"], TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.SetAsync("c", Bytes("c"), null, TimeSpan.FromMinutes(1), CancellationToken.None);

        await store.EvictByTagAsync("products", CancellationToken.None);

        Assert.Null(await store.GetAsync("a", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("b", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("c", CancellationToken.None));

        await store.EvictByTagAsync("all", CancellationToken.None);

        Assert.Null(await store.GetAsync("b", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("c", CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_DeletesExpiredEntriesAndFilesThatAreNotEntries()
    {
        using var store = CreateStore();
        await store.SetAsync("fresh", Bytes("a"), null, TimeSpan.FromMinutes(10), CancellationToken.None);
        await store.SetAsync("stale", Bytes("b"), null, TimeSpan.FromMinutes(1), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_directory, "junk.cache"), "not an entry");
        _time.Now += TimeSpan.FromMinutes(2);

        var deleted = store.Sweep();

        Assert.Equal(2, deleted);
        Assert.NotNull(await store.GetAsync("fresh", CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Get_IgnoresAndDeletesACorruptFile()
    {
        using var store = CreateStore();
        await store.SetAsync("key", Bytes("hello"), null, TimeSpan.FromMinutes(1), CancellationToken.None);
        var path = Directory.EnumerateFiles(_directory).Single();
        await File.WriteAllBytesAsync(path, (await File.ReadAllBytesAsync(path))[..^2]);

        Assert.Null(await store.GetAsync("key", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Set_LeavesNoTemporaryFileBehind()
    {
        using var store = CreateStore();

        await store.SetAsync("key", Bytes("hello"), null, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.All(Directory.EnumerateFiles(_directory), file => Assert.EndsWith(".cache", file));
    }

    [Fact]
    public async Task TheSweepRunsOnItsInterval()
    {
        using var store = CreateStore(TimeSpan.FromMilliseconds(50));
        await store.SetAsync("stale", Bytes("b"), null, TimeSpan.FromMinutes(1), CancellationToken.None);
        _time.Now += TimeSpan.FromMinutes(2);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Directory.EnumerateFiles(_directory).Any() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
