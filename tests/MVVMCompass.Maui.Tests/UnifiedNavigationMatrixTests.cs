using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Fact]
    public async Task Nested_container_removal_guards_retained_inactive_descendants()
    {
        var context = await Open<Flyout>(); var parent = (Flyout)context.Current!.ViewModel;
        var nested = (Tabs)context.Current.Children!.Current!.ViewModel;
        var hidden = (Leaf)context.Deepest.Current!.ViewModel;
        Success(await nested.Navigation.Select("archive", cancellationToken: Token));
        hidden.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await parent.Navigation.Remove("workspace", "other", Token)).Status);
        Assert.False(hidden.Resource.Disposed); Assert.False(nested.IsDismissed);
        hidden.Allowed = true;
        Success(await parent.Navigation.Remove("workspace", "other", Token));
        Assert.True(hidden.Resource.Disposed); Assert.True(nested.Resource.Disposed);
    }

    [Fact]
    public async Task Reset_by_ID_recreates_only_its_own_history_and_rejects_a_guarded_reset()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        var first = (Leaf)context.Deepest.Current!.ViewModel;
        Success(await parent.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var detail = (Detail)context.Deepest.Current!.ViewModel; detail.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await parent.Navigation.Reset("open", Token)).Status);
        detail.Allowed = true;
        Success(await parent.Navigation.Reset("open", Token));
        Assert.True(first.Resource.Disposed); Assert.True(detail.Resource.Disposed);
        Assert.NotSame(first, context.Deepest.Current.ViewModel);
        Assert.Equal(2, context.Current.Children!.Destinations.Count);
    }

    [Fact]
    public async Task Reusing_an_ID_for_a_different_registered_type_replaces_that_owner()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        var first = (Leaf)context.Deepest.Current!.ViewModel;
        Success(await parent.Replace([new(typeof(Detail), "Detail", "open"), Item("archive")]));
        Assert.True(first.Resource.Disposed); Assert.IsType<Detail>(context.Deepest.Current!.ViewModel);
        Assert.Equal("open", context.Current.Children!.SelectedDestinationId);
    }

    [Fact]
    public async Task Nearest_ID_wins_and_container_commands_resolve_from_their_own_collection()
    {
        var context = await Open<Flyout>(); var parent = (Flyout)context.Current!.ViewModel;
        Success(await parent.Replace([new(typeof(Tabs), "Workspace", "workspace"), Item("open")]));
        var nested = context.Deepest.Current;
        Success(await ((Leaf)nested!.ViewModel).Navigation.Select("open", cancellationToken: Token));
        Assert.Same(nested, context.Deepest.Current);
        Success(await parent.Navigation.Select("open", cancellationToken: Token));
        Assert.NotSame(nested, context.Deepest.Current);
        Assert.Equal("open", context.Current.Children!.SelectedDestinationId);
    }

    [Fact]
    public async Task Cancel_during_a_parent_origin_guard_retains_the_committed_items()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        var leaf = (Leaf)context.Deepest.Current!.ViewModel;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        leaf.Guard = async () => { entered.TrySetResult(); await release.Task; return true; };
        using var token = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var selecting = parent.Navigation.Select("archive", cancellationToken: token.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        token.Cancel(); release.TrySetResult();
        Assert.Equal(NavigationStatus.Cancelled, (await selecting).Status);
        Assert.Same(leaf, context.Deepest.Current.ViewModel); Assert.Equal("open", context.Current.Children!.SelectedDestinationId);
        leaf.Guard = null;
        Success(await parent.Navigation.Select("archive", cancellationToken: Token));
    }

    [Fact]
    public async Task Initial_selection_callback_and_retained_selection_report_destination_roots()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        var root = context.Deepest.Current!.ViewModel;
        Assert.Same(root, Assert.Single(parent.Selections));
        Success(await parent.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        Success(await parent.Navigation.Select("archive", cancellationToken: Token));
        Success(await parent.Navigation.Select("open", cancellationToken: Token));
        Assert.Equal(3, parent.Selections.Count); Assert.Same(root, parent.Selections[^1]);
    }

    [Fact]
    public async Task Missing_registration_reports_failure_and_preserves_the_current_origin()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel; var before = context.Deepest.Current;
        var result = await parent.Navigation.NavigateTo<Unregistered>(cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, result.Status); Assert.NotNull(result.Error);
        Assert.Same(before, context.Deepest.Current);
    }

    [Fact]
    public async Task Window_bootstrap_reports_a_missing_registration_without_stranding_its_spinner()
    {
        var window = factory.CreateWindow<Unregistered>(); hosts.Add(factory.ForWindow(window));
        var result = await factory.WaitForInitializationAsync(window, Token);
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.IsType<Label>(((ContentPage)window.Page!).Content);
    }

    [Fact]
    public async Task Background_service_calls_use_the_owning_window_and_preserve_origin_checks()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        Success(await Task.Run(() => parent.Navigation.Select("archive", cancellationToken: Token), Token));
        Assert.Equal("archive", context.Current.Children!.SelectedDestinationId);
        Success(await Task.Run(() => parent.Navigation.NavigateTo<Detail>(cancellationToken: Token), Token));
        Assert.IsType<Detail>(context.Deepest.Current!.ViewModel);
    }

    [Fact]
    public async Task Direct_native_popup_removal_checks_the_ViewModel_guard_before_commit()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        PopupView.OpenedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var showing = parent.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token);
        var popup = await PopupView.OpenedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        popup.ViewModel.Allowed = false;
        await context.Window.Navigation.PopModalAsync(false);
        await MVVMCompass.Services.PopupOwnership.NativeBackCompletion(popup);
        Assert.False(popup.ViewModel.IsDismissed);
        Assert.False(showing.IsCompleted);
        popup.ViewModel.Allowed = true;
        await context.Window.Navigation.PopModalAsync(false);
        await MVVMCompass.Services.PopupOwnership.NativeBackCompletion(popup);
        var result = await showing.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(result.HasResult); Assert.Equal(DismissalReason.Back, result.Reason);
        Assert.True(popup.ViewModel.Resource.Disposed); Assert.Equal(1, popup.ViewModel.Dismissals);
    }

    [Fact]
    public async Task Window_destruction_releases_a_guarded_popup_and_all_retained_scopes()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        PopupView.OpenedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var showing = parent.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token);
        var popup = await PopupView.OpenedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        popup.ViewModel.Allowed = false;
        ((Microsoft.Maui.IWindow)context.Window).Destroying();
        await context.Host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var result = await showing.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(DismissalReason.WindowClosed, result.Reason);
        Assert.True(popup.ViewModel.Resource.Disposed); Assert.True(parent.Resource.Disposed);
        Assert.Equal(1, popup.ViewModel.Dismissals);
    }

    [Fact]
    public async Task Container_swipe_setting_follows_the_active_body_and_obeys_its_guard()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        ((TabsView)context.Current.View).IsBackSwipeEnabled = true;
        Success(await parent.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var body = context.Deepest; var detail = (Detail)body.Current!.ViewModel;
        var gesture = Assert.Single(body.View.Presenter.GestureRecognizers.OfType<SwipeGestureRecognizer>());
        Assert.Empty(context.View.Presenter.GestureRecognizers);
        detail.Allowed = false;
        gesture.SendSwiped(body.View.Presenter, SwipeDirection.Right);
        await body.NativeBackCompletion;
        Assert.Same(detail, body.Current.ViewModel);
        detail.Allowed = true;
        gesture.SendSwiped(body.View.Presenter, SwipeDirection.Right);
        await body.NativeBackCompletion;
        Assert.True(detail.Resource.Disposed);
        ((TabsView)context.Current.View).IsBackSwipeEnabled = false;
        Assert.Empty(body.View.Presenter.GestureRecognizers);
    }

    public sealed class Unregistered : ViewModelBase;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Waiting_popup_guards_can_be_cancelled_and_do_not_block_window_destruction(bool destroyWindow) => TestDispatcher.Run(async () =>
    {
        try
        {
            var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
            PopupView.OpenedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var showing = parent.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token);
            var popup = await PopupView.OpenedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            popup.ViewModel.Guard = async () => { entered.TrySetResult(); await release.Task; return true; };
            using var cancellation = new CancellationTokenSource();
            var closing = popup.ViewModel.Navigation.ClosePopup(cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                if (destroyWindow)
                {
                    ((Microsoft.Maui.IWindow)context.Window).Destroying();
                    await context.Host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
                    Assert.Equal(DismissalReason.WindowClosed, (await showing.WaitAsync(TimeSpan.FromSeconds(10), Token)).Reason);
                    Assert.True(popup.ViewModel.Resource.Disposed);
                }
                else
                {
                    cancellation.Cancel();
                    Assert.Equal(NavigationStatus.Cancelled, (await closing.WaitAsync(TimeSpan.FromSeconds(10), Token)).Status);
                    Assert.False(showing.IsCompleted); Assert.False(popup.ViewModel.Resource.Disposed);
                    popup.ViewModel.Guard = null;
                    Success(await popup.ViewModel.Navigation.ClosePopup(Token));
                    await showing.WaitAsync(TimeSpan.FromSeconds(10), Token);
                }
            }
            finally { release.TrySetResult(); }
            await closing.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(1, popup.ViewModel.Dismissals);
        }
        finally
        {
            // Finish host cleanup while its owning dispatcher is still running.
            foreach (var host in hosts) await host.DisposeAsync();
            hosts.Clear();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Popup_outside_tap_policy_does_not_replace_the_platform_Back_guard(bool outsideTap)
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        PopupView.OpenedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var showing = parent.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token);
        var popup = await PopupView.OpenedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        popup.CanBeDismissedByTappingOutsideOfPopup = outsideTap;
        var layout = (Layout)((ContentPage)context.Window.Navigation.ModalStack[^1]).Content!;
        var overlay = layout.Children.OfType<BoxView>().Single();
        var tap = overlay.GestureRecognizers.OfType<TapGestureRecognizer>().Single();
        popup.ViewModel.Allowed = false;
        DeliverTap();
        await MVVMCompass.Services.PopupOwnership.NativeBackCompletion(popup);
        Assert.False(showing.IsCompleted);
        Assert.True(context.RequestPlatformBack());
        await context.NativeBackCompletion;
        Assert.False(showing.IsCompleted);
        popup.ViewModel.Allowed = true;
        if (outsideTap) DeliverTap();
        else Assert.True(context.RequestPlatformBack());
        await MVVMCompass.Services.PopupOwnership.NativeBackCompletion(popup);
        var result = await showing.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(outsideTap, result.WasDismissedByTappingOutsideOfPopup);
        Assert.False(result.HasResult); Assert.True(popup.ViewModel.Resource.Disposed);
        void DeliverTap() => typeof(TapGestureRecognizer).GetMethod("SendTapped", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(tap, [overlay, (Func<Microsoft.Maui.IElement?, Microsoft.Maui.Graphics.Point?>)(_ => new(-1, -1))]);
    }
}
