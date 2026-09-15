using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[CollectionDefinition("Application roots", DisableParallelization = true)]
public sealed class ApplicationRootsCollection;

[Collection("Application roots")]
public sealed class RootReplacementTests : IDisposable
{
    private readonly Application? previousApplication = Application.Current;
    private readonly TestApplication application = new();

    public void Dispose() => Application.Current = previousApplication;

    [Theory]
    [InlineData("resolution", false)]
    [InlineData("resolution", true)]
    [InlineData("type", false)]
    [InlineData("type", true)]
    [InlineData("parameters", false)]
    [InlineData("parameters", true)]
    [InlineData("initialization", false)]
    [InlineData("initialization", true)]
    public async Task Preparation_failure_preserves_current_root_and_cleans_returned_candidate(string failure, bool navigable)
    {
        var old = new Probe();
        var root = TestNavigationHandler.Create(new ProbePage(old));
        var window = application.Add(root);
        NativeNavigationObserver.Attach(root);
        var candidate = new Probe();
        var error = new TestFailure();
        if (failure == "parameters") candidate.Parameters = () => throw error;
        if (failure == "initialization") candidate.Initialize = () => throw error;
        var locator = new Locator(_ => failure switch
        {
            "resolution" => throw error,
            "type" => new ProbeView(candidate),
            _ => new ProbePage(candidate)
        });
        var service = new LegacyNavigationService(locator);

        var actual = await Assert.ThrowsAnyAsync<Exception>(() => navigable
            ? service.PresentAsNavigableMainPage<Probe>(new())
            : service.PresentAsMainPage<Probe>(new()));

        if (failure == "type") Assert.IsType<InvalidOperationException>(actual);
        else Assert.Same(error, actual);
        Assert.Same(root, window.Page);
        Assert.False(old.IsDismissed);
        Assert.False(old.IsHostReplaced);
        Assert.False(old.Cancelled);
        Assert.Equal(failure == "resolution" ? 0 : 1, candidate.Dismissals);
        Assert.False(candidate.IsHostReplaced);
        if (failure != "resolution") Assert.Equal(DismissalReason.PreparationFailed, candidate.Lifetime.Reason);
        Assert.Null(candidate.PendingRootPreparation);

        // Preparation must not detach the old native-removal observer.
        var pushed = new Probe();
        await root.PushAsync(new ProbePage(pushed), false);
        await root.PopAsync(false);
        await pushed.DismissAsync();
        Assert.Equal(DismissalReason.Back, pushed.Lifetime.Reason);
        NativeNavigationObserver.Detach(root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_tab_failure_cleans_attached_and_unattached_children_before_host(bool standard)
    {
        var old = new Probe();
        var window = application.Add(new ProbePage(old));
        var order = new List<string>();
        var parent = new Probe { Cleanup = () => { order.Add("host"); return Task.CompletedTask; } };
        var first = new Probe { Cleanup = () => { order.Add("first"); return Task.CompletedTask; } };
        var error = new TestFailure();
        var second = new Probe
        {
            Initialize = () => throw error,
            Cleanup = () => { order.Add("second"); throw new TestFailure(); }
        };
        parent.Initialize = () => parent.AddTabs(new(typeof(Child)), new(typeof(Child)));
        var children = new Queue<Probe>([first, second]);
        var service = new LegacyNavigationService(new Locator(type => type == typeof(Child)
            ? new ProbePage(children.Dequeue())
            : standard ? new StandardHost(parent) : new CustomHost(parent)));

        Assert.Same(error, await Assert.ThrowsAsync<TestFailure>(() => service.PresentAsMainPage<Probe>()));
        Assert.Same(old, ((IHasVM)window.Page!).ViewModel);
        Assert.False(old.IsDismissed);
        Assert.Equal(["second", "first", "host"], order);
        Assert.All(new[] { first, second, parent }, model =>
        {
            Assert.Equal(1, model.Dismissals);
            Assert.True(model.Cancelled);
            Assert.False(model.IsHostReplaced);
            Assert.Null(model.ParentViewModel);
            Assert.Null(model.PendingRootPreparation);
            Assert.Equal(0, model.Appearances);
        });
    }

    [Theory]
    [InlineData(false, (int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData(false, (int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    [InlineData(true, (int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData(true, (int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public async Task Shared_owner_is_released_before_default_tab_activation(bool standard, int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var order = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldOwnsIntegration = true;
        var old = new Probe
        {
            Cleanup = async () => { order.Add("old cleanup"); await release.Task; oldOwnsIntegration = false; }
        };
        var oldPage = new ProbePage(old);
        var window = application.Add(oldPage);
        var parent = new Probe();
        var first = new Probe();
        var selected = new Probe
        {
            Initialize = () => { Assert.False(old.IsDismissed); order.Add("prepare"); return Task.CompletedTask; },
            OnAppearing = () =>
            {
                Assert.False(oldOwnsIntegration);
                Assert.NotSame(oldPage, window.Page);
                order.Add("activate");
                return Task.CompletedTask;
            }
        };
        parent.Initialize = () => parent.AddTabs(new(typeof(Child)), new(typeof(Child), shouldBeSelectedByDefault: true, hideInTabBar: true));
        var children = new Queue<Probe>([first, selected]);
        var service = new LegacyNavigationService(new Locator(type => type == typeof(Child)
                ? new ProbePage(children.Dequeue()) : standard ? new StandardHost(parent) : new CustomHost(parent)),
            new NavigationOptions { RetainedViewLifecycleBehavior = profile });

        var replacing = service.PresentAsMainPage<Probe>();
        Assert.False(replacing.IsCompleted);
        Assert.Same(oldPage, window.Page);
        Assert.Equal(["prepare", "old cleanup"], order);
        Assert.True(old.IsDismissed);
        Assert.True(old.IsHostReplaced);
        Assert.Equal(0, selected.Appearances);
        release.SetResult();
        await replacing;

        Assert.Equal(["prepare", "old cleanup", "activate"], order);
        Assert.Equal(1, selected.Appearances);
        Assert.Equal(0, first.Appearances);
        Assert.Equal(profile, selected.RetainedViewLifecycleBehavior);
        Assert.Null(selected.PendingRootPreparation);
    }

    [Fact]
    public async Task Activation_failure_is_reported_with_installed_live_candidate_and_fresh_root_can_recover()
    {
        var old = new Probe();
        var window = application.Add(new ProbePage(old));
        var parent = new Probe();
        parent.Initialize = () => parent.AddTabs(new TabModel(typeof(Child)));
        var error = new TestFailure();
        var child = new Probe { OnAppearing = () => throw error };
        var locator = new Locator(type => type == typeof(Child) ? new ProbePage(child) : new CustomHost(parent));
        var service = new LegacyNavigationService(locator);

        var failure = await Assert.ThrowsAsync<RootReplacementException>(() => service.PresentAsMainPage<Probe>());
        Assert.Equal(RootReplacementStage.Activation, failure.Stage);
        Assert.True(failure.IsReplacementPresented);
        Assert.Same(error, failure.InnerException);
        Assert.Same(parent, ((IHasVM)window.Page!).ViewModel);
        Assert.True(old.IsDismissed);
        Assert.False(parent.IsDismissed);
        Assert.False(child.IsDismissed);
        Assert.Null(parent.PendingRootPreparation);

        var recovery = new Probe();
        // The same service accepts the next operation after failure.
        parent.Initialize = null;
        locator.Factory = _ => new ProbePage(recovery);
        await service.PresentAsNavigableMainPage<Probe>();
        Assert.IsType<NavigationPage>(window.Page);
        Assert.False(recovery.IsDismissed);
        Assert.Equal(1, parent.Dismissals);
        Assert.Equal(1, child.Dismissals);
    }

    [Fact]
    public async Task Concurrent_and_reentrant_roots_fail_promptly_without_corrupting_the_outer_operation()
    {
        var old = new Probe();
        application.Add(new ProbePage(old));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidate = new Probe();
        var service = new LegacyNavigationService(new Locator(_ => new ProbePage(candidate)));
        candidate.Initialize = async () =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PresentAsMainPage<Probe>());
            await release.Task;
        };
        var first = service.PresentAsMainPage<Probe>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateMainPage<Probe>());
        Assert.False(old.IsDismissed);
        release.SetResult();
        await first;
        Assert.True(old.IsDismissed);
        Assert.False(candidate.IsDismissed);
    }

    [Fact]
    public async Task External_root_change_during_preparation_abandons_candidate_without_dismissing_either_live_tree()
    {
        var old = new Probe();
        var window = application.Add(new ProbePage(old));
        var external = new Probe();
        var candidate = new Probe { Initialize = () => { window.Page = new ProbePage(external); return Task.CompletedTask; } };
        var service = new LegacyNavigationService(new Locator(_ => new ProbePage(candidate)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PresentAsMainPage<Probe>());
        Assert.Same(external, ((IHasVM)window.Page!).ViewModel);
        Assert.False(old.IsDismissed);
        Assert.False(external.IsDismissed);
        Assert.True(candidate.IsDismissed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_remains_available_before_a_window_exists_and_presentation_requires_a_window(bool navigable)
    {
        var calls = 0;
        var candidate = new Probe();
        var service = new LegacyNavigationService(new Locator(_ => { calls++; return new ProbePage(candidate); }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PresentAsMainPage<Probe>());
        Assert.Equal(0, calls);
        var page = navigable ? await service.CreateNavigableMainPage<Probe>() : await service.CreateMainPage<Probe>();
        Assert.Equal(navigable, page is NavigationPage);
        Assert.Empty(application.Windows);
        Assert.False(candidate.IsDismissed);
        Assert.Null(candidate.PendingRootPreparation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Singleton_reuse_is_rejected_without_initializing_or_dismissing_the_existing_owner(bool sameView)
    {
        var old = new Probe { Initialize = () => throw new TestFailure() };
        var root = new ProbePage(old);
        var window = application.Add(root);
        var service = new LegacyNavigationService(new Locator(_ => sameView ? root : new ProbePage(old)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PresentAsMainPage<Probe>());
        Assert.Same(root, window.Page);
        Assert.False(old.IsDismissed);
        Assert.Null(old.PendingRootPreparation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flyout_failure_cleans_retained_items_nested_tabs_and_menu_without_activating_them(bool afterComposition)
    {
        var old = new Probe();
        var window = application.Add(new ProbePage(old));
        var order = new List<string>();
        Probe Model(string name) => new() { Cleanup = () => { order.Add(name); return Task.CompletedTask; } };
        var parent = Model("root");
        var first = Model("tabs");
        var leaf = Model("leaf");
        var second = Model("second");
        var menu = Model("menu");
        var error = new TestFailure();
        first.Initialize = () => first.AddTabs(new TabModel(typeof(Grandchild)));
        if (!afterComposition) second.Initialize = () => throw error;
        parent.Initialize = async () =>
        {
            await parent.AddFlyout(new(typeof(Child)), new(typeof(SecondChild)));
            throw error;
        };
        var locator = new Locator(type => type == typeof(Child) ? new StandardHost(first)
            : type == typeof(Grandchild) ? new ProbePage(leaf)
            : type == typeof(SecondChild) ? new ProbePage(second)
            : type == typeof(MenuModel) ? new Menu(menu) { Title = "Menu" }
            : new FlyoutHost(parent));
        var service = new LegacyNavigationService(locator);

        Assert.Same(error, await Assert.ThrowsAsync<TestFailure>(() => service.PresentAsMainPage<Probe>()));
        Assert.Same(old, ((IHasVM)window.Page!).ViewModel);
        Assert.False(old.IsDismissed);
        Assert.True(order.IndexOf("leaf") < order.IndexOf("tabs"));
        Assert.Equal("root", order[^1]);
        Assert.All(new[] { parent, first, leaf, second }, model =>
        {
            Assert.Equal(1, model.Dismissals);
            Assert.Equal(0, model.Appearances);
            Assert.True(model.Cancelled);
            Assert.False(model.IsHostReplaced);
        });
        Assert.Equal(afterComposition ? 1 : 0, menu.Dismissals);

        // Failed flyout composition must not leave a stale temporary lookup stack.
        var recovered = new Probe();
        locator.Factory = _ => new ProbePage(recovered);
        await service.PresentAsMainPage<Probe>();
        Assert.Same(recovered, ((IHasVM)window.Page!).ViewModel);
    }

    [Fact]
    public async Task Outgoing_cleanup_failures_do_not_prevent_prepared_root_activation()
    {
        var old = new Probe { Cleanup = () => throw new TestFailure() };
        var window = application.Add(new ProbePage(old));
        var parent = new Probe();
        parent.Initialize = () => parent.AddTabs(new TabModel(typeof(Child)));
        var child = new Probe();
        var service = new LegacyNavigationService(new Locator(type => type == typeof(Child)
            ? new ProbePage(child) : new CustomHost(parent)));
        await service.PresentAsMainPage<Probe>();
        Assert.Equal(1, old.Dismissals);
        Assert.True(old.IsHostReplaced);
        Assert.Same(parent, ((IHasVM)window.Page!).ViewModel);
        Assert.Equal(1, child.Appearances);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_with_an_existing_window_retains_teardown_and_activation_but_does_not_install(bool failActivation)
    {
        var old = new Probe();
        var oldPage = new ProbePage(old);
        var window = application.Add(oldPage);
        var parent = new Probe();
        parent.Initialize = () => parent.AddTabs(new TabModel(typeof(Child)));
        var child = new Probe();
        if (failActivation) child.OnAppearing = () => throw new TestFailure();
        var service = new LegacyNavigationService(new Locator(type => type == typeof(Child)
            ? new ProbePage(child) : new CustomHost(parent)));
        if (failActivation)
        {
            var error = await Assert.ThrowsAsync<RootReplacementException>(() => service.CreateNavigableMainPage<Probe>());
            Assert.False(error.IsReplacementPresented);
            Assert.Equal(RootReplacementStage.Activation, error.Stage);
            Assert.Equal(1, parent.Dismissals);
            Assert.Equal(1, child.Dismissals);
        }
        else
        {
            var created = await service.CreateNavigableMainPage<Probe>();
            Assert.IsType<CustomHost>(created.CurrentPage);
            Assert.False(parent.IsDismissed);
            Assert.False(child.IsDismissed);
        }
        Assert.Same(oldPage, window.Page);
        Assert.True(old.IsDismissed);
        Assert.Equal(1, child.Appearances);
        Assert.Null(parent.PendingRootPreparation);
    }

    [Fact]
    public async Task Commitment_failure_cleans_uninstalled_candidate_and_reports_irreversible_boundary()
    {
        var old = new Probe();
        var oldPage = new ProbePage(old);
        var window = application.Add(oldPage);
        var parent = new Probe();
        var failure = new TestFailure();
        Microsoft.Maui.Controls.PropertyChangingEventHandler reject = (_, args) =>
        {
            if (args.PropertyName == nameof(Window.Page)) throw failure;
        };
        window.PropertyChanging += reject;
        var service = new LegacyNavigationService(new Locator(_ => new ProbePage(parent)));
        var error = await Assert.ThrowsAsync<RootReplacementException>(() => service.PresentAsMainPage<Probe>());
        window.PropertyChanging -= reject;
        Assert.Equal(RootReplacementStage.Commitment, error.Stage);
        Assert.Same(failure, error.InnerException);
        Assert.False(error.IsReplacementPresented);
        Assert.Same(oldPage, window.Page);
        Assert.True(old.IsDismissed);
        Assert.Equal(1, parent.Dismissals);
    }

    [Fact]
    public async Task External_replacement_during_cleanup_is_not_overwritten()
    {
        var old = new Probe();
        var window = application.Add(new ProbePage(old));
        var external = new ProbePage(new Probe());
        old.Cleanup = () => { window.Page = external; return Task.CompletedTask; };
        var candidate = new Probe();
        var service = new LegacyNavigationService(new Locator(_ => new ProbePage(candidate)));
        var error = await Assert.ThrowsAsync<RootReplacementException>(() => service.PresentAsMainPage<Probe>());
        Assert.Equal(RootReplacementStage.Commitment, error.Stage);
        Assert.False(error.IsReplacementPresented);
        Assert.Same(external, window.Page);
        Assert.True(candidate.IsDismissed);
        Assert.False(external.ViewModel.IsDismissed);
    }

    [Fact]
    public async Task Standard_selection_changed_during_initialization_activates_only_the_final_selection()
    {
        application.Add(new ProbePage(new Probe()));
        var parent = new Probe();
        var host = new StandardHost(parent);
        parent.Initialize = async () =>
        {
            await parent.AddTabs(new(typeof(Child)), new(typeof(Child)));
            host.CurrentPage = host.Children[1];
        };
        var first = new Probe();
        var selected = new Probe();
        var children = new Queue<Probe>([first, selected]);
        var service = new LegacyNavigationService(new Locator(type => type == typeof(Child) ? new ProbePage(children.Dequeue()) : host));
        await service.PresentAsMainPage<Probe>();
        Assert.Equal(0, first.Appearances);
        Assert.Equal(1, selected.Appearances);
    }

    [Fact]
    public async Task Native_appearing_during_preparation_is_discarded_on_abandonment()
    {
        var old = new Probe();
        application.Add(new ProbePage(old));
        var candidate = new Probe();
        var page = new ProbePage(candidate);
        var events = (IPageController)page;
        var error = new TestFailure();
        candidate.Initialize = () => { events.SendAppearing(); throw error; };
        var service = new LegacyNavigationService(new Locator(_ => page));
        await Assert.ThrowsAsync<TestFailure>(() => service.PresentAsMainPage<Probe>());
        Assert.Equal(0, candidate.Appearances);
        events.SendDisappearing();
        events.SendAppearing();
        Assert.Equal(0, candidate.Appearances);
        Assert.False(old.IsDismissed);
    }

    [Fact]
    public async Task Deferred_native_callback_is_awaited_and_its_failure_reaches_the_root_caller()
    {
        var old = new Probe();
        var window = application.Add(new ProbePage(old));
        var candidate = new Probe();
        var page = new ProbePage(candidate);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        candidate.Initialize = () => { ((IPageController)page).SendAppearing(); return Task.CompletedTask; };
        candidate.OnAppearing = () => { Assert.True(old.IsDismissed); return release.Task; };
        var service = new LegacyNavigationService(new Locator(_ => page));
        var pending = service.PresentAsMainPage<Probe>();
        Assert.False(pending.IsCompleted);
        Assert.Same(page, window.Page);
        var failure = new TestFailure();
        release.SetException(failure);
        var error = await Assert.ThrowsAsync<RootReplacementException>(() => pending);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(RootReplacementStage.Activation, error.Stage);
        Assert.True(error.IsReplacementPresented);
        Assert.False(candidate.IsDismissed);
    }

    [Fact]
    public async Task Flyout_helpers_during_preparation_target_the_candidate_without_changing_the_live_root()
    {
        var old = new Probe();
        var oldPage = new FlyoutHost(old) { Flyout = new ContentPage { Title = "Menu" }, Detail = new ContentPage() };
        var window = application.Add(oldPage);
        var candidate = new Probe();
        var page = new FlyoutHost(candidate);
        var menu = new Menu(new Probe()) { Title = "Candidate menu" };
        var error = new TestFailure();
        candidate.Initialize = async () =>
        {
            await candidate.AddFlyout(new FlyoutModel(typeof(Child)));
            candidate.ToggleFlyoutVisibility();
            Assert.True(page.IsPresented);
            Assert.False(oldPage.IsPresented);
            menu.CloseFlyout(menu, null);
            Assert.False(page.IsPresented);
            throw error;
        };
        var service = new LegacyNavigationService(new Locator(type => type == typeof(Child) ? new ProbePage(new Probe())
            : type == typeof(MenuModel) ? menu : page));
        Assert.Same(error, await Assert.ThrowsAsync<TestFailure>(() => service.PresentAsMainPage<Probe>()));
        Assert.Same(oldPage, window.Page);
        Assert.False(old.IsDismissed);
        Assert.False(oldPage.IsPresented);
        Assert.True(candidate.IsDismissed);
        Assert.True(menu.ViewModel.IsDismissed);
    }

    [Fact]
    public async Task Flyout_helpers_leave_a_split_pane_open()
    {
        application.Add(new ProbePage(new Probe()));
        var candidate = new Probe();
        var page = new FlyoutHost(candidate) { Pinned = true };
        var menu = new Menu(new Probe()) { Title = "Pinned menu" };
        candidate.Initialize = () => candidate.AddFlyout(new FlyoutModel(typeof(Child)));
        var service = new LegacyNavigationService(new Locator(type => type == typeof(Child) ? new ProbePage(new Probe())
            : type == typeof(MenuModel) ? menu : page));
        await service.PresentAsMainPage<Probe>();
        page.IsPresented = true;

        candidate.ToggleFlyoutVisibility();
        Assert.True(page.IsPresented);
        menu.CloseFlyout(menu, null);
        Assert.True(page.IsPresented);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_disappearing_lookup_failures_are_reported_at_the_event_boundary(bool failLocator)
    {
        application.Add(new ProbePage(new Probe()));
        var candidate = new Probe();
        var page = new FalliblePage(candidate);
        var locator = new Locator(_ => page);
        var service = new LegacyNavigationService(locator);
        await service.PresentAsMainPage<Probe>();
        ((IPageController)page).SendAppearing();
        var failure = new TestFailure();
        if (failLocator) locator.LookupFailure = failure;
        else page.Failure = failure;
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Report(Exception error, string operation) { if (operation == "Disappearing") reported.TrySetResult(error); }
        NavigationDiagnostics.Error += Report;
        try
        {
            ((IPageController)page).SendDisappearing();
            Assert.Same(failure, await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(candidate.IsDismissed);
        }
        finally { NavigationDiagnostics.Error -= Report; page.Failure = null; locator.LookupFailure = null; }
    }

    [Fact]
    public async Task Asynchronous_disappearing_failure_is_reported_once()
    {
        application.Add(new ProbePage(new Probe()));
        var failure = new TestFailure();
        var candidate = new Probe { OnDisappearing = async () => { await Task.Yield(); throw failure; } };
        var page = new ProbePage(candidate);
        await new LegacyNavigationService(new Locator(_ => page)).PresentAsMainPage<Probe>();
        ((IPageController)page).SendAppearing();
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        void Report(Exception error, string operation)
        {
            if (operation != "Disappearing") return;
            Interlocked.Increment(ref calls);
            reported.TrySetResult(error);
        }
        NavigationDiagnostics.Error += Report;
        try
        {
            ((IPageController)page).SendDisappearing();
            Assert.Same(failure, await reported.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal(1, calls);
        }
        finally { NavigationDiagnostics.Error -= Report; }
    }

    private sealed class TestApplication : Application
    {
        private Page? next;
        internal Window Add(Page page)
        {
            next = page;
            return (Window)((IApplication)this).CreateWindow(null);
        }
        protected override Window CreateWindow(IActivationState? activationState) => new(next!);
    }

    private sealed class TestFailure : Exception;
    private sealed class Child : Probe;
    private sealed class SecondChild : Probe;
    private sealed class Grandchild : Probe;
    private class Probe : ViewModelBase
    {
        internal Func<Task>? Parameters { get; set; }
        internal Func<Task>? Initialize { get; set; }
        internal Func<Task>? OnAppearing { get; set; }
        internal Func<Task>? OnDisappearing { get; set; }
        internal Func<Task>? Cleanup { get; set; }
        internal int Dismissals { get; private set; }
        internal int Appearances { get; private set; }
        internal bool Cancelled => LifetimeToken.IsCancellationRequested;
        internal Task AddTabs(params TabModel[] tabs) => AddTabbedViewModels(new(tabs, this));
        internal Task AddFlyout(params FlyoutModel[] items) => AddFlyoutViewModels(new(items, this, typeof(MenuModel)));
        public override Task GetParameters(Dictionary<string, object> parameters) => Parameters?.Invoke() ?? Task.CompletedTask;
        public override Task BeforeFirstShown() => Initialize?.Invoke() ?? Task.CompletedTask;
        public override Task Appearing() { Appearances++; return OnAppearing?.Invoke() ?? Task.CompletedTask; }
        public override Task Disappearing() => OnDisappearing?.Invoke() ?? Task.CompletedTask;
        public override Task AfterDismissed() { Dismissals++; return Cleanup?.Invoke() ?? Task.CompletedTask; }
    }
    private sealed class MenuModel : Probe;
    private sealed class ProbePage : ContentPage, IHasVM
    {
        internal ProbePage(Probe vm) { ViewModel = vm; BindingContext = vm; Content = new Label(); }
        public ViewModelBase ViewModel { get; }
    }
    private sealed class ProbeView(Probe vm) : ContentView, IHasVM { public ViewModelBase ViewModel { get; } = vm; }
    private sealed class FalliblePage(Probe vm) : ContentPage, IHasVM
    {
        internal Exception? Failure { get; set; }
        public ViewModelBase ViewModel => Failure == null ? vm : throw Failure;
    }
    private sealed class StandardHost(Probe vm) : TabbedPage, IHasVM { public ViewModelBase ViewModel { get; } = vm; }
    private sealed class FlyoutHost(Probe vm) : FlyoutPage, IHasVM, IFlyoutPageController
    {
        public ViewModelBase ViewModel { get; } = vm;
        internal bool Pinned;
        bool IFlyoutPageController.ShouldShowSplitMode => Pinned;
    }
    private sealed class Menu(Probe vm) : FlyoutViewFlyoutBase<Probe>(vm);
    private sealed class CustomHost(Probe vm) : CustomTabbedViewBase<Probe>(vm)
    {
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => true;
        protected override IDisposable ShowLoading(LoadingType loadingType) => new EmptyDisposable();
        protected override void HideLoading() { }
        private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    }
    private sealed class Locator(Func<Type, VisualElement> factory) : IViewLocator
    {
        internal Func<Type, VisualElement> Factory { get; set; } = factory;
        internal Exception? LookupFailure { get; set; }
        public void Initialize(Dictionary<Type, Type> registerPairs) { }
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => Factory(typeof(T));
        public VisualElement CreateAndBindVEFor(Type type) => Factory(type);
        public Type FindVEForViewModel(Type viewModelType) => typeof(ProbePage);
        public Type FindViewModelForVE(Type page) => LookupFailure == null ? typeof(Probe) : throw LookupFailure;
    }
}
