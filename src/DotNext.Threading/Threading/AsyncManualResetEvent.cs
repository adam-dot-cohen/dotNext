using System.Runtime.CompilerServices;
using static System.Threading.Timeout;

namespace DotNext.Threading;

using Tasks.Pooling;

/// <summary>
/// Represents asynchronous version of <see cref="ManualResetEvent"/>.
/// </summary>
public class AsyncManualResetEvent : QueuedSynchronizer, IAsyncResetEvent
{
    private readonly Func<DefaultWaitNode> pool;
    private AtomicBoolean state;

    /// <summary>
    /// Initializes a new asynchronous reset event in the specified state.
    /// </summary>
    /// <param name="initialState"><see langword="true"/> to set the initial state signaled; <see langword="false"/> to set the initial state to non signaled.</param>
    /// <param name="concurrencyLevel">The potential number of suspended callers.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="concurrencyLevel"/> is less than or equal to zero.</exception>
    public AsyncManualResetEvent(bool initialState, int concurrencyLevel)
    {
        if (concurrencyLevel < 1)
            throw new ArgumentOutOfRangeException(nameof(concurrencyLevel));

        pool = new ConstrainedValueTaskPool<DefaultWaitNode>(concurrencyLevel, RemoveAndDrainWaitQueue).Get;
    }

    /// <summary>
    /// Initializes a new asynchronous reset event in the specified state.
    /// </summary>
    /// <param name="initialState"><see langword="true"/> to set the initial state signaled; <see langword="false"/> to set the initial state to non signaled.</param>
    public AsyncManualResetEvent(bool initialState)
    {
        state = new(initialState);
        pool = new UnconstrainedValueTaskPool<DefaultWaitNode>(RemoveAndDrainWaitQueue).Get;
    }

    /// <inheritdoc/>
    EventResetMode IAsyncResetEvent.ResetMode => EventResetMode.ManualReset;

    /// <summary>
    /// Indicates whether this event is set.
    /// </summary>
    public bool IsSet => state.Value;

    /// <summary>
    /// Sets the state of the event to signaled, allowing one or more awaiters to proceed.
    /// </summary>
    /// <returns><see langword="true"/> if the operation succeeds; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
    public bool Set() => Set(false);

    /// <summary>
    /// Sets the state of the event to signaled, allowing one or more awaiters to proceed;
    /// and, optionally, reverts the state of the event to initial state.
    /// </summary>
    /// <param name="autoReset"><see langword="true"/> to reset this object to non-signaled state automatically; <see langword="false"/> to leave this object in signaled state.</param>
    /// <returns><see langword="true"/> if the operation succeeds; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool Set(bool autoReset)
    {
        ThrowIfDisposed();

        var result = !state.Value;
        ResumeSuspendedCallers();
        state.Value = !autoReset;

        return result;
    }

    /// <summary>
    /// Sets the state of this event to non signaled, causing consumers to wait asynchronously.
    /// </summary>
    /// <returns><see langword="true"/> if the operation succeeds; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool Reset()
    {
        ThrowIfDisposed();

        return state.TrueToFalse();
    }

    /// <inheritdoc/>
    bool IAsyncEvent.Signal() => Set();

    private static bool CheckState(ref AtomicBoolean state) => state.Value;

    /// <summary>
    /// Turns caller into idle state until the current event is set.
    /// </summary>
    /// <param name="timeout">The interval to wait for the signaled state.</param>
    /// <param name="token">The token that can be used to abort wait process.</param>
    /// <returns><see langword="true"/> if signaled state was set; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public unsafe ValueTask<bool> WaitAsync(TimeSpan timeout, CancellationToken token = default)
        => WaitNoTimeoutAsync(ref state, &CheckState, pool, out _, timeout, token);

    /// <summary>
    /// Turns caller into idle state until the current event is set.
    /// </summary>
    /// <param name="token">The token that can be used to abort wait process.</param>
    /// <returns>The task representing asynchronous result.</returns>
    /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public unsafe ValueTask WaitAsync(CancellationToken token = default)
        => WaitWithTimeoutAsync(ref state, &CheckState, pool, out _, InfiniteTimeSpan, token);

