using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public Task Ownership_boundary_rejected_raw_popup_preserves_screen(bool windowApi, bool typed, bool otherWindow) => OnRootUI(async () =>
    {
        var owner = await Open<Leaf>(); var origin = otherWindow ? await Open<Leaf>() : owner;
        var borrowed = (Leaf)owner.Current!.ViewModel;
        AttachWindowDialogs(origin);
        var popup = new Popup { BindingContext = borrowed, Content = new Label() };
        var typedPopup = new Popup<string> { BindingContext = borrowed, Content = new Label() };
        Task Show() => windowApi
            ? typed ? factory.ShowPopupAsync(origin.Window, typedPopup, cancellationToken: Token)
                : factory.ShowPopupAsync(origin.Window, popup, cancellationToken: Token)
            : typed ? origin.Current!.View.ShowPopupAsync(typedPopup, token: Token)
                : origin.Current!.View.ShowPopupAsync(popup, token: Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Settle(Show()));
        Assert.Empty(origin.Window.Navigation.ModalStack);
        Assert.False(borrowed.IsDismissed); Assert.False(borrowed.Resource.Disposed);
        Success(await borrowed.Navigation.NavigateTo<Detail>(cancellationToken: Token));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Ownership_boundary_rejected_raw_popup_preserves_another_presentation(bool windowApi, bool sameView) => OnRootUI(async () =>
    {
        var owner = await Open<Leaf>();
        var (existing, showing) = await OpenPopup((Leaf)owner.Current!.ViewModel);
        var origin = await Open<Leaf>(); AttachWindowDialogs(origin);
        var rejected = sameView ? (Popup<string?>)existing : new Popup<string?> { BindingContext = existing.ViewModel, Content = new Label() };
        Task Show() => windowApi ? factory.ShowPopupAsync(origin.Window, rejected, cancellationToken: Token)
            : origin.Current!.View.ShowPopupAsync(rejected, token: Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Settle(Show()));
        Assert.False(existing.ViewModel.IsDismissed); Assert.False(existing.ViewModel.Resource.Disposed);
        Assert.Single(owner.Window.Navigation.ModalStack); Assert.Empty(origin.Window.Navigation.ModalStack);
        Success(await existing.ViewModel.Navigation.ClosePopup("still active", Token));
        Assert.Equal("still active", (await Settle(showing)).Result);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Ownership_boundary_registered_rejection_cleans_candidate_but_preserves_borrowed_screen(bool deferred) => OnRootUI(async () =>
    {
        var root = await Open<Leaf>(); var borrowed = (Leaf)root.Current!.ViewModel;
        PopupModel? candidate = null;
        var child = new ViewModelTests.Probe();
        var actions = services.GetRequiredService<PopupActions>();
        actions.Created = popup => popup.Content = new ContentView { BindingContext = borrowed };
        actions.Before = model => { candidate = model; model.Ownership.Adopt(child.Ownership); return Task.CompletedTask; };

        if (deferred)
        {
            var request = borrowed.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token);
            Assert.Equal(PopupRequestStatus.PreparationFailed, (await Settle(request.Completion)).Status);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => Settle(borrowed.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token)));
        Assert.NotNull(candidate); Assert.True(candidate.IsDismissed); Assert.True(candidate.Resource.Disposed);
        Assert.Equal(1, candidate.Dismissals); Assert.Equal(1, child.Dismissals);
        Assert.False(borrowed.IsDismissed); Assert.False(borrowed.Resource.Disposed);
        Success(await borrowed.Navigation.NavigateTo<Detail>(cancellationToken: Token));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Ownership_boundary_untracked_popup_close_preserves_borrowed_screen(bool otherWindow) => OnRootUI(async () =>
    {
        var owner = await Open<Leaf>(); var origin = otherWindow ? await Open<Leaf>() : owner;
        var borrowed = (Leaf)owner.Current!.ViewModel;
        var popup = new BorrowedModelPopup(borrowed) { Content = new Label() };
        var opened = RootSignal(); popup.Opened += (_, _) => opened.TrySetResult();
        var showing = origin.Window.Navigation.ShowPopupAsync<string>(popup, token: Token);
        await Settle(opened.Task);
        await Settle(popup.CloseAsync("closed", Token));
        Assert.Equal("closed", (await Settle(showing)).Result);
        Assert.False(borrowed.IsDismissed); Assert.False(borrowed.Resource.Disposed);
        Success(await borrowed.Navigation.NavigateTo<Detail>(cancellationToken: Token));
    });

    [Theory]
    [InlineData("close", false)]
    [InlineData("close", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    [InlineData("destroy", false)]
    [InlineData("destroy", true)]
    public Task Ownership_boundary_native_modal_cleanup_preserves_foreign_popup(string end, bool registered) => OnRootUI(async () =>
    {
        var owner = await Open<Leaf>();
        ViewModelBase borrowed;
        Func<Task> closePopup;
        Task showing;
        if (registered)
        {
            var opened = await OpenPopup((Leaf)owner.Current!.ViewModel);
            borrowed = opened.Popup.ViewModel; showing = opened.Result;
            closePopup = async () => Success(await opened.Popup.ViewModel.Navigation.ClosePopup("ok", Token));
        }
        else
        {
            borrowed = new ViewModelTests.Probe();
            AttachWindowDialogs(owner);
            var popup = new Popup<string> { BindingContext = borrowed, Content = new Label() };
            var opened = RootSignal(); popup.Opened += (_, _) => opened.TrySetResult();
            showing = owner.Current!.View.ShowPopupAsync(popup, token: Token);
            await Settle(opened.Task); closePopup = () => popup.CloseAsync("ok", Token);
        }
        var other = await Open<Leaf>();
        await other.Window.Navigation.PushModalAsync(new ContentPage { BindingContext = borrowed }, false);
        await Settle(other.Host.ReconcileNativeAsync());
        if (end == "close") await other.Window.Navigation.PopModalAsync(false);
        else if (end == "replace") other.Window.Page = new ContentPage();
        else ((IWindow)other.Window).Destroying();
        await Settle(other.Host.ReconcileNativeAsync());
        if (end != "close") await other.Host.DisposeAsync();

        Assert.False(borrowed.IsDismissed); Assert.False(borrowed.IsHostReplaced);
        Assert.Single(owner.Window.Navigation.ModalStack);
        await Settle(closePopup()); await Settle(showing);
        Assert.True(borrowed.IsDismissed);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Ownership_boundary_rejected_raw_candidate_remains_caller_owned(bool windowApi) => OnRootUI(async () =>
    {
        var root = await Open<Leaf>(); var borrowed = (Leaf)root.Current!.ViewModel;
        AttachWindowDialogs(root);
        var candidate = new ViewModelTests.Probe();
        var popup = new Popup<string> { BindingContext = candidate, Content = new ContentView { BindingContext = borrowed } };
        Task Show() => windowApi ? factory.ShowPopupAsync(root.Window, popup, cancellationToken: Token)
            : root.Current!.View.ShowPopupAsync(popup, token: Token);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Settle(Show()));
            Assert.False(candidate.IsDismissed); Assert.False(borrowed.IsDismissed);
        }
        finally { await candidate.DismissAsync(); }
    });

    [Fact]
    public Task Ownership_boundary_accepted_raw_candidate_is_cleaned_if_observation_fails() => OnRootUI(async () =>
    {
        var root = await Open<ProfileTabs>(); var parent = (ProfileTabsView)root.Current!.View;
        AttachWindowDialogs(root); parent.RejectModal = true;
        var model = new ViewModelTests.Probe();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Settle(root.Deepest.Current!.View.ShowPopupAsync(
            new Popup<string> { BindingContext = model, Content = new Label() }, token: Token)));
        Assert.True(model.IsDismissed); Assert.Equal(1, model.Dismissals);
        Assert.Single(parent.ModalStarts); Assert.Equal(parent.ModalStarts, parent.ModalEnds);
        Assert.Empty(root.Window.Navigation.ModalStack);
    });

    [Theory]
    [InlineData("screen")]
    [InlineData("popup")]
    [InlineData("application")]
    public Task Ownership_boundary_raw_popup_cannot_take_another_models_owned_child(string ownerKind) => OnRootUI(async () =>
    {
        var owner = await Open<Leaf>();
        ViewModelBase parent = ownerKind switch
        {
            "screen" => owner.Current!.ViewModel,
            "popup" => (await OpenPopup((Leaf)owner.Current!.ViewModel)).Popup.ViewModel,
            _ => new ViewModelTests.Probe()
        };
        var child = new ViewModelTests.Probe(); parent.Ownership.Adopt(child.Ownership);
        var origin = await Open<Leaf>(); AttachWindowDialogs(origin);
        var popup = new Popup<string> { BindingContext = child, Content = new Label() };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Settle(origin.Current!.View.ShowPopupAsync(popup, token: Token)));
            Assert.False(child.IsDismissed); Assert.Same(parent.Ownership, child.Ownership.Parent);
        }
        finally { if (ownerKind == "application") await parent.DismissAsync(); }
    });

    [Fact]
    public Task Ownership_boundary_raw_popup_accepts_and_cleans_its_own_child_tree() => OnRootUI(async () =>
    {
        var origin = await Open<Leaf>(); AttachWindowDialogs(origin);
        var parent = new ViewModelTests.Probe(); var child = new ViewModelTests.Probe();
        parent.Ownership.Adopt(child.Ownership);
        var popup = new Popup<string> { BindingContext = parent, Content = new ContentView { BindingContext = child } };
        var opened = RootSignal(); popup.Opened += (_, _) => opened.TrySetResult();
        var showing = origin.Current!.View.ShowPopupAsync(popup, token: Token);
        await Settle(opened.Task); await Settle(popup.CloseAsync("ok", Token)); await Settle(showing);
        Assert.Equal(1, parent.Dismissals); Assert.Equal(1, child.Dismissals);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Ownership_boundary_preparing_popup_model_cannot_be_taken_by_another_entry(bool asScreen) => OnRootUI(async () =>
    {
        var owner = await Open<Leaf>(); var other = await Open<Leaf>(); AttachWindowDialogs(other);
        var entered = new TaskCompletionSource<PopupModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = RootSignal();
        services.GetRequiredService<PopupActions>().Before = async model => { entered.TrySetResult(model); await release.Task; };
        var opened = OpenPopup((Leaf)owner.Current!.ViewModel);
        var candidate = await Settle(entered.Task);
        try
        {
            if (asScreen)
            {
                var result = await Settle(other.PushAsync(ScreenFactory.Create(() => candidate, model => new ViewBase<PopupModel>(model)), cancellationToken: Token));
                Assert.Equal(NavigationStatus.Failed, result.Status);
            }
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => Settle(other.Current!.View.ShowPopupAsync(
                    new Popup<string> { BindingContext = candidate, Content = new Label() }, token: Token)));
            Assert.False(candidate.IsDismissed); Assert.False(candidate.Resource.Disposed);
        }
        finally { release.TrySetResult(); }
        var existing = await Settle(opened);
        Success(await existing.Popup.ViewModel.Navigation.ClosePopup("prepared", Token));
        Assert.Equal("prepared", (await Settle(existing.Result)).Result);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Ownership_boundary_screen_factory_cannot_take_an_active_raw_popup_or_its_child(bool useChild) => OnRootUI(async () =>
    {
        var owner = await Open<Leaf>(); AttachWindowDialogs(owner);
        var parent = new ViewModelTests.Probe(); var child = new ViewModelTests.Probe();
        parent.Ownership.Adopt(child.Ownership);
        var popup = new Popup<string> { BindingContext = parent, Content = new Label() };
        var opened = RootSignal(); popup.Opened += (_, _) => opened.TrySetResult();
        var showing = owner.Current!.View.ShowPopupAsync(popup, token: Token); await Settle(opened.Task);
        var other = await Open<Leaf>(); var borrowed = useChild ? child : parent;
        var result = await Settle(other.PushAsync(ScreenFactory.Create(() => borrowed, model => new ViewBase<ViewModelTests.Probe>(model)), cancellationToken: Token));
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.False(parent.IsDismissed); Assert.False(child.IsDismissed);
        Assert.Same(parent.Ownership, child.Ownership.Parent);
        await Settle(popup.CloseAsync("ok", Token)); await Settle(showing);
        Assert.Equal(1, parent.Dismissals); Assert.Equal(1, child.Dismissals);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Ownership_boundary_registered_modal_close_preserves_borrowed_root(bool useChild, bool nativeClose) => OnRootUI(async () =>
    {
        var root = await Open<Leaf>(); var parent = (Leaf)root.Current!.ViewModel;
        var child = new ViewModelTests.Probe(); parent.Ownership.Adopt(child.Ownership);
        Success(await parent.Navigation.NavigateTo<Modal>(cancellationToken: Token));
        var modal = root.Host.CurrentContentNavigation!;
        modal.Current!.View.Content = new ContentView { BindingContext = useChild ? child : parent };
        var modalModel = (Modal)modal.Current.ViewModel;
        if (nativeClose)
        {
            await root.Window.Navigation.PopModalAsync(false);
            await Settle(root.Host.ReconcileNativeAsync());
        }
        else Success(await modalModel.Navigation.NavigateBack(Token));
        Assert.True(modalModel.IsDismissed); Assert.True(modalModel.Resource.Disposed);
        Assert.False(parent.IsDismissed); Assert.False(parent.Resource.Disposed); Assert.False(child.IsDismissed);
        Success(await parent.Navigation.NavigateTo<Detail>(cancellationToken: Token));
    });

    private sealed class BorrowedModelPopup(Leaf model) : PopupViewBase<Leaf, string>(model);
}
