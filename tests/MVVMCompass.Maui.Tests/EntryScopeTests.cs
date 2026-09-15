using CommunityToolkit.Maui.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class EntryScopeTests : IAsyncDisposable
{
    private readonly Application? previous = Application.Current;
    private readonly TestApplication application = new();
    private readonly List<MauiNavigationHost> hosts = [];
    private readonly List<ServiceProvider> providers = [];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var host in hosts) await host.DisposeAsync();
            foreach (var provider in providers) await provider.DisposeAsync();
        }
        finally { Application.Current = previous; }
    }

    private (ServiceProvider Provider, ViewLocator Locator, State State) Services(bool scoped = true, Action<IServiceCollection>? configure = null)
    {
        var state = new State(); var builder = MauiApp.CreateBuilder();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton<SingletonResource>();
        builder.Services.AddScoped<Resource>();
        builder.Services.AddTransient<TransientResource>();
        builder.Services.AddTransient<Plain>();
        configure?.Invoke(builder.Services);
        builder.UseMVVMCompass(pairs =>
        {
            pairs.Add<Model, ProbePage>(); pairs.Add<PopupModel, ProbePopup>(); pairs.Add<TabsModel, Tabs>();
        }, new() { UseEntryScopes = scoped });
        var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = scoped });
        providers.Add(provider);
        var locator = (ViewLocator)provider.GetRequiredService<IViewLocator>();
        provider.GetRequiredService<ILegacyNavigationService>().Initialize(provider.GetRequiredService<NavigationRegistrationBuilder>().Freeze());
        return (provider, locator, state);
    }
    private MauiNavigationHost Host(ServiceProvider provider)
    {
        var host = provider.GetRequiredService<MauiNavigationHostFactory>().ForWindow(application.Add());
        hosts.Add(host); return host;
    }
    private static T Success<T>(NavigationOutcome<T> result) where T : class?
    { Assert.True(result.IsSuccess, result.Error?.ToString()); return result.Value!; }

    [Fact]
    public async Task A_destination_shares_one_scope_between_view_model_constructor_and_later_service_lookup()
    {
        var (_, locator, _) = Services();
        var first = (ProbePage)await locator.CreateAndBindVEForAsync(typeof(Model), Token);
        var second = (ProbePage)await locator.CreateAndBindVEForAsync(typeof(Model), Token);
        Assert.Same(first.ViewModel.Resource, first.ConstructorResource);
        await Task.Yield();
        Assert.Same(first.ConstructorResource, first.GetService<Resource>());
        Assert.NotSame(first.ConstructorResource, second.ConstructorResource);
        await first.ViewModel.DismissAsync();
        Assert.Equal(1, first.ConstructorResource!.Disposals);
        Assert.Equal(0, second.ConstructorResource!.Disposals);
        Assert.Throws<ObjectDisposedException>(() => first.GetService<Resource>());
        await second.ViewModel.DismissAsync();
    }

    [Fact]
    public async Task Opt_out_and_the_preserved_synchronous_helper_keep_provider_ownership()
    {
        var (provider, locator, _) = Services(false);
        var first = (ProbePage)await locator.CreateAndBindVEForAsync(typeof(Model), Token);
        var second = (ProbePage)locator.CreateAndBindVEFor<Model>();
        Assert.Same(first.ViewModel.Resource, second.ViewModel.Resource);
        await first.ViewModel.DismissAsync(); await second.ViewModel.DismissAsync();
        Assert.Equal(0, first.ViewModel.Resource.Disposals);
        await provider.DisposeAsync(); Assert.Equal(1, first.ViewModel.Resource.Disposals);
    }

    [Fact]
    public async Task Application_factories_are_used_inside_each_scope()
    {
        var calls = 0;
        var (_, locator, _) = Services(configure: services => services.AddTransient<Model>(provider =>
        { calls++; return new(provider.GetRequiredService<Resource>(), provider.GetRequiredService<State>()); }));
        var first = (ProbePage)await locator.CreateAndBindVEForAsync(typeof(Model), Token);
        var second = (ProbePage)await locator.CreateAndBindVEForAsync(typeof(Model), Token);
        Assert.Equal(2, calls); Assert.NotSame(first.ViewModel, second.ViewModel);
        await first.ViewModel.DismissAsync(); await second.ViewModel.DismissAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_model_or_view_construction_awaits_asynchronous_scope_cleanup(bool model)
    {
        var (_, locator, state) = Services(); state.FailModel = model; state.FailPage = !model;
        var entered = Signal(); var release = Signal();
        state.Release = async () => { entered.TrySetResult(); await release.Task; };
        var pending = locator.CreateAndBindVEForAsync(typeof(Model), Token);
        try { await entered.Task.WaitAsync(Token); Assert.False(pending.IsCompleted); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal(1, Assert.Single(state.Resources).Disposals);
        Assert.True(Assert.Single(state.Models).IsDismissed);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("push")]
    [InlineData("window")]
    public async Task Legacy_parameter_failure_releases_the_candidate_scope(string operation)
    {
        var (provider, _, state) = Services();
        var service = provider.GetRequiredService<ILegacyNavigationService>();
        application.Add(TestNavigationHandler.Create(new ContentPage())); state.FailParameters = true;
        Func<Task> action = operation switch
        {
            "root" => () => service.PresentAsMainPage<Model>(new()),
            "push" => () => service.NavigateTo<Model>(new()),
            _ => () => service.OpenNewWindow<Model>(parameters: new())
        };
        await Assert.ThrowsAsync<InvalidOperationException>(action);
        Assert.Equal(1, Assert.Single(state.Resources).Disposals);
        Assert.Equal(DismissalReason.PreparationFailed, Assert.Single(state.Models).Lifetime.Reason);
    }

    [Fact]
    public async Task Cancellation_during_initialization_waits_for_scope_disposal_and_keeps_the_old_root()
    {
        var (provider, _, state) = Services(); var host = Host(provider); var old = host.Window.Page;
        var started = Signal(); var releasing = Signal(); var released = Signal();
        state.Initialize = async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        state.Release = async () => { releasing.SetResult(); await released.Task; };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = host.ReplaceRootAsync<Model, int>(new(0), cancellationToken: cancel.Token);
        await started.Task.WaitAsync(Token); cancel.Cancel();
        try { await releasing.Task.WaitAsync(Token); Assert.False(pending.IsCompleted); Assert.Same(old, host.Window.Page); }
        finally { released.SetResult(); }
        Assert.Equal(NavigationStatus.Cancelled, (await pending.WaitAsync(Token)).Status);
        Assert.Equal(1, Assert.Single(state.Resources).Disposals);
    }

    [Fact]
    public async Task Pre_cancelled_resolution_creates_no_scope_services()
    {
        var (_, locator, state) = Services(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locator.CreateAndBindVEForAsync(typeof(Model), cancel.Token));
        Assert.Empty(state.Resources); Assert.Empty(state.Models);
    }

    [Fact]
    public async Task A_live_singleton_destination_cannot_be_reused_or_cleaned_as_a_failed_candidate()
    {
        // A caller-supplied singleton is preserved by registration, but cannot become two entries.
        var state = new State(); var resource = new Resource(state, new SingletonResource()); var model = new Model(resource, state);
        var (_, locator, _) = Services(configure: services => services.AddSingleton(model));
        var first = (ProbePage)await locator.CreateAndBindVEForAsync(typeof(Model), Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => locator.CreateAndBindVEForAsync(typeof(Model), Token));
        Assert.False(model.IsDismissed); Assert.Equal(0, resource.Disposals);
        await first.ViewModel.DismissAsync(); Assert.Equal(0, resource.Disposals);
        await resource.DisposeAsync();
    }

    [Fact]
    public async Task Singleton_dependencies_remain_root_owned_and_disposable_transients_are_scope_owned()
    {
        var (provider, locator, state) = Services();
        var page = (ProbePage)await locator.CreateAndBindVEForAsync(typeof(Model), Token);
        var transient = page.GetService<TransientResource>();
        var singleton = provider.GetRequiredService<SingletonResource>();
        await page.ViewModel.DismissAsync();
        Assert.Equal(1, transient.Disposals); Assert.Equal(0, singleton.Disposals);
        Assert.Equal(["model", "transient", "scope"], state.Order);
        await provider.DisposeAsync(); Assert.Equal(1, singleton.Disposals);
    }

    [Fact]
    public async Task A_callback_failure_still_awaits_scope_cleanup_and_late_explicit_resources_run_before_it()
    {
        var (_, locator, state) = Services(); var page = (ProbePage)await locator.CreateAndBindVEForAsync(typeof(Model), Token);
        page.ViewModel.Cleanup = () => throw new InvalidOperationException("model cleanup");
        page.ViewModel.Ownership.RegisterCleanup(() => { state.Order.Add("extra"); Assert.Equal(0, page.ViewModel.Resource.Disposals); return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => page.ViewModel.DismissAsync());
        Assert.Equal(["model", "extra", "scope"], state.Order);
    }

    [Fact]
    public async Task Declared_layout_descendants_and_nonvisual_children_finish_before_the_parent_scope()
    {
        var (provider, _, state) = Services(); var host = Host(provider);
        var child = new ViewModelProbe(() => { state.Order.Add("visual child"); return Task.CompletedTask; });
        state.Build = page => page.Content = new ScrollView { Content = new VerticalStackLayout
        { Children = { new Border { Content = new ContentView { BindingContext = child } } } } };
        var root = Success(await host.ReplaceRootAsync<Model>(new(null), cancellationToken: Token));
        var nonvisual = new NavigationLifetime(() => { Assert.True(root.Entry.ViewModel.IsHostReplaced); state.Order.Add("nonvisual child"); return Task.CompletedTask; });
        root.Entry.Ownership.Adopt(nonvisual.Ownership);
        Assert.Same(root.Entry.Ownership, child.Ownership.Parent);
        Assert.Equal(3, Assert.Single(host.OwnershipRoots).Snapshot().Count);
        state.Build = null;
        Success(await host.ReplaceRootAsync<Model>(new(null), cancellationToken: Token));
        Assert.Equal(["visual child", "nonvisual child", "model", "scope"], state.Order);
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public async Task Tab_selection_retains_independent_scopes_until_native_removal_or_window_cleanup(int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var (provider, _, state) = Services(); provider.GetRequiredService<NavigationOptions>().RetainedViewLifecycleBehavior = profile;
        var host = Host(provider); var root = Success(await host.ReplaceRootAsync<TabsModel>(new(null), cancellationToken: Token));
        var tabs = (Tabs)root.Content; var first = (ProbePage)tabs.Children[0]; var second = (ProbePage)tabs.Children[1];
        Assert.NotSame(first.ViewModel.Resource, second.ViewModel.Resource);
        Assert.Same(root.Entry.ViewModel, first.ViewModel.ConstructorParent);
        Success(await host.SelectTabAsync(tabs, new(1), Token));
        Assert.All(state.Resources, resource => Assert.Equal(0, resource.Disposals));
        tabs.Children.Remove(first); await host.ReconcileNativeAsync().WaitAsync(Token);
        Assert.Equal(1, first.ViewModel.Resource.Disposals); Assert.Equal(0, second.ViewModel.Resource.Disposals);
        await host.DisposeAsync(); Assert.All(state.Resources, resource => Assert.Equal(1, resource.Disposals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stack_and_modal_entries_keep_the_root_scope_live_and_release_on_back(bool modal)
    {
        var (provider, _, _) = Services(); var host = Host(provider);
        var root = Success(await host.ReplaceRootAsync<Model>(new(null), true, Token));
        ((NavigationPage)root.Page).Handler = new TestNavigationHandler();
        var next = Success(modal ? await host.OpenModalAsync<Model>(new(null), animated: false, cancellationToken: Token)
            : await host.PushAsync<Model>(new(null), animated: false, cancellationToken: Token));
        Assert.NotSame(root.Entry.ViewModel.Resource, next.Entry.ViewModel.Resource);
        Assert.Equal(0, root.Entry.ViewModel.Resource.Disposals);
        Success(await host.BackAsync(animated: false, cancellationToken: Token));
        Assert.Equal(1, next.Entry.ViewModel.Resource.Disposals); Assert.Equal(0, root.Entry.ViewModel.Resource.Disposals);
    }

    [Fact]
    public async Task Popup_results_wait_for_their_scope_after_native_close()
    {
        var (provider, _, state) = Services(); var host = Host(provider);
        Success(await host.ReplaceRootAsync<Model>(new(null), cancellationToken: Token));
        var opened = Signal(); state.PopupOpened = opened;
        var showing = host.DisplayPopupAsync<PopupModel, string>(cancellationToken: Token);
        await opened.Task.WaitAsync(Token); var popup = state.Popup!;
        var releasing = Signal(); var release = Signal(); state.Release = async () => { releasing.SetResult(); await release.Task; };
        var closing = popup.CloseAsync("scoped", Token);
        try { await releasing.Task.WaitAsync(Token); Assert.False(showing.IsCompleted); Assert.False(closing.IsCompleted); }
        finally { release.SetResult(); }
        await closing.WaitAsync(Token); Assert.Equal("scoped", (await showing.WaitAsync(Token)).Result);
        Assert.Equal(1, popup.ViewModel.Resource.Disposals); state.Release = null;
    }

    [Theory]
    [InlineData("root")]
    [InlineData("push")]
    [InlineData("modal")]
    public async Task Ordinary_factories_share_an_owned_scope_and_cleanup_after_the_model(string operation)
    {
        var (provider, _, state) = Services(); var host = Host(provider);
        var initial = Success(await host.ReplaceRootAsync<Model>(new(null), true, Token));
        ((NavigationPage)initial.Page).Handler = new TestNavigationHandler();
        Plain? model = null;
        Plain Create(IServiceProvider services) => model = services.GetRequiredService<Plain>();
        Page Page(IServiceProvider services, Plain plain) { Assert.Same(plain.Resource, services.GetRequiredService<Resource>()); return new ContentPage(); }
        if (operation == "root") Success(await host.ReplaceScopedRootAsync(new NavigationRequest<int>(42), Create, Page, cancellationToken: Token));
        else if (operation == "push") Success(await host.PushScopedAsync(new NavigationRequest<int>(42), Create, Page, false, Token));
        else Success(await host.OpenScopedModalAsync(new NavigationRequest<int>(42), Create, Page, animated: false, cancellationToken: Token));
        Assert.Equal(42, model!.Parameter); Assert.Equal(0, model.Resource.Disposals);
        state.Order.Clear(); await host.DisposeAsync();
        Assert.True(state.Order.IndexOf("plain") < state.Order.IndexOf("scope")); Assert.Equal(1, model.Resource.Disposals);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("page")]
    [InlineData("initialization")]
    public async Task Ordinary_factory_failures_release_the_scope_at_every_preparation_stage(string stage)
    {
        var (provider, _, state) = Services(); var host = Host(provider);
        Plain Create(IServiceProvider services)
        {
            var model = services.GetRequiredService<Plain>();
            if (stage == "model") throw new InvalidOperationException("factory");
            model.FailInitialize = stage == "initialization"; return model;
        }
        var result = await host.ReplaceScopedRootAsync(new NavigationRequest<int>(0), Create,
            (_, _) => stage == "page" ? throw new InvalidOperationException("page") : new ContentPage(), cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, result.Status); Assert.False(result.HasCommitted);
        Assert.Equal(1, Assert.Single(state.Resources).Disposals);
        if (stage != "model") Assert.Equal(["plain", "scope"], state.Order);
    }

    [Fact]
    public async Task Async_failed_resolution_restores_the_constructor_parent_before_returning_to_its_caller()
    {
        var (_, locator, state) = Services(); state.FailPage = true;
        var releasing = Signal(); var release = Signal(); state.Release = async () => { releasing.SetResult(); await release.Task; };
        var parent = new ViewModelProbe(() => Task.CompletedTask);
        var pending = NavigationViewFactory.CreateAsync(locator, typeof(Model), parent);
        try
        {
            await releasing.Task.WaitAsync(Token);
            Assert.Null(new ViewModelProbe(() => Task.CompletedTask).ParentViewModel);
            Assert.Same(parent, Assert.Single(state.Models).ConstructorParent);
        }
        finally { release.SetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        await parent.DismissAsync();
    }

    [Fact]
    public async Task Legacy_live_composition_failure_releases_only_new_scopes_and_restores_tabs()
    {
        var (provider, _, state) = Services(); var window = application.Add();
        await provider.GetRequiredService<ILegacyNavigationService>().PresentAsMainPage<TabsModel>();
        var tabs = Assert.IsType<Tabs>(window.Page); var original = tabs.Children.ToArray(); var oldResources = state.Resources.ToArray();
        state.FailParameters = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((TabsModel)tabs.ViewModel).AppendFailing());
        Assert.Equal(original, tabs.Children);
        Assert.All(oldResources, resource => Assert.Equal(0, resource.Disposals));
        Assert.Equal(1, state.Resources.Last().Disposals);
        Assert.Equal(2, tabs.ViewModel.Ownership.Children.Count);
        await tabs.ViewModel.DismissAsync();
    }

    [Fact]
    public async Task A_nonvisual_owned_destination_cannot_be_adopted_by_another_scope()
    {
        var (provider, _, state) = Services(); var host = Host(provider);
        var root = Success(await host.ReplaceRootAsync<Model>(new(null), cancellationToken: Token));
        var detached = new ViewModelProbe(() => Task.CompletedTask);
        root.Entry.Ownership.Adopt(detached.Ownership);
        var registration = new ServiceCollection(); registration.AddSingleton<ContentPage>(_ => new ContentPage { BindingContext = detached });
        await using var service = registration.BuildServiceProvider();
        var locator = new ViewLocator(service, new() { UseEntryScopes = true }); locator.Initialize(new() { [typeof(ViewModelProbe)] = typeof(ContentPage) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => locator.CreateAndBindVEForAsync(typeof(ViewModelProbe), Token));
        Assert.False(detached.IsDismissed); Assert.Same(root.Entry.Ownership, detached.Ownership.Parent);
    }

    [Fact]
    public async Task Scope_disposal_failure_is_shared_and_does_not_skip_other_container_resources()
    {
        var (_, locator, state) = Services(); var page = (ProbePage)await locator.CreateAndBindVEForAsync(typeof(Model), Token);
        var transient = page.GetService<TransientResource>();
        state.Release = () => throw new InvalidOperationException("scope disposal");
        var first = page.ViewModel.DismissAsync(); var second = page.ViewModel.DismissAsync();
        Assert.Same(first, second);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.Equal(1, transient.Disposals); Assert.Equal(["model", "transient"], state.Order);
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public async Task Extracted_custom_tab_pages_keep_their_scope_and_retained_lifetime(int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var services = new ServiceCollection();
        services.AddSingleton(new State()); services.AddSingleton(new NavigationOptions { UseEntryScopes = true, RetainedViewLifecycleBehavior = profile });
        services.AddSingleton<SingletonResource>(); services.AddScoped<Resource>(); services.AddTransient<TabsModel>();
        services.AddTransient<Model>(); services.AddTransient<ProbePage>(); services.AddTransient<CustomTabs>();
        await using var customProvider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var customLocator = new ViewLocator(customProvider, customProvider.GetRequiredService<NavigationOptions>());
        customLocator.Initialize(new() { [typeof(TabsModel)] = typeof(CustomTabs), [typeof(Model)] = typeof(ProbePage) });
        await using var host = new MauiNavigationHostFactory(customLocator, customProvider.GetRequiredService<NavigationOptions>()).ForWindow(application.Add());
        var root = Success(await host.ReplaceRootAsync<TabsModel>(new(null), cancellationToken: Token)); var tabs = (CustomTabs)root.Content;
        var first = (ProbePage)tabs.Children[0].View;
        Assert.Same(root.Entry.ViewModel.Resource, tabs.GetService<Resource>());
        Assert.Same(first.ViewModel.Resource, first.GetService<Resource>());
        Assert.True(await ((ICustomTabbedViewBase)tabs).SwitchToAsync(1)); Assert.Equal(0, first.ViewModel.Resource.Disposals);
        Assert.Same(root.Entry.Ownership, first.ViewModel.Ownership.Parent);
        await host.DisposeAsync(); Assert.Equal(1, first.ViewModel.Resource.Disposals);
    }

    public sealed class State
    {
        public List<Resource> Resources = []; public List<Model> Models = []; public List<string> Order = [];
        public bool FailModel, FailPage, FailParameters;
        public Func<CancellationToken, Task>? Initialize; public Func<Task>? Release; internal Action<ProbePage>? Build;
        public TaskCompletionSource? PopupOpened; public ProbePopup? Popup;
    }
    public sealed class SingletonResource : IDisposable { public int Disposals; public void Dispose() => Disposals++; }
    public sealed class Resource : IAsyncDisposable
    {
        private readonly State state;
        public Resource(State state, SingletonResource singleton) { this.state = state; state.Resources.Add(this); Singleton = singleton; }
        public SingletonResource Singleton { get; } public int Disposals;
        public async ValueTask DisposeAsync() { if (state.Release != null) await state.Release(); state.Order.Add("scope"); Disposals++; }
    }
    public sealed class TransientResource(State state) : IDisposable
    { public int Disposals; public void Dispose() { state.Order.Add("transient"); Disposals++; } }
    public class Model : ViewModelBase, INavigationInitializable<int>
    {
        protected readonly State State;
        public Model(Resource resource, State state)
        {
            Resource = resource; State = state; state.Models.Add(this); ConstructorParent = ParentViewModel;
            if (state.FailModel) throw new InvalidOperationException("model constructor");
        }
        public Resource Resource { get; } public ViewModelBase? ConstructorParent { get; } public Func<Task>? Cleanup;
        public Task InitializeAsync(int parameter, CancellationToken cancellationToken) => State.Initialize?.Invoke(cancellationToken) ?? Task.CompletedTask;
        public override Task GetParameters(Dictionary<string, object> parameters) => State.FailParameters
            ? throw new InvalidOperationException("parameters") : Task.CompletedTask;
        public override async Task AfterDismissed() { State.Order.Add("model"); if (Cleanup != null) await Cleanup(); }
    }
    internal sealed class ProbePage : LegacyViewBase<Model>, IViewContentProvider
    {
        public ProbePage(Model model, State state, NavigationOptions options) : base(model)
        {
            ConstructorResource = options.UseEntryScopes ? GetService<Resource>() : null;
            if (state.FailPage) throw new InvalidOperationException("page constructor"); Content = new Label(); state.Build?.Invoke(this);
        }
        public Resource? ConstructorResource { get; }
        public IView? ViewContent { get => Content; set => Content = (View?)value; }
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => false;
        protected override IDisposable ShowLoading(LoadingType loadingType) => new Empty();
        protected override void HideLoading() { }
    }
    public sealed class TabsModel(Resource resource, State state) : Model(resource, state)
    {
        public override Task BeforeFirstShown() => AddTabbedViewModels(new(new Type[] { typeof(Model), typeof(Model) }, this));
        public Task AppendFailing() => AddTabbedViewModels(new(new[] { new TabModel(typeof(Model), parameters: new()) }, this));
    }
    public sealed class Tabs : TabbedPage, IHasVM
    { public Tabs(TabsModel model) { ViewModel = model; BindingContext = model; } public ViewModelBase ViewModel { get; } }
    internal sealed class CustomTabs(TabsModel model) : CustomTabbedViewBase<TabsModel>(model)
    {
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => false;
        protected override IDisposable ShowLoading(LoadingType type) => new Empty();
        protected override void HideLoading() { }
    }
    public sealed class PopupModel(Resource resource, State state) : Model(resource, state);
    public sealed class ProbePopup : PopupViewBase<PopupModel, string>
    {
        public ProbePopup(PopupModel model, State state) : base(model)
        { state.Popup = this; Opened += (_, _) => state.PopupOpened?.TrySetResult(); }
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => false;
        protected override IDisposable ShowLoading(LoadingType loadingType) => new Empty();
        protected override void HideLoading() { }
    }
    public sealed class Plain(Resource resource, State state) : INavigationInitializable<int>, INavigationAware
    {
        public Resource Resource => resource; public int Parameter; public bool FailInitialize;
        public Task InitializeAsync(int parameter, CancellationToken cancellationToken)
        { Parameter = parameter; if (FailInitialize) throw new InvalidOperationException("initialization"); return Task.CompletedTask; }
        public Task DismissAsync(DismissalReason reason) { state.Order.Add("plain"); return Task.CompletedTask; }
    }
    private sealed class ViewModelProbe(Func<Task> cleanup) : ViewModelBase { public override Task AfterDismissed() => cleanup(); }
    private sealed class Empty : IDisposable { public void Dispose() { } }
    private sealed class TestApplication : Application
    {
        private Window? next;
        protected override Window CreateWindow(IActivationState? activationState) => next!;
        internal Window Add(Page? page = null) { var window = new Window(page ?? new ContentPage()); next = window; ((IApplication)this).CreateWindow(null); next = null; return window; }
    }
}
