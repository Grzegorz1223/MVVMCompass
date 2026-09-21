using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatcher_refusal_settles_root_requests_without_mutation_and_allows_recovery(bool submitted)
    {
        var dispatcher = new RejectingRootDispatcher();
        DispatcherProvider.SetCurrent(dispatcher);
        try
        {
            var context = await Open<Leaf>(); var original = context.Window.Page;
            dispatcher.Reject = true;
            var pending = submitted ? factory.RequestRoot<Tabs>(context.Window, cancellationToken: Token).Completion
                : factory.SetRoot<Tabs>(context.Window, cancellationToken: Token);
            var result = await Settle(pending);
            Assert.Equal(NavigationStatus.Failed, result.Status);
            Assert.False(result.HasCommitted); Assert.IsType<InvalidOperationException>(result.Error);
            Assert.Same(original, context.Window.Page);
            dispatcher.Reject = false;
            Success(await Settle(factory.SetRoot<Tabs>(context.Window, cancellationToken: Token)));
        }
        finally { dispatcher.Reject = false; TestDispatcher.Initialize(); }
    }

    private sealed class RejectingRootDispatcher : IDispatcher, IDispatcherProvider
    {
        internal bool Reject;
        public IDispatcher GetForCurrentThread() => this;
        public bool IsDispatchRequired => false;
        public bool Dispatch(Action action) { if (Reject) return false; action(); return true; }
        public bool DispatchDelayed(TimeSpan delay, Action action) => throw new NotSupportedException();
        public IDispatcherTimer CreateTimer() => throw new NotSupportedException();
    }

    private static RootTransitionOptions Enforced => new() { Mode = RootTransitionMode.Enforced };
    private static TaskCompletionSource RootSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<T> Settle<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);

    [Fact]
    public async Task Application_roots_guard_retained_screens_and_enforced_roots_release_every_scope()
    {
        var context = await Open<Tabs>();
        var tabs = (Tabs)context.Current!.ViewModel;
        var first = (Leaf)context.Deepest.Current!.ViewModel;
        Success(await tabs.Navigation.Select("archive", cancellationToken: Token));
        var second = (Leaf)context.Deepest.Current!.ViewModel;
        first.Allowed = false;
        var rejected = await factory.SetRoot<Leaf>(context.Window, cancellationToken: Token);
        Assert.Equal(NavigationStatus.GuardRejected, rejected.Status);
        Assert.False(rejected.HasCommitted);
        Assert.Same(tabs, context.Current.ViewModel);
        Assert.False(first.Resource.Disposed);
        Success(await factory.SetRoot<Leaf>(context.Window, options: Enforced, cancellationToken: Token));
        foreach (var model in new Leaf[] { tabs, first, second })
        {
            Assert.True(model.Resource.Disposed);
            Assert.Equal(1, model.Dismissals);
            Assert.Equal(DismissalReason.RootReplaced, model.Lifetime.Reason);
        }
        Assert.Equal(NavigationStatus.InvalidOrigin, (await first.Navigation.SetRoot<Tabs>(cancellationToken: Token)).Status);
    }

    [Fact]
    public async Task Awaited_root_from_a_guard_is_reentrant_and_submits_nothing()
    {
        var context = await Open<Leaf>();
        var leaf = (Leaf)context.Current!.ViewModel;
        leaf.Guard = async () =>
        {
            Assert.Equal(NavigationStatus.Reentrant,
                (await factory.SetRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token)).Status);
            return false;
        };
        Assert.Equal(NavigationStatus.GuardRejected, (await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token)).Status);
        Assert.Same(leaf, context.Host.CurrentContentNavigation!.Current!.ViewModel);
        Assert.False(leaf.Resource.Disposed);
    }

    [Fact]
    public async Task Enforced_submission_inside_a_rejecting_guard_supersedes_without_deadlock()
    {
        var context = await Open<Leaf>();
        var leaf = (Leaf)context.Current!.ViewModel;
        RootTransitionHandle? request = null;
        leaf.Guard = () =>
        {
            request = factory.RequestRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token);
            Assert.False(request.Completion.IsCompleted);
            return Task.FromResult(false);
        };
        Assert.Equal(NavigationStatus.Superseded, (await Settle(leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token))).Status);
        Success(await Settle(request!.Completion));
        Assert.IsType<Tabs>(context.Host.CurrentContentNavigation!.Current!.ViewModel);
        Assert.True(leaf.Resource.Disposed);
    }

    [Fact]
    public async Task Submission_from_awaited_activation_waits_for_the_committed_operation()
    {
        var context = await Open<Leaf>();
        var actions = services.GetRequiredService<RootActions>();
        RootTransitionHandle? request = null;
        actions.Appear = _ =>
        {
            request = factory.RequestRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token);
            Assert.False(request.Completion.IsCompleted);
            return Task.CompletedTask;
        };
        var result = await Settle(factory.SetRoot<ApplicationRoot>(context.Window, cancellationToken: Token));
        Success(result);
        Assert.True(result.HasCommitted);
        Success(await Settle(request!.Completion));
        Assert.True(Assert.Single(actions.Created).Resource.Disposed);
        Assert.IsType<Tabs>(context.Host.CurrentContentNavigation!.Current!.ViewModel);
    }

    [Fact]
    public async Task Superseded_initialization_cannot_install_a_late_failure_page()
    {
        var entered = RootSignal(); var release = RootSignal();
        var actions = services.GetRequiredService<RootActions>();
        actions.Before = async _ => { entered.TrySetResult(); await release.Task; throw new InvalidOperationException("late initialization"); };
        var window = factory.CreateWindow<ApplicationRoot>();
        var host = factory.ForWindow(window); hosts.Add(host);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var request = factory.RequestRoot<Leaf>(window, new() { ["filter"] = "blocked" }, Enforced, Token);
        release.TrySetResult();
        Assert.Equal(NavigationStatus.Superseded, (await Settle(factory.WaitForInitializationAsync(window, Token))).Status);
        Success(await Settle(request.Completion));
        Assert.Equal("blocked", ((Leaf)host.CurrentContentNavigation!.Current!.ViewModel).Filter);
        Assert.Same(host.CurrentRoot!.Page, window.Page);
        Assert.True(Assert.Single(actions.Created).Resource.Disposed);
    }

    [Fact]
    public async Task Parameters_are_snapshotted_and_queued_cancellation_settles_without_preparing()
    {
        var context = await Open<Leaf>();
        var entered = RootSignal(); var release = RootSignal();
        var actions = services.GetRequiredService<RootActions>();
        actions.Before = async _ => { entered.TrySetResult(); await release.Task; };
        var current = factory.SetRoot<ApplicationRoot>(context.Window, options: Enforced, cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var parameters = new Dictionary<string, object> { ["filter"] = "captured" };
        var request = factory.RequestRoot<Leaf>(context.Window, parameters, cancellationToken: Token);
        using var cancel = new CancellationTokenSource();
        var cancelled = factory.RequestRoot<ApplicationRoot>(context.Window, cancellationToken: cancel.Token);
        parameters["filter"] = "mutated";
        cancel.Cancel();
        Assert.Equal(NavigationStatus.Cancelled, (await Settle(cancelled.Completion)).Status);
        release.TrySetResult();
        Success(await Settle(current)); Success(await Settle(request.Completion));
        Assert.Single(actions.Created);
        Assert.Equal("captured", ((Leaf)context.Host.CurrentContentNavigation!.Current!.ViewModel).Filter);
    }

    [Fact]
    public async Task Initialization_wait_cancellation_does_not_cancel_initialization()
    {
        var release = RootSignal();
        services.GetRequiredService<RootActions>().Before = _ => release.Task;
        var window = factory.CreateWindow<ApplicationRoot>(); hosts.Add(factory.ForWindow(window));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.WaitForInitializationAsync(window, cancellation.Token));
        release.TrySetResult();
        Success(await Settle(factory.WaitForInitializationAsync(window, Token)));
    }

    [Fact]
    public async Task Closing_during_preparation_settles_all_requests_and_never_installs_the_candidate()
    {
        var context = await Open<Leaf>(); var originalPage = context.Window.Page;
        var release = RootSignal(); var entered = RootSignal();
        var actions = services.GetRequiredService<RootActions>();
        actions.Before = async _ => { entered.TrySetResult(); await release.Task; };
        var preparing = factory.RequestRoot<ApplicationRoot>(context.Window, cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var pending = factory.RequestRoot<Tabs>(context.Window, cancellationToken: Token);
        var closing = context.Host.DisposeAsync().AsTask();
        release.TrySetResult();
        Assert.False((await Settle(preparing.Completion)).IsSuccess);
        Assert.False((await Settle(pending.Completion)).IsSuccess);
        await closing.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Same(originalPage, context.Window.Page);
        Assert.True(Assert.Single(actions.Created).Resource.Disposed);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await factory.SetRoot<Leaf>(context.Window, cancellationToken: Token)).Status);
    }

    [Fact]
    public async Task Application_roots_report_committed_activation_and_cleanup_failures()
    {
        var context = await Open<ApplicationRoot>();
        var actions = services.GetRequiredService<RootActions>();
        actions.Cleanup = _ => throw new InvalidOperationException("dispose failed");
        var cleanupFailure = await factory.SetRoot<Leaf>(context.Window, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, cleanupFailure.Status);
        Assert.True(cleanupFailure.HasCommitted);
        Assert.NotEmpty(cleanupFailure.CleanupErrors);
        Assert.IsType<Leaf>(context.Host.CurrentContentNavigation!.Current!.ViewModel);
        actions.Cleanup = null;
        actions.Appear = _ => throw new InvalidOperationException("activation failed");
        var activationFailure = await factory.SetRoot<ApplicationRoot>(context.Window, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, activationFailure.Status);
        Assert.True(activationFailure.HasCommitted);
        Assert.NotNull(activationFailure.Error);
        Assert.IsType<ApplicationRoot>(context.Host.CurrentContentNavigation!.Current!.ViewModel);
    }

    [Fact]
    public async Task Window_validation_and_enforced_policy_do_not_affect_other_windows()
    {
        var first = await Open<Leaf>(); var second = await Open<Leaf>();
        Assert.Throws<ArgumentException>(() => factory.RequestRoot<Leaf>(new Window(new ContentPage()), cancellationToken: Token));
        Assert.Throws<ArgumentNullException>(() => { _ = factory.SetRoot<Leaf>(null!, cancellationToken: Token); });
        Assert.Throws<ArgumentException>(() => factory.RequestRoot<Leaf>(first.Window, options: Enforced with { CoalescingKey = "blocked" }, cancellationToken: Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = factory.SetRoot<Leaf>(first.Window, options: new() { Mode = (RootTransitionMode)99 }, cancellationToken: Token); });
        ((Leaf)second.Current!.ViewModel).Allowed = false;
        Success(await factory.SetRoot<Tabs>(first.Window, options: Enforced, cancellationToken: Token));
        Assert.False(second.IsClosed);
        Assert.False(((Leaf)second.Current.ViewModel).Resource.Disposed);
    }

    public sealed class RootActions
    {
        public List<ApplicationRoot> Created { get; } = [];
        public Func<ApplicationRoot, Task>? Before, Appear, Cleanup;
    }

    public sealed class ApplicationRoot : Leaf
    {
        private readonly RootActions actions;
        public ApplicationRoot(INavigationService navigation, Resource resource, RootActions actions) : base(navigation, resource)
        { this.actions = actions; actions.Created.Add(this); }
        public override Task BeforeFirstShown() => actions.Before?.Invoke(this) ?? Task.CompletedTask;
        public override Task Appearing() => actions.Appear?.Invoke(this) ?? Task.CompletedTask;
        public override async Task AfterDismissed() { await base.AfterDismissed(); if (actions.Cleanup != null) await actions.Cleanup(this); }
    }
    public sealed class ApplicationRootView(ApplicationRoot model) : ViewBase<ApplicationRoot>(model);
}
