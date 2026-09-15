using MVVMCompass.Core;

namespace MVVMCompass.Core.Tests;

public sealed class NavigationOwnershipTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static NavigationLifetime Lifetime(Func<Task>? cleanup = null) => new(cleanup ?? (() => Task.CompletedTask));

    [Fact]
    public async Task Descendants_are_marked_before_callbacks_and_resources_release_after_the_owner()
    {
        List<string> order = [];
        var parent = Lifetime(() => { order.Add("parent"); return Task.CompletedTask; });
        var child = Lifetime(() => { Assert.True(parent.IsDismissed); order.Add("child"); return Task.CompletedTask; });
        parent.Ownership.RegisterCleanup(() => { order.Add("scope"); return Task.CompletedTask; }, runLast: true);
        parent.Ownership.RegisterCleanup(() => { order.Add("resource"); return Task.CompletedTask; });
        parent.Ownership.Adopt(child.Ownership);
        Assert.Equal([child.Ownership, parent.Ownership], parent.Ownership.Snapshot());
        await parent.DismissAsync(DismissalReason.RootReplaced);
        Assert.Equal(["child", "parent", "resource", "scope"], order);
        Assert.Equal(DismissalReason.RootReplaced, child.Reason);
        Assert.Empty(parent.Ownership.Children);
        Assert.Null(child.Ownership.Parent);
    }

    [Fact]
    public async Task A_parent_waits_for_a_child_already_being_cleaned_by_another_caller()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentCalls = 0;
        var parent = Lifetime(() => { parentCalls++; return Task.CompletedTask; });
        var child = Lifetime(() => release.Task);
        parent.Ownership.Adopt(child.Ownership);
        var childTask = child.DismissAsync(DismissalReason.Back);
        var parentTask = parent.DismissAsync(DismissalReason.WindowClosed);
        try { Assert.False(parentTask.IsCompleted); Assert.Equal(0, parentCalls); }
        finally { release.SetResult(); }
        await Task.WhenAll(childTask, parentTask).WaitAsync(Token);
        Assert.Equal(1, parentCalls);
        Assert.Equal(DismissalReason.Back, child.Reason);
    }

    [Fact]
    public async Task Failures_do_not_skip_siblings_owner_or_scope_and_are_not_retried()
    {
        List<string> order = [];
        var parent = Lifetime(() => { order.Add("parent"); throw new InvalidOperationException("owner"); });
        parent.Ownership.Adopt(Lifetime(() => throw new InvalidOperationException("child")).Ownership);
        parent.Ownership.Adopt(Lifetime(() => { order.Add("sibling"); return Task.CompletedTask; }).Ownership);
        parent.Ownership.RegisterCleanup(() => { order.Add("resource"); throw new InvalidOperationException("resource"); });
        parent.Ownership.RegisterCleanup(() => { order.Add("scope"); return Task.CompletedTask; }, runLast: true);
        var task = parent.DismissAsync();
        var error = await Assert.ThrowsAsync<AggregateException>(() => task);
        Assert.Equal(3, error.InnerExceptions.Count);
        Assert.Equal(["sibling", "parent", "resource", "scope"], order);
        Assert.Same(task, parent.DismissAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_child_cannot_await_its_ancestor_during_either_cleanup_entry_path(bool startParent)
    {
        var parent = Lifetime();
        var child = Lifetime(() => parent.DismissAsync());
        parent.Ownership.Adopt(child.Ownership);
        await Assert.ThrowsAsync<InvalidOperationException>(() => (startParent ? parent.DismissAsync() : child.DismissAsync()).WaitAsync(Token));
        if (!startParent) await parent.DismissAsync();
    }

    [Fact]
    public async Task A_callback_cannot_join_itself_and_deadlock()
    {
        NavigationLifetime? lifetime = null;
        lifetime = Lifetime(() => lifetime!.DismissAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.DismissAsync().WaitAsync(Token));
    }

    [Fact]
    public async Task Ownership_rejects_cycles_multiple_parents_and_duplicate_adoption_is_idempotent()
    {
        var parent = Lifetime(); var child = Lifetime(); var other = Lifetime();
        parent.Ownership.Adopt(child.Ownership);
        parent.Ownership.Adopt(child.Ownership);
        Assert.Single(parent.Ownership.Children);
        Assert.Throws<InvalidOperationException>(() => child.Ownership.Adopt(parent.Ownership));
        Assert.Throws<InvalidOperationException>(() => parent.Ownership.Adopt(parent.Ownership));
        Assert.Throws<InvalidOperationException>(() => other.Ownership.Adopt(child.Ownership));
        await parent.DismissAsync(); await other.DismissAsync();
    }

    [Fact]
    public async Task Detachment_explicitly_transfers_responsibility_without_ending_the_child()
    {
        var first = Lifetime(); var second = Lifetime(); var child = Lifetime();
        first.Ownership.Adopt(child.Ownership);
        first.Ownership.Detach(child.Ownership);
        second.Ownership.Adopt(child.Ownership);
        await first.DismissAsync();
        Assert.False(child.IsDismissed);
        await second.DismissAsync(); Assert.True(child.IsDismissed);
    }

    [Fact]
    public async Task Marking_freezes_all_descendants_before_cleanup_starts()
    {
        var parent = Lifetime(); var child = Lifetime(); var detached = Lifetime();
        parent.Ownership.Adopt(child.Ownership);
        parent.MarkDismissed(DismissalReason.WindowClosed);
        Assert.Throws<InvalidOperationException>(() => parent.Ownership.Detach(child.Ownership));
        Assert.Throws<InvalidOperationException>(() => child.Ownership.Adopt(detached.Ownership));
        Assert.Throws<InvalidOperationException>(() => child.Ownership.RegisterCleanup(() => Task.CompletedTask));
        Assert.Throws<InvalidOperationException>(() => detached.Ownership.Adopt(child.Ownership));
        await parent.DismissAsync(); await detached.DismissAsync();
    }

    [Fact]
    public async Task Cancellation_callback_failures_do_not_prevent_child_resource_release()
    {
        var released = false;
        var parent = Lifetime(); var child = Lifetime();
        parent.Ownership.Adopt(child.Ownership);
        child.Token.Register(() => throw new InvalidOperationException("cancellation"));
        child.Ownership.RegisterCleanup(() => { released = true; return Task.CompletedTask; });
        await parent.DismissAsync();
        Assert.True(released); Assert.Single(child.CancellationErrors);
    }
}
