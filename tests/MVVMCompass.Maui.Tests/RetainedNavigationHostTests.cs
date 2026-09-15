using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class RetainedNavigationHostTests : IAsyncDisposable
{
    private readonly Application? previous = Application.Current;
    private readonly TestApplication application = new();
    private readonly List<MauiNavigationHost> hosts = [];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public async ValueTask DisposeAsync()
    { try { foreach (var host in hosts) await host.DisposeAsync(); } finally { Application.Current = previous; } }

    [Theory]
    [InlineData(false, (int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData(true, (int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData(false, (int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    [InlineData(true, (int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public async Task Tab_entries_preserve_profiles_identity_hidden_children_and_origins(bool custom, int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var (host, page, parent) = await TabsAsync(custom, profile);
        var items = host.GetItems(page);
        var first = items[0].Entry!;
        var second = items[1].Entry!;
        Assert.Same(first, host.CurrentEntry);
        Assert.Equal(NavigationEntryState.Prepared, second.State);
        var selected = await host.SelectTabAsync(page, new(1) { Origin = first }, Token);
        Assert.True(selected.IsSuccess, selected.Error?.ToString());
        Assert.Same(items[1], selected.Value);
        Assert.Same(second, host.CurrentEntry);
        Assert.Equal(NavigationEntryState.Inactive, first.State);
        Assert.False(first.Lifetime.IsDismissed);
        Assert.Equal(profile == RetainedViewLifecycleBehavior.Deactivate ? 1 : 0, ((Probe)first.ViewModel).Deactivations);
        Assert.Equal(profile == RetainedViewLifecycleBehavior.LegacyAfterDismissed ? 1 : 0, ((Probe)first.ViewModel).Dismissals);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await host.SelectTabAsync(page, new(0) { Origin = first }, Token)).Status);
        Assert.True((await host.SelectTabAsync(page, new(0) { Origin = second }, Token)).IsSuccess);
        Assert.Same(first, host.CurrentEntry);
        Assert.Equal(2, ((Probe)first.ViewModel).Appearances);
        Assert.Same(items[0], host.GetItems(page)[0]);
        Assert.Equal(NavigationEntryState.Active, host.CurrentRoot!.Entry.State);
        Assert.False(parent.IsDismissed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Legacy_control_selection_uses_the_host_guard_and_completion(bool custom) => TestDispatcher.Run(async () =>
    {
        try
        {
            var (host, page, _) = await TabsAsync(custom);
            var first = (Probe)host.GetItems(page)[0].Entry!.ViewModel;
            first.Allow = false;
            if (custom) Assert.False(await ((ICustomTabbedViewBase)page).SwitchToAsync(1));
            else { ((TabbedPage)page).CurrentPage = ((TabbedPage)page).Children[1]; await Await(host.NativeSelectionCompletion); }
            Assert.Same(first, host.CurrentEntry!.ViewModel);
            Assert.Equal(0, first.Deactivations);
            Assert.Equal(1, first.Guards);
            first.Allow = true;
            if (custom) Assert.True(await ((ICustomTabbedViewBase)page).SwitchToAsync(1));
            else { ((TabbedPage)page).CurrentPage = ((TabbedPage)page).Children[1]; await Await(host.NativeSelectionCompletion); }
            Assert.Equal(1, first.Deactivations);
            Assert.Equal(2, first.Guards);
        }
        finally
        {
            // Native change notifications must share one UI queue with selection and teardown.
            // The immediate dispatcher can otherwise reconcile halfway through CurrentPage's setter.
            foreach (var host in hosts) await host.DisposeAsync();
            hosts.Clear();
        }
    });

    [Fact]
    public async Task Disabled_custom_tabs_reject_required_selection_without_deactivation()
    {
        var (host, page, _) = await TabsAsync(true);
        ((ICustomTabbedViewBase)page).IsTabBarEnabled = false;
        var result = await host.SelectTabAsync(page, new(1) { Priority = NavigationPriority.Required }, Token);
        Assert.Equal(NavigationStatus.GuardRejected, result.Status);
        Assert.False(result.HasCommitted);
        Assert.Same(host.GetItems(page)[0].Entry, host.CurrentEntry);
    }

    [Fact]
    public async Task Ordinary_child_models_retain_their_own_stacks_and_lifetimes()
    {
        var host = Host();
        var left = new Plain(); var right = new Plain();
        var first = TestNavigationHandler.Create(new ContentPage { BindingContext = left });
        var second = TestNavigationHandler.Create(new ContentPage { BindingContext = right });
        var tabs = new TabbedPage(); tabs.Children.Add(first); tabs.Children.Add(second);
        Assert.True((await host.ReplaceRootAsync(new NavigationRequest<int>(0), () => new Plain(), _ => tabs, cancellationToken: Token)).IsSuccess);
        var entries = host.GetItems(tabs);
        Assert.Equal(1, left.Activations);
        var detail = new Plain();
        var pushed = await host.PushAsync(new NavigationRequest<int>(42) { Origin = entries[0].Entry }, () => detail,
            _ => new ContentPage(), false, cancellationToken: Token);
        Assert.True(pushed.IsSuccess, pushed.Error?.ToString());
        Assert.Equal(NavigationEntryState.Inactive, entries[0].Entry!.State);
        Assert.Equal(NavigationEntryState.Active, host.CurrentRoot!.Entry.State);
        Assert.True((await host.SelectTabAsync(tabs, new(1) { Origin = pushed.Value!.Entry }, Token)).IsSuccess);
        Assert.Equal(1, detail.Deactivations);
        Assert.False(pushed.Value.Entry.Lifetime.IsDismissed);
        Assert.True((await host.SelectTabAsync(tabs, new(0) { Origin = entries[1].Entry }, Token)).IsSuccess);
        Assert.Same(pushed.Value.Entry, host.CurrentEntry);
        Assert.Equal(2, detail.Activations);
        Assert.Equal(1, left.Activations);
        Assert.True((await host.BackAsync(new(null) { Origin = pushed.Value.Entry }, false, Token)).IsSuccess);
        Assert.Same(entries[0].Entry, host.CurrentEntry);
        Assert.Equal(2, left.Activations);
        await host.DisposeAsync();
        Assert.Equal(1, left.Dismissals); Assert.Equal(1, right.Dismissals); Assert.Equal(1, detail.Dismissals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modal_coverage_invalidates_child_origins_and_reactivates_the_same_selection(bool custom)
    {
        var (host, page, _) = await TabsAsync(custom);
        var first = host.CurrentEntry!;
        var modal = await host.OpenModalAsync(new NavigationRequest<int>(0) { Origin = first }, () => new Plain(),
            _ => new ContentPage(), animated: false, cancellationToken: Token);
        Assert.True(modal.IsSuccess, modal.Error?.ToString());
        Assert.Equal(NavigationEntryState.Inactive, first.State);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await host.SelectTabAsync(page, new(1) { Origin = first }, Token)).Status);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await host.SelectTabAsync(page, new(1), Token)).Status);
        Assert.True((await host.CloseModalAsync(animated: false, cancellationToken: Token)).IsSuccess);
        Assert.Same(first, host.CurrentEntry);
        Assert.Equal(2, ((Probe)first.ViewModel).Appearances);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_guards_leave_selection_and_child_lifetimes_unchanged(bool custom)
    {
        var (host, page, _) = await TabsAsync(custom);
        var old = host.CurrentEntry!;
        var entered = Signal();
        ((Probe)old.ViewModel).Guard = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = host.SelectTabAsync(page, new(1), cancel.Token);
        await Await(entered.Task);
        cancel.Cancel();
        Assert.Equal(NavigationStatus.Cancelled, (await Await(pending)).Status);
        Assert.Same(old, host.CurrentEntry);
        Assert.Equal(NavigationEntryState.Active, old.State);
        Assert.False(old.Lifetime.IsDismissed);
    }

    [Fact]
    public async Task Required_root_supersedes_an_uncommitted_selection()
    {
        var (host, page, _) = await TabsAsync(true);
        var old = host.CurrentEntry!;
        var entered = Signal();
        ((Probe)old.ViewModel).Guard = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; };
        var pending = host.SelectTabAsync(page, new(1), Token);
        await Await(entered.Task);
        var root = host.ReplaceRootAsync(new NavigationRequest<int>(0) { Priority = NavigationPriority.Required },
            () => new Plain(), _ => new ContentPage(), cancellationToken: Token);
        Assert.Equal(NavigationStatus.Superseded, (await Await(pending)).Status);
        Assert.True((await Await(root)).IsSuccess);
        Assert.True(old.Lifetime.IsDismissed);
        Assert.Empty(host.GetItems(page));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_tab_composition_appends_once_and_keeps_one_selection_subscription(bool custom)
    {
        var (host, page, parent) = await TabsAsync(custom);
        var original = host.GetItems(page).ToArray();
        await parent.Tabs(new TabModel(typeof(Child)));
        await parent.Tabs(new TabModel(typeof(Child)));
        Assert.Equal(4, host.GetItems(page).Count);
        Assert.Same(original[0], host.GetItems(page)[0]);
        var first = (Probe)original[0].Entry!.ViewModel;
        Assert.Equal(1, first.Appearances);
        if (custom) Assert.True(await ((ICustomTabbedViewBase)page).SwitchToAsync(3));
        else { ((TabbedPage)page).CurrentPage = ((TabbedPage)page).Children[3]; await Await(host.NativeSelectionCompletion); }
        Assert.Equal(1, first.Deactivations);
        Assert.Equal(1, ((Probe)host.CurrentEntry!.ViewModel).Appearances);
        Assert.All(original, item => Assert.False(item.Entry!.Lifetime.IsDismissed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_composition_releases_candidates_and_preserves_existing_children(bool custom)
    {
        var (host, page, parent) = await TabsAsync(custom);
        var entries = host.GetItems(page);
        var contexts = (page as CustomHost)?.TabItems.ToArray();
        var originalGrid = custom ? ((Grid)((Grid)((ContentPage)page).Content).Children[1]).Children.OfType<Grid>().First() : null;
        var originalOverlay = originalGrid?.Children.OfType<BoxView>().Single();
        await Assert.ThrowsAnyAsync<Exception>(() => parent.Tabs(new(typeof(Child)), new(typeof(FailingChild))));
        Assert.Equal(2, host.GetItems(page).Count);
        Assert.Same(entries[0].Entry, host.CurrentEntry);
        Assert.Equal(2, custom ? ((ICustomTabbedViewBase)page).ChildCount : ((TabbedPage)page).Children.Count);
        if (custom)
        {
            Assert.Same(contexts![0], ((CustomHost)page).TabItems[0]);
            Assert.Contains(originalOverlay!, originalGrid!.Children);
        }
        Assert.True((await host.SelectTabAsync(page, new(1), Token)).IsSuccess);
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate, false)]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate, true)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed, false)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed, true)]
    public async Task Flyout_helper_routes_guards_parameters_and_repeated_rebuilds(int profileValue, bool pinned)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var parent = new Probe(); var page = new FlyoutHost(parent) { Pinned = pinned };
        var host = Host(type => type == typeof(Probe) ? page : Create(type), profile);
        parent.Before = () => parent.Flyout();
        var result = await host.ReplaceRootAsync<Probe>(new(null), cancellationToken: Token);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        page.IsPresented = true;
        var oldItems = host.GetItems(page);
        Assert.Equal(2, oldItems.Count);
        var oldMenu = (Menu)page.Flyout;
        var first = (Probe)oldItems[0].Entry!.ViewModel;
        first.Allow = false;
        await oldMenu.SelectMenuItemById("second", null);
        Assert.Same(first, host.CurrentEntry!.ViewModel);
        first.Allow = true;
        await oldMenu.SelectMenuItemById("second", new() { ["value"] = 17 });
        Assert.Same(oldItems[1].Entry, host.CurrentEntry);
        Assert.Equal(17, ((Probe)host.CurrentEntry!.ViewModel).Parameter);
        Assert.Equal(1, ((Probe)host.CurrentEntry.ViewModel).Appearances);
        Assert.False(first.IsDismissed);
        Assert.Equal(pinned, page.IsPresented);
        await oldMenu.SelectMenuItemById("second", null);
        Assert.Equal(pinned, page.IsPresented);
        await parent.Flyout();
        Assert.All(oldItems, item => Assert.True(item.Entry!.Lifetime.IsDismissed));
        Assert.True(oldMenu.ViewModel.IsDismissed);
        Assert.Equal(2, host.GetItems(page).Count);
        Assert.NotSame(oldMenu, page.Flyout);
        await oldMenu.SelectMenuItemById("first", null);
        Assert.Same(host.GetItems(page)[0].View, page.Detail);
        await ((Menu)page.Flyout).SelectMenuItemById("second", null);
        Assert.Same(host.GetItems(page)[1].Entry, host.CurrentEntry);
    }

    [Fact]
    public async Task A_foreign_container_cannot_be_selected_through_another_window()
    {
        var (first, _, _) = await TabsAsync(false);
        var (_, foreign, _) = await TabsAsync(false);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await first.SelectTabAsync(foreign, new(1), Token)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_composition_can_select_a_new_default_once(bool custom)
    {
        var (host, page, parent) = await TabsAsync(custom);
        var first = (Probe)host.CurrentEntry!.ViewModel;
        await parent.Tabs(new TabModel(typeof(Child), shouldBeSelectedByDefault: true));
        Assert.Same(host.GetItems(page)[2].Entry, host.CurrentEntry);
        Assert.Equal(1, first.Deactivations);
        Assert.Equal(1, ((Probe)host.CurrentEntry!.ViewModel).Appearances);
        Assert.False(first.IsDismissed);
    }

    [Fact]
    public async Task Selection_coalescing_replaces_a_guarded_request_before_commitment()
    {
        var (host, page, _) = await TabsAsync(true);
        var model = (Probe)host.CurrentEntry!.ViewModel;
        var entered = Signal(); var calls = 0;
        model.Guard = async token =>
        {
            if (calls++ == 0) { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return true;
        };
        var first = host.SelectTabAsync(page, new(1) { CoalescingKey = "selection" }, Token);
        await Await(entered.Task);
        var last = host.SelectTabAsync(page, new(1) { CoalescingKey = "selection" }, Token);
        Assert.Equal(NavigationStatus.Superseded, (await Await(first)).Status);
        Assert.True((await Await(last)).IsSuccess);
        Assert.Equal(1, model.Deactivations);
    }

    [Fact]
    public async Task Guards_cannot_deadlock_by_awaiting_selection_on_the_same_host()
    {
        var (host, page, _) = await TabsAsync(false);
        ((Probe)host.CurrentEntry!.ViewModel).Guard = async _ =>
        {
            Assert.Equal(NavigationStatus.Reentrant, (await host.SelectTabAsync(page, new(1), Token)).Status);
            return true;
        };
        Assert.True((await Await(host.SelectTabAsync(page, new(1), Token))).IsSuccess);
    }

    [Fact]
    public async Task Failed_custom_activation_keeps_installed_children_owned_for_recovery()
    {
        var (host, page, _) = await TabsAsync(true);
        var target = host.GetItems(page)[1];
        ((Probe)target.Entry!.ViewModel).Appear = () => throw new InvalidOperationException("activation failed");
        var failed = await host.SelectTabAsync(page, new(1), Token);
        Assert.Equal(NavigationStatus.Failed, failed.Status);
        Assert.True(failed.HasCommitted);
        var error = Assert.IsType<MauiNavigationException>(failed.Error);
        Assert.True(error.HasPresentationChanged);
        Assert.False(target.Entry.Lifetime.IsDismissed);
        Assert.Same(target.View, ((ICustomTabbedViewBase)page).CurrentTab!.View);
        Assert.True((await host.SelectTabAsync(page, new(0), Token)).IsSuccess);
        await host.DisposeAsync();
        Assert.True(target.Entry.Lifetime.IsDismissed);
    }

    [Fact]
    public async Task Terminal_cleanup_marks_all_children_before_awaiting_hidden_owners()
    {
        var (host, page, _) = await TabsAsync(true);
        var items = host.GetItems(page);
        var entered = Signal(); var release = Signal();
        ((Probe)items[1].Entry!.ViewModel).Cleanup = async () =>
        {
            Assert.All(items, item => Assert.True(item.Entry!.Lifetime.IsDismissed));
            Assert.True(host.CurrentRoot!.Entry.Lifetime.IsDismissed);
            entered.SetResult(); await release.Task;
        };
        var closing = host.DisposeAsync().AsTask();
        await Await(entered.Task);
        try { Assert.False(closing.IsCompleted); }
        finally { release.SetResult(); }
        await Await(closing);
        Assert.All(items, item => Assert.Equal(1, ((Probe)item.Entry!.ViewModel).Dismissals));
    }

    [Fact]
    public async Task An_ordinary_prepared_child_is_cleaned_up_when_root_initialization_fails()
    {
        var host = Host(); var child = new Plain();
        var tabs = new TabbedPage(); tabs.Children.Add(new ContentPage { BindingContext = child });
        var failed = await host.ReplaceRootAsync(new NavigationRequest<int>(0),
            () => new Plain { Initialize = () => throw new InvalidOperationException("initialize failed") }, _ => tabs, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, failed.Status);
        Assert.False(failed.HasCommitted);
        Assert.Equal(1, child.Dismissals);
        Assert.Equal(0, child.Activations);
        Assert.Empty(host.GetItems(tabs));
    }

    [Fact]
    public async Task Native_appearing_events_do_not_repeat_managed_retained_activation()
    {
        var (host, page, _) = await TabsAsync(false);
        var first = (Probe)host.CurrentEntry!.ViewModel;
        ((IPageController)host.GetItems(page)[0].View).SendAppearing();
        Assert.Equal(1, first.Appearances);
        Assert.True((await host.SelectTabAsync(page, new(1), Token)).IsSuccess);
        ((IPageController)host.GetItems(page)[1].View).SendAppearing();
        Assert.Equal(1, ((Probe)host.CurrentEntry!.ViewModel).Appearances);
    }

    [Fact]
    public async Task Failed_flyout_parameter_delivery_reactivates_the_unchanged_visible_owner()
    {
        var parent = new Probe(); var page = new FlyoutHost(parent);
        var host = Host(type => type == typeof(Probe) ? page : Create(type));
        parent.Before = () => parent.Flyout();
        Assert.True((await host.ReplaceRootAsync<Probe>(new(null), cancellationToken: Token)).IsSuccess);
        var first = host.CurrentEntry!;
        var target = (Probe)host.GetItems(page)[1].Entry!.ViewModel;
        target.FailParameters = true;
        var failed = await host.SelectFlyoutItemAsync(page, new("second"), new() { ["value"] = 2 }, Token);
        Assert.Equal(NavigationStatus.Failed, failed.Status);
        Assert.True(failed.HasCommitted);
        Assert.False(Assert.IsType<MauiNavigationException>(failed.Error).HasPresentationChanged);
        Assert.Same(first, host.CurrentEntry);
        Assert.Equal(NavigationEntryState.Active, first.State);
        Assert.False(target.IsDismissed);
        target.FailParameters = false;
        Assert.True((await host.SelectFlyoutItemAsync(page, new("second") { Origin = first }, cancellationToken: Token)).IsSuccess);
    }

    [Fact]
    public async Task A_new_retained_child_cannot_adopt_the_still_live_previous_root_model()
    {
        var host = Host(); var model = new Plain();
        var old = await host.ReplaceRootAsync(new NavigationRequest<int>(0), () => model, _ => new ContentPage(), cancellationToken: Token);
        Assert.True(old.IsSuccess);
        var tabs = new TabbedPage(); tabs.Children.Add(new ContentPage { BindingContext = model });
        var rejected = await host.ReplaceRootAsync(new NavigationRequest<int>(0), () => new Plain(), _ => tabs, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, rejected.Status);
        Assert.False(rejected.HasCommitted);
        Assert.Same(old.Value!.Entry, host.CurrentEntry);
        Assert.Equal(0, model.Dismissals);
    }

    private async Task<(MauiNavigationHost Host, Page Page, Probe Parent)> TabsAsync(bool custom,
        RetainedViewLifecycleBehavior profile = RetainedViewLifecycleBehavior.Deactivate)
    {
        var parent = new Probe();
        Page page = custom ? new CustomHost(parent) : new StandardHost(parent);
        parent.Before = () => parent.Tabs(new(typeof(Child)), new(typeof(Child), hideInTabBar: true));
        var host = Host(type => type == typeof(Probe) ? page : Create(type), profile);
        var result = await host.ReplaceRootAsync<Probe>(new(null), cancellationToken: Token);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return (host, page, parent);
    }
    private MauiNavigationHost Host(Func<Type, VisualElement>? create = null,
        RetainedViewLifecycleBehavior profile = RetainedViewLifecycleBehavior.Deactivate)
    {
        var host = new MauiNavigationHostFactory(new Locator(create ?? Create), new() { RetainedViewLifecycleBehavior = profile })
            .ForWindow(application.Add());
        hosts.Add(host); return host;
    }
    private static VisualElement Create(Type type) => type == typeof(MenuModel) ? new Menu(new MenuModel()) { Title = "Menu" }
        : new ProbePage(type == typeof(FailingChild) ? new FailingChild() : new Child(), type == typeof(Second) ? "second" : "first");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private static Task<T> Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private sealed class TestApplication : Application
    {
        internal Window Add() => (Window)((IApplication)this).CreateWindow(null);
        protected override Window CreateWindow(IActivationState? activationState) => new(new ContentPage());
    }
    private class Probe : ViewModelBase, INavigationGuard
    {
        internal bool Allow = true;
        internal bool FailParameters;
        internal int Appearances, Deactivations, Dismissals, Guards;
        internal object? Parameter;
        internal Func<Task>? Before, Appear, Cleanup;
        internal Func<CancellationToken, Task<bool>>? Guard;
        public override Task BeforeFirstShown() => Before?.Invoke() ?? Task.CompletedTask;
        public override Task GetParameters(Dictionary<string, object> parameters)
        {
            if (FailParameters) throw new InvalidOperationException("parameters failed");
            Parameter = parameters.GetValueOrDefault("value"); return Task.CompletedTask;
        }
        public override Task Appearing() { Appearances++; return Appear?.Invoke() ?? Task.CompletedTask; }
        public override Task Deactivated() { Deactivations++; return Task.CompletedTask; }
        public override Task AfterDismissed() { Dismissals++; return Cleanup?.Invoke() ?? Task.CompletedTask; }
        public override Task<bool> CanNavigate() { Guards++; return Task.FromResult(Allow); }
        public Task<bool> CanNavigateAsync(CancellationToken cancellationToken) => Guard?.Invoke(cancellationToken) ?? CanNavigate();
        internal Task Tabs(params TabModel[] tabs) => AddTabbedViewModels(new(tabs, this));
        internal Task Flyout() => AddFlyoutViewModels(new([new(typeof(Child)), new(typeof(Second))], this, typeof(MenuModel)));
    }
    private sealed class Child : Probe;
    private sealed class Second : Probe;
    private sealed class FailingChild : Probe { public override Task BeforeFirstShown() => throw new InvalidOperationException("candidate failed"); }
    private sealed class MenuModel : Probe;
    private sealed class ProbePage : ContentPage, IHasVM, IIdProvider
    {
        private readonly string id;
        internal ProbePage(Probe model, string id) { ViewModel = model; BindingContext = model; this.id = id; Content = new Label(); }
        public ViewModelBase ViewModel { get; }
        public string GetId() => id;
    }
    private sealed class StandardHost(Probe model) : TabbedPage, IHasVM { public ViewModelBase ViewModel { get; } = model; }
    private sealed class FlyoutHost(Probe model) : FlyoutPage, IHasVM, IFlyoutPageController
    {
        public ViewModelBase ViewModel { get; } = model;
        internal bool Pinned;
        bool IFlyoutPageController.ShouldShowSplitMode => Pinned;
    }
    private sealed class Menu(MenuModel model) : FlyoutViewFlyoutBase<MenuModel>(model);
    private sealed class CustomHost(Probe model) : CustomTabbedViewBase<Probe>(model)
    {
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => true;
        protected override IDisposable ShowLoading(LoadingType loadingType) => new Empty();
        protected override void HideLoading() { }
        private sealed class Empty : IDisposable { public void Dispose() { } }
    }
    private sealed class Plain : INavigationInitializable<int>, INavigationAware
    {
        internal int Activations, Deactivations, Dismissals;
        internal Func<Task>? Initialize;
        public Task InitializeAsync(int parameter, CancellationToken cancellationToken) => Initialize?.Invoke() ?? Task.CompletedTask;
        public Task ActivateAsync(CancellationToken lifetimeToken) { Activations++; return Task.CompletedTask; }
        public Task DeactivateAsync() { Deactivations++; return Task.CompletedTask; }
        public Task DismissAsync(DismissalReason reason) { Dismissals++; return Task.CompletedTask; }
    }
    private sealed class Locator(Func<Type, VisualElement> create) : IViewLocator
    {
        public void Initialize(Dictionary<Type, Type> registerPairs) { }
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => create(typeof(T));
        public VisualElement CreateAndBindVEFor(Type type) => create(type);
        public Type FindVEForViewModel(Type type) => typeof(ProbePage);
        public Type FindViewModelForVE(Type type) => typeof(Probe);
    }
}
