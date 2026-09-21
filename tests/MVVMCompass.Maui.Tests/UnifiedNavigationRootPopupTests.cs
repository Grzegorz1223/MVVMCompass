using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    public sealed class PopupActions
    {
        internal Func<PopupModel, Task>? Before;
        internal Func<PopupModel, Task>? Cleanup;
        internal Action<PopupView>? Opened;
    }

    private Task OnRootUI(Func<Task> test) => TestDispatcher.Run(async () =>
    {
        try { await test(); }
        finally
        {
            foreach (var host in hosts) await host.DisposeAsync();
            hosts.Clear();
        }
    });

    private static async Task<(PopupView Popup, Task<PopupNavigationResult<string?>> Result)> OpenPopup(Leaf origin)
    {
        PopupView.OpenedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var showing = origin.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token);
        await Task.WhenAny(showing, PopupView.OpenedSignal.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (showing.IsFaulted) await showing;
        return (await PopupView.OpenedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Token), showing);
    }

    [Fact]
    public Task Public_popup_origins_can_nest_but_covered_and_dismissed_origins_cannot_act() => OnRootUI(async () =>
    {
        var context = await Open<Leaf>();
        var (parent, parentResult) = await OpenPopup((Leaf)context.Current!.ViewModel);
        var (child, childResult) = await OpenPopup(parent.ViewModel);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await parent.ViewModel.Navigation.ClosePopup(Token)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => parent.ViewModel.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token));
        Success(await child.ViewModel.Navigation.ClosePopup<string?>(null, Token));
        var result = await Settle(childResult);
        Assert.True(result.HasResult); Assert.Null(result.Result);
        Assert.True(child.ViewModel.Resource.Disposed); Assert.False(parent.ViewModel.Resource.Disposed);
        Success(await parent.ViewModel.Navigation.ClosePopup("parent", Token));
        Assert.Equal("parent", (await Settle(parentResult)).Result);
        await Assert.ThrowsAsync<InvalidOperationException>(() => child.ViewModel.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token));
        Assert.Empty(context.Window.Navigation.ModalStack);
    });

    [Fact]
    public Task Enforced_root_dismisses_modal_and_nested_popups_and_rejects_all_old_origins() => OnRootUI(async () =>
    {
        var context = await Open<Tabs>();
        var leaf = (Leaf)context.Deepest.Current!.ViewModel;
        Success(await leaf.Navigation.NavigateTo<Modal>(cancellationToken: Token));
        var modal = (Modal)context.Host.CurrentContentNavigation!.Current!.ViewModel;
        var (parent, parentResult) = await OpenPopup(modal);
        var (child, childResult) = await OpenPopup(parent.ViewModel);
        child.ViewModel.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await factory.SetRoot<Leaf>(context.Window, cancellationToken: Token)).Status);
        Assert.False(parentResult.IsCompleted); Assert.False(childResult.IsCompleted);
        Success(await Settle(factory.SetRoot<Leaf>(context.Window, options: Enforced, cancellationToken: Token)));
        foreach (var pending in new[] { parentResult, childResult })
        {
            var result = await Settle(pending);
            Assert.False(result.HasResult); Assert.Equal(DismissalReason.RootReplaced, result.Reason);
        }
        foreach (var model in new Leaf[] { leaf, modal, parent.ViewModel, child.ViewModel })
        {
            Assert.True(model.Resource.Disposed); Assert.Equal(1, model.Dismissals);
            Assert.Equal(NavigationStatus.InvalidOrigin, (await model.Navigation.ClosePopup(Token)).Status);
        }
        Assert.Empty(context.Window.Navigation.ModalStack);
        Assert.IsType<Leaf>(context.Host.CurrentContentNavigation!.Current!.ViewModel);
    });

    [Fact]
    public Task Root_submission_inside_a_popup_guard_waits_for_the_guard_to_finish() => OnRootUI(async () =>
    {
        var context = await Open<Leaf>();
        var (popup, result) = await OpenPopup((Leaf)context.Current!.ViewModel);
        var entered = RootSignal(); var release = RootSignal(); RootTransitionHandle? request = null;
        popup.ViewModel.Guard = async () =>
        {
            Assert.Equal(NavigationStatus.Reentrant, (await factory.SetRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token)).Status);
            request = factory.RequestRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token);
            entered.TrySetResult(); await release.Task;
            Assert.False(popup.ViewModel.Resource.Disposed);
            return false;
        };
        var closing = popup.ViewModel.Navigation.ClosePopup(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(request!.Completion.IsCompleted);
        release.TrySetResult();
        Assert.Equal(NavigationStatus.GuardRejected, (await Settle(closing)).Status);
        Success(await Settle(request.Completion));
        Assert.Equal(DismissalReason.RootReplaced, (await Settle(result)).Reason);
        Assert.True(popup.ViewModel.Resource.Disposed);
    });

    [Fact]
    public Task Submitted_roots_coalesce_normals_but_preserve_required_order() => OnRootUI(async () =>
    {
        var context = await Open<Leaf>();
        var leaf = (Leaf)context.Current!.ViewModel;
        var entered = RootSignal(); var release = RootSignal();
        leaf.Guard = async () => { entered.TrySetResult(); await release.Task; return true; };
        var push = leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var options = new RootTransitionOptions { CoalescingKey = "resume" };
        var first = factory.RequestRoot<Leaf>(context.Window, new() { ["filter"] = "old" }, options, Token);
        var latest = factory.RequestRoot<Leaf>(context.Window, new() { ["filter"] = "new" }, options, Token);
        Assert.Equal(NavigationStatus.Superseded, (await Settle(first.Completion)).Status);
        var required = factory.RequestRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token);
        var lastRequired = factory.RequestRoot<Leaf>(context.Window, new() { ["filter"] = "last" }, Enforced, Token);
        Assert.Equal(NavigationStatus.Superseded, (await Settle(latest.Completion)).Status);
        release.TrySetResult();
        Assert.Equal(NavigationStatus.Superseded, (await Settle(push)).Status);
        Success(await Settle(required.Completion)); Success(await Settle(lastRequired.Completion));
        Assert.Equal("last", ((Leaf)context.Host.CurrentContentNavigation!.Current!.ViewModel).Filter);
    });

    [Fact]
    public async Task CloseFlyout_hides_only_the_enclosing_drawer_without_guards_or_history_changes()
    {
        var context = await Open<Flyout>();
        var flyout = context.Current!.Children!;
        var leaf = (Leaf)context.Deepest.Current!.ViewModel;
        Success(await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var detail = (Detail)context.Deepest.Current!.ViewModel;
        detail.Guard = () => throw new InvalidOperationException("Closing a drawer must not run guards.");
        var second = await Open<Flyout>();
        var otherFlyout = second.Current!.Children!;
        flyout.SetFlyout(true); otherFlyout.SetFlyout(true);
        var result = await detail.Navigation.CloseFlyout(Token);
        Success(result); Assert.True(result.HasCommitted);
        Assert.False(flyout.IsFlyoutOpen); Assert.True(otherFlyout.IsFlyoutOpen);
        Assert.Same(detail, context.Deepest.Current!.ViewModel);
        Assert.True(context.CanGoBack); Assert.False(detail.Resource.Disposed);
        result = await detail.Navigation.CloseFlyout(Token);
        Success(result); Assert.False(result.HasCommitted);
        detail.Guard = null;
        Success(await detail.Navigation.NavigateBack(Token));
        Assert.Equal(NavigationStatus.InvalidOrigin, (await detail.Navigation.CloseFlyout(Token)).Status);
        var plain = await Open<Leaf>();
        Assert.Equal(NavigationStatus.DestinationNotFound, (await ((Leaf)plain.Current!.ViewModel).Navigation.CloseFlyout(Token)).Status);
    }

    [Fact]
    public async Task CloseFlyout_from_a_guard_is_reentrant_and_does_not_hide_the_drawer()
    {
        var context = await Open<Flyout>();
        var leaf = (Leaf)context.Deepest.Current!.ViewModel;
        var flyout = context.Current!.Children!; flyout.SetFlyout(true);
        leaf.Guard = async () =>
        {
            Assert.Equal(NavigationStatus.Reentrant, (await leaf.Navigation.CloseFlyout(Token)).Status);
            return false;
        };
        Assert.Equal(NavigationStatus.GuardRejected, (await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token)).Status);
        Assert.True(flyout.IsFlyoutOpen);
    }

    [Fact]
    public async Task Both_alert_overloads_reject_hidden_and_dismissed_screen_origins()
    {
        var context = await Open<Leaf>();
        var leaf = (Leaf)context.Current!.ViewModel;
        var navigator = context.Current.View.Navigator;
        Success(await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator.DisplayAlertAsync("Title", "Message", "OK"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator.DisplayAlertAsync("Title", "Message", "Yes", "No"));
        Success(await factory.SetRoot<Leaf>(context.Window, options: Enforced, cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator.DisplayAlertAsync("Title", "Message", "OK"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator.DisplayAlertAsync("Title", "Message", "Yes", "No"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Submitted_roots_wait_for_popup_preparation_and_cleanup_callbacks(bool cleanup) => OnRootUI(async () =>
    {
        var context = await Open<Leaf>();
        var entered = RootSignal(); var release = RootSignal(); RootTransitionHandle? request = null;
        async Task Callback(PopupModel model)
        {
            Assert.Equal(NavigationStatus.Reentrant, (await factory.SetRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token)).Status);
            request = factory.RequestRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token);
            entered.TrySetResult(); await release.Task;
            Assert.False(model.Resource.Disposed);
        }
        var actions = services.GetRequiredService<PopupActions>();
        if (cleanup) actions.Cleanup = Callback; else actions.Before = Callback;
        var opening = OpenPopup((Leaf)context.Current!.ViewModel);
        Task<NavigationResult>? closing = null;
        if (cleanup) closing = (await opening).Popup.ViewModel.Navigation.ClosePopup(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(request!.Completion.IsCompleted);
        release.TrySetResult();
        var (popup, showing) = await opening;
        if (closing != null) Success(await Settle(closing));
        Success(await Settle(request.Completion));
        Assert.Equal(cleanup ? DismissalReason.DialogClosed : DismissalReason.RootReplaced, (await Settle(showing)).Reason);
        Assert.True(popup.ViewModel.Resource.Disposed); Assert.Equal(1, popup.ViewModel.Dismissals);
    });
}
