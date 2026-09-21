using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Initial_resolver_constructs_only_the_selected_root_and_waits_for_activation(bool activated) => OnRootUI(async () =>
    {
        var resolve = new TaskCompletionSource<InitialRoot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = RootSignal(); var activate = RootSignal(); var calls = 0;
        var actions = services.GetRequiredService<RootActions>();
        actions.Appear = _ => activate.Task;
        var thread = Environment.CurrentManagedThreadId;
        var loading = new Label { Text = "Brand" };
        var window = factory.CreateWindow(_ =>
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            calls++; entered.TrySetResult(); return resolve.Task;
        }, new() { LoadingContentFactory = () => loading });
        var host = factory.ForWindow(window); hosts.Add(host);
        var bootstrap = Assert.IsType<ContentPage>(window.Page);
        var initialized = factory.WaitForInitializationAsync(window, Token);
        try
        {
            await Settle(entered.Task);
            Assert.Equal(1, calls); Assert.Empty(actions.Created); Assert.Null(host.CurrentRoot);
            Assert.Same(loading, bootstrap.Content);
            Assert.Empty(Descendants(bootstrap).OfType<ActivityIndicator>());
            Assert.False(initialized.IsCompleted);
            resolve.SetResult(activated ? InitialRoot.For<ApplicationRoot>() : InitialRoot.For<Leaf>());
            if (activated)
            {
                while (actions.Created.Count == 0) await Task.Yield();
                Assert.False(initialized.IsCompleted);
            }
        }
        finally { activate.TrySetResult(); }
        Success(await Settle(initialized));
        Assert.Equal(activated ? typeof(ApplicationRoot) : typeof(Leaf), host.CurrentContentNavigation!.Current!.ViewModel.GetType());
        Assert.Equal(activated ? 1 : 0, actions.Created.Count);
        Assert.Null(bootstrap.Content); Assert.Null(loading.Parent); Assert.Equal(1, calls);
    });

    [Fact]
    public Task Initial_root_parameters_are_readonly_snapshots() => OnRootUI(async () =>
    {
        var parameters = new Dictionary<string, object> { ["filter"] = "chosen" };
        var selection = InitialRoot.For<Leaf>(parameters);
        parameters["filter"] = "later";
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object>)selection.Parameters!)["filter"] = "mutation");
        var window = factory.CreateWindow(_ => Task.FromResult(selection));
        var host = factory.ForWindow(window); hosts.Add(host);
        Success(await Settle(factory.WaitForInitializationAsync(window, Token)));
        Assert.Equal("chosen", ((Leaf)host.CurrentContentNavigation!.Current!.ViewModel).Filter);
        Assert.Throws<ArgumentNullException>(() => new InitialRoot(null!));
        Assert.Throws<ArgumentException>(() => new InitialRoot(typeof(string)));
        Assert.Throws<ArgumentException>(() => new InitialRoot(typeof(ViewModelBase)));
    });

    [Theory]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("null-task")]
    [InlineData("null-root")]
    [InlineData("unregistered")]
    public Task Resolver_failure_or_cancellation_releases_bootstrap_and_can_recover(string scenario) => OnRootUI(async () =>
    {
        NavigationResult? failure = null;
        var content = new Label { Text = "Try again" };
        var loading = new Label();
        var window = factory.CreateWindow(_ => scenario switch
        {
            "throw" => throw new InvalidOperationException("resolution failed"),
            "cancel" => Task.FromCanceled<InitialRoot>(new CancellationToken(true)),
            "null-task" => null!,
            "null-root" => Task.FromResult<InitialRoot>(null!),
            _ => Task.FromResult(InitialRoot.For<UnregisteredStartup>())
        }, new() { LoadingContentFactory = () => loading, FailureContentFactory = result => { failure = result; return content; } });
        var host = factory.ForWindow(window); hosts.Add(host);
        var result = await Settle(factory.WaitForInitializationAsync(window, Token));
        Assert.Equal(scenario == "cancel" ? NavigationStatus.Cancelled : NavigationStatus.Failed, result.Status);
        Assert.False(result.HasCommitted); Assert.Null(host.CurrentRoot); Assert.NotNull(failure);
        Assert.Equal(result.Status, failure.Status);
        Assert.Same(content, Assert.IsType<ContentPage>(window.Page).Content);
        Assert.Null(loading.Parent); Assert.Empty(services.GetRequiredService<RootActions>().Created);
        Success(await Settle(factory.RequestRoot<Leaf>(window, cancellationToken: Token).Completion));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Enforced_root_finishes_without_waiting_for_uncooperative_resolver(bool faultLate) => OnRootUI(async () =>
    {
        var resolve = new TaskCompletionSource<InitialRoot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = RootSignal(); var resolverToken = CancellationToken.None; var failures = 0;
        var window = factory.CreateWindow(token => { resolverToken = token; entered.TrySetResult(); return resolve.Task; },
            new() { FailureContentFactory = _ => { failures++; return new Label(); } });
        var host = factory.ForWindow(window); hosts.Add(host);
        await Settle(entered.Task);
        var successor = factory.RequestRoot<Leaf>(window, new() { ["filter"] = "blocked" }, Enforced, Token);
        Assert.Equal(NavigationStatus.Superseded, (await Settle(factory.WaitForInitializationAsync(window, Token))).Status);
        Success(await Settle(successor.Completion));
        Assert.True(resolverToken.IsCancellationRequested); Assert.False(resolve.Task.IsCompleted);
        var committed = window.Page;
        if (faultLate) resolve.SetException(new InvalidOperationException("late resolution"));
        else resolve.SetResult(InitialRoot.For<ApplicationRoot>());
        await host.DispatchContentAsync(() => { });
        Assert.Same(committed, window.Page); Assert.Equal(0, failures);
        Assert.Empty(services.GetRequiredService<RootActions>().Created);
        Assert.Equal("blocked", ((Leaf)host.CurrentContentNavigation!.Current!.ViewModel).Filter);
    });

    [Fact]
    public Task Closing_window_cancels_uncooperative_resolver_without_installing_a_root() => OnRootUI(async () =>
    {
        var entered = RootSignal(); var resolve = new TaskCompletionSource<InitialRoot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolverToken = CancellationToken.None; var failures = 0;
        var window = factory.CreateWindow(token => { resolverToken = token; entered.TrySetResult(); return resolve.Task; },
            new() { FailureContentFactory = _ => { failures++; return new Label(); } });
        var host = factory.ForWindow(window); hosts.Add(host);
        var bootstrap = Assert.IsType<ContentPage>(window.Page);
        await Settle(entered.Task);
        await Settle(host.DisposeAsync().AsTask());
        Assert.False((await Settle(factory.WaitForInitializationAsync(window, Token))).HasCommitted);
        Assert.True(resolverToken.IsCancellationRequested); Assert.Null(bootstrap.Content); Assert.Equal(0, failures);
        resolve.SetResult(InitialRoot.For<ApplicationRoot>());
        await host.DispatchContentAsync(() => { });
        Assert.Same(bootstrap, window.Page); Assert.Empty(services.GetRequiredService<RootActions>().Created);
    });

    [Fact]
    public Task Cancelling_initialization_wait_does_not_cancel_the_resolver() => OnRootUI(async () =>
    {
        var entered = RootSignal(); var resolve = new TaskCompletionSource<InitialRoot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolverToken = CancellationToken.None;
        var window = factory.CreateWindow(token => { resolverToken = token; entered.TrySetResult(); return resolve.Task; });
        hosts.Add(factory.ForWindow(window)); await Settle(entered.Task);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.WaitForInitializationAsync(window, new CancellationToken(true)));
        Assert.False(resolverToken.IsCancellationRequested);
        resolve.SetResult(InitialRoot.For<Leaf>());
        Success(await Settle(factory.WaitForInitializationAsync(window, Token)));
    });

    [Theory]
    [InlineData("throw")]
    [InlineData("null")]
    [InlineData("parented")]
    public Task Invalid_bootstrap_factory_reports_failure_and_uses_safe_fallback(string scenario) => OnRootUI(async () =>
    {
        var parented = new Label(); var owner = new ContentView { Content = parented }; var called = false;
        var window = factory.CreateWindow(_ => { called = true; return Task.FromResult(InitialRoot.For<Leaf>()); }, new()
        {
            LoadingContentFactory = () => scenario switch { "throw" => throw new InvalidOperationException("loading"), "null" => null!, _ => parented },
            FailureContentFactory = _ => throw new InvalidOperationException("failure content")
        });
        var host = factory.ForWindow(window); hosts.Add(host);
        var result = await Settle(factory.WaitForInitializationAsync(window, Token));
        Assert.Equal(NavigationStatus.Failed, result.Status); Assert.False(called);
        Assert.Single(result.CleanupErrors); AssertBootstrapFailure(window, host);
        Assert.Same(owner, parented.Parent);
    });

    [Fact]
    public Task Failure_factory_can_submit_a_successor_without_installing_stale_failure_content() => OnRootUI(async () =>
    {
        RootTransitionHandle? successor = null; Window? window = null; var fallback = new Label();
        window = factory.CreateWindow(_ => Task.FromException<InitialRoot>(new InvalidOperationException("startup")), new()
        {
            FailureContentFactory = _ => { successor = factory.RequestRoot<Leaf>(window!, options: Enforced, cancellationToken: Token); return fallback; }
        });
        var host = factory.ForWindow(window); hosts.Add(host);
        var result = await Settle(factory.WaitForInitializationAsync(window, Token));
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Success(await Settle(successor!.Completion));
        Assert.Null(fallback.Parent); Assert.Same(host.CurrentRoot!.Page, window.Page);
    });

    [Fact]
    public Task Known_root_overload_accepts_custom_bootstrap_and_preserves_parameters() => OnRootUI(async () =>
    {
        var entered = RootSignal(); var release = RootSignal(); var loading = new Label();
        services.GetRequiredService<RootActions>().Before = _ => { entered.TrySetResult(); return release.Task; };
        var window = factory.CreateWindow<ApplicationRoot>(new() { ["filter"] = "known" }, new() { LoadingContentFactory = () => loading });
        var host = factory.ForWindow(window); hosts.Add(host);
        await Settle(entered.Task);
        Assert.Same(loading, Assert.IsType<ContentPage>(window.Page).Content);
        release.SetResult();
        Success(await Settle(factory.WaitForInitializationAsync(window, Token)));
        Assert.Equal("known", Assert.IsType<ApplicationRoot>(host.CurrentContentNavigation!.Current!.ViewModel).Filter);
        Assert.Null(loading.Parent);
    });

    private sealed class UnregisteredStartup : ViewModelBase;
}
