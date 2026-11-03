using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    private TabProfileState Profile => services.GetRequiredService<TabProfileState>();
    private void CompatibilityProfile() => Profile.Options = new()
    {
        Initialization = TabInitialization.Eager,
        DetailNavigation = TabDetailNavigation.AboveTabs,
        Lifecycle = TabLifecycle.TabSwitchCompatibility
    };

    [Theory]
    [InlineData(TabInitialization.Lazy, 1)]
    [InlineData(TabInitialization.Eager, 3)]
    public async Task Preparation_policy_constructs_owned_roots_but_only_appears_the_selected_tab(TabInitialization mode, int count)
    {
        Profile.Options = new() { Initialization = mode };
        var root = await Open<ProfileTabs>();
        var view = (ProfileTabsView)root.Current!.View;
        Assert.Equal(count, Profile.Created.Count);
        Assert.Equal(count, view.TabItems.Count(item => item.ViewModel != null));
        Assert.All(Profile.Created, item => Assert.Equal(1, item.Initializations));
        Assert.Equal(1, Profile.Created.Sum(item => item.Appearances));
        Assert.Equal("one", view.CurrentTab!.Id);
        Assert.All(view.TabItems, item => Assert.Equal(typeof(ProfileLeaf), item.ViewModelType));
        if (mode == TabInitialization.Eager)
        {
            Assert.Equal(new[] { "one", "two", "hidden" }, Profile.Created.Select(item => item.Filter));
            Assert.Equal(NavigationStatus.InvalidOrigin, (await Profile.Created[1].Navigation.NavigateTo<Detail>(cancellationToken: Token)).Status);
        }
    }

    [Fact]
    public async Task Details_cover_the_container_and_back_preserves_selected_instances_and_activation()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>();
        var container = root.Current!; var tabs = (ProfileTabs)container.ViewModel;
        var leaf = (ProfileLeaf)root.Deepest.Current!.ViewModel;
        Success(await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var detail = (Detail)root.Current!.ViewModel;
        Assert.IsType<DetailView>(root.View.Presenter.CurrentContent);
        Assert.Equal(1, tabs.Covers); Assert.Equal(0, leaf.Dismissals); Assert.Equal(0, leaf.Deactivations);
        Assert.False(leaf.IsDismissed); Assert.False(leaf.Resource.Disposed);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await tabs.Navigation.Select("two", cancellationToken: Token)).Status);
        Success(await detail.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var nested = (Detail)root.Current.ViewModel;
        Success(await nested.Navigation.NavigateBackToRoot(Token));
        Assert.Same(container, root.Current); Assert.Same(leaf, root.Deepest.Current!.ViewModel);
        Assert.Equal(1, leaf.Appearances); Assert.Equal(1, leaf.Initializations);
        Assert.True(detail.Resource.Disposed); Assert.True(nested.Resource.Disposed);
        Assert.Same(container.View, root.View.Presenter.CurrentContent);
    }

    [Fact]
    public async Task Actual_selection_uses_compatibility_callback_without_ending_ownership()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var tabs = (ProfileTabs)root.Current!.ViewModel;
        var first = Profile.Created[0]; var second = Profile.Created[1];
        first.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await tabs.Navigation.Select("two", cancellationToken: Token)).Status);
        Assert.Equal(0, first.Dismissals); Assert.Equal(0, second.Appearances);
        first.Allowed = true;
        Success(await tabs.Navigation.Select("two", cancellationToken: Token));
        Assert.Equal(1, first.Dismissals); Assert.Equal(0, first.Deactivations);
        Assert.False(first.IsDismissed); Assert.False(first.LifetimeEnded);
        Assert.False(first.Resource.Disposed); Assert.Equal(1, second.Appearances);
        Success(await second.Navigation.Select("one", cancellationToken: Token));
        Assert.Same(first, root.Deepest.Current!.ViewModel); Assert.Equal(2, first.Appearances);
        Success(await tabs.Navigation.Select("one", cancellationToken: Token));
        Assert.Equal(2, first.Appearances); Assert.Equal(1, first.Dismissals);
        Success(await factory.SetRoot<Leaf>(root.Window, cancellationToken: Token));
        Assert.All(Profile.Created, item => { Assert.True(item.IsDismissed); Assert.True(item.Resource.Disposed); Assert.Equal(1, item.TerminalDismissals); });
    }

    [Fact]
    public async Task Switching_a_nested_compatibility_container_still_notifies_the_outgoing_tab_root()
    {
        CompatibilityProfile();
        var root = await Open<ProfileNestedTabs>(); var parent = (ProfileNestedTabs)root.Current!.ViewModel;
        var nested = (ProfileTabs)root.Current.Children!.Current!.ViewModel;
        Success(await parent.Navigation.Select("other", cancellationToken: Token));
        Assert.Equal(1, nested.Dismissals); Assert.Equal(0, nested.Covers);
        Assert.False(nested.IsDismissed); Assert.False(nested.Resource.Disposed);
        Success(await parent.Navigation.Select("workspace", cancellationToken: Token));
        Assert.Same(nested, root.Current.Children.Current!.ViewModel);
    }

    [Fact]
    public async Task Cancelled_eager_reconciliation_releases_candidates_and_retains_previous_membership()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var tabs = (ProfileTabs)root.Current!.ViewModel;
        var selected = root.Deepest.Current;
        using var cancellation = new CancellationTokenSource();
        Profile.Preparing = model => { if (model.Filter == "new") cancellation.Cancel(); };
        var result = await tabs.Replace([ProfileItem("one"), ProfileItem("new"), ProfileItem("later")], cancellation.Token);
        Assert.Equal(NavigationStatus.Cancelled, result.Status); Assert.False(result.HasCommitted);
        Assert.Same(selected, root.Deepest.Current); Assert.Equal(4, Profile.Created.Count);
        Assert.True(Profile.Created[^1].Resource.Disposed);
        Assert.All(Profile.Created.Take(3), model => Assert.False(model.Resource.Disposed));
    }

    [Fact]
    public async Task Shared_stack_inside_a_flyout_covers_the_nearest_tabs_and_keeps_the_flyout()
    {
        CompatibilityProfile();
        var root = await Open<ProfileFlyout>(); var flyout = root.Current!;
        var outer = flyout.Children!; var container = outer.Current!;
        var leaf = (ProfileLeaf)root.Deepest.Current!.ViewModel;
        Success(await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        Assert.Same(flyout, root.Current); Assert.IsType<Detail>(outer.Current!.ViewModel);
        Success(await ((Detail)outer.Current.ViewModel).Navigation.NavigateBack(Token));
        Assert.Same(container, outer.Current); Assert.Same(leaf, root.Deepest.Current!.ViewModel);
        Assert.Equal(1, leaf.Appearances);
    }

    [Fact]
    public async Task Modal_covering_compatibility_tabs_preserves_activation_and_guards()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var leaf = Profile.Created[0];
        leaf.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await leaf.Navigation.NavigateTo<Modal>(cancellationToken: Token)).Status);
        leaf.Allowed = true;
        Success(await leaf.Navigation.NavigateTo<Modal>(cancellationToken: Token));
        Assert.False(root.IsActive); Assert.Equal(0, leaf.Dismissals);
        var modal = (Modal)root.Host.CurrentContentNavigation!.Current!.ViewModel;
        Success(await modal.Navigation.NavigateBack(Token));
        Assert.True(root.IsActive); Assert.Equal(1, leaf.Appearances);
        Assert.Same(leaf, root.Deepest.Current!.ViewModel);
    }

    [Fact]
    public async Task Throwing_compatibility_callback_restores_selected_branch_for_retry()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var tabs = (ProfileTabs)root.Current!.ViewModel;
        var first = Profile.Created[0]; first.FailSelection = true;
        var result = await tabs.Navigation.Select("two", cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.Same(first, root.Deepest.Current!.ViewModel); Assert.True(root.Deepest.IsActive);
        Assert.False(first.Resource.Disposed); Assert.False(first.IsDismissed);
        first.FailSelection = false;
        Success(await tabs.Navigation.Select("two", cancellationToken: Token));
        Assert.Same(Profile.Created[1], root.Deepest.Current!.ViewModel);
    }

    [Fact]
    public async Task Current_tab_event_tracks_selection_and_root_replacement_without_duplicate_notifications()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var view = (ProfileTabsView)root.Current!.View;
        var changes = 0;
        view.CurrentTabChanged += (_, _) =>
        {
            changes++;
            Assert.Same(view.CurrentTab!.ViewModel, view.ViewModel.LastActive);
            Assert.True(((ProfileLeaf)view.CurrentTab.ViewModel!).Appearances > 0);
        };
        Success(await view.ViewModel.Navigation.Select("one", cancellationToken: Token));
        Assert.Equal(0, changes);
        Success(await view.ViewModel.Navigation.Select("two", cancellationToken: Token));
        Assert.Equal(1, changes); Assert.Same(Profile.Created[1], view.CurrentTab!.ViewModel);
        Success(await view.ViewModel.Navigation.Reset("two", Token));
        Assert.Equal(2, changes); Assert.Same(Profile.Created[^1], view.CurrentTab!.ViewModel);
    }

    [Fact]
    public async Task Eager_reconciliation_prepares_new_inactive_destinations_and_preserves_old_roots()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var tabs = (ProfileTabs)root.Current!.ViewModel;
        var selected = root.Deepest.Current;
        Success(await tabs.Replace([ProfileItem("one"), ProfileItem("two"), ProfileItem("new")], Token));
        Assert.Equal(4, Profile.Created.Count); Assert.Equal(0, Profile.Created[^1].Appearances);
        Assert.Equal(1, Profile.Created[^1].Initializations); Assert.Same(selected, root.Deepest.Current);
        Assert.True(Profile.Created[2].Resource.Disposed); Assert.Equal(0, Profile.Created[0].Dismissals);
        Success(await tabs.Navigation.Reset("new", Token));
        Assert.Equal(5, Profile.Created.Count); Assert.Equal(0, Profile.Created[^1].Appearances);
        Assert.True(Profile.Created[3].Resource.Disposed); Assert.Same(selected, root.Deepest.Current);
    }

    [Fact]
    public async Task Failed_eager_reconciliation_releases_new_scopes_and_does_not_commit_membership()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var tabs = (ProfileTabs)root.Current!.ViewModel;
        var selected = root.Deepest.Current;
        Profile.FailId = "fail";
        var result = await tabs.Replace([ProfileItem("one"), ProfileItem("new"), ProfileItem("fail")], Token);
        Assert.Equal(NavigationStatus.Failed, result.Status); Assert.False(result.HasCommitted);
        Assert.Same(selected, root.Deepest.Current);
        Assert.Equal(new[] { "one", "two", "hidden" }, ((ProfileTabsView)root.Current.View).TabItems.Select(item => item.Id));
        Assert.All(Profile.Created.Skip(3), item => Assert.True(item.Resource.Disposed));
        Assert.All(Profile.Created.Take(3), item => Assert.False(item.Resource.Disposed));
    }

    [Fact]
    public async Task Failed_initial_eager_preparation_cleans_all_prepared_and_failing_roots()
    {
        CompatibilityProfile(); Profile.FailId = "two";
        application.Next = () => factory.CreateWindow<ProfileTabs>();
        var window = (Window)((Microsoft.Maui.IApplication)application).CreateWindow(null);
        hosts.Add(MauiNavigationHostFactory.GetOrCreateHost(window));
        var result = await factory.WaitForInitializationAsync(window, Token);
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.Equal(2, Profile.Created.Count);
        Assert.All(Profile.Created, item =>
        {
            Assert.True(item.Resource.Disposed);
            Assert.True(item.IsPermanentlyDismissed);
            Assert.False(item.IsHostReplaced);
        });
    }

    [Fact]
    public async Task Activity_from_an_inactive_eager_tab_does_not_reconcile_layout_or_request_a_loader()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var view = (ProfileTabsView)root.Current!.View;
        var child = root.Current.Children!; var before = child.View.TabSelector.Content;
        var published = 0; child.PropertyChanged += (_, _) => published++;
        Profile.Created[1].Working = true;
        Assert.True(view.TabItems[1].IsBusy); Assert.False(Profile.Created[1].IsBusy);
        Assert.Same(before, child.View.TabSelector.Content); Assert.Equal(0, published);
        Assert.False(root.View.IsBusyPresented);
        Profile.Created[1].Working = false; Assert.False(view.TabItems[1].IsBusy);
    }

    [Fact]
    public async Task Desktop_chrome_preserves_body_and_full_width_shared_panel_through_mode_changes()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var view = (ProfileTabsView)root.Current!.View;
        var child = root.Current.Children!; var screen = child.Current;
        var rail = new Label { Text = "Rail" }; var picker = new Label { Text = "Picker" };
        var shared = new Entry { Text = "Shared state" }; view.SharedContent = shared;
        view.LeadingContent = rail; view.LeadingContentWidth = new GridLength(48);
        view.IsTabBarVisible = false;
        var leadingHost = Assert.IsType<ContentView>(rail.Parent);
        var bodyRow = Assert.IsType<Grid>(leadingHost.Parent);
        Assert.Equal(new GridLength(48), bodyRow.ColumnDefinitions[0].Width);
        Assert.False(child.View.TabSelector.IsVisible);
        Assert.DoesNotContain(shared, Descendants(bodyRow));
        view.BodyRowOverlayContent = picker; view.IsLeadingContentVisible = false;
        Assert.Equal(new GridLength(0), bodyRow.ColumnDefinitions[0].Width);
        Assert.Equal(2, Grid.GetColumnSpan((BindableObject)picker.Parent!));
        view.BodyRowOverlayContent = null; view.IsLeadingContentVisible = true;
        Assert.Null(picker.Parent); Assert.Equal(new GridLength(48), bodyRow.ColumnDefinitions[0].Width);
        view.IsTabBarVisible = true; view.LeadingContent = null;
        Assert.Same(screen, child.Current); Assert.Same(shared, view.SharedContent);
        Assert.Same(view.ViewModel, shared.BindingContext);
        Assert.Equal(new GridLength(0), bodyRow.ColumnDefinitions[0].Width);
    }

    [Fact]
    public async Task Custom_tab_row_keeps_trailing_controls_outside_selection_input_and_releases_content()
    {
        var root = await Open<ProfileTabs>(); var view = (ProfileTabsView)root.Current!.View;
        var child = root.Current.Children!;
        var trailing = new Button { Text = "Close" };
        view.TabBarTrailingContent = trailing; view.IsTabBarEnabled = false;
        Assert.True(trailing.IsEnabled);
        Assert.All(Descendants(child.View.TabSelector).OfType<Button>().Where(button => button != trailing), button => Assert.False(button.IsEnabled));
        var row = new Grid(); view.TabBarContent = row; view.TabBarHeight = 32;
        Assert.False(child.View.TabSelector.IsVisible);
        Assert.Equal(32, Assert.IsType<ContentView>(row.Parent).HeightRequest);
        view.TabBarContent = null; Assert.Null(row.Parent);
        Assert.True(child.View.TabSelector.IsVisible);
    }

    [Fact]
    public async Task Body_frame_templates_preserve_roots_and_keep_header_and_overlays_outside_the_frame()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var view = (ProfileTabsView)root.Current!.View;
        var child = root.Current.Children!; var retained = child.Current;
        var shared = new Entry { Text = "Keep this input" };
        var header = new Label { Text = "Window header" };
        var overlay = new Label { Text = "Whole container overlay" };
        var rail = new Label { Text = "Rail" };
        var picker = new Label { Text = "Body row picker" };
        view.HeaderContent = header; view.OverlayContent = overlay;
        view.SharedContent = shared; view.LeadingContent = rail;
        view.BodyRowOverlayContent = picker;
        var body = child.View.TabSelector.Parent;
        var creations = 0;
        Border? frame = null;
        var template = new ControlTemplate(() =>
        {
            creations++;
            return frame = new Border
            {
                StrokeThickness = 0, Margin = new Thickness(8, 0, 8, 8),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                Content = new ContentPresenter()
            };
        });
        view.BodyTemplate = template;
        view.DetailMargin = new Thickness(0, 0, 8, 8);
        Assert.Equal(1, creations);
        Assert.NotNull(frame);
        Assert.Same(view.ViewModel, frame.BindingContext);
        Assert.Contains(shared, Descendants(frame));
        Assert.Contains(rail, Descendants(frame));
        Assert.Contains(picker, Descendants(frame));
        Assert.DoesNotContain(header, Descendants(frame));
        Assert.DoesNotContain(overlay, Descendants(frame));
        Assert.Equal(view.DetailMargin, child.View.Presenter.Margin);

        // Applying other slots or moving between tabs must not recreate the frame or roots.
        view.LeadingContentWidth = new GridLength(50);
        Success(await view.ViewModel.Navigation.Select("two", cancellationToken: Token));
        Success(await view.ViewModel.Navigation.Select("one", cancellationToken: Token));
        Assert.Equal(1, creations);
        Assert.Same(retained, child.Current);
        Assert.Same(body, child.View.TabSelector.Parent);
        view.BodyTemplate = null; view.DetailMargin = Thickness.Zero;
        Assert.Contains(shared, Descendants(child.View));
        Assert.Contains(header, Descendants(child.View));
        Assert.Same(retained!.View, child.View.Presenter.CurrentContent);
        Assert.Equal("Keep this input", shared.Text);
        Assert.Equal(Thickness.Zero, child.View.Presenter.Margin);
        Assert.False(root.View.IsBusyPresented);
        view.BodyTemplate = template;
        Assert.Equal(2, creations);
        Assert.Same(retained, child.Current);
        Success(await factory.SetRoot<Leaf>(root.Window, cancellationToken: Token));
        Assert.Null(shared.Parent);
        Assert.Null(rail.Parent);
        Assert.Null(picker.Parent);
        Assert.True(retained.ViewModel.IsDismissed);
    }

    [Fact]
    public async Task Tab_bar_input_availability_does_not_replace_the_models_navigation_guard()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var view = (ProfileTabsView)root.Current!.View;
        view.IsTabBarEnabled = false;
        Success(await view.ViewModel.Navigation.Select("two", cancellationToken: Token));
        Profile.Created[1].Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected,
            (await view.ViewModel.Navigation.Select("one", cancellationToken: Token)).Status);
        Assert.Equal("two", view.CurrentTab!.Id);
    }

    private static NavigationItem ProfileItem(string id) => new(typeof(ProfileLeaf), id, id, new() { ["filter"] = id });
    public sealed class TabProfileState
    {
        public TabNavigationOptions Options { get; set; } = new();
        public List<ProfileLeaf> Created { get; } = [];
        public string? FailId;
        public Action<ProfileLeaf>? Preparing;
        public Action<ProfileTabsView>? ViewCreated;
    }
    public sealed class ProfileTabs : Leaf
    {
        public ProfileTabs(INavigationService navigation, Resource resource, TabProfileState state) : base(navigation, resource) => TabOptions = state.Options;
        public int Covers;
        public ViewModelBase? LastActive;
        public override Task OnActiveTabChanged(ViewModelBase activeTab) { LastActive = activeTab; return Task.CompletedTask; }
        public override Task Disappearing() { Covers++; return Task.CompletedTask; }
        public override async Task BeforeFirstShown() => Success(await SetTabs([
            ProfileItem("one"), ProfileItem("two"), new(typeof(ProfileLeaf), "hidden", "hidden", new() { ["filter"] = "hidden" }) { IsVisible = false, IsEnabled = false }]));
        public Task<NavigationResult> Replace(IEnumerable<NavigationItem> items, CancellationToken token) => SetTabs(items, token);
    }
    public sealed class ProfileLeaf : Leaf, IActivityIndicatorProvider
    {
        private readonly TabProfileState state;
        public ProfileLeaf(INavigationService navigation, Resource resource, TabProfileState state) : base(navigation, resource)
        { this.state = state; state.Created.Add(this); }
        private bool working;
        public bool Working { get => working; set => SetProperty(ref working, value); }
        public bool IsTabBusy => Working;
        public bool LifetimeEnded => LifetimeToken.IsCancellationRequested;
        public bool FailSelection;
        public int Initializations, Appearances, Deactivations, TerminalDismissals;
        public override Task BeforeFirstShown()
        { Initializations++; state.Preparing?.Invoke(this); return Filter == state.FailId ? Task.FromException(new InvalidOperationException("preparation")) : Task.CompletedTask; }
        public override Task Appearing() { Appearances++; return Task.CompletedTask; }
        public override Task Deactivated() { Deactivations++; return Task.CompletedTask; }
        public override Task AfterDismissed()
        {
            if (IsDismissed) TerminalDismissals++;
            else if (FailSelection) throw new InvalidOperationException("selection cleanup");
            return base.AfterDismissed();
        }
    }
    public sealed class ProfileTabsView : TabbedViewBase<ProfileTabs>, IModalPresentationObserver
    {
        public ProfileTabsView(ProfileTabs model, TabProfileState state) : base(model) => state.ViewCreated?.Invoke(this);
        public List<ModalPresentation> ModalStarts { get; } = [];
        public List<ModalPresentation> ModalEnds { get; } = [];
        public bool RejectModal;
        public Action? ModalDismissed;
        public void OnModalPresenting(ModalPresentation presentation)
        { ModalStarts.Add(presentation); if (RejectModal) throw new InvalidOperationException("observer rejected"); }
        public void OnModalDismissed(ModalPresentation presentation) { ModalEnds.Add(presentation); ModalDismissed?.Invoke(); }
    }
    public sealed class ProfileLeafView(ProfileLeaf model) : ViewBase<ProfileLeaf>(model), IModalPresentationObserver
    {
        public List<ModalPresentation> ModalStarts { get; } = [];
        public List<ModalPresentation> ModalEnds { get; } = [];
        public void OnModalPresenting(ModalPresentation presentation) => ModalStarts.Add(presentation);
        public void OnModalDismissed(ModalPresentation presentation) => ModalEnds.Add(presentation);
    }
    public sealed class ProfileFlyout(INavigationService navigation, Resource resource) : Leaf(navigation, resource)
    {
        public override async Task BeforeFirstShown() => Success(await SetFlyoutItems([
            new(typeof(ProfileTabs), "Workspace", "workspace"), new(typeof(Leaf), "Other", "other")]));
    }
    public sealed class ProfileFlyoutView(ProfileFlyout model) : FlyoutViewBase<ProfileFlyout>(model);
    public sealed class ProfileNestedTabs : Leaf
    {
        public ProfileNestedTabs(INavigationService navigation, Resource resource, TabProfileState state) : base(navigation, resource) => TabOptions = state.Options;
        public override async Task BeforeFirstShown() => Success(await SetTabs([
            new(typeof(ProfileTabs), "Workspace", "workspace"), new(typeof(Leaf), "Other", "other")]));
    }
    public sealed class ProfileNestedTabsView(ProfileNestedTabs model) : TabbedViewBase<ProfileNestedTabs>(model);
}
