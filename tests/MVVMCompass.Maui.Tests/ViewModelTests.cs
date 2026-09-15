using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace MVVMCompass.Maui.Tests;

public sealed class ViewModelTests
{
    [Fact]
    public async Task Permanent_dismissal_cancels_work_waits_once_and_disconnects_view_callbacks()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new Probe { Cleanup = () => release.Task };
        var toastCalls = 0;
        vm.DisplayToastEvent += _ => { toastCalls++; return Task.CompletedTask; };
        var first = vm.DismissAsync();
        var second = vm.DismissAsync();
        Assert.Same(first, second);
        Assert.True(vm.Token.IsCancellationRequested);
        Assert.False(first.IsCompleted);
        release.SetResult();
        await first;
        await vm.Toast();
        Assert.Equal(0, toastCalls);
        Assert.Equal(1, vm.Dismissals);
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate, 1, 0)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed, 0, 1)]
    public async Task Both_retained_lifecycle_profiles_keep_the_instance_usable(
        int behaviorValue, int deactivations, int dismissals)
    {
        var behavior = (RetainedViewLifecycleBehavior)behaviorValue;

        var vm = new Probe { RetainedViewLifecycleBehavior = behavior };
        var toastCalls = 0;
        vm.DisplayToastEvent += _ => { toastCalls++; return Task.CompletedTask; };
        await vm.DeactivateRetainedAsync();
        Assert.Equal(deactivations, vm.Deactivations);
        Assert.Equal(dismissals, vm.Dismissals);
        Assert.False(vm.IsDismissed);
        Assert.False(vm.Token.IsCancellationRequested);
        await vm.Toast();
        Assert.Equal(1, toastCalls);
    }

    [Fact]
    public async Task Failed_cleanup_still_disconnects_view_callbacks()
    {
        var vm = new Probe { Cleanup = () => throw new InvalidOperationException("teardown") };
        var calls = 0;
        vm.DisplayToastEvent += _ => { calls++; return Task.CompletedTask; };
        await Assert.ThrowsAsync<InvalidOperationException>(vm.DismissAsync);
        await vm.Toast();
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Every_async_subscriber_is_awaited_in_order_and_custom_actions_return_the_value()
    {
        var vm = new Probe();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<int>();
        vm.SendCustomActionEvent += async _ => { calls.Add(1); await release.Task; calls.Add(2); return "first"; };
        vm.SendCustomActionEvent += _ => { calls.Add(3); return Task.FromResult<object?>(42); };
        var pending = vm.Action();
        Assert.False(pending.IsCompleted);
        Assert.Equal([1], calls);
        release.SetResult();
        Assert.Equal(42, await pending);
        Assert.Equal([1, 2, 3], calls);
    }

    [Fact]
    public async Task An_earlier_async_handler_failure_is_observed()
    {
        var vm = new Probe();
        var laterCalled = false;
        vm.DisplayToastEvent += async _ => { await Task.Yield(); throw new InvalidOperationException("toast"); };
        vm.DisplayToastEvent += _ => { laterCalled = true; return Task.CompletedTask; };
        await Assert.ThrowsAsync<InvalidOperationException>(vm.Toast);
        Assert.False(laterCalled);
    }

    [Fact]
    public async Task Custom_actions_wait_for_the_view_dispatcher_and_return_its_result()
    {
        var vm = new Probe();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = false;
        vm.CustomActionDispatcher = async action => { await release.Task; return await action(); };
        vm.SendCustomActionEvent += _ => { called = true; return Task.FromResult<object?>("result"); };
        var pending = vm.Action();
        Assert.False(called);
        Assert.False(pending.IsCompleted);
        release.SetResult();
        Assert.Equal("result", await pending);
        Assert.True(called);
    }

    [Fact]
    public void Synchronous_command_restores_busy_after_failure()
    {
        var vm = new Probe();
        var command = vm.SyncCommand(() => throw new InvalidOperationException());
        Assert.Throws<InvalidOperationException>(() => command.Execute(null));
        Assert.False(vm.IsBusy);
        Assert.True(command.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Asynchronous_command_restores_configured_busy_after_failure(bool busyAfter)
    {
        var vm = new Probe();
        var command = (IAsyncRelayCommand)vm.AsyncCommand(() => Task.FromException(new InvalidOperationException()), busyAfter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteAsync(null));
        Assert.Equal(busyAfter, vm.IsBusy);
    }

    [Fact]
    public void Nested_construction_scopes_restore_the_previous_parent()
    {
        var outer = new Probe();
        var inner = new Probe();
        using (ViewModelBase.SetPendingParent(outer))
        {
            Assert.Same(outer, new Probe().ParentViewModel);
            using (ViewModelBase.SetPendingParent(inner)) Assert.Same(inner, new Probe().ParentViewModel);
            Assert.Same(outer, new Probe().ParentViewModel);
        }
        Assert.Null(new Probe().ParentViewModel);
    }

    [Fact]
    public void Type_only_and_metadata_tab_declarations_are_both_supported()
    {
        var vm = new Probe();
        var simple = new TabbedViewModelsEventArgs(new[] { typeof(Probe) }, vm);
        Assert.Equal([typeof(Probe)], simple.TabbedViewModelTypes);
        var parameters = new Dictionary<string, object> { ["record"] = 7 };
        var metadata = new TabModel(typeof(Probe), true, parameters, true);
        var rich = new TabbedViewModelsEventArgs(new[] { metadata }, vm);
        var tab = Assert.Single(rich.TabbedViewModels);
        Assert.True(tab.HideInTabBar);
        Assert.True(tab.ShouldBeSelectedByDefault);
        Assert.Same(parameters, tab.Parameters);
    }

    internal sealed class Probe : ViewModelBase
    {
        public Func<Task> Cleanup { get; init; } = () => Task.CompletedTask;
        public int Dismissals { get; private set; }
        public int Deactivations { get; private set; }
        public CancellationToken Token => LifetimeToken;
        public override Task AfterDismissed() { Dismissals++; return Cleanup(); }
        public override Task Deactivated() { Deactivations++; return Task.CompletedTask; }
        public Task Toast() => DisplayToast(new ToastEventArgs("test"));
        public Task<object?> Action() => SendCustomAction(new CustomActionEventArgs("test"));
        public ICommand SyncCommand(Action action) => CreateCommand(action);
        public ICommand AsyncCommand(Func<Task> action, bool busyAfter) => CreateCommand(action, busyAfter);
    }
}
