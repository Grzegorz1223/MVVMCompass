using MVVMCompass.Core;

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
    public async Task View_lifecycle_returns_to_tab_root_once_after_back_or_pop(bool compatibility, bool popToRoot, bool deferredAttachment)
    {
        Profile.Options = new() { Lifecycle = compatibility ? TabLifecycle.TabSwitchCompatibility : TabLifecycle.Standard };
        var root = await Open<ProfileTabs>(); var parent = (ProfileTabsView)root.Current!.View;
        var tab = (ProfileLeafView)parent.CurrentTab!.View!;
        AttachDesktop(root.Window, parent, tab);
        var events = new List<ViewNavigationKind>();
        var selections = 0;
        tab.NavigatedTo += (_, args) => events.Add(args.Kind);
        parent.CurrentTabChanged += (_, _) => selections++;
        Success(await tab.ViewModel.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        if (popToRoot) Success(await ((Leaf)root.Deepest.Current!.ViewModel).Navigation.NavigateTo<Detail>(cancellationToken: Token));
        if (deferredAttachment) tab.Handler = null;

        var detail = (Leaf)root.Deepest.Current!.ViewModel;
        Success(await (popToRoot ? detail.Navigation.NavigateBackToRoot(Token) : detail.Navigation.NavigateBack(Token)));
        Assert.Same(tab, root.Deepest.Current!.View);
        if (deferredAttachment)
        {
            Assert.Empty(events);
            tab.Handler = new TestNavigationHandler();
        }
        Assert.Equal([ViewNavigationKind.Returned], events);
        Assert.Equal(0, selections);
        Assert.Same(tab.ViewModel, parent.ViewModel.LastActive);
        tab.Handler = new TestNavigationHandler();
        Success(await parent.ViewModel.Navigation.Select("one", cancellationToken: Token));
        Assert.Single(events); Assert.Equal(0, selections);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task View_lifecycle_active_removal_notifies_replacement_in_order(bool compatibility, bool eager)
    {
        Profile.Options = new() { Initialization = eager ? TabInitialization.Eager : TabInitialization.Lazy,
            Lifecycle = compatibility ? TabLifecycle.TabSwitchCompatibility : TabLifecycle.Standard };
        var root = await Open<ProfileTabs>(); var parent = (ProfileTabsView)root.Current!.View;
        AttachDesktop(root.Window, parent, (ViewBase)parent.CurrentTab!.View!);
        var events = new List<string>();
        var selectionsAtActivation = new List<ViewModelBase?>();
        parent.CurrentTabChanged += (_, _) => events.Add("selection");
        void Observe(ViewBase view)
        {
            view.NavigatedTo += (_, args) =>
            {
                events.Add("view:" + args.Kind);
                selectionsAtActivation.Add(parent.ViewModel.LastActive);
            };
            view.Handler = new TestNavigationHandler();
        }
        if (eager) Observe((ViewBase)parent.TabItems[1].View!);

        Success(await parent.ViewModel.Navigation.Remove("one", "two", Token));
        var replacement = (ProfileLeafView)parent.CurrentTab!.View!;
        if (!eager) Observe(replacement);
        Assert.Equal(eager ? ["view:TabSelected", "selection"] : new[] { "selection", "view:TabSelected" }, events);
        Assert.Same(replacement.ViewModel, Assert.Single(selectionsAtActivation));
        Assert.Same(replacement.ViewModel, parent.ViewModel.LastActive);
        Success(await parent.ViewModel.Navigation.Select("two", cancellationToken: Token));
        Assert.Equal(2, events.Count);
    }

    [Theory]
    [InlineData("back", false)]
    [InlineData("back", true)]
    [InlineData("root", false)]
    [InlineData("root", true)]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    [InlineData("reset", false)]
    [InlineData("reset", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    public async Task View_lifecycle_committed_changes_complete_notifications_even_when_cleanup_fails(string operation, bool failCleanup)
    {
        var root = await Open<ProfileTabs>(); var parent = (ProfileTabsView)root.Current!.View;
        AttachDesktop(root.Window, parent);
        var selections = 0; parent.CurrentTabChanged += (_, _) => selections++;
        var removed = (Leaf)root.Deepest.Current!.ViewModel;
        if (operation is "back" or "root")
        {
            Success(await removed.Navigation.NavigateTo<Detail>(cancellationToken: Token));
            if (operation == "root") Success(await ((Leaf)root.Deepest.Current!.ViewModel).Navigation.NavigateTo<Detail>(cancellationToken: Token));
            removed = (Leaf)root.Deepest.Current!.ViewModel;
        }
        var cleanupError = new InvalidOperationException("terminal cleanup");
        if (failCleanup) removed.Ownership.RegisterCleanup(() => Task.FromException(cleanupError));

        var result = operation switch
        {
            "back" => await removed.Navigation.NavigateBack(Token),
            "root" => await removed.Navigation.NavigateBackToRoot(Token),
            "remove" => await parent.ViewModel.Navigation.Remove("one", "two", Token),
            "reset" => await parent.ViewModel.Navigation.Reset("one", Token),
            _ => await parent.ViewModel.Replace([ProfileItem("two")], Token)
        };
        if (failCleanup)
        {
            Assert.Equal(NavigationStatus.Failed, result.Status); Assert.True(result.HasCommitted);
            Assert.Same(cleanupError, result.Error!.GetBaseException());
        }
        else Success(result);
        Assert.True(removed.IsDismissed); Assert.True(removed.Resource.Disposed);
        var current = (ViewBase)root.Deepest.Current!.View;
        Assert.Same(current.ViewModel, parent.ViewModel.LastActive);
        Assert.Equal(operation is "back" or "root" ? 0 : 1, selections);
        var events = new List<ViewNavigationKind>();
        current.NavigatedTo += (_, args) => events.Add(args.Kind);
        current.Handler = new TestNavigationHandler();
        Assert.Equal([operation is "back" or "root" ? ViewNavigationKind.Returned : ViewNavigationKind.TabSelected], events);
        current.Handler = new TestNavigationHandler();
        Assert.Single(events);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("selector")]
    [InlineData("qualified")]
    public async Task View_lifecycle_retained_detail_observes_updated_parent_selection(string route)
    {
        var qualifiedPath = route == "qualified";
        var root = qualifiedPath ? await Open<ProfileNestedTabs>() : await Open<ProfileTabs>();
        var parent = (ProfileTabsView)(qualifiedPath ? root.Current!.Children!.Current!.View : root.Current!.View);
        AttachDesktop(root.Window, root.ActiveChain().Select(context => context.Current!.View).ToArray());
        var first = (ProfileLeaf)root.Deepest.Current!.ViewModel;
        Success(await first.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var detail = root.Deepest.Current!.View;
        detail.Handler = new TestNavigationHandler();
        Success(await parent.ViewModel.Navigation.Select("two", cancellationToken: Token));
        var events = new List<string>();
        var selectionsAtActivation = new List<ViewModelBase?>();
        detail.NavigatedTo += (_, _) => { events.Add("view"); selectionsAtActivation.Add(parent.ViewModel.LastActive); };
        parent.CurrentTabChanged += (_, _) => events.Add("selection");

        if (route == "selector")
            // The built-in selector uses SelectAsync; the service uses SelectPathAsync.
            Success(NavigationResult.From(await root.Current!.Children!.SelectAsync("one", cancellationToken: Token)));
        else Success(await (qualifiedPath
                ? ((Leaf)root.Current!.ViewModel).Navigation.Select("workspace/one", cancellationToken: Token)
                : parent.ViewModel.Navigation.Select("one", cancellationToken: Token)));
        Assert.Same(detail, root.Deepest.Current!.View);
        Assert.Same(first, Assert.Single(selectionsAtActivation));
        Assert.Equal(["view", "selection"], events);
    }

    [Fact]
    public async Task View_lifecycle_failed_tab_switch_reactivates_view_without_changing_selection()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var parent = (ProfileTabsView)root.Current!.View;
        var first = (ProfileLeafView)parent.CurrentTab!.View!;
        AttachDesktop(root.Window, parent, first);
        var events = new List<ViewNavigationKind>();
        first.NavigatedFrom += (_, args) => events.Add(args.Kind);
        first.NavigatedTo += (_, args) => events.Add(args.Kind);
        var selections = 0; parent.CurrentTabChanged += (_, _) => selections++;
        first.ViewModel.FailSelection = true;

        var result = await parent.ViewModel.Navigation.Select("two", cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.Same(first, root.Deepest.Current!.View);
        Assert.Equal([ViewNavigationKind.TabDeselected, ViewNavigationKind.Returned], events);
        Assert.Equal(0, selections); Assert.Same(first.ViewModel, parent.ViewModel.LastActive);
        first.ViewModel.FailSelection = false;
        Success(await parent.ViewModel.Navigation.Select("two", cancellationToken: Token));
        Assert.Equal(1, selections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task View_lifecycle_flyout_return_or_replacement_delivers_activation(bool remove)
    {
        Profile.Options = new() { DetailNavigation = TabDetailNavigation.AboveTabs };
        var root = await Open<ProfileFlyout>(); var parent = (ProfileFlyout)root.Current!.ViewModel;
        var workspace = root.Current.Children!.Current!.View;
        AttachDesktop(root.Window, root.ActiveChain().Select(context => context.Current!.View).ToArray());
        var events = new List<ViewNavigationKind>();
        if (remove)
        {
            Success(await parent.Navigation.Remove("workspace", "other", Token));
            var replacement = root.Deepest.Current!.View;
            replacement.NavigatedTo += (_, args) => events.Add(args.Kind);
            replacement.Handler = new TestNavigationHandler();
            Assert.Equal([ViewNavigationKind.TabSelected], events);
        }
        else
        {
            workspace.NavigatedTo += (_, args) => events.Add(args.Kind);
            Success(await parent.Navigation.NavigateTo<Detail>(cancellationToken: Token));
            Assert.IsType<Detail>(root.Current.Children.Current!.ViewModel);
            Success(await ((Leaf)root.Deepest.Current!.ViewModel).Navigation.NavigateBack(Token));
            Assert.Same(workspace, root.Current.Children.Current!.View);
            Assert.Equal([ViewNavigationKind.Returned], events);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task View_lifecycle_modal_return_preserves_deferred_activation_even_when_cleanup_fails(bool compatibility, bool failCleanup)
    {
        Profile.Options = new() { Lifecycle = compatibility ? TabLifecycle.TabSwitchCompatibility : TabLifecycle.Standard };
        var root = await Open<ProfileTabs>(); var parent = (ProfileTabsView)root.Current!.View;
        var tab = (ProfileLeafView)parent.CurrentTab!.View!;
        var events = new List<ViewNavigationKind>();
        tab.NavigatedTo += (_, args) => events.Add(args.Kind);
        var selections = 0; parent.CurrentTabChanged += (_, _) => selections++;
        Success(await tab.ViewModel.Navigation.NavigateTo<Modal>(cancellationToken: Token));
        var modal = (Modal)root.Host.CurrentContentNavigation!.Current!.ViewModel;
        if (failCleanup) modal.Ownership.RegisterCleanup(() => Task.FromException(new InvalidOperationException("modal cleanup")));

        Success(await modal.Navigation.NavigateBack(Token));
        Assert.Empty(events); Assert.Empty(root.Window.Navigation.ModalStack);
        Assert.Same(tab, root.Deepest.Current!.View);
        AttachDesktop(root.Window, parent, tab);
        Assert.Equal([compatibility ? ViewNavigationKind.TabSelected : ViewNavigationKind.Returned], events);
        Assert.Equal(compatibility ? 1 : 2, tab.ViewModel.Appearances);
        Assert.Equal(0, selections);
        Assert.True(modal.IsDismissed); Assert.True(modal.Resource.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task View_lifecycle_rejected_or_cancelled_selection_does_not_notify(bool cancel)
    {
        var root = await Open<ProfileTabs>(); var parent = (ProfileTabsView)root.Current!.View;
        var tab = (ProfileLeafView)parent.CurrentTab!.View!;
        AttachDesktop(root.Window, parent, tab);
        var events = new List<string>();
        tab.NavigatedTo += (_, _) => events.Add("to");
        tab.NavigatedFrom += (_, _) => events.Add("from");
        parent.CurrentTabChanged += (_, _) => events.Add("selection");
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel(); else tab.ViewModel.Allowed = false;

        var result = await parent.ViewModel.Navigation.Select("two", cancellationToken: cancellation.Token);
        Assert.Equal(cancel ? NavigationStatus.Cancelled : NavigationStatus.GuardRejected, result.Status);
        Assert.False(result.HasCommitted); Assert.Empty(events);
        Assert.Same(tab, root.Deepest.Current!.View);
        Assert.Same(tab.ViewModel, parent.ViewModel.LastActive);
    }
}
