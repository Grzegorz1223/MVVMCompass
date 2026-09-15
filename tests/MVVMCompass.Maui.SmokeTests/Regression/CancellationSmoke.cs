using MVVMCompass.Core;

namespace MVVMCompass.Sample;

internal static class CancellationSmoke
{
    public static async Task RunAsync(MauiNavigationHost host, NavigationOptions options, Action<bool, string> check)
    {
        var previous = options.RetainedViewLifecycleBehavior;
        try
        {
            foreach (var profile in Enum.GetValues<RetainedViewLifecycleBehavior>())
            {
                options.RetainedViewLifecycleBehavior = profile;
                void Check(bool value, string name) => check(value, $"{profile}: cancellation {name}");
                var entered = Signal(); var release = Signal(); var cancelled = Signal();
                var order = new List<string>();
                var root = Require(await host.ReplaceRootAsync(new NavigationRequestOptions(), () => new Model(), Page, navigable: true));
                var lower = Require(await host.PushAsync(new NavigationRequestOptions(), () => new Model
                { Cleanup = () => { order.Add("lower"); return Task.CompletedTask; } }, Page, animated: false));
                var upper = Require(await host.PushAsync(new NavigationRequestOptions(), () => new Model
                {
                    Cleanup = async () =>
                    {
                        order.Add("upper-start"); entered.SetResult(); await release.Task;
                        order.Add("upper-end");
                    }
                }, Page, animated: false));
                var resourceReleased = false;
                lower.Entry.Ownership.RegisterCleanup(() =>
                { resourceReleased = true; order.Add("lower-resource"); return Task.CompletedTask; });
                Task<NavigationOutcome<MauiNavigationPage?>>? reentry = null;
                Task? closing = null, reconciling = null;
                var cancellationOnUi = false;
                // Observe the actual cancellation thread. Capturing WinUI's synchronization
                // context asks CancellationToken to call Send, which WinUI does not support.
                using var registration = lower.Entry.Lifetime.Token.Register(() =>
                {
                    cancellationOnUi = MainThread.IsMainThread;
                    reentry = host.BackAsync(animated: false);
                    closing = host.DisposeAsync().AsTask(); reconciling = host.ReconcileNativeAsync();
                    cancelled.TrySetResult();
                });
                var stack = (NavigationPage)root.Page;
                try
                {
                    await stack.PopAsync(false);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Check(MainThread.IsMainThread && upper.Entry.Lifetime.Token.IsCancellationRequested, "first native cleanup runs on UI");
                    await stack.PopAsync(false);
                    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Check(cancellationOnUi && lower.Entry.Lifetime.Token.IsCancellationRequested && !root.Entry.Lifetime.Token.IsCancellationRequested,
                        "later native pop cancels promptly on UI and retains root");
                    Check(lower.Entry.ViewModel.Dismissals == 0 && !resourceReleased, "later cleanup and resources remain ordered");
                    Check((await reentry!).Status == NavigationStatus.Reentrant, "token callback rejects host navigation reentry");
                    Check(await Rejected(closing!), "token callback rejects host disposal reentry");
                    Check(await Rejected(reconciling!), "token callback rejects reconciliation reentry");
                    release.TrySetResult();
                    await host.ReconcileNativeAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Check(order.SequenceEqual(new[] { "upper-start", "upper-end", "lower", "lower-resource" })
                        && upper.Entry.ViewModel.Dismissals == 1 && lower.Entry.ViewModel.Dismissals == 1, "cleanup and resource release complete once in order");
                    Check(root.Entry.State == NavigationEntryState.Active && MainThread.IsMainThread, "root reactivates on UI after cleanup");
                }
                finally { release.TrySetResult(); }
                Require(await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true));
            }
        }
        finally { options.RetainedViewLifecycleBehavior = previous; }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Page Page(Model model) => new ContentPage { Content = new Label { Text = "Overlapping native removals", Margin = 24 } };
    private static T Require<T>(NavigationOutcome<T> result) where T : class => result.IsSuccess ? result.Value!
        : throw result.Error ?? new InvalidOperationException(result.Status.ToString());
    private static async Task<bool> Rejected(Task task)
    { try { await task; return false; } catch (InvalidOperationException) { return true; } }
    private sealed class Model : INavigationAware
    {
        internal Func<Task>? Cleanup;
        internal int Dismissals;
        public Task DismissAsync(DismissalReason reason) { Dismissals++; return Cleanup?.Invoke() ?? Task.CompletedTask; }
    }
}