    /// <summary>
    /// Suspends the caller until this event is set.
    /// </summary>
    /// <remarks>
    /// If given predicate returns true then caller will not be suspended.
    /// </remarks>
    /// <typeparam name="T">The type of predicate parameter.</typeparam>
    /// <param name="condition">Additional condition that must be checked before suspension.</param>
    /// <param name="arg">The argument to be passed to the predicate.</param>
    /// <param name="timeout">The number of time to wait before this event is set.</param>
    /// <param name="token">The token that can be used to cancel waiting operation.</param>
    /// <returns><see langword="true"/>, if this event was set; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public unsafe ValueTask<bool> WaitAsync<T>(Predicate<T> condition, T arg, TimeSpan timeout, CancellationToken token = default)
        => state.Value || condition(arg) ? new(true) : WaitNoTimeoutAsync(ref state, &CheckState, pool, out _, timeout, token);

    /// <summary>
    /// Suspends the caller until this event is set.
    /// </summary>
    /// <remarks>
    /// If given predicate returns true then caller will not be suspended.
    /// </remarks>
    /// <typeparam name="T">The type of predicate parameter.</typeparam>
    /// <param name="condition">Additional condition that must be checked before suspension.</param>
    /// <param name="arg">The argument to be passed to the predicate.</param>
    /// <param name="token">The token that can be used to cancel waiting operation.</param>
    /// <returns><see langword="true"/>, if this event was set; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public unsafe ValueTask WaitAsync<T>(Predicate<T> condition, T arg, CancellationToken token = default)
        => state.Value || condition(arg) ? ValueTask.CompletedTask : WaitWithTimeoutAsync(ref state, &CheckState, pool, out _, InfiniteTimeSpan, token);

    /// <summary>
    /// Suspends the caller until this event is set.
    /// </summary>
    /// <remarks>
    /// If given predicate returns true then caller will not be suspended.
    /// </remarks>
    /// <typeparam name="T1">The type of the first predicate parameter.</typeparam>
    /// <typeparam name="T2">The type of the second predicate parameter.</typeparam>
    /// <param name="condition">Additional condition that must be checked before suspension.</param>
    /// <param name="arg1">The first argument to be passed to the predicate.</param>
    /// <param name="arg2">The second argument to be passed to the predicate.</param>
    /// <param name="timeout">The number of time to wait before this event is set.</param>
    /// <param name="token">The token that can be used to cancel waiting operation.</param>
    /// <returns><see langword="true"/>, if this event was set; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public unsafe ValueTask<bool> WaitAsync<T1, T2>(Func<T1, T2, bool> condition, T1 arg1, T2 arg2, TimeSpan timeout, CancellationToken token = default)
        => state.Value || condition(arg1, arg2) ? new(true) : WaitNoTimeoutAsync(ref state, &CheckState, pool, out _, timeout, token);

    /// <summary>
    /// Suspends the caller until this event is set.
    /// </summary>
    /// <remarks>
    /// If given predicate returns true then caller will not be suspended.
    /// </remarks>
    /// <typeparam name="T1">The type of the first predicate parameter.</typeparam>
    /// <typeparam name="T2">The type of the second predicate parameter.</typeparam>
    /// <param name="condition">Additional condition that must be checked before suspension.</param>
    /// <param name="arg1">The first argument to be passed to the predicate.</param>
    /// <param name="arg2">The second argument to be passed to the predicate.</param>
    /// <param name="token">The token that can be used to cancel waiting operation.</param>
    /// <returns><see langword="true"/>, if this event was set; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ObjectDisposedException">The current instance has already been disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public unsafe ValueTask WaitAsync<T1, T2>(Func<T1, T2, bool> condition, T1 arg1, T2 arg2, CancellationToken token = default)
        => state.Value || condition(arg1, arg2) ? ValueTask.CompletedTask : WaitWithTimeoutAsync(ref state, &CheckState, pool, out _, InfiniteTimeSpan, token);
}