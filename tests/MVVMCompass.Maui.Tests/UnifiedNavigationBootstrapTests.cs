using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Theory]
    [InlineData("initial-failure")]
    [InlineData("cancelled-normal")]
    [InlineData("failed-enforced")]
    [InlineData("cancelled-enforced")]
    [InlineData("successful-enforced")]
    public Task Bootstrap_settles_after_initialization_and_its_successor(string scenario) => OnRootUI(async () =>
    {
        var entered = RootSignal(); var release = RootSignal();
        var actions = services.GetRequiredService<RootActions>();
        actions.Before = async _ => { entered.TrySetResult(); await release.Task; throw new InvalidOperationException("initial preparation"); };
        var window = factory.CreateWindow<ApplicationRoot>();
        var host = factory.ForWindow(window); hosts.Add(host);
        var bootstrap = window.Page;
        RootTransitionHandle? successor = null;
        using var cancel = new CancellationTokenSource();
        try
        {
            await Settle(entered.Task);
            if (scenario == "failed-enforced")
                successor = factory.RequestRoot<ApplicationRoot>(window, options: Enforced, cancellationToken: Token);
            else if (scenario != "initial-failure")
                successor = factory.RequestRoot<Leaf>(window, options: scenario == "cancelled-normal" ? null : Enforced,
                    cancellationToken: cancel.Token);
            if (scenario.StartsWith("cancelled", StringComparison.Ordinal))
            {
                cancel.Cancel();
                var cancelled = await Settle(successor!.Completion);
                Assert.Equal(NavigationStatus.Cancelled, cancelled.Status);
                Assert.False(cancelled.HasCommitted);
                Assert.Same(bootstrap, window.Page);
            }
        }
        finally { release.TrySetResult(); }

        var initial = await Settle(factory.WaitForInitializationAsync(window, Token));
        Assert.Equal(scenario.Contains("enforced", StringComparison.Ordinal) ? NavigationStatus.Superseded : NavigationStatus.Failed,
            initial.Status);
        Assert.False(initial.HasCommitted);
        if (successor != null)
        {
            var result = await Settle(successor.Completion);
            Assert.Equal(scenario == "successful-enforced" ? NavigationStatus.Completed : scenario == "failed-enforced"
                ? NavigationStatus.Failed : NavigationStatus.Cancelled, result.Status);
            Assert.Equal(scenario == "successful-enforced", result.HasCommitted);
        }
        Assert.All(actions.Created, model => { Assert.True(model.Resource.Disposed); Assert.Equal(1, model.Dismissals); });
        if (scenario == "successful-enforced") Assert.IsType<Leaf>(host.CurrentContentNavigation!.Current!.ViewModel);
        else AssertBootstrapFailure(window, host);

        Success(await Settle(factory.SetRoot<Leaf>(window, new() { ["filter"] = "recovered" }, cancellationToken: Token)));
        Assert.Equal("recovered", ((Leaf)host.CurrentContentNavigation!.Current!.ViewModel).Filter);
        Assert.Equal(initial, await factory.WaitForInitializationAsync(window, Token));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Bootstrap_waits_for_all_successors_before_recovery(bool lastSucceeds) => OnRootUI(async () =>
    {
        var initialEntered = RootSignal(); var initialRelease = RootSignal();
        var successorEntered = RootSignal(); var successorRelease = RootSignal();
        var actions = services.GetRequiredService<RootActions>();
        actions.Before = async model =>
        {
            var initial = ReferenceEquals(model, actions.Created[0]);
            (initial ? initialEntered : successorEntered).TrySetResult();
            await (initial ? initialRelease : successorRelease).Task;
            throw new InvalidOperationException(initial ? "initial" : "successor");
        };
        var window = factory.CreateWindow<ApplicationRoot>();
        var host = factory.ForWindow(window); hosts.Add(host);
        var bootstrap = window.Page;
        RootTransitionHandle? failing = null, last = null;
        using var cancel = new CancellationTokenSource();
        try
        {
            await Settle(initialEntered.Task);
            failing = factory.RequestRoot<ApplicationRoot>(window, cancellationToken: Token);
            last = factory.RequestRoot<Leaf>(window, cancellationToken: cancel.Token);
            if (!lastSucceeds)
            {
                cancel.Cancel();
                Assert.Equal(NavigationStatus.Cancelled, (await Settle(last.Completion)).Status);
            }
            initialRelease.TrySetResult();
            await Settle(successorEntered.Task);
            Assert.Same(bootstrap, window.Page);
        }
        finally { initialRelease.TrySetResult(); successorRelease.TrySetResult(); }
        Assert.Equal(NavigationStatus.Failed, (await Settle(factory.WaitForInitializationAsync(window, Token))).Status);
        Assert.Equal(NavigationStatus.Failed, (await Settle(failing!.Completion)).Status);
        var result = await Settle(last!.Completion);
        Assert.Equal(lastSucceeds ? NavigationStatus.Completed : NavigationStatus.Cancelled, result.Status);
        if (lastSucceeds) Assert.IsType<Leaf>(host.CurrentContentNavigation!.Current!.ViewModel);
        else AssertBootstrapFailure(window, host);
        Assert.Equal(2, actions.Created.Count);
        Assert.All(actions.Created, model => { Assert.True(model.Resource.Disposed); Assert.Equal(1, model.Dismissals); });
    });

    [Theory]
    [InlineData(RootTransitionMode.Normal, false)]
    [InlineData(RootTransitionMode.Normal, true)]
    [InlineData(RootTransitionMode.Enforced, false)]
    [InlineData(RootTransitionMode.Enforced, true)]
    public async Task Bootstrap_recovery_rechecks_a_new_request_before_installing(RootTransitionMode mode, bool successorFails)
    {
        var dispatcher = new BootstrapDispatcher();
        DispatcherProvider.SetCurrent(dispatcher);
        Action? resume = null;
        try
        {
            var actions = services.GetRequiredService<RootActions>();
            actions.Before = _ => { Interlocked.Exchange(ref dispatcher.HoldNext, 1); throw new InvalidOperationException("initial"); };
            var window = factory.CreateWindow<ApplicationRoot>();
            var host = factory.ForWindow(window); hosts.Add(host);
            resume = await Settle(dispatcher.Held.Task);
            var errorPages = 0;
            window.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(Window.Page) && window.Page is ContentPage { Content: Label }) errorPages++;
            };
            var successor = successorFails ? factory.RequestRoot<Failing>(window, options: new() { Mode = mode }, cancellationToken: Token)
                : factory.RequestRoot<Leaf>(window, options: new() { Mode = mode }, cancellationToken: Token);
            resume(); resume = null;
            Assert.Equal(NavigationStatus.Failed, (await Settle(factory.WaitForInitializationAsync(window, Token))).Status);
            var result = await Settle(successor.Completion);
            Assert.Equal(successorFails ? NavigationStatus.Failed : NavigationStatus.Completed, result.Status);
            if (successorFails) AssertBootstrapFailure(window, host);
            else Assert.IsType<Leaf>(host.CurrentContentNavigation!.Current!.ViewModel);
            Assert.Equal(successorFails ? 1 : 0, errorPages);
            Assert.True(Assert.Single(actions.Created).Resource.Disposed);
        }
        finally { resume?.Invoke(); TestDispatcher.Initialize(); }
    }

    [Theory]
    [InlineData(RootTransitionMode.Normal)]
    [InlineData(RootTransitionMode.Enforced)]
    public async Task Bootstrap_cancelled_successor_during_recovery_still_finishes_bootstrap(RootTransitionMode mode)
    {
        var dispatcher = new BootstrapDispatcher();
        DispatcherProvider.SetCurrent(dispatcher);
        Action? resume = null;
        try
        {
            var actions = services.GetRequiredService<RootActions>();
            actions.Before = _ => { Interlocked.Exchange(ref dispatcher.HoldNext, 1); throw new InvalidOperationException("initial"); };
            var window = factory.CreateWindow<ApplicationRoot>();
            var host = factory.ForWindow(window); hosts.Add(host);
            resume = await Settle(dispatcher.Held.Task);
            using var cancel = new CancellationTokenSource();
            var successor = factory.SetRoot<Leaf>(window, options: new() { Mode = mode }, cancellationToken: cancel.Token);
            cancel.Cancel();
            resume(); resume = null;
            var initial = await Settle(factory.WaitForInitializationAsync(window, Token));
            var result = await Settle(successor);
            Assert.Equal(NavigationStatus.Failed, initial.Status);
            Assert.Equal(NavigationStatus.Cancelled, result.Status);
            Assert.False(initial.HasCommitted); Assert.False(result.HasCommitted);
            AssertBootstrapFailure(window, host);
            Assert.True(Assert.Single(actions.Created).Resource.Disposed);
            Assert.Equal(1, actions.Created[0].Dismissals);
        }
        finally { resume?.Invoke(); TestDispatcher.Initialize(); }
    }

    [Fact]
    public Task Bootstrap_closure_settles_initialization_and_successors_without_installing_recovery() => OnRootUI(async () =>
    {
        var entered = RootSignal(); var release = RootSignal();
        var actions = services.GetRequiredService<RootActions>();
        actions.Before = async _ => { entered.TrySetResult(); await release.Task; throw new InvalidOperationException("initial"); };
        var window = factory.CreateWindow<ApplicationRoot>();
        var host = factory.ForWindow(window); hosts.Add(host);
        var bootstrap = window.Page;
        await Settle(entered.Task);
        var successor = factory.RequestRoot<Leaf>(window, options: Enforced, cancellationToken: Token);
        var closing = host.DisposeAsync().AsTask();
        release.TrySetResult();
        Assert.False((await Settle(factory.WaitForInitializationAsync(window, Token))).HasCommitted);
        Assert.False((await Settle(successor.Completion)).HasCommitted);
        await Settle(closing);
        Assert.Same(bootstrap, window.Page);
        Assert.True(Assert.Single(actions.Created).Resource.Disposed);
        Assert.Equal(1, actions.Created[0].Dismissals);
    });

    [Fact]
    public Task Bootstrap_recovery_preserves_preparation_and_cleanup_errors() => OnRootUI(async () =>
    {
        var actions = services.GetRequiredService<RootActions>();
        actions.Before = _ => throw new InvalidOperationException("initial");
        actions.Cleanup = _ => throw new InvalidOperationException("cleanup");
        var window = factory.CreateWindow<ApplicationRoot>();
        var host = factory.ForWindow(window); hosts.Add(host);
        var result = await Settle(factory.WaitForInitializationAsync(window, Token));
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.False(result.HasCommitted);
        Assert.Equal("initial", result.Error!.Message);
        Assert.Contains(result.CleanupErrors, error => error.ToString().Contains("cleanup", StringComparison.Ordinal));
        AssertBootstrapFailure(window, host);
        Assert.True(Assert.Single(actions.Created).Resource.Disposed);
        Assert.Equal(1, actions.Created[0].Dismissals);
    });

    [Fact]
    public Task Bootstrap_does_not_replace_an_installed_root_whose_activation_failed() => OnRootUI(async () =>
    {
        var actions = services.GetRequiredService<RootActions>();
        actions.Appear = _ => throw new InvalidOperationException("activation");
        var window = factory.CreateWindow<ApplicationRoot>();
        var host = factory.ForWindow(window); hosts.Add(host);
        var result = await Settle(factory.WaitForInitializationAsync(window, Token));
        Assert.Equal(NavigationStatus.Failed, result.Status); Assert.True(result.HasCommitted);
        Assert.Same(host.CurrentRoot!.Page, window.Page);
        Assert.Same(Assert.Single(actions.Created), host.CurrentContentNavigation!.Current!.ViewModel);
        Assert.False(actions.Created[0].Resource.Disposed);
    });

    [Fact]
    public async Task Bootstrap_dispatch_failure_settles_and_reports_recovery_failure()
    {
        var dispatcher = new RejectingRootDispatcher { Reject = true };
        DispatcherProvider.SetCurrent(dispatcher);
        try
        {
            var window = factory.CreateWindow<Leaf>();
            hosts.Add(factory.ForWindow(window));
            var result = await Settle(factory.WaitForInitializationAsync(window, Token));
            Assert.Equal(NavigationStatus.Failed, result.Status); Assert.False(result.HasCommitted);
            Assert.NotNull(result.Error); Assert.Single(result.CleanupErrors);
            dispatcher.Reject = false;
            Success(await Settle(factory.SetRoot<Leaf>(window, cancellationToken: Token)));
        }
        finally { dispatcher.Reject = false; TestDispatcher.Initialize(); }
    }

    private static Task Settle(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private static void AssertBootstrapFailure(Window window, MauiNavigationHost host)
    {
        Assert.Null(host.CurrentRoot);
        var page = Assert.IsType<ContentPage>(window.Page);
        Assert.Contains("could not open", Assert.IsType<Label>(page.Content).Text, StringComparison.Ordinal);
    }

    private sealed class BootstrapDispatcher : IDispatcher, IDispatcherProvider
    {
        internal int HoldNext;
        internal TaskCompletionSource<Action> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDispatcher GetForCurrentThread() => this;
        public bool IsDispatchRequired => false;
        public bool Dispatch(Action action)
        {
            if (Interlocked.Exchange(ref HoldNext, 0) == 1) Held.TrySetResult(action);
            else action();
            return true;
        }
        public bool DispatchDelayed(TimeSpan delay, Action action) => throw new NotSupportedException();
        public IDispatcherTimer CreateTimer() => throw new NotSupportedException();
    }
}
