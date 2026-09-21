using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Fact]
    public Task Tabs_initialize_once_reactivate_on_return_and_release_subscriptions_only_on_removal() => OnRootUI(async () =>
    {
        var root = await Open<Tabs>(); var tabs = (Tabs)root.Current!.ViewModel;
        var actions = services.GetRequiredService<RootActions>();
        var prepared = 0; var appeared = 0; var deactivated = 0; var released = 0;
        actions.Before = model =>
        {
            prepared++;
            model.Deactivate = () => { deactivated++; return Task.CompletedTask; };
            model.Ownership.RegisterCleanup(() => { released++; return Task.CompletedTask; });
            return Task.CompletedTask;
        };
        actions.Appear = _ => { appeared++; return Task.CompletedTask; };
        Success(await tabs.Replace([Item("open"), new(typeof(ApplicationRoot), "Lifecycle", "lifecycle")]));
        Assert.Equal(0, prepared);
        Success(await tabs.Navigation.Select("lifecycle", cancellationToken: Token));
        var retained = root.Deepest.Current;
        Success(await tabs.Navigation.Select("open", cancellationToken: Token));
        Assert.Equal(0, released); Assert.Equal(1, deactivated);
        Success(await tabs.Navigation.Select("lifecycle", cancellationToken: Token));
        Assert.Same(retained, root.Deepest.Current); Assert.Equal(1, prepared); Assert.Equal(2, appeared);
        Success(await tabs.Replace([Item("open")]));
        Assert.Equal(1, released); Assert.Equal(2, deactivated);
    });

    [Theory]
    [InlineData("caller")]
    [InlineData("root")]
    [InlineData("window")]
    public Task Pending_tab_warning_settles_when_cancelled_or_its_owner_ends(string end) => OnRootUI(async () =>
    {
        var root = await Open<Tabs>(); var origin = (Leaf)root.Deepest.Current!.ViewModel;
        var (cover, coveringResult) = await OpenPopup(origin); var opened = NextPopup();
        using var cancellation = new CancellationTokenSource();
        var warning = origin.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: cancellation.Token);
        if (end == "caller") cancellation.Cancel();
        else if (end == "root") Success(await factory.SetRoot<Leaf>(root.Window, options: Enforced, cancellationToken: Token));
        else await root.Host.DisposeAsync();
        var result = await Settle(warning.Completion);
        Assert.Equal(end switch { "caller" => PopupRequestStatus.Cancelled, "root" => PopupRequestStatus.RootReplaced,
            _ => PopupRequestStatus.WindowClosed }, result.Status);
        Assert.False(result.WasPresented); Assert.False(opened.Task.IsCompleted);
        if (end == "caller") Success(await cover.ViewModel.Navigation.ClosePopup(Token));
        await Settle(coveringResult);
    });

    [Fact]
    public Task Removing_a_tab_invalidates_the_warning_queued_by_its_leave_guard() => OnRootUI(async () =>
    {
        var root = await Open<Tabs>(); var tabs = (Tabs)root.Current!.ViewModel;
        var origin = (Leaf)root.Deepest.Current!.ViewModel;
        var opened = NextPopup(); PopupRequest<string?>? warning = null;
        origin.Guard = () =>
        {
            warning = origin.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token);
            return Task.FromResult(true);
        };
        Success(await tabs.Replace([Item("archive")]));
        Assert.Equal(PopupRequestStatus.InvalidOrigin, (await Settle(warning!.Completion)).Status);
        Assert.False(opened.Task.IsCompleted);
        Assert.True(origin.IsDismissed); Assert.True(origin.Resource.Disposed); Assert.Equal(1, origin.Dismissals);
    });

    [Fact]
    public Task Cancelling_tab_selection_keeps_the_active_warning_eligible() => OnRootUI(async () =>
    {
        var root = await Open<Tabs>(); var tabs = (Tabs)root.Current!.ViewModel;
        var origin = (Leaf)root.Deepest.Current!.ViewModel;
        var opened = NextPopup(); PopupRequest<string?>? warning = null;
        using var cancellation = new CancellationTokenSource();
        origin.Guard = () =>
        {
            warning = origin.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token);
            cancellation.Cancel(); return Task.FromResult(false);
        };
        Assert.Equal(NavigationStatus.Cancelled, (await tabs.Navigation.Select("archive", cancellationToken: cancellation.Token)).Status);
        Assert.Same(origin, root.Deepest.Current!.ViewModel);
        var popup = await Opened(opened); Success(await popup.ViewModel.Navigation.ClosePopup(Token));
        Assert.Equal(PopupRequestStatus.Completed, (await Settle(warning!.Completion)).Status);
    });

    [Theory]
    [InlineData("remove")]
    [InlineData("root")]
    [InlineData("window")]
    public Task Retained_tab_loading_and_lifetime_cleanup_are_independent_of_selection(string end) => OnRootUI(async () =>
    {
        var root = await Open<Tabs>(); var tabs = (Tabs)root.Current!.ViewModel;
        var first = (LeafView)root.Deepest.Current!.View;
        root.Current.View.LoadingPresentationTemplate = new DataTemplate(() => new Label());
        first.LoadingBackdrop = new SolidColorBrush(Colors.Red);
        var releases = 0;
        first.ViewModel.Ownership.RegisterCleanup(() => { releases++; return Task.CompletedTask; });
        var loading = first.BeginLoading(LoadingType.LogingIn);
        Assert.True(root.View.IsBusyPresented); Assert.Same(first.LoadingBackdrop, root.View.BusyBackdrop);
        Success(await tabs.Navigation.Select("archive", cancellationToken: Token));
        Assert.False(root.View.IsBusyPresented); Assert.False(first.ViewModel.IsDismissed); Assert.Equal(0, releases);
        var second = (LeafView)root.Deepest.Current!.View;
        second.LoadingBackdrop = new SolidColorBrush(Colors.Blue);
        using (second.BeginLoading(LoadingType.Loading))
        {
            Assert.Same(second.LoadingBackdrop, root.View.BusyBackdrop);
            Assert.Equal(LoadingType.Loading, Assert.IsType<LoadingPresentationContext>(root.View.BusyContent!.BindingContext).LoadingType);
        }
        Success(await tabs.Navigation.Select("open", cancellationToken: Token));
        Assert.Same(first, root.Deepest.Current!.View);
        Assert.True(root.View.IsBusyPresented); Assert.Same(first.LoadingBackdrop, root.View.BusyBackdrop);
        Assert.Equal(LoadingType.LogingIn, Assert.IsType<LoadingPresentationContext>(root.View.BusyContent!.BindingContext).LoadingType);
        if (end == "remove") Success(await tabs.Replace([Item("archive")]));
        else if (end == "root") Success(await factory.SetRoot<Leaf>(root.Window, options: Enforced, cancellationToken: Token));
        else await root.Host.DisposeAsync();
        Assert.Equal(1, releases); Assert.True(first.ViewModel.Resource.Disposed);
        loading.Dispose(); loading.Dispose();
        Assert.False(root.View.IsBusyPresented);
    });
}
