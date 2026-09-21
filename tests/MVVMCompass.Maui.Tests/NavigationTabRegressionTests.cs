using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Tabs_Rejected_selection_defers_warning_until_guard_finishes(bool nested, bool selector) => OnRootUI(async () =>
    {
        var root = nested ? await Open<Flyout>() : await Open<Tabs>();
        var tabOwner = root.ActiveChain().Single(context => context.Current?.ViewModel is Tabs);
        var tabs = (Tabs)tabOwner.Current!.ViewModel;
        var child = tabOwner.Current.Children!;
        var origin = (Leaf)child.Current!.ViewModel;
        var entered = RootSignal(); var release = RootSignal(); var opened = NextPopup();
        PopupRequest<string?>? request = null;
        origin.Guard = async () =>
        {
            request = origin.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token);
            entered.TrySetResult(); await release.Task; return false;
        };
        Task<NavigationResult>? selection = null;
        if (selector) child.TabItems.Single(item => item.Id == "archive").SelectCommand.Execute(null);
        else selection = tabs.Navigation.Select("archive", cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.False(opened.Task.IsCompleted);
        Assert.False(root.View.IsBusyPresented);
        Assert.Equal("open", child.SelectedDestinationId);
        release.TrySetResult();
        if (selection != null) Assert.Equal(NavigationStatus.GuardRejected, (await Settle(selection)).Status);
        var popup = await Opened(opened);
        Assert.Same(origin, child.Current.ViewModel);
        Assert.Equal("open", child.SelectedDestinationId);
        Success(await popup.ViewModel.Navigation.ClosePopup("explained", Token));
        Assert.Equal("explained", (await Settle(request!.Completion)).PopupResult!.Result);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Tabs_Leaving_then_returning_invalidates_old_activation_warning(bool nested) => OnRootUI(async () =>
    {
        var root = nested ? await Open<Flyout>() : await Open<Tabs>();
        var tabOwner = root.ActiveChain().Single(context => context.Current?.ViewModel is Tabs);
        var tabs = (Tabs)tabOwner.Current!.ViewModel;
        var origin = (Leaf)root.Deepest.Current!.ViewModel;
        var opened = NextPopup(); PopupRequest<string?>? request = null;
        origin.Guard = () =>
        {
            request = origin.Navigation.RequestPopup<PopupModel, string?>(requestKey: "warning", cancellationToken: Token);
            return Task.FromResult(true);
        };
        Success(await tabs.Navigation.Select("archive", cancellationToken: Token));
        Success(await tabs.Navigation.Select("open", cancellationToken: Token));
        Assert.Same(origin, root.Deepest.Current!.ViewModel);
        Assert.Equal(PopupRequestStatus.InvalidOrigin, (await Settle(request!.Completion)).Status);
        Assert.False(opened.Task.IsCompleted);
        origin.Guard = null;
        var fresh = origin.Navigation.RequestPopup<PopupModel, string?>(requestKey: "warning", cancellationToken: Token);
        var popup = await Opened(opened);
        Assert.NotSame(request, fresh);
        Success(await popup.ViewModel.Navigation.ClosePopup(Token));
        Assert.Equal(PopupRequestStatus.Completed, (await Settle(fresh.Completion)).Status);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Tabs_New_tab_requests_popup_during_preparation_or_activation(bool activation) => OnRootUI(async () =>
    {
        var root = await Open<Tabs>(); var tabs = (Tabs)root.Current!.ViewModel;
        Success(await tabs.Replace([Item("open"), new(typeof(ApplicationRoot), "Notice", "notice")]));
        var entered = RootSignal(); var release = RootSignal(); var opened = NextPopup();
        PopupRequest<string?>? request = null;
        async Task Callback(ApplicationRoot model)
        {
            request = model.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token);
            entered.TrySetResult(); await release.Task;
        }
        var actions = services.GetRequiredService<RootActions>();
        if (activation) actions.Appear = Callback; else actions.Before = Callback;
        var selection = tabs.Navigation.Select("notice", cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.False(opened.Task.IsCompleted); Assert.False(selection.IsCompleted);
        release.TrySetResult(); Success(await Settle(selection));
        var popup = await Opened(opened);
        Assert.Equal("notice", root.Current.Children!.SelectedDestinationId);
        Success(await popup.ViewModel.Navigation.ClosePopup(Token));
        Assert.Equal(PopupRequestStatus.Completed, (await Settle(request!.Completion)).Status);
    });

    [Fact]
    public Task Tabs_Toolbar_follows_committed_tab_and_ignores_inactive_bindings() => OnRootUI(async () =>
    {
        var root = await Open<Tabs>(); var tabs = (Tabs)root.Current!.ViewModel;
        var toolbar = root.View.Toolbar;
        var first = root.Deepest.Current!;
        var firstCenter = new Entry { Text = "First" };
        first.View.Toolbar = new()
        {
            CenterContent = firstCenter, Background = new SolidColorBrush(Colors.Red), Padding = 1,
            LeadingSlotWidth = 30, CenterPlacement = ToolbarCenterPlacement.Middle,
            RightItems = [new() { Text = "First action", Command = new Command(() => { }) }]
        };
        var oldAction = Descendants(toolbar).OfType<Button>().Single(button => button.Text == "First action").Command!;
        Assert.True(oldAction.CanExecute(null));
        Success(await tabs.Navigation.Select("archive", cancellationToken: Token));
        var second = root.Deepest.Current!;
        second.View.Toolbar = new() { Background = new SolidColorBrush(Colors.Blue), Padding = 9 };
        Assert.Same(second.View.Toolbar.Background, toolbar.EffectiveBackground);
        Assert.Equal(new Thickness(9), toolbar.LayoutForTests.Padding);
        Assert.False(oldAction.CanExecute(null));
        first.View.Toolbar.Background = new SolidColorBrush(Colors.Green);
        first.View.Toolbar.Padding = 3;
        Assert.Same(second.View.Toolbar.Background, toolbar.EffectiveBackground);
        Assert.Equal(new Thickness(9), toolbar.LayoutForTests.Padding);
        ((Leaf)second.ViewModel).Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await tabs.Navigation.Select("open", cancellationToken: Token)).Status);
        Assert.Same(second.View.Toolbar.Background, toolbar.EffectiveBackground);
        ((Leaf)second.ViewModel).Allowed = true;
        Success(await tabs.Navigation.Select("open", cancellationToken: Token));
        Assert.Same(toolbar, root.View.Toolbar);
        Assert.Same(firstCenter, toolbar.CenterHostForTests.Content);
        Assert.Same(first.View.Toolbar.Background, toolbar.EffectiveBackground);
        Assert.Equal(new Thickness(3), toolbar.LayoutForTests.Padding);
        Assert.False(oldAction.CanExecute(null));
    });

    [Theory]
    [InlineData(TabBarPosition.Top, TabItemSizing.Content)]
    [InlineData(TabBarPosition.Bottom, TabItemSizing.Content)]
    [InlineData(TabBarPosition.Left, TabItemSizing.Content)]
    [InlineData(TabBarPosition.Right, TabItemSizing.Content)]
    [InlineData(TabBarPosition.Top, TabItemSizing.Equal)]
    [InlineData(TabBarPosition.Bottom, TabItemSizing.Equal)]
    [InlineData(TabBarPosition.Left, TabItemSizing.Equal)]
    [InlineData(TabBarPosition.Right, TabItemSizing.Equal)]
    public Task Tabs_Membership_retains_controls_and_publishes_one_collection(TabBarPosition position, TabItemSizing sizing) => OnRootUI(async () =>
    {
        var root = await Open<Tabs>(); var owner = (TabsView)root.Current!.View;
        var tabs = owner.ViewModel; var child = root.Current.Children!;
        owner.TabBarPosition = position; owner.TabItemSizing = sizing;
        var created = 0;
        var template = new DataTemplate(() => { created++; return new Label(); });
        owner.SelectedTabItemTemplate = owner.UnselectedTabItemTemplate = template;
        var selector = position is TabBarPosition.Left or TabBarPosition.Right ? child.View.RailSelector : child.View.TabSelector;
        var content = selector.Content;
        var trailingParent = owner.TabBarTrailingContent!.Parent;
        var archive = Descendants(selector).OfType<Button>().Single(button => button.AutomationId == "destination-archive");
        var before = created;
        var counts = new List<int>();
        ((System.Collections.Specialized.INotifyCollectionChanged)child.TabItems).CollectionChanged += (_, _) => counts.Add(child.TabItems.Count);
        Success(await tabs.Replace([Item("archive"), Item("open"), Item("third")]));
        Assert.Equal([3], counts);
        Assert.Equal(before + 1, created);
        Assert.Same(archive, Descendants(selector).OfType<Button>().Single(button => button.AutomationId == "destination-archive"));
        Assert.Same(content, selector.Content);
        Assert.Same(trailingParent, owner.TabBarTrailingContent.Parent);
        Assert.Same(tabs, owner.TabBarTrailingContent.BindingContext);
        Success(await tabs.Navigation.Select("archive", cancellationToken: Token));
        Assert.Equal(before + 1, created);
        var removed = Descendants(selector).OfType<Button>().Single(button => button.AutomationId == "destination-third").Command!;
        Success(await tabs.Replace([Item("archive"), Item("open")]));
        Assert.Equal([3, 2], counts);
        Assert.False(removed.CanExecute(null));
    });
}
