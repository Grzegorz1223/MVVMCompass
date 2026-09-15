using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class CancellationNavigationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);

    [Fact]
    public Task Native_confirmation_during_root_cleanup_preserves_the_uninstalled_candidate() => Run(async () =>
    {
        var previous = Application.Current; var application = new TestApplication();
        var host = Host(application.Add(), RetainedViewLifecycleBehavior.Deactivate);
        var entered = Signal(); var release = Signal();
        var outgoing = new object(); var incoming = new object(); var child = new Model();
        var tabs = new TabbedPage { BindingContext = incoming }; tabs.Children.Add(new ModelPage(child));
        try
        {
            Assert.True((await host.ReplaceRootAsync(new NavigationRequestOptions(), () => outgoing,
                _ => new ContentPage(), cleanup: async _ => { entered.SetResult(); await release.Task; }, cancellationToken: Token)).IsSuccess);
            var replacement = host.ReplaceRootAsync(new NavigationRequestOptions(), () => incoming,
                _ => tabs, cancellationToken: Token);
            await Await(entered.Task);
            var reconciliation = host.ReconcileNativeAsync();
            var posted = Signal(); SynchronizationContext.Current!.Post(_ => posted.SetResult(), null);
            await Await(posted.Task);
            Assert.False(child.Lifetime.Token.IsCancellationRequested);
            release.TrySetResult();
            var result = await replacement;
            Assert.True(result.IsSuccess, $"{result.Status}: {result.Error}");
            await Await(reconciliation);
            Assert.False(result.Value!.Entry.Lifetime.IsDismissed); Assert.False(child.IsDismissed);
        }
        finally
        {
            release.TrySetResult();
            try { await Await(host.DisposeAsync().AsTask()); }
            finally { Application.Current = previous; }
        }
    });

    public static IEnumerable<object[]> Removals()
    {
        foreach (var profile in Enum.GetValues<RetainedViewLifecycleBehavior>())
            foreach (var explicitHost in new[] { false, true })
                foreach (var direct in new[] { false, true }) yield return [profile, explicitHost, direct];
    }

    [Theory]
    [MemberData(nameof(Removals))]
    public Task Later_removal_cancels_before_earlier_cleanup_finishes(int profileValue, bool explicitHost, bool direct) => Run(async () =>
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var previous = Application.Current;
        var application = new TestApplication();
        var window = application.Add();
        MauiNavigationHost? host = null;
        NavigationPage stack;
        if (explicitHost)
        {
            host = Host(window, profile);
            var result = await host.ReplaceRootAsync<Model>(new(null), true, cancellationToken: Token);
            Assert.True(result.IsSuccess, result.Error?.ToString());
            stack = (NavigationPage)result.Value!.Page; stack.Handler = new TestNavigationHandler();
        }
        else { stack = TestNavigationHandler.Create(new ContentPage()); window.Page = stack; NativeNavigationObserver.Attach(stack); }
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        var order = new List<string>();
        var lower = new Model { Cleanup = () => { order.Add("lower"); return Task.CompletedTask; } };
        var upper = new Model { Cleanup = async () => { order.Add("upper-start"); entered.SetResult(); await release.Task; order.Add("upper-end"); } };
        var lowerPage = new ModelPage(lower);
        using var registration = lower.Lifetime.Token.Register(() => cancelled.TrySetResult());
        Task<NavigationOutcome<MauiNavigationPage?>>? reentry = null;
        Task? dispose = null, reconcile = null;
        using var guard = lower.Lifetime.Token.Register(() =>
        {
            if (host == null) return;
            reentry = host.BackAsync(animated: false, cancellationToken: Token);
            dispose = host.DisposeAsync().AsTask(); reconcile = host.ReconcileNativeAsync();
        });
        try
        {
            await stack.PushAsync(lowerPage, false);
            if (direct) await stack.PushAsync(new ContentPage(), false);
            await stack.PushAsync(new ModelPage(upper), false);
            if (host != null) await Await(host.ReconcileNativeAsync());
            await stack.PopAsync(false); await Await(entered.Task);
            if (direct) stack.Navigation.RemovePage(lowerPage);
            else await stack.PopAsync(false);
            await Await(cancelled.Task);
            Assert.True(lower.IsDismissed); Assert.Equal(0, lower.Dismissals);
            Assert.Equal(new[] { "upper-start" }, order);
            var completion = host?.ReconcileNativeAsync() ?? NativeNavigationObserver.Completion(stack);
            Assert.False(completion.IsCompleted);
            if (host != null)
            {
                Assert.Equal(NavigationStatus.Reentrant, (await reentry!).Status);
                await Assert.ThrowsAsync<InvalidOperationException>(() => dispose!);
                await Assert.ThrowsAsync<InvalidOperationException>(() => reconcile!);
                Assert.False(host.CurrentRoot!.Entry.Lifetime.Token.IsCancellationRequested);
            }
            release.TrySetResult(); await Await(completion);
            Assert.Equal(new[] { "upper-start", "upper-end", "lower" }, order);
            Assert.Equal(1, lower.Dismissals); Assert.Equal(1, upper.Dismissals);
        }
        finally
        {
            release.TrySetResult();
            try { if (host != null) await Await(host.DisposeAsync().AsTask()); else NativeNavigationObserver.Detach(stack); }
            finally { Application.Current = previous; }
        }
    });

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate, "tab")]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed, "tab")]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate, "root")]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed, "root")]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate, "window")]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed, "window")]
    public Task Confirmed_tab_root_and_window_removal_signal_while_cleanup_is_queued(int profileValue, string removal) => Run(async () =>
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var previous = Application.Current; var application = new TestApplication();
        var window = application.Add(); var host = Host(window, profile);
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        var first = new Model { Cleanup = async () => { entered.SetResult(); await release.Task; } };
        var second = new Model(); var retained = new Model();
        var firstPage = new ModelPage(first); var secondPage = new ModelPage(second);
        var tabs = new TabbedPage(); tabs.Children.Add(firstPage); tabs.Children.Add(secondPage); tabs.Children.Add(new ModelPage(retained));
        using var registration = second.Lifetime.Token.Register(() => cancelled.TrySetResult());
        try
        {
            Assert.True((await host.ReplaceRootAsync(new NavigationRequestOptions(), () => new object(), _ => tabs, cancellationToken: Token)).IsSuccess);
            tabs.Children.Remove(firstPage);
            var pending = host.ReconcileNativeAsync();
            await Await(entered.Task);
            Task? closing = null;
            if (removal == "tab") tabs.Children.Remove(secondPage);
            else if (removal == "root") window.Page = new ContentPage();
            else closing = host.DisposeAsync().AsTask();
            await Await(cancelled.Task);
            Assert.True(second.IsDismissed); Assert.Equal(0, second.Dismissals);
            if (removal == "tab") Assert.False(retained.Lifetime.Token.IsCancellationRequested);
            else Assert.True(retained.Lifetime.Token.IsCancellationRequested);
            release.TrySetResult(); await Await(pending);
            if (closing != null) await Await(closing); else await Await(host.ReconcileNativeAsync());
            Assert.Equal(1, first.Dismissals); Assert.Equal(1, second.Dismissals);
        }
        finally
        {
            release.TrySetResult();
            try { await Await(host.DisposeAsync().AsTask()); }
            finally { Application.Current = previous; }
        }
    });

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public Task Later_native_modal_cancels_while_previous_modal_cleanup_is_pending(int profileValue) => Run(async () =>
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var previous = Application.Current; var application = new TestApplication();
        var window = application.Add(); var host = Host(window, profile);
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        var lower = new Model(); var upper = new Model { Cleanup = async () => { entered.SetResult(); await release.Task; } };
        using var registration = lower.Lifetime.Token.Register(() => cancelled.TrySetResult());
        try
        {
            Assert.True((await host.ReplaceRootAsync<Model>(new(null), cancellationToken: Token)).IsSuccess);
            await window.Navigation.PushModalAsync(new ModelPage(lower), false); await Await(host.ReconcileNativeAsync());
            await window.Navigation.PushModalAsync(new ModelPage(upper), false); await Await(host.ReconcileNativeAsync());
            await window.Navigation.PopModalAsync(false); await Await(entered.Task);
            await window.Navigation.PopModalAsync(false); await Await(cancelled.Task);
            Assert.Equal(0, lower.Dismissals);
            release.TrySetResult(); await Await(host.ReconcileNativeAsync());
            Assert.Equal(1, lower.Dismissals); Assert.Equal(1, upper.Dismissals);
        }
        finally
        {
            release.TrySetResult();
            try { await Await(host.DisposeAsync().AsTask()); }
            finally { Application.Current = previous; }
        }
    });

    private static MauiNavigationHost Host(Window window, RetainedViewLifecycleBehavior profile) =>
        new MauiNavigationHostFactory(new Locator(), new() { RetainedViewLifecycleBehavior = profile }).ForWindow(window);

    [Fact]
    public Task Background_window_disposal_signals_and_cleans_on_the_owning_dispatcher() => Run(async () =>
    {
        var previous = Application.Current; var application = new TestApplication();
        var host = Host(application.Add(), RetainedViewLifecycleBehavior.Deactivate);
        var ownerThread = Environment.CurrentManagedThreadId;
        var callbackThread = 0; var cleanupThread = 0;
        Task? reentry = null;
        try
        {
            var root = await host.ReplaceRootAsync<Model>(new(null), cancellationToken: Token);
            Assert.True(root.IsSuccess);
            root.Value!.Entry.ViewModel.Cleanup = () => { cleanupThread = Environment.CurrentManagedThreadId; return Task.CompletedTask; };
            using var registration = root.Value.Entry.Lifetime.Token.Register(() =>
            {
                callbackThread = Environment.CurrentManagedThreadId;
                reentry = host.DisposeAsync().AsTask();
            }, useSynchronizationContext: true);
            await Await(Task.Run(() => host.DisposeAsync().AsTask(), Token));
            Assert.Equal(ownerThread, callbackThread); Assert.Equal(ownerThread, cleanupThread);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reentry!);
            Assert.Equal(1, root.Value.Entry.ViewModel.Dismissals);
        }
        finally
        {
            try { await Await(host.DisposeAsync().AsTask()); }
            finally { Application.Current = previous; }
        }
    });

    [Fact]
    public Task Reinserted_tab_stays_live_while_an_earlier_tab_is_cleaning_up() => Run(async () =>
    {
        var previous = Application.Current; var application = new TestApplication();
        var host = Host(application.Add(), RetainedViewLifecycleBehavior.Deactivate);
        var entered = Signal(); var release = Signal(); var cancelled = Signal();
        var first = new Model { Cleanup = async () => { entered.SetResult(); await release.Task; } };
        var second = new Model(); var firstPage = new ModelPage(first); var secondPage = new ModelPage(second);
        var tabs = new TabbedPage(); tabs.Children.Add(firstPage); tabs.Children.Add(secondPage); tabs.Children.Add(new ContentPage());
        using var registration = second.Lifetime.Token.Register(() => cancelled.TrySetResult());
        try
        {
            Assert.True((await host.ReplaceRootAsync(new NavigationRequestOptions(), () => new object(), _ => tabs, cancellationToken: Token)).IsSuccess);
            tabs.Children.Remove(firstPage); var cleanup = host.ReconcileNativeAsync(); await Await(entered.Task);
            tabs.Children.Remove(secondPage); tabs.Children.Insert(0, secondPage);
            await Task.Yield(); await Task.Yield();
            Assert.False(second.IsDismissed); Assert.False(second.Lifetime.Token.IsCancellationRequested);
            tabs.Children.Remove(secondPage); await Await(cancelled.Task);
            Assert.Equal(0, second.Dismissals);
            release.TrySetResult(); await Await(cleanup); await Await(host.ReconcileNativeAsync());
            Assert.Equal(1, second.Dismissals);
        }
        finally
        {
            release.TrySetResult();
            try { await Await(host.DisposeAsync().AsTask()); }
            finally { Application.Current = previous; }
        }
    });

    [Fact]
    public Task Cancelled_legacy_transition_keeps_the_lifetime_live() => Run(async () =>
    {
        var previous = Application.Current; var application = new TestApplication();
        var window = application.Add(); var stack = TestNavigationHandler.Create(new ContentPage()); window.Page = stack;
        var model = new Model(); var page = new ModelPage(model);
        await stack.PushAsync(page, false); NativeNavigationObserver.Attach(stack);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = (INavigationPageController)stack;
        void Pause(object? sender, NavigationRequestedEventArgs args) => args.Task = pending.Task;
        controller.PopRequested += Pause;
        try
        {
            var popping = controller.PopAsyncInner(true, false);
            await Task.Yield();
            Assert.False(model.IsDismissed); Assert.False(model.Lifetime.Token.IsCancellationRequested);
            pending.TrySetResult(false);
            var error = await Record.ExceptionAsync(() => Await(popping));
            if (error != null)
            {
                Assert.IsType<NotImplementedException>(error);
                Assert.Contains("Microsoft.Maui.Platform.ViewExtensions.OnUnloaded", error.StackTrace);
            }
            await Await(NativeNavigationObserver.Completion(stack));
            Assert.Contains(page, stack.Navigation.NavigationStack);
            Assert.False(model.IsDismissed); Assert.Equal(0, model.Dismissals);
        }
        finally
        {
            pending.TrySetResult(false); controller.PopRequested -= Pause;
            NativeNavigationObserver.Detach(stack); Application.Current = previous;
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Native_cancellation_failure_is_reported_once_and_cleanup_finishes(bool explicitHost) => Run(async () =>
    {
        var previous = Application.Current; var application = new TestApplication(); var window = application.Add();
        var host = explicitHost ? Host(window, RetainedViewLifecycleBehavior.Deactivate) : null;
        NavigationPage stack;
        if (host != null)
        {
            var root = await host.ReplaceRootAsync<Model>(new(null), true, cancellationToken: Token);
            Assert.True(root.IsSuccess); stack = (NavigationPage)root.Value!.Page; stack.Handler = new TestNavigationHandler();
        }
        else { stack = TestNavigationHandler.Create(new ContentPage()); window.Page = stack; NativeNavigationObserver.Attach(stack); }
        var model = new Model(); var failure = new InvalidOperationException("cancel callback");
        var observed = new List<Exception>();
        using var registration = model.Lifetime.Token.Register(() => throw failure);
        void Report(Exception error, string operation) { if (ReferenceEquals(error, failure)) observed.Add(error); }
        NavigationDiagnostics.Error += Report;
        try
        {
            await stack.PushAsync(new ModelPage(model), false);
            if (host != null) await Await(host.ReconcileNativeAsync());
            await stack.PopAsync(false);
            await Await(host?.ReconcileNativeAsync() ?? NativeNavigationObserver.Completion(stack));
            Assert.Equal(1, model.Dismissals); Assert.Single(model.Lifetime.CancellationErrors); Assert.Single(observed);
        }
        finally
        {
            NavigationDiagnostics.Error -= Report;
            try { if (host != null) await Await(host.DisposeAsync().AsTask()); else NativeNavigationObserver.Detach(stack); }
            finally { Application.Current = previous; }
        }
    });

    private static Task Run(Func<Task> action) => TestDispatcher.Run(action);

    private sealed class TestApplication : Application
    {
        internal Window Add() => (Window)((IApplication)this).CreateWindow(null);
        protected override Window CreateWindow(IActivationState? state) => new(new ContentPage());
    }
    private sealed class Model : ViewModelBase
    {
        internal Func<Task>? Cleanup;
        internal int Dismissals;
        public override Task AfterDismissed()
        {
            if (!IsDismissed) return Task.CompletedTask;
            Dismissals++; return Cleanup?.Invoke() ?? Task.CompletedTask;
        }
    }
    private sealed class ModelPage(Model model) : ContentPage, IHasVM
    { public ViewModelBase ViewModel => model; }
    private sealed class Locator : IViewLocator
    {
        public void Initialize(Dictionary<Type, Type> pairs) { }
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => new ModelPage(new Model());
        public VisualElement CreateAndBindVEFor(Type type) => new ModelPage(new Model());
        public Type FindVEForViewModel(Type type) => typeof(ModelPage);
        public Type FindViewModelForVE(Type type) => typeof(Model);
    }
}
