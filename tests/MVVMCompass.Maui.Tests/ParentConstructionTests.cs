using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Core;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

public sealed class ParentConstructionTests
{
    [Fact]
    public async Task Constructor_sees_parent_without_navigation_or_implicit_lifetime_ownership()
    {
        var parent = new Model();
        var child = ViewModelBase.ConstructWithParent(parent, () => new Model());
        Assert.Same(parent, child.ConstructorParent);
        Assert.Same(parent, child.ParentViewModel);
        Assert.False(NavigationEntryScope.IsOwned(child));
        await parent.Ownership.Lifetime.DismissAsync(DismissalReason.RootReplaced);
        Assert.False(child.IsDismissed);
        await child.Ownership.Lifetime.DismissAsync(DismissalReason.RootReplaced);
        Assert.True(child.IsHostReplaced);
        Assert.Null(child.ParentViewModel);
        Assert.Null(new Model().ConstructorParent);
    }

    [Fact]
    public void Nested_and_failed_factories_restore_the_previous_parent()
    {
        var outer = new Model(); var inner = new Model();
        ViewModelBase.ConstructWithParent(outer, () =>
        {
            Assert.Same(inner, ViewModelBase.ConstructWithParent(inner, () => new Model()).ConstructorParent);
            Assert.Throws<InvalidOperationException>(() => ViewModelBase.ConstructWithParent<Model>(inner, () => throw new InvalidOperationException()));
            Assert.Throws<InvalidOperationException>(() => ViewModelBase.ConstructWithParent<Model>(inner, () => null!));
            Assert.Same(outer, new Model().ConstructorParent);
            return new Model();
        });
        Assert.Null(new Model().ConstructorParent);
    }

    [Fact]
    public async Task Concurrent_factories_and_captured_background_flows_do_not_leak_parents()
    {
        var first = new Model(); var second = new Model();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Model>? background = null;
        var models = await Task.WhenAll(new[] { first, second }.Select(parent => Task.Run(() =>
            ViewModelBase.ConstructWithParent(parent, () =>
            {
                if (parent == first) background = Task.Run(async () => { await release.Task; return new Model(); });
                return new Model();
            }))));
        Assert.Same(first, models[0].ConstructorParent); Assert.Same(second, models[1].ConstructorParent);
        release.SetResult();
        Assert.Null((await background!.WaitAsync(TestContext.Current.CancellationToken)).ConstructorParent);
        Assert.Null(new Model().ConstructorParent);
    }

    [Fact]
    public async Task Invalid_arguments_and_registered_construction_cannot_override_navigation()
    {
        var parent = new Model();
        Assert.Throws<ArgumentNullException>(() => ViewModelBase.ConstructWithParent(null!, () => new Model()));
        Assert.Throws<ArgumentNullException>(() => ViewModelBase.ConstructWithParent<Model>(parent, null!));
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var entry = new NavigationEntryScope(services.GetRequiredService<IServiceScopeFactory>());
        using (entry.Construct())
            Assert.Throws<InvalidOperationException>(() => ViewModelBase.ConstructWithParent(parent, () => new Model()));
        parent.Ownership.Lifetime.MarkDismissed(DismissalReason.RootReplaced);
        Assert.Throws<ObjectDisposedException>(() => ViewModelBase.ConstructWithParent(parent, () => new Model()));
        Assert.Null(new Model().ConstructorParent);
    }

    private sealed class Model : ViewModelBase
    {
        public Model() => ConstructorParent = ParentViewModel;
        internal ViewModelBase? ConstructorParent { get; }
    }
}
