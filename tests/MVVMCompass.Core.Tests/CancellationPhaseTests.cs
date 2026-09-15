using MVVMCompass.Core;

namespace MVVMCompass.Core.Tests;

public sealed class CancellationPhaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_thrown_by_cleanup_is_a_shared_failure_not_a_cancelled_dismissal(bool asynchronous)
    {
        var error = new OperationCanceledException("cleanup failed");
        var lifetime = new NavigationLifetime(async () => { if (asynchronous) await Task.Yield(); throw error; });
        var completion = lifetime.DismissAsync();
        Assert.Same(error, await Assert.ThrowsAsync<OperationCanceledException>(() => completion));
        Assert.True(completion.IsFaulted); Assert.False(completion.IsCanceled);
        Assert.Same(completion, lifetime.DismissAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_dismissal_does_not_block_on_a_synchronous_callback_and_shares_its_result(bool fail)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var token = TestContext.Current.CancellationToken;
        var calls = 0;
        var error = new InvalidOperationException("cleanup");
        var lifetime = new NavigationLifetime(() =>
        {
            Interlocked.Increment(ref calls); entered.SetResult(); release.Wait(token);
            return fail ? Task.FromException(error) : Task.CompletedTask;
        });
        var firstCaller = Task.Run(() => new[] { lifetime.DismissAsync() }, token);
        Task? concurrent = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            concurrent = lifetime.DismissAsync();
            Assert.False(concurrent.IsCompleted);
        }
        finally { release.Set(); }
        var first = (await firstCaller.WaitAsync(TimeSpan.FromSeconds(10), token))[0];
        Assert.Same(first, concurrent); Assert.Same(first, lifetime.DismissAsync());
        if (fail) Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        else await first.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_cancellation_rejects_reentry_and_preserves_registered_context(bool useSynchronizationContext)
    {
        var contextValue = new AsyncLocal<string?> { Value = "registered" };
        var root = new NavigationLifetime(() => Task.CompletedTask);
        var child = new NavigationLifetime(() => Task.CompletedTask);
        root.Ownership.Adopt(child.Ownership);
        Task? self = null, ancestor = null;
        string? observed = null;
        using var registration = child.Token.Register(() =>
        {
            observed = contextValue.Value;
            self = child.DismissAsync(); ancestor = root.DismissAsync();
        }, useSynchronizationContext);
        contextValue.Value = "cancelling";
        await root.DismissAsync();
        Assert.Equal("registered", observed);
        Assert.Equal("cancelling", contextValue.Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => self!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ancestor!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Group_or_owner_cancels_later_work_before_awaiting_the_first_cleanup(bool dismissOwner)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var first = new NavigationLifetime(async () => { order.Add("first-start"); entered.SetResult(); await release.Task; order.Add("first-end"); });
        var second = new NavigationLifetime(() => { order.Add("second"); return Task.CompletedTask; });
        var stopped = Task.Delay(Timeout.Infinite, second.Token);
        var owner = new NavigationLifetime(() => Task.CompletedTask);
        if (dismissOwner) { owner.Ownership.Adopt(first.Ownership); owner.Ownership.Adopt(second.Ownership); }
        var cleanup = dismissOwner ? owner.DismissAsync() : NavigationLifetimeGroup.DismissAsync([first, second]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(second.Token.IsCancellationRequested); Assert.True(stopped.IsCanceled);
            Assert.Equal(new[] { "first-start" }, order); Assert.False(cleanup.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await cleanup.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "first-start", "first-end", "second" }, order);
    }

    [Fact]
    public async Task Cancellation_callbacks_cannot_queue_the_coordinator_they_are_stopping()
    {
        var coordinator = new NavigationCoordinator();
        var other = new NavigationCoordinator();
        using var stop = new CancellationTokenSource();
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<NavigationOutcome<int>>? reentry = null, independent = null;
        var operation = coordinator.RunAsync(new NavigationRequest<int>(0), async context =>
        {
            using var registration = context.CancellationToken.Register(() =>
            {
                reentry = coordinator.RunAsync(new NavigationRequest<int>(1), _ => Task.FromResult(1), TestContext.Current.CancellationToken);
                independent = other.RunAsync(new NavigationRequest<int>(1), _ => Task.FromResult(1), TestContext.Current.CancellationToken);
            });
            registered.SetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return 0;
        }, stop.Token);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        stop.Cancel();
        Assert.Equal(NavigationStatus.Cancelled, (await operation).Status);
        Assert.Equal(NavigationStatus.Reentrant, (await reentry!).Status);
        Assert.True((await independent!).IsSuccess);
        Assert.True((await coordinator.RunAsync(new NavigationRequest<int>(2), _ => Task.FromResult(2), TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Cancellation_callback_cannot_wait_for_its_entry_lifecycle_gate()
    {
        var coordinator = new NavigationCoordinator();
        var entry = coordinator.CreateEntry(new object());
        Task? deactivation = null;
        using var registration = entry.Lifetime.Token.Register(() => deactivation = entry.DeactivateAsync());
        await entry.DismissAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => deactivation!);
    }

    [Fact]
    public async Task Signal_stops_work_without_starting_cleanup_or_releasing_resources()
    {
        var cleaned = 0; var released = 0;
        var lifetime = new NavigationLifetime(() => { cleaned++; return Task.CompletedTask; });
        lifetime.Ownership.RegisterCleanup(() => { released++; return Task.CompletedTask; });
        var work = Task.Delay(Timeout.Infinite, lifetime.Token);
        lifetime.MarkDismissed(DismissalReason.Back);
        Assert.False(lifetime.Token.IsCancellationRequested);
        lifetime.SignalCancellation([]);
        Assert.True(lifetime.Token.IsCancellationRequested);
        Assert.True(work.IsCanceled);
        Assert.Equal(0, cleaned); Assert.Equal(0, released);
        await lifetime.DismissAsync();
        Assert.Equal(1, cleaned); Assert.Equal(1, released);
    }

    [Fact]
    public async Task Concurrent_signals_and_cleanup_cancel_exactly_once()
    {
        var cancellations = 0; var cleaned = 0;
        var lifetime = new NavigationLifetime(() => { Interlocked.Increment(ref cleaned); return Task.CompletedTask; });
        using var registration = lifetime.Token.Register(() => Interlocked.Increment(ref cancellations));
        lifetime.MarkDismissed(DismissalReason.Back);
        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(async () =>
        {
            if (index % 2 == 0) lifetime.SignalCancellation([]);
            else await lifetime.DismissAsync();
        })));
        Assert.Equal(1, cancellations); Assert.Equal(1, cleaned);
    }

    [Fact]
    public async Task Cleanup_waits_for_concurrent_cancellation_callbacks_before_disposing()
    {
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var cleaned = false;
        var lifetime = new NavigationLifetime(() => { cleaned = true; return Task.CompletedTask; });
        using var registration = lifetime.Token.Register(() => { entered.SetResult(); release.Wait(token); });
        lifetime.MarkDismissed(DismissalReason.Back);
        var signalling = Task.Run(() => lifetime.SignalCancellation([]), token);
        Task? cleanup = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            cleanup = lifetime.DismissAsync();
            Assert.True(lifetime.Token.IsCancellationRequested);
            Assert.False(cleanup.IsCompleted); Assert.False(cleaned);
        }
        finally { release.Set(); }
        await signalling.WaitAsync(TimeSpan.FromSeconds(10), token);
        await cleanup!.WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.True(cleaned);
    }

    [Fact]
    public async Task Early_callback_failure_is_observable_and_does_not_skip_cleanup()
    {
        var cleaned = false;
        var lifetime = new NavigationLifetime(() => { cleaned = true; return Task.CompletedTask; });
        using var registration = lifetime.Token.Register(() => throw new InvalidOperationException("cancellation"));
        lifetime.MarkDismissed(DismissalReason.Back);
        lifetime.SignalCancellation([]);
        Assert.Single(lifetime.CancellationErrors); Assert.False(cleaned);
        await lifetime.DismissAsync();
        Assert.True(cleaned); Assert.Single(lifetime.CancellationErrors);
    }

    [Fact]
    public async Task Early_descendant_callback_rejects_self_and_ancestor_dismissal()
    {
        var root = new NavigationLifetime(() => Task.CompletedTask);
        var child = new NavigationLifetime(() => Task.CompletedTask);
        root.Ownership.Adopt(child.Ownership);
        Task? self = null, ancestor = null;
        using var registration = child.Token.Register(() => { self = child.DismissAsync(); ancestor = root.DismissAsync(); });
        root.MarkDismissed(DismissalReason.Back);
        root.SignalCancellation([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => self!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ancestor!);
        await root.DismissAsync();
    }

    [Fact]
    public async Task Marked_tree_cancels_all_tokens_before_ordered_cleanup_starts()
    {
        var order = new List<string>();
        var root = new NavigationLifetime(() => { order.Add("root"); return Task.CompletedTask; });
        var child = new NavigationLifetime(() => { order.Add("child"); return Task.CompletedTask; });
        root.Ownership.Adopt(child.Ownership);
        using var registration = root.Token.Register(() => Assert.True(child.IsDismissed));
        root.MarkDismissed(DismissalReason.Back);
        root.SignalCancellation([]);
        Assert.True(root.Token.IsCancellationRequested); Assert.True(child.Token.IsCancellationRequested);
        Assert.Empty(order);
        await root.DismissAsync();
        Assert.Equal(new[] { "child", "root" }, order);
    }

    [Fact]
    public async Task Signalling_an_unmarked_lifetime_is_rejected_without_cancellation()
    {
        var lifetime = new NavigationLifetime(() => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(() => lifetime.SignalCancellation([]));
        Assert.False(lifetime.Token.IsCancellationRequested);
        await lifetime.DismissAsync();
    }

    [Fact]
    public async Task Coordinator_callback_scope_rejects_reentry_and_releases_afterwards()
    {
        var coordinator = new NavigationCoordinator();
        using (coordinator.EnterCallback())
            Assert.Equal(NavigationStatus.Reentrant, (await coordinator.RunAsync(new NavigationRequest<int>(0), _ => Task.FromResult(1), TestContext.Current.CancellationToken)).Status);
        Assert.True((await coordinator.RunAsync(new NavigationRequest<int>(0), _ => Task.FromResult(1), TestContext.Current.CancellationToken)).IsSuccess);
    }
}
