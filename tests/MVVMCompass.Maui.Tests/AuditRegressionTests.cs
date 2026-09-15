using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class AuditRegressionTests : IAsyncDisposable
{
    private readonly Application? previous = Application.Current;
    private readonly TestApplication application = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(3), Token);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_disposal_without_an_owned_root_awaits_nested_popup_cleanup_and_scopes(bool failCleanup)
    {
        var window = application.Add();
        var firstModel = new Probe(); var secondModel = new Probe();
        var first = new ProbePopup(firstModel); var second = new ProbePopup(secondModel);
        Queue<ProbePopup> popups = new([first, second]);
        var host = new MauiNavigationHostFactory(new Locator(_ => popups.Dequeue()), new()).ForWindow(window);
        await using var provider = new ServiceCollection().AddScoped<Resource>().BuildServiceProvider();
        var firstScope = new NavigationEntryScope(provider.GetRequiredService<IServiceScopeFactory>());
        var secondScope = new NavigationEntryScope(provider.GetRequiredService<IServiceScopeFactory>());
        var firstResource = firstScope.Services.GetRequiredService<Resource>();
        var secondResource = secondScope.Services.GetRequiredService<Resource>();
        firstScope.Bind(first, firstModel.Lifetime); secondScope.Bind(second, secondModel.Lifetime);
        var firstOpened = Signal(); var secondOpened = Signal();
        first.Opened += (_, _) => firstOpened.TrySetResult(); second.Opened += (_, _) => secondOpened.TrySetResult();
        var firstShowing = host.DisplayPopupAsync<Probe, string?>(cancellationToken: Token); await Await(firstOpened.Task);
        var secondShowing = host.DisplayPopupAsync<Probe, string?>(cancellationToken: Token); await Await(secondOpened.Task);
        var entered = Signal(); var release = Signal();
        secondModel.Cleanup = async () =>
        {
            Assert.True(firstModel.IsDismissed);
            entered.TrySetResult();
            await release.Task;
            if (failCleanup) throw new InvalidOperationException("popup cleanup");
        };
        var disposing = host.DisposeAsync().AsTask();
        try
        {
            await Await(entered.Task);
            Assert.False(disposing.IsCompleted);
            Assert.False(firstShowing.IsCompleted);
            Assert.False(secondShowing.IsCompleted);
            Assert.Equal(0, secondResource.Disposals);
        }
        finally { release.TrySetResult(); }
        await Await(disposing);
        Assert.True(host.IsClosed);
        Assert.Empty(window.Navigation.ModalStack);
        Assert.Equal(DismissalReason.Removed, (await firstShowing).Reason);
        if (failCleanup) await Assert.ThrowsAsync<AggregateException>(() => Await(secondShowing));
        else Assert.Equal(DismissalReason.Removed, (await secondShowing).Reason);
        Assert.Equal(1, firstModel.Dismissals); Assert.Equal(1, secondModel.Dismissals);
        Assert.Equal(1, firstResource.Disposals); Assert.Equal(1, secondResource.Disposals);
        Assert.Empty(host.OwnershipRoots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_disposal_cancels_and_awaits_popup_preparation_without_late_presentation(bool ownedRoot)
    {
        var window = application.Add(); var entered = Signal(); var release = Signal();
        var model = new Probe { Before = async () => { entered.TrySetResult(); await release.Task; } };
        var popup = new ProbePopup(model); var opens = 0;
        popup.Opened += (_, _) => opens++;
        var host = new MauiNavigationHostFactory(new Locator(_ => popup), new()).ForWindow(window);
        if (ownedRoot)
            Assert.True((await host.ReplaceRootAsync(new NavigationRequest<int>(0), () => new Initialized(), _ => new ContentPage(), cancellationToken: Token)).IsSuccess);
        var showing = host.DisplayPopupAsync<Probe, string?>(cancellationToken: Token);
        await Await(entered.Task);
        var disposing = host.DisposeAsync().AsTask();
        try { Assert.True(host.IsClosed); Assert.False(disposing.IsCompleted); }
        finally { release.TrySetResult(); }
        await Await(disposing);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Await(showing));
        Assert.Equal(0, opens);
        Assert.Empty(window.Navigation.ModalStack);
        Assert.True(model.IsDismissed);
        Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Popup_preparation_cannot_await_disposal_of_its_own_host()
    {
        var window = application.Add(); var model = new Probe(); var popup = new ProbePopup(model);
        var host = new MauiNavigationHostFactory(new Locator(_ => popup), new()).ForWindow(window);
        model.Before = () => host.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Await(host.DisplayPopupAsync<Probe, string?>(cancellationToken: Token)));
        Assert.False(host.IsClosed);
        Assert.True(model.IsDismissed);
        await host.DisposeAsync();
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate, false)]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate, true)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed, false)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed, true)]
    public async Task Successful_legacy_flyout_rebuild_releases_obsolete_owners_even_after_cleanup_failure(
        int profileValue, bool failCleanup)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        application.Add();
        var owner = new FlyoutOwner(); var root = new FlyoutRoot(owner);
        List<Probe> created = []; var resources = 0;
        var locator = new Locator(type =>
        {
            if (type == typeof(FlyoutOwner)) return root;
            Probe model = type == typeof(MenuModel) ? new MenuModel() : new Probe();
            model.Ownership.RegisterCleanup(() => { resources++; return Task.CompletedTask; }, runLast: true);
            created.Add(model);
            return model is MenuModel menu ? new MenuPage(menu) { Title = "Menu" } : new ChildPage(model);
        });
        var service = new LegacyNavigationService(locator, new() { RetainedViewLifecycleBehavior = profile });
        owner.Before = owner.Compose;
        await service.PresentAsMainPage<FlyoutOwner>();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var old = created.ToArray();
            if (failCleanup) old[^1].Cleanup = () => throw new InvalidOperationException("old menu cleanup");
            await owner.Compose();
            Assert.All(old, model => Assert.True(model.IsDismissed));
            Assert.All(old, model => Assert.Equal(1, model.Dismissals));
            Assert.Equal(old.Length, resources);
            Assert.Equal(2, owner.Ownership.Children.Count);
        }
        await owner.DismissAsync();
        Assert.Equal(created.Count, resources);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Task_helper_reports_immediate_and_async_errors_through_the_selected_route(bool asynchronous, bool explicitCallback)
    {
        var error = new InvalidOperationException("task failure");
        var observed = Signal(); var callbackCalls = 0; var diagnosticCalls = 0;
        void Diagnostic(Exception actual, string operation)
        {
            if (!ReferenceEquals(actual, error)) return;
            Assert.Contains("FireAndForget", operation);
            diagnosticCalls++; observed.TrySetResult();
        }
        NavigationDiagnostics.Error += Diagnostic;
        try
        {
            var pending = Signal();
            Task task = asynchronous ? pending.Task : Task.FromException(error);
            task.FireAndForget(exceptionCallBack: explicitCallback ? actual =>
            { Assert.Same(error, actual); callbackCalls++; observed.TrySetResult(); } : null);
            if (asynchronous) pending.SetException(error);
            await Await(observed.Task);
            Assert.Equal(explicitCallback ? 1 : 0, callbackCalls);
            Assert.Equal(explicitCallback ? 0 : 1, diagnosticCalls);
        }
        finally { NavigationDiagnostics.Error -= Diagnostic; }
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public async Task Guard_confirmation_on_the_owning_page_completes_before_navigation(int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var window = application.Add(); var model = new GuardModel(); var page = new GuardPage(model);
        var popupModel = new Probe(); var popup = new ProbePopup(popupModel);
        var host = new MauiNavigationHostFactory(new Locator(_ => page), new() { RetainedViewLifecycleBehavior = profile }).ForWindow(window);
        var root = await host.ReplaceRootAsync<GuardModel>(new(null), navigable: true, cancellationToken: Token);
        Assert.True(root.IsSuccess); ((NavigationPage)root.Value!.Page).Handler = new TestNavigationHandler();
        var opened = Signal(); popup.Opened += (_, _) => opened.TrySetResult();
        model.Guard = async () => (await page.ShowPopupAsync(popup)).Result == "yes";
        var pushing = host.PushAsync(new NavigationRequestOptions { Origin = root.Value.Entry },
            () => new object(), _ => new ContentPage(), animated: false, cancellationToken: Token);
        await Await(opened.Task);
        Assert.False(pushing.IsCompleted);
        await popup.CloseAsync("yes", Token);
        Assert.True((await pushing).IsSuccess);
        Assert.True(popupModel.IsDismissed);
        await host.DisposeAsync();
    }

    private sealed class GuardModel : Probe, INavigationGuard
    {
        internal Func<Task<bool>>? Guard;
        public Task<bool> CanNavigateAsync(CancellationToken cancellationToken) => Guard?.Invoke() ?? Task.FromResult(true);
    }
    private sealed class GuardPage(GuardModel model) : LegacyViewBase<GuardModel>(model)
    {
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => true;
        protected override IDisposable ShowLoading(LoadingType type) => new Empty();
        protected override void HideLoading() { }
    }

    private sealed class TestApplication : Application
    {
        internal Window Add() => (Window)((IApplication)this).CreateWindow(null);
        protected override Window CreateWindow(IActivationState? state) => new(new ContentPage());
    }
    private class Probe : ViewModelBase
    {
        internal Func<Task>? Before, Cleanup;
        internal int Dismissals;
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
    }
    private sealed class Empty : IDisposable { public void Dispose() { } }
    private sealed class Initialized : INavigationInitializable<int>
    { public Task InitializeAsync(int parameter, CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class Locator(Func<Type, VisualElement> create) : IViewLocator
    {
        public void Initialize(Dictionary<Type, Type> pairs) { }
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => create(typeof(T));
        public VisualElement CreateAndBindVEFor(Type type) => create(type);
        public Type FindVEForViewModel(Type type) => typeof(ProbePopup);
        public Type FindViewModelForVE(Type type) => typeof(Probe);
    }
    private sealed class FlyoutOwner : Probe
    { internal Task Compose() => AddFlyoutViewModels(new([new(typeof(Probe))], this, typeof(MenuModel))); }
    private sealed class MenuModel : Probe;
    private sealed class MenuPage(MenuModel model) : FlyoutViewFlyoutBase<MenuModel>(model);
    private sealed class FlyoutRoot(FlyoutOwner model) : FlyoutPage, IHasVM
    { public ViewModelBase ViewModel { get; } = model; }
    private sealed class ChildPage(Probe model) : ContentPage, IHasVM
    { public ViewModelBase ViewModel { get; } = model; }
    public sealed class Resource : IAsyncDisposable
    { public int Disposals; public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; } }
}
