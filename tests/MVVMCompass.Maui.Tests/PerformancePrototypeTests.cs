using System.Collections.Concurrent;
using Microsoft.Maui.Controls;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class PerformancePrototypeTests
{
    [Fact]
    public async Task Deferred_legacy_removal_preserves_page_reinserted_in_same_dispatcher_turn()
    {
        await RunOnPump(async () =>
        {
            var model = new Model();
            var page = new ContentPage { BindingContext = model };
            var navigation = new NavigationPage(new ContentPage()) { Handler = new TestNavigationHandler() };
            await navigation.PushAsync(page, false);
            await navigation.PushAsync(new ContentPage(), false);
            NativeNavigationObserver.Attach(navigation);
            try
            {
                navigation.Navigation.RemovePage(page);
                navigation.Navigation.InsertPageBefore(page, navigation.CurrentPage);
                await NativeNavigationObserver.Completion(navigation);
                Assert.False(model.IsDismissed);
                Assert.False(model.Lifetime.Token.IsCancellationRequested);
                Assert.Equal(0, model.Calls);
                navigation.Navigation.RemovePage(page);
                await NativeNavigationObserver.Completion(navigation);
                Assert.True(model.Lifetime.Token.IsCancellationRequested);
                Assert.Equal(1, model.Calls);
            }
            finally { NativeNavigationObserver.Detach(navigation); }
        });
    }

    [Fact]
    public async Task Removal_batch_completion_includes_later_removal_and_keeps_cleanup_order()
    {
        await RunOnPump(async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var lower = new Model { Cleanup = () => { order.Add("lower"); return Task.CompletedTask; } };
            var upper = new Model { Cleanup = async () => { order.Add("upper-start"); entered.SetResult(); await release.Task; order.Add("upper-end"); } };
            var navigation = new NavigationPage(new ContentPage()) { Handler = new TestNavigationHandler() };
            await navigation.PushAsync(new ContentPage { BindingContext = lower }, false);
            await navigation.PushAsync(new ContentPage { BindingContext = upper }, false);
            NativeNavigationObserver.Attach(navigation);
            try
            {
                await navigation.PopAsync(false);
                await entered.Task;
                await navigation.PopAsync(false);
                var completion = NativeNavigationObserver.Completion(navigation);
                Assert.False(completion.IsCompleted);
                Assert.True(lower.IsDismissed);
                Assert.Equal(0, lower.Calls);
                release.SetResult();
                await completion;
                Assert.Equal(new[] { "upper-start", "upper-end", "lower" }, order);
                Assert.Equal(1, upper.Calls);
                Assert.Equal(1, lower.Calls);
            }
            finally { release.TrySetResult(); NativeNavigationObserver.Detach(navigation); }
        });
    }

    private static Task RunOnPump(Func<Task> action) => Task.Run(() =>
    {
        using var pump = new Pump();
        pump.Run(action);
    }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private sealed class Model : ViewModelBase
    {
        internal Func<Task>? Cleanup;
        internal int Calls;
        public override Task AfterDismissed() { Calls++; return Cleanup?.Invoke() ?? Task.CompletedTask; }
    }

    private sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
        internal void Run(Func<Task> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                var task = action();
                _ = task.ContinueWith(_ => queue.CompleteAdding(), TaskScheduler.Default);
                foreach (var item in queue.GetConsumingEnumerable()) item.Callback(item.State);
                task.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); }
        }
        public void Dispose() => queue.Dispose();
    }
}
