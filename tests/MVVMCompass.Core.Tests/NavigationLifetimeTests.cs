using MVVMCompass.Core;

namespace MVVMCompass.Core.Tests;

public sealed class NavigationLifetimeTests
{
    [Fact]
    public async Task Concurrent_callers_share_one_cleanup_and_wait_for_it()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var lifetime = new NavigationLifetime(() => { Interlocked.Increment(ref calls); return release.Task; });
        var tasks = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            new[] { lifetime.DismissAsync(DismissalReason.Back) })));
        Assert.All(tasks, task => Assert.Same(tasks[0][0], task[0]));
        Assert.False(tasks[0][0].IsCompleted);
        Assert.True(lifetime.Token.IsCancellationRequested);
        Assert.Equal(1, calls);
        release.SetResult();
        await Task.WhenAll(tasks.Select(task => task[0]));
        Assert.True(lifetime.IsDismissed);
        Assert.Equal(DismissalReason.Back, lifetime.Reason);
    }

    [Fact]
    public async Task Cancellation_callback_failure_does_not_skip_cleanup()
    {
        var cleaned = false;
        var lifetime = new NavigationLifetime(() => { cleaned = true; return Task.CompletedTask; });
        lifetime.Token.Register(() => throw new InvalidOperationException("callback"));
        await lifetime.DismissAsync();
        Assert.True(cleaned);
        Assert.Single(lifetime.CancellationErrors);
        Assert.True(lifetime.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Cleanup_failure_is_shared_and_never_retried()
    {
        var calls = 0;
        var error = new InvalidOperationException("cleanup");
        var lifetime = new NavigationLifetime(() => { calls++; throw error; });
        var first = lifetime.DismissAsync();
        var second = lifetime.DismissAsync();
        Assert.Same(first, second);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => second));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Group_marks_every_entry_before_first_callback_and_keeps_going_after_failure()
    {
        var order = new List<int>();
        NavigationLifetime[] lifetimes = [];
        lifetimes = Enumerable.Range(0, 3).Select(index => new NavigationLifetime(async () =>
        {
            Assert.All(lifetimes, lifetime => Assert.True(lifetime.IsDismissed));
            order.Add(index);
            await Task.Yield();
            if (index == 0) throw new InvalidOperationException("first");
        })).ToArray();
        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            NavigationLifetimeGroup.DismissAsync(lifetimes.Concat([lifetimes[0]]), DismissalReason.RootReplaced));
        Assert.Single(error.InnerExceptions);
        Assert.Equal([0, 1, 2], order);
        Assert.All(lifetimes, lifetime => Assert.Equal(DismissalReason.RootReplaced, lifetime.Reason));
    }

    [Fact]
    public async Task Marking_is_synchronous_and_first_reason_wins()
    {
        var calls = 0;
        var lifetime = new NavigationLifetime(() => { calls++; return Task.CompletedTask; });
        lifetime.MarkDismissed(DismissalReason.WindowClosed);
        Assert.True(lifetime.IsDismissed);
        Assert.False(lifetime.Token.IsCancellationRequested);
        Assert.Equal(0, calls);
        await lifetime.DismissAsync(DismissalReason.RootReplaced);
        Assert.Equal(DismissalReason.WindowClosed, lifetime.Reason);
    }

    [Theory]
    [InlineData(DismissalReason.None)]
    [InlineData((DismissalReason)100)]
    public void Invalid_reasons_do_not_mark_an_entry(DismissalReason reason)
    {
        var lifetime = new NavigationLifetime(() => Task.CompletedTask);
        Assert.Throws<ArgumentOutOfRangeException>(() => lifetime.MarkDismissed(reason));
        Assert.False(lifetime.IsDismissed);
    }
}
