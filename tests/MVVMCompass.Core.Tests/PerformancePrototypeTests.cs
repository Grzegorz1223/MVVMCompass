using MVVMCompass.Core;

namespace MVVMCompass.Core.Tests;

public sealed class PerformancePrototypeTests
{
    [Fact]
    public async Task Concurrent_inspection_preserves_one_node_owner_and_identity()
    {
        var owner = new object();
        var lifetime = new NavigationLifetime(() => Task.CompletedTask, owner);
        var nodes = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => lifetime.Ownership)));
        var ids = await Task.WhenAll(nodes.Select(node => Task.Run(() => node.Id)));
        Assert.All(nodes, node => { Assert.Same(nodes[0], node); Assert.Same(owner, node.Owner); });
        Assert.NotEqual(Guid.Empty, ids[0]);
        Assert.All(ids, id => Assert.Equal(ids[0], id));
        await lifetime.DismissAsync();
        Assert.Same(nodes[0], lifetime.Ownership);
        Assert.Equal(ids[0], lifetime.Ownership.Id);
    }

    [Fact]
    public async Task First_ownership_access_during_cleanup_cannot_acquire_resources()
    {
        NavigationLifetime lifetime = null!;
        var child = new NavigationLifetime(() => Task.CompletedTask);
        lifetime = new NavigationLifetime(() =>
        {
            Assert.Throws<InvalidOperationException>(() => lifetime.Ownership.Adopt(child.Ownership));
            Assert.Throws<InvalidOperationException>(() => lifetime.Ownership.RegisterCleanup(() => Task.CompletedTask));
            return Task.CompletedTask;
        });
        await lifetime.DismissAsync();
        Assert.True(lifetime.IsDismissed);
        Assert.False(child.IsDismissed);
        await child.DismissAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deep_tree_marks_all_then_cleans_children_resources_and_scopes_in_order(bool fail)
    {
        const int count = 64;
        var all = new NavigationLifetime[count];
        var order = new List<string>();
        for (int i = 0; i < count; i++)
        {
            int index = i;
            all[i] = new NavigationLifetime(async () =>
            {
                Assert.All(all, item => Assert.True(item.IsDismissed));
                Assert.True(all[index].Token.IsCancellationRequested);
                await Task.Yield();
                order.Add($"owner:{index}");
                if (fail && index == count - 1) throw new InvalidOperationException("leaf failure");
            });
            all[i].Ownership.RegisterCleanup(() => { order.Add($"resource:{index}"); return Task.CompletedTask; });
            all[i].Ownership.RegisterCleanup(() => { order.Add($"scope:{index}"); return Task.CompletedTask; }, runLast: true);
            if (i > 0) all[i - 1].Ownership.Adopt(all[i].Ownership);
        }
        var task = all[0].DismissAsync();
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => task); else await task;
        Assert.Same(task, all[0].DismissAsync());
        Assert.Equal(Enumerable.Range(0, count).Reverse().SelectMany(i => new[] { $"owner:{i}", $"resource:{i}", $"scope:{i}" }), order);
        Assert.All(all, item => { Assert.True(item.Token.IsCancellationRequested); Assert.Empty(item.Ownership.Children); });
    }

    [Fact]
    public async Task Descendant_callback_rejects_ancestor_dismissal_after_async_yield()
    {
        var root = new NavigationLifetime(() => Task.CompletedTask);
        var child = new NavigationLifetime(async () =>
        {
            await Task.Yield();
            await Assert.ThrowsAsync<InvalidOperationException>(() => root.DismissAsync());
        });
        root.Ownership.Adopt(child.Ownership);
        await root.DismissAsync();
    }

    [Fact]
    public async Task Completed_lifetime_still_rejects_invalid_dismissal_reasons()
    {
        var lifetime = new NavigationLifetime(() => Task.CompletedTask);
        await lifetime.DismissAsync();
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = lifetime.DismissAsync(DismissalReason.None); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = lifetime.DismissAsync((DismissalReason)999); });
    }
}
