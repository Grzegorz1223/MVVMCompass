using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class NativeReconciliationTests : IAsyncDisposable
{
    private readonly Application? previous = Application.Current;
    private readonly TestApplication application = new();
    private readonly List<MauiNavigationHost> hosts = [];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public async ValueTask DisposeAsync()
    { try { foreach (var host in hosts) await host.DisposeAsync(); } finally { Application.Current = previous; } }

    [Fact]
    public async Task Native_push_adopts_the_actual_model_and_back_reactivates_the_same_root()
    {
        var (host, root, stack) = await StackAsync();
        var model = new Plain(); var page = PageFor(model);
        await stack.PushAsync(page, false);
        await Settle(host);
        var entry = host.CurrentEntry!;
        Assert.Same(model, entry.ViewModel);
        Assert.Same(page, host.CurrentPage!.Page);
        Assert.Equal(1, model.Activations);
        Assert.Equal(1, root.Deactivations);
        await stack.PopAsync(false);
        await Settle(host);
        Assert.Equal(1, model.Dismissals);
        Assert.Equal(NavigationEntryState.Dismissed, entry.State);
        Assert.Same(root, host.CurrentEntry!.ViewModel);
        Assert.Equal(2, root.Activations);
    }

    [Fact]
    public async Task Direct_insertion_and_inactive_removal_promote_a_new_native_root()
    {
        var (host, oldRoot, stack) = await StackAsync();
        var top = new Plain(); await stack.PushAsync(PageFor(top), false); await Settle(host);
        var newRoot = new Plain(); var page = PageFor(newRoot);
        stack.Navigation.InsertPageBefore(page, stack.RootPage);
        await Settle(host);
        Assert.Same(newRoot, host.CurrentRoot!.Entry.ViewModel);
        Assert.Equal(0, newRoot.Activations);
        var oldPage = stack.Navigation.NavigationStack[1];
        stack.Navigation.RemovePage(oldPage); await Settle(host);
        Assert.Equal(1, oldRoot.Dismissals);
        Assert.Equal(0, top.Dismissals);
        Assert.True((await host.PopToRootAsync(animated: false, cancellationToken: Token)).IsSuccess);
        Assert.Same(newRoot, host.CurrentEntry!.ViewModel);
        Assert.Equal(1, top.Dismissals);
        Assert.True((await host.PushAsync(new NavigationRequest<int>(0), () => new Plain(), PageFor, false, cancellationToken: Token)).IsSuccess);
    }

    [Fact]
    public async Task Removing_an_inactive_inserted_page_does_not_activate_or_recreate_it()
    {
        var (host, root, stack) = await StackAsync();
        var model = new Plain(); var page = PageFor(model);
        stack.Navigation.InsertPageBefore(page, stack.RootPage); await Settle(host);
        stack.Navigation.RemovePage(page); await Settle(host);
        Assert.Equal(0, model.Activations);
        Assert.Equal(1, model.Dismissals);
        Assert.Same(root, host.CurrentEntry!.ViewModel);
        Assert.Equal(1, root.Activations);
    }

    [Fact]
    public async Task Cancelled_legacy_renderer_pop_keeps_the_entry_and_activation_generation()
    {
        var (host, _, stack) = await StackAsync();
        var model = new Plain(); await stack.PushAsync(PageFor(model), false); await Settle(host);
        var entry = host.CurrentEntry!;
        var controller = (INavigationPageController)stack;
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Cancel(object? sender, NavigationRequestedEventArgs args) => args.Task = pending.Task;
        controller.PopRequested += Cancel;
        try
        {
            var popping = controller.PopAsyncInner(true, false);
            var completion = host.NativeNavigationCompletion;
            Assert.False(completion.IsCompleted);
            Assert.False(entry.Lifetime.IsDismissed);
            pending.SetResult(false);
            await AwaitCancelledRendererPop(popping); await Settle(host);
        }
        finally { controller.PopRequested -= Cancel; pending.TrySetResult(false); }
        Assert.Same(entry, host.CurrentEntry);
        Assert.Equal(1, model.Activations);
        Assert.Equal(0, model.Deactivations);
        Assert.Equal(0, model.Dismissals);
        Assert.True((await host.BackAsync(new(null) { Origin = entry }, false, Token)).IsSuccess);
        Assert.Equal(1, model.Dismissals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_modal_close_keeps_owned_modal_content(bool navigable)
    {
        var (host, _, _) = await StackAsync();
        var model = new Plain(); Page page = navigable ? TestNavigationHandler.Create(PageFor(model)) : PageFor(model);
        await host.Window.Navigation.PushModalAsync(page, false); await Settle(host);
        var entry = host.CurrentEntry!;
        Assert.True(host.CurrentPage!.IsModal);
        void Cancel(object? sender, ModalPoppingEventArgs args) => args.Cancel = true;
        host.Window.ModalPopping += Cancel;
        try { await host.Window.Navigation.PopModalAsync(false); await Settle(host); }
        finally { host.Window.ModalPopping -= Cancel; }
        Assert.Same(entry, host.CurrentEntry);
        Assert.Same(page, host.Window.Navigation.ModalStack.Single());
        Assert.Equal(0, model.Dismissals);
        await host.Window.Navigation.PopModalAsync(false); await Settle(host);
        Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Removing_a_selected_tab_marks_children_before_awaiting_cleanup_and_activates_afterwards()
    {
        var host = Host(); var parent = new Plain(); var first = new Plain(); var second = new Plain();
        var nested = new Probe(); var childView = new ContentView { BindingContext = nested };
        var firstPage = PageFor(first); firstPage.Content = childView;
        var tabs = new TabbedPage(); tabs.Children.Add(firstPage); tabs.Children.Add(PageFor(second));
        Assert.True((await host.ReplaceRootAsync(new NavigationRequest<int>(0), () => parent, _ => tabs, cancellationToken: Token)).IsSuccess);
        var oldEntry = host.GetItems(tabs)[0].Entry!;
        var entered = Signal(); var release = Signal();
        nested.Cleanup = async () => { Assert.True(oldEntry.Lifetime.IsDismissed); entered.SetResult(); await release.Task; };
        tabs.Children.Remove(firstPage);
        var completion = host.ReconcileNativeAsync();
        try
        {
            await Await(entered.Task);
            Assert.False(completion.IsCompleted);
            Assert.Equal(0, first.Dismissals);
            Assert.Equal(0, second.Activations);
        }
        finally { release.TrySetResult(); }
        await Await(completion);
        Assert.Single(host.GetItems(tabs));
        Assert.Same(second, host.CurrentEntry!.ViewModel);
        Assert.Equal(1, nested.Dismissals);
        Assert.Equal(1, first.Dismissals);
        Assert.Equal(1, second.Activations);
        Assert.Equal(0, parent.Dismissals);
    }

    [Fact]
    public async Task Direct_tab_add_remove_and_clear_keep_stable_surviving_entries()
    {
        var host = Host(); var parent = new Plain(); var first = new Plain();
        var tabs = new TabbedPage(); tabs.Children.Add(PageFor(first));
        Assert.True((await host.ReplaceRootAsync(new NavigationRequest<int>(0), () => parent, _ => tabs, cancellationToken: Token)).IsSuccess);
        var original = host.GetItems(tabs)[0];
        var added = new Plain(); var page = PageFor(added); tabs.Children.Add(page); await Settle(host);
        Assert.Same(original, host.GetItems(tabs)[0]);
        Assert.Equal(NavigationEntryState.Prepared, host.GetItems(tabs)[1].Entry!.State);
        tabs.Children.Remove(page); await Settle(host);
        Assert.Equal(1, added.Dismissals);
        Assert.Equal(0, added.Activations);
        tabs.Children.Clear(); await Settle(host);
        Assert.Empty(host.GetItems(tabs));
        Assert.Null(tabs.CurrentPage);
        Assert.Equal(1, first.Dismissals);
        Assert.Same(parent, host.CurrentEntry!.ViewModel);
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public async Task Direct_flyout_selection_and_menu_removal_preserve_profiles(int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var host = Host(profile: profile); var first = new Probe(); var second = new Probe();
        var firstPage = new ProbePage(first); var secondPage = new ProbePage(second);
        var menu = new Menu(new Probe()) { Title = "Menu", MenuItems = [Item("first", firstPage), Item("second", secondPage)] };
        var flyout = new FlyoutPage { Flyout = menu, Detail = firstPage };
        Assert.True((await host.ReplaceRootAsync(new NavigationRequest<int>(0), () => new Plain(), _ => flyout, cancellationToken: Token)).IsSuccess);
        var entry = host.GetItems(flyout)[1].Entry;
        flyout.Detail = secondPage; await Settle(host);
        Assert.Same(entry, host.CurrentEntry);
        Assert.False(first.IsDismissed);
        Assert.Equal(profile == RetainedViewLifecycleBehavior.Deactivate ? 1 : 0, first.Deactivations);
        Assert.Equal(profile == RetainedViewLifecycleBehavior.LegacyAfterDismissed ? 1 : 0, first.Dismissals);
        Assert.False(menu.MenuItems[0].IsSelected); Assert.True(menu.MenuItems[1].IsSelected);
        menu.MenuItems.RemoveAt(0); await Settle(host);
        Assert.True(first.IsDismissed);
        Assert.Single(host.GetItems(flyout));
        menu.MenuItems.Clear(); await Settle(host);
        Assert.Empty(host.GetItems(flyout));
        Assert.False(second.IsDismissed); // The same page remains the presented detail.
        flyout.Detail = new ContentPage(); await Settle(host);
        Assert.True(second.IsDismissed);
    }

    [Fact]
    public async Task Replacing_a_native_flyout_menu_releases_obsolete_owners()
    {
        var host = Host(); var first = new Probe(); var hidden = new Probe(); var menuModel = new Probe();
        var detail = new ProbePage(first);
        var menu = new Menu(menuModel) { Title = "Menu", MenuItems = [Item("first", detail), Item("hidden", new ProbePage(hidden))] };
        var flyout = new FlyoutPage { Flyout = menu, Detail = detail };
        Assert.True((await host.ReplaceRootAsync(new NavigationRequest<int>(0), () => new Plain(), _ => flyout, cancellationToken: Token)).IsSuccess);
        flyout.Flyout = new Menu(new Probe()) { Title = "Replacement", MenuItems = [Item("first", detail)] };
        await Settle(host);
        Assert.True(menuModel.IsDismissed); Assert.True(hidden.IsDismissed); Assert.False(first.IsDismissed);
        Assert.Single(host.GetItems(flyout));
        Assert.Same(first, host.CurrentEntry!.ViewModel);
    }

    [Fact]
    public async Task Reconciliation_cannot_be_awaited_from_its_own_activation_callback()
    {
        var (host, _, stack) = await StackAsync();
        Exception? error = null;
        var model = new Plain { Activate = async () => error = await Record.ExceptionAsync(host.ReconcileNativeAsync) };
        await stack.PushAsync(PageFor(model), false); await Settle(host);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(NavigationEntryState.Active, host.CurrentEntry!.State);
    }

    [Fact]
    public async Task Native_cleanup_continues_after_a_child_throws()
    {
        var (host, root, stack) = await StackAsync();
        var parent = new Plain(); var child = new Probe { Cleanup = () => throw new InvalidOperationException("child cleanup") };
        var page = PageFor(parent); page.Content = new ContentView { BindingContext = child };
        await stack.PushAsync(page, false); await Settle(host);
        var failures = new List<string>();
        void Report(Exception error, string operation) => failures.Add(operation);
        NavigationDiagnostics.Error += Report;
        try { await stack.PopAsync(false); await Settle(host); }
        finally { NavigationDiagnostics.Error -= Report; }
        Assert.Equal(1, child.Dismissals); Assert.Equal(1, parent.Dismissals);
        Assert.Contains("Native ownership cleanup", failures);
        Assert.Same(root, host.CurrentEntry!.ViewModel);
    }

    [Fact]
    public async Task Closing_during_an_unfinished_native_transition_releases_the_completion_waiter()
    {
        var (host, root, stack) = await StackAsync();
        var model = new Plain(); await stack.PushAsync(PageFor(model), false); await Settle(host);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = (INavigationPageController)stack;
        void Pause(object? sender, NavigationRequestedEventArgs args) => args.Task = pending.Task;
        controller.PopRequested += Pause;
        var pop = controller.PopAsyncInner(true, false);
        var completing = host.NativeNavigationCompletion;
        try { await Await(host.DisposeAsync().AsTask()); await Await(completing); }
        finally { pending.TrySetResult(false); controller.PopRequested -= Pause; await AwaitCancelledRendererPop(pop); }
        Assert.Equal(1, model.Dismissals); Assert.Equal(1, root.Dismissals);
    }

    [Fact]
    public async Task Legacy_only_direct_remove_awaits_the_same_terminal_lifetime()
    {
        var stack = TestNavigationHandler.Create(new ContentPage());
        NativeNavigationObserver.Attach(stack);
        try
        {
            var model = new Probe(); var page = new ProbePage(model);
            await stack.PushAsync(page, false); await stack.PushAsync(new ContentPage(), false);
            stack.Navigation.RemovePage(page);
            await Await(NativeNavigationObserver.Completion(stack));
            Assert.True(model.IsDismissed); Assert.Equal(1, model.Dismissals);
        }
        finally { NativeNavigationObserver.Detach(stack); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_only_terminal_dismissal_releases_library_page_subscriptions_even_after_failure(bool fail)
    {
        var model = new Probe(); var page = new ProbePage(model);
        var service = new LegacyNavigationService(new Locator(_ => page));
        service.WireRoot(page, model);
        application.Add().Page = page;
        ((IPageController)page).SendAppearing();
        Assert.Equal(1, model.Appearances);
        if (fail) model.Cleanup = () => throw new InvalidOperationException("cleanup");
        _ = await Record.ExceptionAsync(model.DismissAsync);
        // Clearing the binding verifies that the library handler was detached, rather than
        // merely returning early after resolving an already terminal model.
        page.BindingContext = null; page.ExposeModel = false;
        var errors = new List<Exception>();
        void Report(Exception error, string operation) => errors.Add(error);
        NavigationDiagnostics.Error += Report;
        try { ((IPageController)page).SendDisappearing(); ((IPageController)page).SendAppearing(); }
        finally { NavigationDiagnostics.Error -= Report; }
        Assert.Empty(errors); Assert.Equal(1, model.Appearances); Assert.Equal(1, model.Dismissals);
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public async Task Native_back_finishes_async_cleanup_before_incoming_legacy_appearance(int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var root = new Probe(); var rootPage = new ProbePage(root);
        var host = Host(_ => rootPage, profile);
        var result = await host.ReplaceRootAsync<Probe>(new(null), true, cancellationToken: Token);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        var stack = (NavigationPage)result.Value!.Page; stack.Handler = new TestNavigationHandler();
        var top = new Plain(); var topPage = PageFor(top);
        await stack.PushAsync(topPage, false); await Settle(host);
        var appearances = root.Appearances;
        var entered = Signal(); var release = Signal();
        top.Cleanup = async () => { entered.SetResult(); await release.Task; };
        await stack.PopAsync(false);
        var completion = host.ReconcileNativeAsync();
        try
        {
            await Await(entered.Task);
            Assert.False(completion.IsCompleted);
            Assert.Equal(appearances, root.Appearances);
            Assert.Equal(NavigationEntryState.Inactive, result.Value.Entry.State);
        }
        finally { release.TrySetResult(); }
        await Await(completion);
        Assert.Equal(appearances + 1, root.Appearances);
        Assert.Equal(NavigationEntryState.Active, result.Value.Entry.State);
    }

    [Fact]
    public async Task Adoption_transfers_legacy_page_wiring_without_duplicate_lifecycle_callbacks()
    {
        var (host, _, stack) = await StackAsync();
        var model = new Probe(); var page = new ProbePage(model);
        new LegacyNavigationService(new Locator(_ => page)).WireRoot(page, model);
        await stack.PushAsync(page, false); await Settle(host);
        var before = model.Appearances;
        ((IPageController)page).SendDisappearing(); ((IPageController)page).SendAppearing();
        await Settle(host);
        Assert.Equal(before + 1, model.Appearances);
        Assert.Same(model, host.CurrentEntry!.ViewModel);
        await stack.PopAsync(false); await Settle(host);
        Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Invalid_native_adoption_and_window_teardown_cannot_dismiss_another_hosts_model()
    {
        var foreign = new Probe(); var first = Host(_ => new ProbePage(foreign));
        Assert.True((await first.ReplaceRootAsync<Probe>(new(null), cancellationToken: Token)).IsSuccess);
        var (second, _, stack) = await StackAsync();
        await stack.PushAsync(new ProbePage(foreign), false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Settle(second));
        await second.DisposeAsync();
        Assert.False(foreign.IsDismissed);
        Assert.Equal(NavigationEntryState.Active, first.CurrentEntry!.State);
    }

    private MauiNavigationHost Host(Func<Type, VisualElement>? create = null,
        RetainedViewLifecycleBehavior profile = RetainedViewLifecycleBehavior.Deactivate)
    {
        var host = new MauiNavigationHostFactory(new Locator(create ?? (_ => throw new InvalidOperationException("No registration"))),
            new() { RetainedViewLifecycleBehavior = profile }).ForWindow(application.Add());
        hosts.Add(host); return host;
    }
    private async Task<(MauiNavigationHost Host, Plain Root, NavigationPage Stack)> StackAsync()
    {
        var host = Host(); var root = new Plain();
        var result = await host.ReplaceRootAsync(new NavigationRequest<int>(0), () => root, PageFor, true, cancellationToken: Token);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        var stack = (NavigationPage)result.Value!.Page; stack.Handler = new TestNavigationHandler();
        return (host, root, stack);
    }
    private static FlyoutMenuItem Item(string id, Page page) => new(id, () => id, () => "", () => "", page, _ => false);
    private static ContentPage PageFor(Plain model) => new() { BindingContext = model, Content = new Label() };
    private static Task Settle(MauiNavigationHost host) => Await(host.ReconcileNativeAsync());
    private static async Task AwaitCancelledRendererPop(Task pop)
    {
        // MAUI's headless target sends the real NavigatedFrom event, then attempts
        // platform-only unload wiring for a cancelled page that remains in its window.
        // Device gesture/unload behavior is a separate platform validation gate.
        var error = await Record.ExceptionAsync(() => Await(pop));
        if (error != null)
        {
            Assert.IsType<NotImplementedException>(error);
            Assert.Contains("Microsoft.Maui.Platform.ViewExtensions.OnUnloaded", error.StackTrace);
        }
    }
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class TestApplication : Application
    {
        internal Window Add() => (Window)((IApplication)this).CreateWindow(null);
        protected override Window CreateWindow(IActivationState? state) => new(new ContentPage());
    }
    private sealed class Plain : INavigationAware, INavigationInitializable<int>
    {
        internal int Activations, Deactivations, Dismissals;
        internal Func<Task>? Activate, Cleanup;
        public Task InitializeAsync(int parameter, CancellationToken token) => Task.CompletedTask;
        public Task ActivateAsync(CancellationToken token) { Activations++; return Activate?.Invoke() ?? Task.CompletedTask; }
        public Task DeactivateAsync() { Deactivations++; return Task.CompletedTask; }
        public Task DismissAsync(DismissalReason reason) { Dismissals++; return Cleanup?.Invoke() ?? Task.CompletedTask; }
    }
    private sealed class Probe : ViewModelBase
    {
        internal int Appearances, Deactivations, Dismissals;
        internal Func<Task>? Cleanup;
        public override Task Appearing() { Appearances++; return Task.CompletedTask; }
        public override Task Deactivated() { Deactivations++; return Task.CompletedTask; }
        public override Task AfterDismissed() { Dismissals++; return Cleanup?.Invoke() ?? Task.CompletedTask; }
    }
    private sealed class ProbePage(Probe model) : ContentPage, IHasVM
    {
        internal bool ExposeModel = true;
        public ViewModelBase ViewModel => ExposeModel ? model : throw new InvalidOperationException("Released page was queried");
    }
    private sealed class Menu(Probe model) : FlyoutViewFlyoutBase<Probe>(model);
    private sealed class Locator(Func<Type, VisualElement> create) : IViewLocator
    {
        public void Initialize(Dictionary<Type, Type> pairs) { }
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => create(typeof(T));
        public VisualElement CreateAndBindVEFor(Type type) => create(type);
        public Type FindVEForViewModel(Type type) => typeof(ProbePage);
        public Type FindViewModelForVE(Type type) => typeof(Probe);
    }
}
