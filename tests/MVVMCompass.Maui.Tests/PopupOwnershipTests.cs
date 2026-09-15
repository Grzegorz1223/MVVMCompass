using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class PopupOwnershipTests : IAsyncDisposable
{
    private readonly Application? previous = Application.Current;
    private readonly TestApplication application = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var window in application.Windows.ToArray())
                while (window.Navigation.ModalStack.Count != 0) await window.Navigation.PopModalAsync(false);
        }
        finally { Application.Current = previous; }
    }

    [Theory]
    [InlineData("value")]
    [InlineData(null)]
    public async Task Typed_results_and_null_wait_for_terminal_cleanup(string? value)
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        var showing = await OpenAsync(window, popup);
        var entered = Signal(); var release = Signal();
        model.Cleanup = async () => { entered.SetResult(); await release.Task; };
        var closing = popup.CloseAsync(value, Token);
        try
        {
            await Await(entered.Task);
            Assert.True(model.IsDismissed);
            Assert.False(showing.IsCompleted); Assert.False(closing.IsCompleted);
            Assert.Empty(window.Navigation.ModalStack);
        }
        finally { release.TrySetResult(); }
        await Await(closing); var result = await Await(showing);
        Assert.Equal(value, result.Result); Assert.False(result.WasDismissedByTappingOutsideOfPopup);
        Assert.Equal(DismissalReason.DialogClosed, result.Reason);
        Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Concurrent_close_calls_share_one_native_removal_and_the_first_result()
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        var showing = await OpenAsync(window, popup);
        var entered = Signal(); var release = Signal(); var popped = 0;
        window.ModalPopped += (_, _) => popped++;
        model.Cleanup = async () => { entered.SetResult(); await release.Task; };
        var first = popup.CloseAsync("first", Token); await Await(entered.Task);
        var second = popup.CloseAsync("second", Token);
        try { Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); }
        finally { release.TrySetResult(); }
        await Await(Task.WhenAll(first, second));
        Assert.Equal("first", (await Await(showing)).Result);
        Assert.Equal(1, popped); Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Native_modal_veto_leaves_the_popup_live_and_close_can_be_retried()
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        var showing = await OpenAsync(window, popup);
        void Veto(object? sender, ModalPoppingEventArgs args) => args.Cancel = true;
        window.ModalPopping += Veto;
        try
        {
            await Assert.ThrowsAsync<PopupCloseRejectedException>(() => Await(popup.CloseAsync("vetoed", Token)));
            Assert.False(model.IsDismissed); Assert.False(showing.IsCompleted); Assert.Single(window.Navigation.ModalStack);
        }
        finally { window.ModalPopping -= Veto; }
        await Await(popup.CloseAsync("retry", Token));
        Assert.Equal("retry", (await Await(showing)).Result); Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Cancellation_before_close_preserves_the_live_popup()
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        var showing = await OpenAsync(window, popup);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => popup.CloseAsync("unused", cancelled.Token));
        Assert.False(model.IsDismissed); Assert.False(showing.IsCompleted);
        await popup.CloseAsync("retained", Token); Assert.Equal("retained", (await Await(showing)).Result);
    }

    [Fact]
    public async Task Cancelling_the_result_wait_keeps_cleanup_owned_until_actual_close()
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var showing = await OpenAsync(window, popup, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(showing));
        Assert.False(model.IsDismissed); Assert.Single(window.Navigation.ModalStack);
        await Await(popup.CloseAsync("later", Token));
        Assert.Equal(1, model.Dismissals); Assert.Empty(PopupOwnership.ModelsFor(window.Page!));
    }

    [Fact]
    public async Task Direct_window_modal_removal_completes_the_result_and_cleanup()
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        var showing = await OpenAsync(window, popup);
        await window.Navigation.PopModalAsync(false);
        var result = await Await(showing);
        Assert.Equal(DismissalReason.Back, result.Reason); Assert.Null(result.Result);
        Assert.Equal(1, model.Dismissals); Assert.Empty(PopupOwnership.ModelsFor(window.Page!));
    }

    [Fact]
    public async Task Nested_popups_block_outer_close_and_complete_independently()
    {
        var window = application.Add(); var outerModel = new Probe(); var innerModel = new Probe();
        var outer = new ProbePopup(outerModel); var inner = new ProbePopup(innerModel);
        var first = await OpenAsync(window, outer); var second = await OpenAsync(window, inner);
        await Assert.ThrowsAsync<PopupBlockedException>(() => outer.CloseAsync("blocked", Token));
        Assert.False(outerModel.IsDismissed); Assert.False(innerModel.IsDismissed);
        await PopupOwnership.CloseTopAsync(window, Token); Assert.Null((await Await(second)).Result);
        Assert.False(first.IsCompleted); Assert.False(outerModel.IsDismissed);
        await outer.CloseAsync("outer", Token); Assert.Equal("outer", (await Await(first)).Result);
        Assert.Equal(1, innerModel.Dismissals); Assert.Equal(1, outerModel.Dismissals);
    }

    [Fact]
    public async Task Ordinary_covering_modals_are_not_popped_by_close_popup()
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        var showing = await OpenAsync(window, popup);
        var covering = new ContentPage(); await window.Navigation.PushModalAsync(covering, false);
        await Assert.ThrowsAsync<PopupBlockedException>(() => PopupOwnership.CloseTopAsync(window, Token));
        Assert.Same(covering, window.Navigation.ModalStack[^1]); Assert.False(model.IsDismissed);
        await window.Navigation.PopModalAsync(false); await PopupOwnership.CloseTopAsync(window, Token); await Await(showing);
        Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Child_cleanup_is_ordered_and_failures_do_not_strand_the_toolkit_result()
    {
        var window = application.Add(); var order = new List<string>(); var model = new Probe(); var child = new Probe();
        var popup = new ProbePopup(model) { Content = new ContentView { BindingContext = child } };
        var showing = await OpenAsync(window, popup);
        child.Cleanup = () => { Assert.True(model.IsDismissed); order.Add("child"); throw new InvalidOperationException("child cleanup"); };
        model.Cleanup = () => { order.Add("parent"); return Task.CompletedTask; };
        var closed = 0; popup.Closed += (_, _) => closed++;
        await Assert.ThrowsAsync<AggregateException>(() => Await(popup.CloseAsync("value", Token)));
        await Assert.ThrowsAsync<AggregateException>(() => Await(showing));
        Assert.Equal(new[] { "child", "parent" }, order); Assert.Equal(1, closed);
        // A second popup proves the Toolkit's global navigation semaphore was released.
        var next = new ProbePopup(new Probe()); var nextShowing = await OpenAsync(window, next);
        await next.CloseAsync("next", Token); Assert.Equal("next", (await Await(nextShowing)).Result);
    }

    [Fact]
    public async Task Cleanup_cannot_wait_for_its_own_close()
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        var showing = await OpenAsync(window, popup); Exception? error = null;
        model.Cleanup = async () => error = await Record.ExceptionAsync(() => popup.CloseAsync("recursive", Token));
        await Await(popup.CloseAsync("done", Token)); await Await(showing);
        Assert.IsType<InvalidOperationException>(error); Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Service_preparation_failure_releases_the_candidate_without_changing_origin_flags()
    {
        application.Add(); var caller = new Probe(); var candidate = new Probe { Before = () => throw new InvalidOperationException("preparation") };
        var service = new LegacyNavigationService(new Locator(_ => new ProbePopup(candidate)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisplayPopupWithResult<Probe, string?>(caller));
        Assert.Equal(1, candidate.Dismissals); Assert.Equal(DismissalReason.PreparationFailed, candidate.Lifetime.Reason);
        Assert.False(caller.IsComingFromPopup); Assert.False(caller.IsPopupOpen); Assert.False(caller.IsDismissed);
    }

    [Fact]
    public async Task A_window_change_during_preparation_abandons_the_candidate()
    {
        var window = application.Add(); var replacement = new ContentPage();
        var candidate = new Probe { Before = () => { window.Page = replacement; return Task.CompletedTask; } };
        var service = new LegacyNavigationService(new Locator(_ => new ProbePopup(candidate)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisplayPopup<Probe>(new Probe()));
        Assert.Equal(1, candidate.Dismissals); Assert.Same(replacement, window.Page); Assert.Empty(window.Navigation.ModalStack);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Window_host_teardown_awaits_popups_before_the_root(bool dispose)
    {
        var window = application.Add(); var popupModel = new Probe(); var popup = new ProbePopup(popupModel);
        await using var host = new MauiNavigationHostFactory(new Locator(_ => popup), new()).ForWindow(window);
        var rootModel = new Plain();
        Assert.True((await host.ReplaceRootAsync(new NavigationRequest<int>(0), () => rootModel, _ => new ContentPage(), cancellationToken: Token)).IsSuccess);
        var opened = Signal(); popup.Opened += (_, _) => opened.TrySetResult();
        var showing = host.DisplayPopupAsync<Probe, string?>(cancellationToken: Token); await Await(opened.Task);
        var entered = Signal(); var release = Signal();
        popupModel.Cleanup = async () => { Assert.True(host.CurrentRoot!.Entry.Lifetime.IsDismissed); entered.SetResult(); await release.Task; };
        Task replacing = dispose ? host.DisposeAsync().AsTask()
            : host.ReplaceRootAsync(new NavigationRequest<int>(0), () => new Plain(), _ => new ContentPage(), cancellationToken: Token);
        try { await Await(entered.Task); Assert.False(replacing.IsCompleted); Assert.Equal(0, rootModel.Dismissals); }
        finally { release.TrySetResult(); }
        await Await(replacing); var result = await Await(showing);
        Assert.Equal(dispose ? DismissalReason.Removed : DismissalReason.RootReplaced, result.Reason);
        Assert.Equal(1, popupModel.Dismissals); Assert.Equal(1, rootModel.Dismissals);
    }

    [Fact]
    public async Task Explicit_window_popup_helpers_do_not_redirect_to_the_first_window()
    {
        var first = application.Add(); var second = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        await using var host = new MauiNavigationHostFactory(new Locator(_ => popup), new()).ForWindow(second);
        var opened = Signal(); popup.Opened += (_, _) => opened.TrySetResult();
        var showing = host.DisplayPopupAsync<Probe, string?>(cancellationToken: Token); await Await(opened.Task);
        Assert.Empty(first.Navigation.ModalStack); Assert.Single(second.Navigation.ModalStack);
        await host.ClosePopupAsync(Token); Assert.Null((await Await(showing)).Result);
        Assert.Equal(1, model.Dismissals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delegated_notifications_balance_failed_open_and_failed_completion(bool failOpening)
    {
        var host = new ModalHost { FailOpening = failOpening, FailClosing = true };
        var invoked = false;
        var error = await Record.ExceptionAsync(() => HandledPageDialogs.RunAsync<int>(host, () =>
        { invoked = true; throw new InvalidOperationException("presentation"); }));
        Assert.IsType<InvalidOperationException>(error); Assert.Equal(failOpening ? "opening" : "presentation", error.Message);
        Assert.Equal(!failOpening, invoked); Assert.Equal(1, host.Openings); Assert.Equal(1, host.Closings); Assert.Equal(0, host.Depth);
    }

    [Fact]
    public async Task Nested_delegated_dialogs_balance_each_independent_completion()
    {
        var host = new ModalHost(); var outer = new TaskCompletionSource<int>(); var inner = new TaskCompletionSource<int>();
        var first = HandledPageDialogs.RunAsync(host, () => outer.Task);
        var second = HandledPageDialogs.RunAsync(host, () => inner.Task);
        Assert.Equal(2, host.Depth); inner.SetResult(2); Assert.Equal(2, await Await(second)); Assert.Equal(1, host.Depth);
        outer.SetResult(1); Assert.Equal(1, await Await(first));
        Assert.Equal(0, host.Depth); Assert.Equal(2, host.Openings); Assert.Equal(2, host.Closings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Toolkit_back_dismissal_honors_the_outside_option(bool dismissible)
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model)
            { CanBeDismissedByTappingOutsideOfPopup = dismissible };
        var showing = await OpenAsync(window, popup);
        Assert.True(window.Navigation.ModalStack[^1].SendBackButtonPressed());
        if (!dismissible)
        {
            Assert.False(showing.IsCompleted); Assert.False(model.IsDismissed);
            await popup.CloseAsync("explicit", Token);
        }
        var result = await Await(showing);
        Assert.Equal(dismissible, result.WasDismissedByTappingOutsideOfPopup);
        Assert.Equal(dismissible ? null : "explicit", result.Result); Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Direct_toolkit_show_of_a_popup_base_still_awaits_cleanup()
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        var opened = Signal(); popup.Opened += (_, _) => opened.TrySetResult();
        var showing = window.Navigation.ShowPopupAsync<string?>(popup, token: Token); await Await(opened.Task);
        var entered = Signal(); var release = Signal();
        model.Cleanup = async () => { entered.SetResult(); await release.Task; };
        var closing = popup.CloseAsync("direct", Token);
        try { await Await(entered.Task); Assert.False(showing.IsCompleted); }
        finally { release.TrySetResult(); }
        await Await(closing); Assert.Equal("direct", (await Await(showing)).Result); Assert.Equal(1, model.Dismissals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task External_window_boundaries_complete_popup_waits(bool destroy)
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        var showing = await OpenAsync(window, popup);
        if (destroy) ((IWindow)window).Destroying(); else window.Page = new ContentPage();
        var result = await Await(showing);
        Assert.Equal(destroy ? DismissalReason.WindowClosed : DismissalReason.RootReplaced, result.Reason);
        Assert.Equal(1, model.Dismissals); Assert.Equal(!destroy, model.IsHostReplaced);
    }

    [Fact]
    public async Task Legacy_root_replacement_completes_nested_popups_before_parent_cleanup()
    {
        var window = application.Add(); var rootModel = new Probe(); window.Page!.BindingContext = rootModel;
        var outer = new ProbePopup(new Probe()); var inner = new ProbePopup(new Probe());
        var first = await OpenAsync(window, outer); var second = await OpenAsync(window, inner);
        var order = new List<string>();
        inner.ViewModel.Cleanup = () => { Assert.True(rootModel.IsDismissed); Assert.True(inner.ViewModel.IsHostReplaced); order.Add("inner"); throw new InvalidOperationException("cleanup"); };
        outer.ViewModel.Cleanup = () => { order.Add("outer"); return Task.CompletedTask; };
        rootModel.Cleanup = () => { order.Add("root"); return Task.CompletedTask; };
        var service = new LegacyNavigationService(new Locator(_ => new ProbePage(new Probe())));
        await Await(service.PresentAsMainPage<Probe>());
        await Assert.ThrowsAsync<AggregateException>(() => Await(second));
        Assert.Equal(DismissalReason.RootReplaced, (await Await(first)).Reason);
        Assert.Equal(new[] { "inner", "outer", "root" }, order); Assert.Empty(window.Navigation.ModalStack);
    }

    [Fact]
    public async Task A_popup_cannot_borrow_a_model_owned_by_a_window_entry()
    {
        var window = application.Add(); var root = new Probe();
        await using var host = new MauiNavigationHostFactory(new Locator(_ => new ProbePage(root)), new()).ForWindow(window);
        Assert.True((await host.ReplaceRootAsync<Probe>(new(null), cancellationToken: Token)).IsSuccess);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PopupOwnership.ShowAsync<string?>(window, new ProbePopup(root), cancellationToken: Token));
        Assert.False(root.IsDismissed); Assert.Empty(window.Navigation.ModalStack);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delegated_helpers_balance_only_after_popup_cleanup(bool fireAndReturn)
    {
        var window = application.Add(); var host = new ModalHost(); window.Page = host;
        var origin = new Probe(); var child = new DelegatedPage(origin) { Parent = host };
        var model = new Probe(); var popup = new Popup<int> { BindingContext = model };
        var opened = Signal(); popup.Opened += (_, _) => opened.TrySetResult();
        Task<IPopupResult<int>>? showing = null;
        if (fireAndReturn) child.ShowPopup(popup); else showing = child.ShowPopupAsync(popup);
        await Await(opened.Task); Assert.Equal(1, host.Depth); Assert.True(origin.IsPopupOpen);
        var entered = Signal(); var release = Signal();
        model.Cleanup = async () => { entered.SetResult(); await release.Task; };
        var closing = popup.CloseAsync(42, Token);
        try { await Await(entered.Task); Assert.Equal(0, host.Closings); Assert.False(showing?.IsCompleted ?? false); }
        finally { release.TrySetResult(); }
        await Await(closing);
        if (showing != null) Assert.Equal(42, (await Await(showing)).Result);
        await Await(host.Completed.Task);
        Assert.Equal(1, host.Openings); Assert.Equal(1, host.Closings); Assert.Equal(0, host.Depth);
        Assert.False(origin.IsDismissed); Assert.False(origin.IsPopupOpen); Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Outside_dismissal_returns_default_for_a_nonnullable_result()
    {
        var window = application.Add(); var popup = new Popup<int> { CanBeDismissedByTappingOutsideOfPopup = true };
        var opened = Signal(); popup.Opened += (_, _) => opened.TrySetResult();
        var showing = PopupOwnership.ShowAsync<int>(window, popup, cancellationToken: Token); await Await(opened.Task);
        window.Navigation.ModalStack[^1].SendBackButtonPressed();
        var result = await Await(showing); Assert.True(result.WasDismissedByTappingOutsideOfPopup); Assert.Equal(0, result.Result);
    }

    [Fact]
    public async Task A_popup_without_a_view_model_preserves_the_root_teardown_reason()
    {
        var window = application.Add();
        await using var host = new MauiNavigationHostFactory(new Locator(_ => new ProbePage(new Probe())), new()).ForWindow(window);
        Assert.True((await host.ReplaceRootAsync<Probe>(new(null), cancellationToken: Token)).IsSuccess);
        var popup = new Popup<int>(); var opened = Signal(); popup.Opened += (_, _) => opened.TrySetResult();
        var showing = PopupOwnership.ShowAsync<int>(window, popup, cancellationToken: Token); await Await(opened.Task);
        var replacement = await host.ReplaceRootAsync<Probe>(new(null), cancellationToken: Token);
        Assert.True(replacement.IsSuccess, $"{replacement.Status}: {replacement.Error}");
        Assert.Equal(DismissalReason.RootReplaced, (await Await(showing)).Reason);
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public async Task Flyout_popup_items_create_fresh_owners_when_reopened(int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var window = application.Add(); var rootModel = new FlyoutModelProbe(); var root = new FlyoutHost(rootModel);
        var models = new List<Probe>();
        var locator = new Locator(type => type == typeof(FlyoutModelProbe) ? root
            : type == typeof(MenuModel) ? new Menu(new MenuModel()) { Title = "Menu" }
            : type == typeof(PageModel) ? new ProbePage(new PageModel())
            : NewPopup());
        View NewPopup() { var model = new Probe(); models.Add(model); return new ProbePopup(model); }
        await using var host = new MauiNavigationHostFactory(locator, new() { RetainedViewLifecycleBehavior = profile }).ForWindow(window);
        rootModel.Before = rootModel.Compose;
        var replaced = await host.ReplaceRootAsync<FlyoutModelProbe>(new(null), cancellationToken: Token);
        Assert.True(replaced.IsSuccess, replaced.Error?.ToString());
        var menu = (Menu)root.Flyout; var item = menu.MenuItems[1];
        for (var i = 0; i < 2; i++)
        {
            await menu.SelectMenuItem(item, null);
            var popup = Assert.IsType<ProbePopup>(item.Content);
            Assert.Same(rootModel, models[i].ParentViewModel);
            await popup.CloseAsync("menu", Token); await Await(host.ReconcileNativeAsync());
            Assert.Equal(1, models[i].Dismissals); Assert.False(menu.ViewModel.IsDismissed);
        }
        Assert.Equal(2, models.Count); Assert.NotSame(models[0], models[1]);
        Assert.Same(models[1], host.GetItems(root)[1].Entry!.ViewModel);
    }

    private sealed class DelegatedPage(Probe model) : LegacyViewBase<Probe>(model)
    {
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => true;
        protected override IDisposable ShowLoading(LoadingType type) => throw new NotSupportedException();
        protected override void HideLoading() { }
    }
    private sealed class FlyoutModelProbe : Probe
    {
        internal Task Compose() => AddFlyoutViewModels(new([new(typeof(PageModel)), new(typeof(Probe))], this, typeof(MenuModel)));
    }
    private sealed class ProbePage(Probe model) : ContentPage, IHasVM { public ViewModelBase ViewModel { get; } = model; }
    private sealed class PageModel : Probe;
    private sealed class MenuModel : Probe;
    private sealed class Menu(MenuModel model) : FlyoutViewFlyoutBase<MenuModel>(model);
    private sealed class FlyoutHost(Probe model) : FlyoutPage, IHasVM { public ViewModelBase ViewModel { get; } = model; }

    private static async Task<Task<PopupNavigationResult<string?>>> OpenAsync(Window window, ProbePopup popup, CancellationToken? token = null)
    {
        var opened = Signal(); popup.Opened += (_, _) => opened.TrySetResult();
        var showing = PopupOwnership.ShowAsync<string?>(window, popup, cancellationToken: token ?? Token);
        await Task.WhenAny(opened.Task, showing).WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (showing.IsFaulted) await showing;
        await Await(opened.Task); return showing;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private static Task<T> Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private sealed class TestApplication : Application
    {
        internal Window Add() => (Window)((IApplication)this).CreateWindow(null);
        protected override Window CreateWindow(IActivationState? state) => new(new ContentPage());
    }
    private class Probe : ViewModelBase
    {
        internal int Dismissals;
        internal Func<Task>? Cleanup, Before;
        public override Task BeforeFirstShown() => Before?.Invoke() ?? Task.CompletedTask;
        public override Task AfterDismissed() { Dismissals++; return Cleanup?.Invoke() ?? Task.CompletedTask; }
    }
    private sealed class ProbePopup(Probe model) : PopupViewBase<Probe, string?>(model)
    {
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => true;
        protected override IDisposable ShowLoading(LoadingType type) => new Empty();
        protected override void HideLoading() { }
        private sealed class Empty : IDisposable { public void Dispose() { } }
    }
    private sealed class Plain : INavigationInitializable<int>, INavigationAware
    {
        internal int Dismissals;
        public Task InitializeAsync(int parameter, CancellationToken token) => Task.CompletedTask;
        public Task ActivateAsync(CancellationToken token) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public Task DismissAsync(DismissalReason reason) { Dismissals++; return Task.CompletedTask; }
    }
    private sealed class ModalHost : ContentPage, IHandledPageModalHost
    {
        internal int Openings, Closings, Depth;
        internal TaskCompletionSource Completed = Signal();
        internal bool FailOpening, FailClosing;
        public void OnHandledPageModalOpening() { Openings++; Depth++; if (FailOpening) throw new InvalidOperationException("opening"); }
        public void OnHandledPageModalClosed() { Closings++; Depth--; Completed.TrySetResult(); if (FailClosing) throw new InvalidOperationException("closing"); }
    }
    private sealed class Locator(Func<Type, VisualElement> create) : IViewLocator
    {
        public void Initialize(Dictionary<Type, Type> pairs) { }
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => create(typeof(T));
        public VisualElement CreateAndBindVEFor(Type type) => create(type);
        public Type FindVEForViewModel(Type type) => typeof(ProbePopup);
        public Type FindViewModelForVE(Type type) => typeof(Probe);
    }
}
