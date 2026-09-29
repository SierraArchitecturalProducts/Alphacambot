using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AlphacamBridge.ComInterop;

/// <summary>
/// Manages a dedicated Single-Threaded Apartment (STA) thread for marshalling COM calls to Alphacam.
/// This is critical because Alphacam's COM objects must be accessed from an STA thread.
/// </summary>
public class StaThreadDispatcher : IDisposable
{
    private readonly Thread _staThread;
    private readonly BlockingCollection<WorkItem> _workQueue;
    private readonly ILogger<StaThreadDispatcher> _logger;
    private readonly CancellationTokenSource _shutdownToken;
    private volatile bool _isDisposed;
    private readonly TimeSpan _defaultTimeout;

    /// <summary>
    /// Creates a new StaThreadDispatcher with default 60-second timeout.
    /// </summary>
    public StaThreadDispatcher(ILogger<StaThreadDispatcher> logger, TimeSpan? defaultTimeout = null)
    {
        _logger = logger;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(60);
        _workQueue = new BlockingCollection<WorkItem>();
        _shutdownToken = new CancellationTokenSource();
        _isDisposed = false;

        // Create and start the STA thread
        _staThread = new Thread(RunMessagePump)
        {
            Name = "Alphacam-COM-STA",
            IsBackground = false
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        _logger.LogInformation("StaThreadDispatcher initialized with STA thread {ThreadId}", _staThread.ManagedThreadId);
    }

    /// <summary>
    /// The message pump that runs on the STA thread, processing work items from the queue.
    /// </summary>
    private void RunMessagePump()
    {
        _logger.LogDebug("STA message pump started on thread {ThreadId}", Thread.CurrentThread.ManagedThreadId);

        try
        {
            while (!_shutdownToken.Token.IsCancellationRequested)
            {
                if (_workQueue.TryTake(out var item, 100, _shutdownToken.Token))
                {
                    _logger.LogDebug("Processing work item: {Action}", item.Action.Method.Name);

                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        item.Execute();
                        stopwatch.Stop();
                        _logger.LogDebug("Work item {Action} completed in {ElapsedMs}ms", item.Action.Method.Name, stopwatch.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        stopwatch.Stop();
                        _logger.LogError(ex, "Work item {Action} failed after {ElapsedMs}ms", item.Action.Method.Name, stopwatch.ElapsedMilliseconds);
                        item.SetException(ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("STA message pump cancelled");
        }
        finally
        {
            _logger.LogDebug("STA message pump shutting down");
        }
    }

    /// <summary>
    /// Invokes an action on the STA thread and waits for its completion.
    /// </summary>
    /// <param name="action">The action to invoke</param>
    /// <param name="timeout">Optional timeout; uses default if not specified</param>
    /// <returns>A task that completes when the action finishes</returns>
    public Task InvokeAsync(Action action, TimeSpan? timeout = null)
    {
        ThrowIfDisposed();
        var item = new WorkItem(action, timeout ?? _defaultTimeout);
        _workQueue.Add(item);
        return item.CompletionTask;
    }

    /// <summary>
    /// Invokes a function on the STA thread and waits for its result.
    /// </summary>
    /// <typeparam name="T">The return type of the function</typeparam>
    /// <param name="action">The function to invoke</param>
    /// <param name="timeout">Optional timeout; uses default if not specified</param>
    /// <returns>A task that completes with the function's result</returns>
    public Task<T> InvokeAsync<T>(Func<T> action, TimeSpan? timeout = null)
    {
        ThrowIfDisposed();
        var item = new WorkItem<T>(action, timeout ?? _defaultTimeout);
        _workQueue.Add(item);
        return item.CompletionTask;
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(StaThreadDispatcher));
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        _logger.LogInformation("Disposing StaThreadDispatcher");

        // Signal shutdown
        _shutdownToken.Cancel();

        // Wait for the STA thread to finish (with timeout to avoid hanging)
        if (!_staThread.Join(TimeSpan.FromSeconds(10)))
        {
            _logger.LogWarning("STA thread did not shut down gracefully within 10 seconds");
            _staThread.Interrupt();
        }

        _workQueue.Dispose();
        _shutdownToken.Dispose();

        _logger.LogInformation("StaThreadDispatcher disposed");
    }

    /// <summary>
    /// Base class for work items to be executed on the STA thread.
    /// </summary>
    private class WorkItem
    {
        public Action Action { get; }
        protected TimeSpan Timeout { get; }
        private readonly TaskCompletionSource<object?> _tcs;
        private CancellationTokenSource? _timeoutCts;

        public WorkItem(Action action, TimeSpan timeout)
        {
            Action = action;
            Timeout = timeout;
            _tcs = new TaskCompletionSource<object?>();
        }

        public Task CompletionTask => _tcs.Task;

        public virtual void Execute()
        {
            _timeoutCts = new CancellationTokenSource(Timeout);
            try
            {
                Action.Invoke();
                _tcs.SetResult(null);
            }
            catch (OperationCanceledException)
            {
                _tcs.SetException(new TimeoutException($"COM call timed out after {Timeout.TotalSeconds}s"));
            }
            finally
            {
                _timeoutCts?.Dispose();
            }
        }

        public void SetException(Exception ex)
        {
            _tcs.SetException(ex);
        }
    }

    /// <summary>
    /// Work item that returns a result.
    /// </summary>
    private class WorkItem<T> : WorkItem
    {
        private readonly Func<T> _function;
        private readonly TaskCompletionSource<T> _genericTcs;

        public new Task<T> CompletionTask => _genericTcs.Task;

        public WorkItem(Func<T> function, TimeSpan timeout)
            : base(() => function.Invoke(), timeout)
        {
            _function = function;
            _genericTcs = new TaskCompletionSource<T>();
        }

        public override void Execute()
        {
            var timeoutCts = new CancellationTokenSource(Timeout);
            try
            {
                var result = _function.Invoke();
                _genericTcs.SetResult(result);
            }
            catch (OperationCanceledException)
            {
                _genericTcs.SetException(new TimeoutException($"COM call timed out after {Timeout.TotalSeconds}s"));
            }
            catch (Exception ex)
            {
                _genericTcs.SetException(ex);
            }
            finally
            {
                timeoutCts.Dispose();
            }
        }
    }
}
