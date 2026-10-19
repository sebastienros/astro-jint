using Microsoft.Extensions.Logging;

namespace AstroJint.AspNetCore;

/// <summary>
/// A fixed-size pool of expensive, single-user objects (JavaScript engines). Objects are created lazily or by
/// <see cref="WarmUpAsync"/>, handed out to one caller at a time and, when a caller reports them as unhealthy,
/// disposed of and replaced in the background.
/// </summary>
/// <remarks>
/// The most recently returned object is handed out first. An engine that has just rendered a page has the modules of
/// that page evaluated and its caches filled, and its data is still in the processor caches, so at low traffic a few
/// engines do all the work and stay warm instead of every engine paying the first-render cost of every page in turn.
/// </remarks>
internal sealed partial class EnginePool<T> : IAsyncDisposable
    where T : class, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Stack<T> _idle = new();
    private readonly Queue<TaskCompletionSource<T?>> _waiters = new();
    private readonly Func<Task<T>> _create;
    private readonly ILogger _logger;
    private int _reserved;
    private bool _disposed;

    public EnginePool(int size, Func<Task<T>> create, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        Size = size;
        _create = create;
        _logger = logger;
    }

    public int Size { get; }

    /// <summary>Objects that exist, are being created, or are in use.</summary>
    public int Reserved => Volatile.Read(ref _reserved);

    /// <summary>Objects that exist and wait for a caller.</summary>
    public int Idle
    {
        get
        {
            lock (_lock)
            {
                return _idle.Count;
            }
        }
    }

    /// <summary>Creates up to <see cref="Size"/> objects in parallel. Failures are logged and retried on demand.</summary>
    public Task WarmUpAsync() => Task.WhenAll(Enumerable.Range(0, Size).Select(_ => Task.Run(CreateIntoPoolAsync)));

    /// <summary>Waits for a free object, creating one when the pool has not reached its size yet.</summary>
    public async ValueTask<T> RentAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TaskCompletionSource<T?>? waiter = null;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_idle.TryPop(out var idle))
                {
                    return idle;
                }

                if (!TryReserve())
                {
                    waiter = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Enqueue(waiter);
                }
            }

            if (waiter is null)
            {
                try
                {
                    return await Task.Run(_create, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    Release();
                    WakeOneWaiter();
                    throw;
                }
            }

            T? slot;
            using (cancellationToken.UnsafeRegister(static state => ((TaskCompletionSource<T?>)state!).TrySetCanceled(), waiter))
            {
                slot = await waiter.Task.ConfigureAwait(false);
            }

            if (slot is not null)
            {
                return slot;
            }

            // A wake-up call without a slot: a replacement failed, so try to create one here, or wait again.
        }
    }

    /// <summary>Gives an object back. A discarded object is disposed of and replaced in the background.</summary>
    public void Return(T slot, bool discard)
    {
        if (!discard && TryHandOver(slot))
        {
            return;
        }

        slot.Dispose();
        Release();

        if (!Volatile.Read(ref _disposed))
        {
            _ = Task.Run(CreateIntoPoolAsync);
        }
    }

    public ValueTask DisposeAsync()
    {
        List<T> idle;
        List<TaskCompletionSource<T?>> waiters;
        lock (_lock)
        {
            _disposed = true;
            idle = [.. _idle];
            _idle.Clear();
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var slot in idle)
        {
            slot.Dispose();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetException(new ObjectDisposedException(GetType().Name));
        }

        return ValueTask.CompletedTask;
    }

    private async Task CreateIntoPoolAsync()
    {
        lock (_lock)
        {
            if (_disposed || !TryReserve())
            {
                return;
            }
        }

        try
        {
            var slot = await _create().ConfigureAwait(false);
            if (!TryHandOver(slot))
            {
                slot.Dispose();
                Release();
            }
        }
        catch (Exception ex)
        {
            Release();
            LogCreateFailed(_logger, ex);
            WakeOneWaiter();
        }
    }

    // Gives the slot to the longest waiting caller, or keeps it as the next one to hand out. False once disposed.
    private bool TryHandOver(T slot)
    {
        while (true)
        {
            TaskCompletionSource<T?>? waiter;
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                if (!_waiters.TryDequeue(out waiter))
                {
                    _idle.Push(slot);
                    return true;
                }
            }

            // A waiter that was cancelled in the meantime does not take the slot; the next one does.
            if (waiter.TrySetResult(slot))
            {
                return true;
            }
        }
    }

    private void WakeOneWaiter()
    {
        while (true)
        {
            TaskCompletionSource<T?>? waiter;
            lock (_lock)
            {
                if (!_waiters.TryDequeue(out waiter))
                {
                    return;
                }
            }

            if (waiter.TrySetResult(null))
            {
                return;
            }
        }
    }

    private bool TryReserve()
    {
        while (true)
        {
            var current = Volatile.Read(ref _reserved);
            if (current >= Size)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reserved, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private void Release() => Interlocked.Decrement(ref _reserved);

    [LoggerMessage(Level = LogLevel.Error, Message = "Creating a JavaScript engine failed. The next on-demand request will try again.")]
    private static partial void LogCreateFailed(ILogger logger, Exception exception);
}
