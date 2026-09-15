using MVVMCompass.Core;

namespace MVVMCompass.Core.Tests;

public sealed class NavigationEntryTests
{
    [Fact]
    public async Task Attached_entries_share_adapter_lifetime_and_do_not_duplicate_its_callbacks()
    {
        var calls = 0;
        var model = new Model();
        var lifetime = new NavigationLifetime(() => { calls++; return Task.CompletedTask; });
        var coordinator = new NavigationCoordinator();
        var entry = coordinator.AttachEntry(model, lifetime);
        await entry.InitializeAsync(new Input("attached"), TestContext.Current.CancellationToken);
        await entry.ActivateAsync();
        await entry.DeactivateAsync();
        await entry.ActivateAsync();
        Assert.Same(lifetime, entry.Lifetime);
        Assert.Equal("attached", model.Value);
        Assert.Equal(0, model.Activations);
        Assert.Equal(0, model.Deactivations);
        var ended = lifetime.DismissAsync(DismissalReason.WindowClosed);
        Assert.Same(ended, entry.DismissAsync());
        await ended;
        Assert.Equal(1, calls);
        Assert.Equal(0, model.Dismissals);
        Assert.Equal(NavigationEntryState.Dismissed, entry.State);
        var rejected = await coordinator.RunAsync(new NavigationRequest<int>(0) { Origin = entry }, _ => Task.FromResult(true), TestContext.Current.CancellationToken);
        Assert.Equal(NavigationStatus.InvalidOrigin, rejected.Status);
        Assert.Throws<InvalidOperationException>(() => coordinator.AttachEntry(new object(), lifetime));
    }

    [Fact]
    public async Task Plain_view_models_have_distinct_entry_identity_and_explicit_cleanup()
    {
        var coordinator = new NavigationCoordinator();
        var calls = 0;
        var first = coordinator.CreateEntry(new object(), () => { calls++; return Task.CompletedTask; });
        var second = coordinator.CreateEntry(new object());
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(NavigationEntryState.Prepared, first.State);
        var cleanup = first.DismissAsync(DismissalReason.PreparationFailed);
        Assert.Same(cleanup, first.DismissAsync());
        await cleanup;
        Assert.True(first.Lifetime.Token.IsCancellationRequested);
        Assert.Equal(NavigationEntryState.Dismissed, first.State);
        Assert.Equal(1, calls);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Typed_initialization_and_retained_activation_keep_the_same_live_entry()
    {
        var model = new Model();
        await using var entry = new NavigationCoordinator().CreateEntry(model);
        await entry.InitializeAsync(new Input("document-7"), TestContext.Current.CancellationToken);
        await entry.ActivateAsync();
        await entry.ActivateAsync();
        Assert.Equal("document-7", model.Value);
        Assert.Equal(1, model.Activations);
        await entry.DeactivateAsync();
        await entry.DeactivateAsync();
        Assert.Equal(NavigationEntryState.Inactive, entry.State);
        Assert.False(entry.Lifetime.Token.IsCancellationRequested);
        await entry.ActivateAsync();
        Assert.Equal(2, model.Activations);
        Assert.Equal(1, model.Deactivations);
        Assert.Equal(entry.Lifetime.Token, model.ActiveToken);
    }

    [Fact]
    public async Task Failed_initialization_cannot_be_activated_or_retried_and_is_abandoned()
    {
        var model = new Model { Initialization = _ => throw new InvalidOperationException("prepare") };
        var entry = new NavigationCoordinator().CreateEntry(model);
        await Assert.ThrowsAsync<InvalidOperationException>(() => entry.InitializeAsync(new Input("bad"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => entry.InitializeAsync(new Input("retry"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => entry.ActivateAsync());
        await entry.DisposeAsync();
        Assert.Equal(DismissalReason.PreparationFailed, model.Reason);
        Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Dismissal_cancels_pending_initialization_then_waits_for_cleanup()
    {
        var model = new Model { Initialization = token => Task.Delay(Timeout.InfiniteTimeSpan, token) };
        var entry = new NavigationCoordinator().CreateEntry(model);
        var initialization = entry.InitializeAsync(new Input("pending"), TestContext.Current.CancellationToken);
        var dismissal = entry.DismissAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await dismissal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, model.Dismissals);
        Assert.True(entry.Lifetime.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Caller_cancellation_during_initialization_requires_abandonment()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new Model { Initialization = token => Task.Delay(Timeout.InfiniteTimeSpan, token) };
        var entry = new NavigationCoordinator().CreateEntry(model);
        var initialization = entry.InitializeAsync(new Input("pending"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        Assert.False(entry.Lifetime.IsDismissed);
        await entry.DisposeAsync();
        Assert.Equal(DismissalReason.PreparationFailed, model.Reason);
    }

    [Fact]
    public async Task Every_owned_cleanup_runs_even_when_lifecycle_cleanup_fails()
    {
        var model = new Model { Cleanup = () => throw new InvalidOperationException("model cleanup") };
        var ownerCalled = false;
        var entry = new NavigationCoordinator().CreateEntry(model, () =>
        {
            ownerCalled = true;
            throw new ArgumentException("owner cleanup");
        });
        var task = entry.DismissAsync();
        var error = await Assert.ThrowsAsync<AggregateException>(() => task);
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.True(ownerCalled);
        Assert.Same(task, entry.DismissAsync());
    }

    [Fact]
    public async Task A_parameter_type_mismatch_is_explicit_and_terminal_entries_cannot_reactivate()
    {
        var entry = new NavigationCoordinator().CreateEntry(new Model());
        await Assert.ThrowsAsync<InvalidOperationException>(() => entry.InitializeAsync(42, TestContext.Current.CancellationToken));
        await entry.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => entry.ActivateAsync());
    }

    internal sealed record Input(string Id);
    internal sealed class Model : INavigationAware, INavigationInitializable<Input>
    {
        internal string? Value;
        internal int Activations, Deactivations, Dismissals;
        internal DismissalReason Reason;
        internal CancellationToken ActiveToken;
        internal Func<CancellationToken, Task> Initialization = _ => Task.CompletedTask;
        internal Func<Task> Cleanup = () => Task.CompletedTask;
        public async Task InitializeAsync(Input parameter, CancellationToken cancellationToken)
        {
            await Initialization(cancellationToken);
            Value = parameter.Id;
        }
        public Task ActivateAsync(CancellationToken lifetimeToken) { Activations++; ActiveToken = lifetimeToken; return Task.CompletedTask; }
        public Task DeactivateAsync() { Deactivations++; return Task.CompletedTask; }
        public Task DismissAsync(DismissalReason reason) { Dismissals++; Reason = reason; return Cleanup(); }
    }
}
