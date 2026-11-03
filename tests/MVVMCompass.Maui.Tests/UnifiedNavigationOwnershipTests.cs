using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task External_window_boundaries_release_hidden_destinations_and_modal_scopes_once(bool destroy) => OnRootUI(async () =>
    {
        var root = await Open<Flyout>();
        var parent = (Flyout)root.Current!.ViewModel;
        Success(await parent.Navigation.Select("workspace/archive", cancellationToken: Token));
        Success(await parent.Navigation.Select("other", cancellationToken: Token));
        var retained = root.AllScreens().Select(screen => (Leaf)screen.ViewModel).ToArray();
        Success(await parent.Navigation.NavigateTo<Modal>(cancellationToken: Token));
        var modal = Modal.Last!;
        var reason = destroy ? DismissalReason.WindowClosed : DismissalReason.RootReplaced;
        if (destroy) ((IWindow)root.Window).Destroying();
        else root.Window.Page = new ContentPage();
        await root.Host.ReconcileNativeAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
        await root.Host.DisposeAsync();
        Assert.All(retained.Append(modal), model =>
        {
            Assert.True(model.IsDismissed);
            Assert.Equal(1, model.Dismissals);
            Assert.True(model.Resource.Disposed);
            Assert.Equal(reason, model.Lifetime.Reason);
            Assert.Equal(!destroy, model.IsHostReplaced);
        });
    });

    [Fact]
    public Task Native_modal_cover_deactivates_and_reactivates_the_owned_root_without_dismissing_it() => OnRootUI(async () =>
    {
        var actions = services.GetRequiredService<RootActions>();
        var appearances = 0;
        actions.Appear = _ => { appearances++; return Task.CompletedTask; };
        var root = await Open<ApplicationRoot>();
        var model = (ApplicationRoot)root.Current!.ViewModel;
        var deactivations = 0;
        model.Deactivate = () => { deactivations++; return Task.CompletedTask; };
        var nativeModel = new ViewModelTests.Probe();
        var modal = new ContentPage { BindingContext = nativeModel };
        await root.Window.Navigation.PushModalAsync(modal, false);
        await root.Host.ReconcileNativeAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, deactivations);
        Assert.False(root.IsActive);
        Assert.False(model.IsDismissed);
        await root.Window.Navigation.PopModalAsync(false);
        await root.Host.ReconcileNativeAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
        await nativeModel.DismissAsync();
        Assert.True(root.IsActive);
        Assert.Equal(2, appearances);
        Assert.Equal(1, nativeModel.Dismissals);
        Assert.Equal(DismissalReason.Back, nativeModel.Lifetime.Reason);
        Assert.Equal(0, model.Dismissals);
        Assert.False(model.Resource.Disposed);
    });

    [Fact]
    public Task Native_modal_cleanup_cannot_dismiss_a_model_borrowed_from_another_window() => OnRootUI(async () =>
    {
        var first = await Open<Leaf>(); var second = await Open<Leaf>();
        var borrowed = (Leaf)first.Current!.ViewModel;
        await second.Window.Navigation.PushModalAsync(new ContentPage { BindingContext = borrowed }, false);
        await second.Host.ReconcileNativeAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
        await second.Window.Navigation.PopModalAsync(false);
        await second.Host.ReconcileNativeAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(borrowed.IsDismissed);
        Assert.False(borrowed.Resource.Disposed);
        Success(await borrowed.Navigation.NavigateTo<Detail>(cancellationToken: Token));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Window_teardown_cannot_dismiss_a_model_borrowed_by_a_native_modal(bool destroy) => OnRootUI(async () =>
    {
        var first = await Open<Leaf>(); var second = await Open<Leaf>();
        var borrowed = (Leaf)first.Current!.ViewModel;
        await second.Window.Navigation.PushModalAsync(new ContentPage { BindingContext = borrowed }, false);
        await second.Host.ReconcileNativeAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (destroy) ((IWindow)second.Window).Destroying();
        else second.Window.Page = new ContentPage();
        await second.Host.ReconcileNativeAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
        await second.Host.DisposeAsync();
        Assert.False(borrowed.IsDismissed);
        Assert.False(borrowed.IsHostReplaced);
        Assert.False(borrowed.Resource.Disposed);
        Success(await borrowed.Navigation.NavigateTo<Detail>(cancellationToken: Token));
    });

    [Fact]
    public Task External_root_replacement_cancels_preparation_and_cleans_its_late_candidate() => OnRootUI(async () =>
    {
        var root = await Open<Leaf>(); var outgoing = (Leaf)root.Current!.ViewModel;
        var actions = services.GetRequiredService<RootActions>();
        var entered = RootSignal(); var release = RootSignal();
        actions.Before = async _ => { entered.TrySetResult(); await release.Task; };
        var pending = factory.SetRoot<ApplicationRoot>(root.Window, cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var external = new ContentPage();
        root.Window.Page = external;
        release.TrySetResult();
        Assert.False((await Settle(pending)).IsSuccess);
        await root.Host.ReconcileNativeAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Same(external, root.Window.Page);
        Assert.True(outgoing.IsHostReplaced);
        Assert.True(outgoing.Resource.Disposed);
        var candidate = Assert.Single(actions.Created);
        Assert.Equal(1, candidate.Dismissals);
        Assert.True(candidate.Resource.Disposed);
        Assert.Equal(DismissalReason.PreparationFailed, candidate.Lifetime.Reason);
    });

    [Fact]
    public Task Window_destruction_from_cleanup_finishes_without_reentering_the_operation() => OnRootUI(async () =>
    {
        var root = await Open<ApplicationRoot>();
        var actions = services.GetRequiredService<RootActions>();
        actions.Cleanup = _ => { ((IWindow)root.Window).Destroying(); return Task.CompletedTask; };
        var replacement = await Settle(factory.SetRoot<Leaf>(root.Window, cancellationToken: Token));
        Assert.False(replacement.IsSuccess);
        await root.Host.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(root.Host.IsClosed);
        Assert.All(actions.Created, model => { Assert.Equal(1, model.Dismissals); Assert.True(model.Resource.Disposed); });
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Popup_preparation_failure_or_window_change_releases_the_candidate_scope(bool replaceWindow) => OnRootUI(async () =>
    {
        var root = await Open<Leaf>(); var caller = (Leaf)root.Current!.ViewModel;
        PopupModel? candidate = null;
        services.GetRequiredService<PopupActions>().Before = model =>
        {
            candidate = model;
            if (replaceWindow) { root.Window.Page = new ContentPage(); return Task.CompletedTask; }
            throw new InvalidOperationException("preparation");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => Settle(caller.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token)));
        Assert.NotNull(candidate);
        Assert.True(candidate.Resource.Disposed);
        Assert.Equal(1, candidate.Dismissals);
        Assert.Equal(DismissalReason.PreparationFailed, candidate.Lifetime.Reason);
        Assert.False(caller.IsPopupOpen);
        Assert.Empty(root.Window.Navigation.ModalStack);
        if (!replaceWindow) { Assert.False(caller.IsDismissed); Assert.False(caller.IsComingFromPopup); }
    });
}
