using MVVMCompass.Core;

namespace MVVMCompass.Core.Tests;

public sealed class AuditRegressionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public async Task Lifecycle_callbacks_reject_self_or_ancestor_dismissal_before_marking(int phase, int route)
    {
        var model = new CallbackModel();
        var entry = new NavigationCoordinator().CreateEntry(model);
        var parent = new NavigationLifetime(() => Task.CompletedTask);
        parent.Ownership.Adopt(entry.Ownership);
        if (phase == 2) await entry.ActivateAsync();
        Func<Task> callback = route switch
        {
            0 => () => entry.DismissAsync(),
            1 => () => entry.Lifetime.DismissAsync(),
            _ => () => parent.DismissAsync()
        };
        if (phase == 0) model.Initialize = callback;
        else if (phase == 1) model.Activate = callback;
        else model.Deactivate = callback;

        var action = phase switch
        {
            0 => entry.InitializeAsync(1, Token),
            1 => entry.ActivateAsync(),
            _ => entry.DeactivateAsync()
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => action.WaitAsync(TimeSpan.FromSeconds(2), Token));
        Assert.False(entry.Lifetime.IsDismissed);
        Assert.False(parent.IsDismissed);
        Assert.Equal(0, model.CleanupCalls);
        await parent.DismissAsync().WaitAsync(Token);
        Assert.Equal(1, model.CleanupCalls);
        Assert.Empty(parent.Ownership.Children);
    }

    [Fact]
    public async Task External_dismissal_waits_for_activation_then_releases_resources_once()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new CallbackModel { Activate = async () => { entered.SetResult(); await release.Task; } };
        var entry = new NavigationCoordinator().CreateEntry(model);
        var resources = 0;
        entry.Ownership.RegisterCleanup(() => { resources++; return Task.CompletedTask; });
        var activation = entry.ActivateAsync();
        await entered.Task.WaitAsync(Token);
        var dismissal = entry.DismissAsync();
        try
        {
            Assert.False(dismissal.IsCompleted);
            Assert.True(entry.Lifetime.Token.IsCancellationRequested);
            Assert.Equal(0, model.CleanupCalls);
        }
        finally { release.SetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.WaitAsync(Token));
        await dismissal.WaitAsync(Token);
        await entry.DismissAsync();
        Assert.Equal(1, model.CleanupCalls);
        Assert.Equal(1, resources);
    }

    [Fact]
    public async Task Deferred_callback_work_can_dismiss_after_the_lifecycle_callback_finishes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? deferred = null;
        var model = new CallbackModel();
        var entry = new NavigationCoordinator().CreateEntry(model);
        model.Activate = () =>
        {
            deferred = Task.Run(async () => { await release.Task; await entry.DismissAsync(); }, Token);
            return Task.CompletedTask;
        };
        await entry.ActivateAsync();
        release.SetResult();
        await deferred!.WaitAsync(Token);
        Assert.Equal(1, model.CleanupCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Abandonment_reports_descendant_cancellation_failures_once_and_cleans_every_owner(bool alsoOwnChild)
    {
        var coordinator = new NavigationCoordinator();
        var parent = coordinator.CreateEntry(new object());
        var child = coordinator.CreateEntry(new object());
        var grandchild = new NavigationLifetime(() => Task.CompletedTask);
        var sibling = new NavigationLifetime(() => Task.CompletedTask);
        parent.Ownership.Adopt(child.Ownership);
        child.Ownership.Adopt(grandchild.Ownership);
        parent.Ownership.Adopt(sibling.Ownership);
        var childError = new InvalidOperationException("child cancellation");
        var grandchildError = new InvalidOperationException("grandchild cancellation");
        using var first = child.Lifetime.Token.Register(() => throw childError);
        using var second = grandchild.Token.Register(() => throw grandchildError);
        List<string> released = [];
        grandchild.Ownership.RegisterCleanup(() => { released.Add("grandchild"); return Task.CompletedTask; });
        child.Ownership.RegisterCleanup(() => { released.Add("child"); return Task.CompletedTask; });
        sibling.Ownership.RegisterCleanup(() => { released.Add("sibling"); return Task.CompletedTask; });
        parent.Ownership.RegisterCleanup(() => { released.Add("parent scope"); return Task.CompletedTask; }, runLast: true);

        var result = await coordinator.RunAsync(new NavigationRequest<int>(0), context =>
        {
            context.Own(parent);
            if (alsoOwnChild) context.Own(child);
            return Task.FromResult(42);
        }, Token);

        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.False(result.HasCommitted);
        Assert.Equal(2, result.CleanupErrors.Count);
        Assert.Contains(childError, result.CleanupErrors);
        Assert.Contains(grandchildError, result.CleanupErrors);
        Assert.Equal(["grandchild", "child", "sibling", "parent scope"], released);
        Assert.Empty(parent.Ownership.Children);
    }

    private sealed class CallbackModel : INavigationAware, INavigationInitializable<int>
    {
        internal Func<Task> Initialize = () => Task.CompletedTask;
        internal Func<Task> Activate = () => Task.CompletedTask;
        internal Func<Task> Deactivate = () => Task.CompletedTask;
        internal int CleanupCalls;
        public Task InitializeAsync(int parameter, CancellationToken cancellationToken) => Initialize();
        public Task ActivateAsync(CancellationToken lifetimeToken) => Activate();
        public Task DeactivateAsync() => Deactivate();
        public Task DismissAsync(DismissalReason reason) { CleanupCalls++; return Task.CompletedTask; }
    }
}
