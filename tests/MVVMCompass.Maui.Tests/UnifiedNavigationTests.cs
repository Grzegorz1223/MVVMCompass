using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed partial class UnifiedNavigationTests : IAsyncDisposable
{
    private readonly Application? previous = Application.Current;
    private readonly App application = new();
    private readonly ServiceProvider services;
    private readonly MauiNavigationHostFactory factory;
    private readonly List<MauiNavigationHost> hosts = [];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public UnifiedNavigationTests()
    {
        var builder = MauiApp.CreateBuilder();
        builder.Services.AddScoped<Resource>();
        builder.UseMVVMCompass(pairs =>
        {
            pairs.Add<Tabs, TabsView>(); pairs.Add<Flyout, FlyoutView>(); pairs.Add<Leaf, LeafView>();
            pairs.Add<PopupModel, PopupView>(); pairs.Add<Detail, DetailView>(); pairs.Add<Modal, ModalView>(); pairs.Add<Failing, FailingView>();
        });
        services = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        foreach (var initializer in services.GetServices<IMauiInitializeService>()) initializer.Initialize(services);
        factory = services.GetRequiredService<MauiNavigationHostFactory>();
    }

    public async ValueTask DisposeAsync()
    {
        try { foreach (var host in hosts) await host.DisposeAsync(); await services.DisposeAsync(); }
        finally { Modal.Last = null; Application.Current = previous; }
    }

    [Fact]
    public async Task Initial_nested_selector_buttons_are_enabled_after_root_activation()
    {
        var context = await Open<Tabs>();
        var button = Descendants(context.Current!.Children!.View.TabSelector).OfType<Button>()
            .Single(item => item.AutomationId == "destination-archive");
        Assert.True(button.Command!.CanExecute(null));
        Assert.True(button.IsEnabled);
    }

    [Fact]
    public async Task Nested_containers_leave_toolbar_content_owned_by_the_root_toolbar()
    {
        var context = await Open<Flyout>();
        var parent = (Flyout)context.Current!.ViewModel;
        var child = context.Current.Children!;
        var tabs = (TabsView)child.Current!.View;
        var center = Assert.IsType<Entry>(tabs.ToolbarCenterContent);

        AssertSingleOwner(center);
        tabs.ToolbarCenterContent = center = new Entry { Text = "Updated nested toolbar" };
        AssertSingleOwner(center);
        Success(await parent.Navigation.Select("other", cancellationToken: Token));
        Assert.DoesNotContain(center, Descendants(context.View.Toolbar));
        Success(await parent.Navigation.Select("workspace", cancellationToken: Token));
        AssertSingleOwner(center);

        void AssertSingleOwner(Entry content)
        {
            Assert.Same(content, Assert.Single(Descendants(context.View.Toolbar).OfType<Entry>()));
            Assert.Single(Descendants(context.View), element => ReferenceEquals(element, content));
            foreach (var nested in context.ActiveChain().Skip(1))
            {
                Assert.False(nested.View.Toolbar.IsVisible);
                Assert.DoesNotContain(content, Descendants(nested.View.Toolbar));
            }
        }
    }

    [Fact]
    public async Task Registered_destinations_receive_separate_scopes_and_parameters_for_repeated_types()
    {
        var context = await Open<Tabs>(); var tabs = (Tabs)context.Current!.ViewModel;
        var first = (Leaf)context.Deepest.Current!.ViewModel;
        Assert.Equal("open", first.Filter); Assert.Same(tabs, first.ParentViewModel);
        Success(await tabs.Navigation.Select("archive", cancellationToken: Token));
        var second = (Leaf)context.Deepest.Current!.ViewModel;
        Assert.Equal("archive", second.Filter); Assert.NotSame(first, second); Assert.NotSame(first.Resource, second.Resource);
        Success(await tabs.Navigation.Select("open", cancellationToken: Token));
        Assert.Same(first, context.Deepest.Current!.ViewModel);
        Assert.False(first.Resource.Disposed); Assert.False(second.Resource.Disposed);
    }

    [Fact]
    public async Task Service_pushes_fresh_details_and_retains_the_container_toolbar_and_shared_content()
    {
        var context = await Open<Tabs>(); var tabs = (Tabs)context.Current!.ViewModel;
        var view = (TabsView)context.Current.View; var toolbar = context.View.Toolbar; var shared = view.SharedContent;
        var child = context.Current.Children!; var root = child.Current!;
        Success(await tabs.Navigation.NavigateTo<Detail>(new() { ["filter"] = "detail" }, Token));
        Assert.IsType<Detail>(context.Deepest.Current!.ViewModel);
        Assert.Same(toolbar, context.View.Toolbar); Assert.Same(shared, view.SharedContent);
        Assert.Equal(NavigationLeadingAction.Back, context.LeadingAction);
        var detail = (Detail)context.Deepest.Current.ViewModel;
        Success(await tabs.Navigation.NavigateBack(Token));
        Assert.Same(root, child.Current); Assert.True(detail.Resource.Disposed); Assert.Equal(1, detail.Dismissals);
    }

    [Fact]
    public async Task Hidden_origins_are_rejected_and_parent_commands_follow_the_active_branch()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        var old = (Leaf)context.Deepest.Current!.ViewModel;
        Success(await parent.Navigation.Select("archive", cancellationToken: Token));
        Assert.Equal(NavigationStatus.InvalidOrigin, (await old.Navigation.NavigateTo<Detail>(cancellationToken: Token)).Status);
        Success(await parent.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        Assert.IsType<Detail>(context.Deepest.Current!.ViewModel);
        Success(await parent.Navigation.Select("open", cancellationToken: Token));
        Assert.Same(old, context.Deepest.Current!.ViewModel);
        Success(await parent.Navigation.Select("archive", cancellationToken: Token));
        Assert.IsType<Detail>(context.Deepest.Current!.ViewModel);
    }

    [Fact]
    public async Task Selection_is_unambiguous_by_ID_and_never_falls_back_to_a_push()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        var current = context.Deepest.Current;
        Assert.Equal(NavigationStatus.AmbiguousDestination, (await parent.Navigation.Select<Leaf>(cancellationToken: Token)).Status);
        Assert.Equal(NavigationStatus.DestinationNotFound, (await parent.Navigation.Select("missing", cancellationToken: Token)).Status);
        Assert.Same(current, context.Deepest.Current);
        Success(await parent.Replace([Item("open"), Disabled("disabled")]));
        Assert.Equal(NavigationStatus.DestinationUnavailable, (await parent.Navigation.Select("disabled", cancellationToken: Token)).Status);
        Assert.Same(current, context.Deepest.Current);
    }

    [Fact]
    public async Task Guard_rejection_and_failure_do_not_change_committed_selection()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel; var leaf = (Leaf)context.Deepest.Current!.ViewModel;
        leaf.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await parent.Navigation.Select("archive", cancellationToken: Token)).Status);
        Assert.Equal("open", context.Current.Children!.SelectedDestinationId);
        leaf.Guard = () => throw new InvalidOperationException("guard");
        Assert.Equal(NavigationStatus.Failed, (await parent.Navigation.Select("archive", cancellationToken: Token)).Status);
        Assert.Same(leaf, context.Deepest.Current!.ViewModel);
        leaf.Guard = null; leaf.Allowed = true;
        Success(await parent.Navigation.Select("archive", cancellationToken: Token));
    }

    [Fact]
    public async Task Collection_reconciliation_preserves_histories_and_guards_removal_before_mutating()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        Success(await parent.Navigation.NavigateTo<Detail>(cancellationToken: Token)); var detail = (Detail)context.Deepest.Current!.ViewModel;
        detail.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await parent.Replace([Item("archive")])).Status);
        Assert.Equal(2, context.Current.Children!.Destinations.Count); Assert.Same(detail, context.Deepest.Current.ViewModel);
        detail.Allowed = true;
        Success(await parent.Replace([Item("archive"), Item("open"), Item("third")]));
        Assert.Same(detail, context.Deepest.Current.ViewModel);
        Assert.Equal(new[] { "archive", "open", "third" }, context.Current.Children.TabItems.Select(item => item.Id));
        Success(await parent.Replace([Item("archive")]));
        Assert.True(detail.IsDismissed); Assert.True(detail.Resource.Disposed); Assert.Equal("archive", context.Current.Children.SelectedDestinationId);
    }

    [Fact]
    public async Task Duplicate_IDs_are_rejected_before_model_or_visual_state_changes()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel; var before = context.Deepest.Current;
        await Assert.ThrowsAsync<ArgumentException>(() => parent.Replace([Item("same"), Item("same")]));
        Assert.Same(before, context.Deepest.Current); Assert.Equal(2, parent.CompositionItems.Count);
    }

    [Fact]
    public async Task Nested_flyout_selection_uses_nearest_container_and_restores_tab_histories()
    {
        var context = await Open<Flyout>(); var parent = (Flyout)context.Current!.ViewModel;
        var first = context.Deepest.Current;
        Assert.Equal(NavigationLeadingAction.Menu, context.LeadingAction);
        Success(await parent.Navigation.Select("workspace/archive", cancellationToken: Token));
        var leaf = (Leaf)context.Deepest.Current!.ViewModel;
        Assert.Equal("archive", leaf.Filter);
        Success(await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token)); var detail = context.Deepest.Current;
        Success(await parent.Navigation.Select("other", cancellationToken: Token));
        Success(await parent.Navigation.Select("workspace", cancellationToken: Token));
        Assert.Same(detail, context.Deepest.Current);
        Success(await ((Detail)detail!.ViewModel).Navigation.Select("open", cancellationToken: Token));
        Assert.Same(first, context.Deepest.Current);
    }

    [Fact]
    public async Task Invalid_nested_path_does_not_commit_a_partial_selection()
    {
        var context = await Open<Flyout>(); var parent = (Flyout)context.Current!.ViewModel;
        Success(await parent.Navigation.Select("other", cancellationToken: Token)); var before = context.Deepest.Current;
        Assert.Equal(NavigationStatus.DestinationNotFound, (await parent.Navigation.Select("workspace/missing", cancellationToken: Token)).Status);
        Assert.Same(before, context.Deepest.Current); Assert.Equal("other", context.Current.Children!.SelectedDestinationId);
    }

    [Fact]
    public async Task Modal_scope_covers_parent_services_and_restores_them_after_guarded_close()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        Success(await parent.Navigation.NavigateTo<Modal>(cancellationToken: Token));
        var modalContext = context.Host.CurrentContentNavigation!; var modal = (Modal)modalContext.Current!.ViewModel;
        Assert.True(modalContext.IsModal); Assert.Equal(NavigationLeadingAction.Close, modalContext.LeadingAction);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await parent.Navigation.NavigateTo<Detail>(cancellationToken: Token)).Status);
        modal.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await modal.Navigation.NavigateBack(Token)).Status);
        modal.Allowed = true; Success(await modal.Navigation.NavigateBack(Token));
        Assert.True(modal.Resource.Disposed); Assert.True(context.IsActive);
        Success(await parent.Navigation.Select("archive", cancellationToken: Token));
    }

    [Fact]
    public async Task Window_services_and_identical_destination_IDs_remain_independent()
    {
        var first = await Open<Tabs>(); var second = await Open<Tabs>();
        var a = (Tabs)first.Current!.ViewModel; var b = (Tabs)second.Current!.ViewModel;
        Assert.NotSame(a.Navigation, b.Navigation);
        Success(await a.Navigation.Select("archive", cancellationToken: Token));
        Assert.Equal("archive", first.Current.Children!.SelectedDestinationId); Assert.Equal("open", second.Current.Children!.SelectedDestinationId);
    }

    [Fact]
    public async Task Root_replacement_checks_guards_and_disposes_all_retained_scopes()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel; var first = (Leaf)context.Deepest.Current!.ViewModel;
        Success(await parent.Navigation.Select("archive", cancellationToken: Token)); var second = (Leaf)context.Deepest.Current!.ViewModel;
        first.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await parent.Navigation.SetRoot<Leaf>(cancellationToken: Token)).Status);
        first.Allowed = true;
        Success(await parent.Navigation.SetRoot<Leaf>(cancellationToken: Token));
        Assert.True(first.Resource.Disposed); Assert.True(second.Resource.Disposed); Assert.True(parent.Resource.Disposed);
    }

    [Fact]
    public async Task Cancelled_and_reentrant_requests_leave_the_committed_branch_usable()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel; var leaf = (Leaf)context.Deepest.Current!.ViewModel;
        leaf.Guard = async () => { Assert.Equal(NavigationStatus.Reentrant, (await parent.Navigation.NavigateBack(Token)).Status); return false; };
        Assert.Equal(NavigationStatus.GuardRejected, (await parent.Navigation.Select("archive", cancellationToken: Token)).Status);
        leaf.Guard = null;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal(NavigationStatus.Cancelled, (await parent.Navigation.Select("archive", cancellationToken: cancellation.Token)).Status);
        Success(await parent.Navigation.Select("archive", cancellationToken: Token));
    }

    [Fact]
    public async Task Preparation_failure_releases_the_candidate_scope_and_keeps_the_visible_body()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel; var before = context.Deepest.Current;
        Failing.Last = null;
        Assert.Equal(NavigationStatus.Failed, (await parent.Navigation.NavigateTo<Failing>(cancellationToken: Token)).Status);
        Assert.NotNull(Failing.Last); Assert.True(Failing.Last!.Resource.Disposed); Assert.Equal(1, Failing.Last.Dismissals);
        Assert.Same(before, context.Deepest.Current);
    }

    [Theory]
    [InlineData(true, "accepted")]
    [InlineData(true, null)]
    [InlineData(false, null)]
    public async Task Scoped_popup_service_guards_close_and_distinguishes_a_returned_null_from_dismissal(bool returnValue, string? value)
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel;
        PopupView.OpenedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var showing = parent.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token);
        await Task.WhenAny(showing, PopupView.OpenedSignal.Task).WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (showing.IsFaulted) await showing;
        var popup = await PopupView.OpenedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var model = popup.ViewModel;
        Assert.Equal(NavigationStatus.InvalidOrigin, (await parent.Navigation.NavigateBack(Token)).Status);
        model.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await model.Navigation.ClosePopup(Token)).Status);
        Assert.False(showing.IsCompleted); Assert.False(model.Resource.Disposed);
        model.Allowed = true;
        Success(returnValue ? await model.Navigation.ClosePopup(value, Token) : await model.Navigation.ClosePopup(Token));
        var result = await showing.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(returnValue, result.HasResult); Assert.Equal(value, result.Result);
        Assert.True(model.Resource.Disposed); Assert.Equal(1, model.Dismissals);
        Success(await parent.Navigation.Select("archive", cancellationToken: Token));
    }

    [Fact]
    public async Task Flyout_repeated_models_have_separate_ids_parameters_and_guarded_reconciliation()
    {
        var context = await Open<Flyout>(); var parent = (Flyout)context.Current!.ViewModel;
        Success(await parent.Replace([Item("personal"), Item("shared")]));
        var first = (Leaf)context.Deepest.Current!.ViewModel;
        Success(await parent.Navigation.Select("shared", cancellationToken: Token));
        var second = (Leaf)context.Deepest.Current!.ViewModel;
        Assert.NotSame(first.Resource, second.Resource); Assert.Equal("personal", first.Filter); Assert.Equal("shared", second.Filter);
        first.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await parent.Replace([Item("shared")])).Status);
        Assert.False(first.IsDismissed); Assert.Equal(2, context.Current.Children!.MenuItems.Count);
        first.Allowed = true; Success(await parent.Replace([Item("shared")]));
        Assert.True(first.Resource.Disposed); Assert.Same(second, context.Deepest.Current.ViewModel);
    }

    [Fact]
    public async Task Item_templates_receive_committed_selection_busy_state_and_automatic_input()
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel; var leaf = (Leaf)context.Deepest.Current!.ViewModel;
        var container = context.Current.Children!;
        var view = (TabsView)context.Current.View;
        view.SelectedTabItemTemplate = new DataTemplate(() => new Label { Text = "selected" });
        view.UnselectedTabItemTemplate = new DataTemplate(() => new Label { Text = "unselected" });
        leaf.IsBusy = true; Assert.True(container.TabItems[0].IsBusy);
        var input = Descendants(container.View.TabSelector).OfType<Button>().Single(button => button.AutomationId == "destination-archive");
        leaf.Allowed = false; input.Command!.Execute(null);
        Assert.Equal("open", container.SelectedDestinationId); Assert.True(container.TabItems[0].IsSelected);
        leaf.Allowed = true; input.Command.Execute(null);
        Assert.Equal("archive", container.SelectedDestinationId); Assert.True(container.TabItems[1].IsSelected);
        Success(await parent.Navigation.Select("open", cancellationToken: Token));
        Assert.Same(leaf, context.Deepest.Current.ViewModel);
    }

    private static IEnumerable<Element> Descendants(Element element)
    {
        yield return element;
        foreach (var child in ((IElementController)element).LogicalChildren)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public async Task Tab_layout_changes_retain_the_selected_entry_shared_binding_and_strip_background(TabBarPosition position, TabItemSizing sizing, SharedContentPosition sharedPosition)
    {
        var context = await Open<Tabs>(); var parent = (Tabs)context.Current!.ViewModel; var view = (TabsView)context.Current.View;
        Success(await parent.Navigation.Select("archive", cancellationToken: Token)); var selected = context.Deepest.Current;
        var shared = view.SharedContent; var brush = new LinearGradientBrush();
        view.TabBarPosition = position; view.TabItemSizing = sizing; view.SharedContentPosition = sharedPosition; view.TabBarBackground = brush;
        var layout = context.Current.Children!.View;
        var selector = position is TabBarPosition.Left or TabBarPosition.Right ? layout.RailSelector : layout.TabSelector;
        Assert.True(selector.IsVisible); Assert.Equal(sizing, selector.ItemSizing); Assert.Same(brush, selector.Background);
        Assert.Same(selected, context.Deepest.Current); Assert.Same(shared, view.SharedContent); Assert.Same(parent, shared!.BindingContext);
        Assert.Equal(position == TabBarPosition.Bottom ? 2 : 0, Grid.GetRow(layout.TabSelector));
        Assert.Equal(position == TabBarPosition.Right ? 2 : 0, Grid.GetColumn(layout.RailSelector));
    }

    public static IEnumerable<object[]> LayoutCases => from position in Enum.GetValues<TabBarPosition>()
        from sizing in Enum.GetValues<TabItemSizing>() from shared in Enum.GetValues<SharedContentPosition>() select new object[] { position, sizing, shared };

    private async Task<NavigationContext> Open<T>() where T : ViewModelBase
    {
        application.Next = () => factory.CreateWindow<T>();
        var window = ((IApplication)application).CreateWindow(null) as Window ?? throw new InvalidOperationException();
        var host = factory.ForWindow(window); hosts.Add(host);
        Success(await factory.WaitForInitializationAsync(window, Token).WaitAsync(TimeSpan.FromSeconds(10), Token));
        var context = host.CurrentContentNavigation!;
        foreach (var item in context.ActiveChain()) item.View.Presenter.AnimateNavigation = false;
        return context;
    }
    private static NavigationItem Item(string id) => new(typeof(Leaf), id, id, new() { ["filter"] = id });
    private static NavigationItem Disabled(string id) => new(typeof(Leaf), id, id) { IsEnabled = false };
    private static void Success(NavigationResult result) => Assert.True(result.IsSuccess, $"{result.Status}: {result.Error}");
    private sealed class App : Application
    {
        internal Func<Window> Next = null!;
        protected override Window CreateWindow(IActivationState? activationState) => Next();
    }
    public sealed class Resource : IAsyncDisposable
    {
        public bool Disposed;
        public ValueTask DisposeAsync() { Assert.False(Disposed); Disposed = true; return ValueTask.CompletedTask; }
    }
    public class Leaf(INavigationService navigation, Resource resource) : ViewModelBase
    {
        public INavigationService Navigation { get; } = navigation;
        public Resource Resource { get; } = resource;
        public string Filter { get; private set; } = "";
        public bool Allowed = true;
        public Func<Task<bool>>? Guard;
        public Func<Task>? Deactivate;
        public int Dismissals;
        public override Task Deactivated() => Deactivate?.Invoke() ?? Task.CompletedTask;
        public override Task<bool> CanNavigate() => Guard?.Invoke() ?? Task.FromResult(Allowed);
        public override Task GetParameters(Dictionary<string, object> parameters) { Filter = (string)parameters.GetValueOrDefault("filter", ""); return Task.CompletedTask; }
        public override Task AfterDismissed() { Dismissals++; return Task.CompletedTask; }
    }
    public sealed class Tabs(INavigationService navigation, Resource resource) : Leaf(navigation, resource)
    {
        public List<ViewModelBase> Selections { get; } = [];
        public override Task OnActiveTabChanged(ViewModelBase selected) { Selections.Add(selected); return Task.CompletedTask; }
        public override async Task BeforeFirstShown() => Success(await SetTabs([Item("open"), Item("archive")]));
        public Task<NavigationResult> Replace(IEnumerable<NavigationItem> items) => SetTabs(items, Token);
    }
    public sealed class Flyout(INavigationService navigation, Resource resource) : Leaf(navigation, resource)
    {
        public override async Task BeforeFirstShown() => Success(await SetFlyoutItems([new(typeof(Tabs), "Workspace", "workspace"), Item("other")]));
        public Task<NavigationResult> Replace(IEnumerable<NavigationItem> items) => SetFlyoutItems(items, Token);
    }
    public sealed class PopupModel(INavigationService navigation, Resource resource) : Leaf(navigation, resource);
    public sealed class PopupView : PopupViewBase<PopupModel, string?>
    {
        internal static TaskCompletionSource<PopupView> OpenedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PopupView(PopupModel model) : base(model) { Opened += (_, _) => OpenedSignal.TrySetResult(this); }
    }
    public sealed class Detail(INavigationService navigation, Resource resource) : Leaf(navigation, resource);
    public sealed class Modal : Leaf
    {
        public static Modal? Last;
        public Modal(INavigationService navigation, Resource resource) : base(navigation, resource) { IsModal = true; Last = this; }
    }
    public sealed class Failing(INavigationService navigation, Resource resource) : Leaf(navigation, resource)
    {
        public static Failing? Last;
        public override Task BeforeFirstShown() { Last = this; throw new InvalidOperationException("preparation"); }
    }
    public sealed class TabsView : TabbedViewBase<Tabs>
    {
        public TabsView(Tabs model) : base(model)
        {
            SharedContent = new Label { Text = "Persistent shared panel" };
            ToolbarCenterContent = new Entry { Text = "Persistent toolbar" };
            TabBarCenterContent = new Label { Text = "+" }; TabBarTrailingContent = new Label { Text = "More" };
        }
    }
    public sealed class FlyoutView(Flyout model) : FlyoutViewBase<Flyout>(model);
    public sealed class LeafView(Leaf model) : ViewBase<Leaf>(model);
    public sealed class DetailView(Detail model) : ViewBase<Detail>(model);
    public sealed class ModalView(Modal model) : ViewBase<Modal>(model);
    public sealed class FailingView(Failing model) : ViewBase<Failing>(model);
}
