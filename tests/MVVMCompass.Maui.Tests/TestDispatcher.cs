using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Dispatching;

namespace MVVMCompass.Maui.Tests;

// This is a headless dispatcher, not a substitute for platform UI-thread tests.
internal sealed class TestDispatcher : IDispatcher, IDispatcherProvider
{
    [ModuleInitializer]
    internal static void Initialize() => DispatcherProvider.SetCurrent(new TestDispatcher());
    public IDispatcher GetForCurrentThread() => this;
    public bool IsDispatchRequired => false;
    public bool Dispatch(Action action) { action(); return true; }
    public bool DispatchDelayed(TimeSpan delay, Action action) => throw new NotSupportedException("Timers are not part of these headless tests.");
    public IDispatcherTimer CreateTimer() => throw new NotSupportedException("Timers are not part of these headless tests.");

    // Use a single UI queue when testing asynchronous native lifecycle callbacks.
    internal static Task Run(Func<Task> action) => Task.Run(() =>
    {
        using var pump = new Pump();
        DispatcherProvider.SetCurrent(new QueuedDispatcher(pump));
        try { pump.Run(action); }
        finally { TestDispatcher.Initialize(); }
    }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        private int operations = 1;
        internal int ThreadId { get; } = Environment.CurrentManagedThreadId;
        public override void OperationStarted() => Interlocked.Increment(ref operations);
        public override void OperationCompleted()
        {
            if (Interlocked.Decrement(ref operations) == 0) queue.CompleteAdding();
        }
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (Environment.CurrentManagedThreadId == ThreadId) { callback(state); return; }
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ => { try { callback(state); done.TrySetResult(); } catch (Exception error) { done.TrySetException(error); } }, null);
            done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
        internal void Run(Func<Task> action)
        {
            var previous = Current; SetSynchronizationContext(this);
            try
            {
                var task = action();
                // async void native event observers also own the context until their continuations finish.
                _ = task.ContinueWith(_ => OperationCompleted(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                foreach (var item in queue.GetConsumingEnumerable()) item.Callback(item.State);
                task.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }
        public void Dispose() => queue.Dispose();
    }
    private sealed class QueuedDispatcher(Pump pump) : IDispatcher, IDispatcherProvider
    {
        public IDispatcher GetForCurrentThread() => this;
        public bool IsDispatchRequired => Environment.CurrentManagedThreadId != pump.ThreadId;
        public bool Dispatch(Action action) { pump.Post(_ => action(), null); return true; }
        public bool DispatchDelayed(TimeSpan delay, Action action) => throw new NotSupportedException();
        public IDispatcherTimer CreateTimer() => throw new NotSupportedException();
    }
}
