using CommunityToolkit.Maui.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tab_views_are_available_at_first_selection_and_track_reset_removal_and_terminal_cleanup(bool eager)
    {
        Profile.Options = new() { Initialization = eager ? TabInitialization.Eager : TabInitialization.Lazy };
        var firstNotification = false;
        Profile.ViewCreated = view => view.CurrentTabChanged += (_, _) =>
        {
            if (firstNotification) return;
            firstNotification = true;
            Assert.NotNull(view.CurrentTab!.View);
            Assert.Same(view.CurrentTab.ViewModel, ((ViewBase)view.CurrentTab.View).ViewModel);
            if (eager) Assert.All(view.TabItems, item => Assert.NotNull(item.View));
        };
        var root = await Open<ProfileTabs>();
        var view = (ProfileTabsView)root.Current!.View;
        Assert.True(firstNotification);
        var first = view.TabItems[0]; var original = first.View;
        var changes = 0;
        first.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(NavigationItemContext.View)) changes++; };
        Success(await view.ViewModel.Navigation.Select("two", cancellationToken: Token));
        Success(await view.ViewModel.Navigation.Select("one", cancellationToken: Token));
        Assert.Same(original, first.View); Assert.Equal(0, changes);
        Success(await view.ViewModel.Navigation.Reset("one", Token));
        Assert.NotSame(original, first.View); Assert.Equal(1, changes);
        Success(await view.ViewModel.Navigation.Remove("one", "two", Token));
        Assert.Null(first.View); Assert.Null(first.ViewModel); Assert.Equal(2, changes);
        var remaining = view.TabItems.ToArray();
        Success(await factory.SetRoot<Leaf>(root.Window, cancellationToken: Token));
        Assert.All(remaining, item => Assert.Null(item.View));
    }

    [Fact]
    public async Task View_activation_waits_for_both_handlers_and_discards_obsolete_selections()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var parent = (ProfileTabsView)root.Current!.View;
        var first = (ViewBase)parent.TabItems[0].View!; var second = (ViewBase)parent.TabItems[1].View!;
        var events = new List<string>();
        first.NavigatedTo += (_, args) => events.Add("first:" + args.Kind);
        second.NavigatedTo += (_, args) =>
        { Assert.Same(second.ViewModel, parent.ViewModel.LastActive); events.Add("second:" + args.Kind); };
        first.Handler = new TestNavigationHandler();
        Assert.Empty(events);
        Success(await parent.ViewModel.Navigation.Select("two", cancellationToken: Token));
        root.Window.Handler = new DesktopWindowHandler();
        Assert.Empty(events);
        second.Handler = new TestNavigationHandler();
        Assert.Equal(["second:TabSelected"], events);
        Success(await parent.ViewModel.Navigation.Select("one", cancellationToken: Token));
        Assert.Equal(["second:TabSelected", "first:TabSelected"], events);
        Assert.Same(first.ViewModel, parent.ViewModel.LastActive);
        Success(await parent.ViewModel.Navigation.Select("one", cancellationToken: Token));
        Assert.Equal(2, events.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task View_cover_and_return_follow_the_selected_tab_lifecycle(bool compatibility)
    {
        Profile.Options = new() { Initialization = TabInitialization.Eager, DetailNavigation = TabDetailNavigation.AboveTabs,
            Lifecycle = compatibility ? TabLifecycle.TabSwitchCompatibility : TabLifecycle.Standard };
        var root = await Open<ProfileTabs>(); var parent = (ProfileTabsView)root.Current!.View;
        var child = (ViewBase)parent.CurrentTab!.View!;
        AttachDesktop(root.Window, parent, child);
        var parentEvents = new List<ViewNavigationKind>(); var childEvents = new List<ViewNavigationKind>();
        parent.NavigatedFrom += (_, args) => parentEvents.Add(args.Kind);
        parent.NavigatedTo += (_, args) => parentEvents.Add(args.Kind);
        child.NavigatedFrom += (_, args) => childEvents.Add(args.Kind);
        child.NavigatedTo += (_, args) => childEvents.Add(args.Kind);
        Success(await parent.ViewModel.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var detail = root.Current!.View;
        ViewNavigationKind? detailKind = null; detail.NavigatedTo += (_, args) => detailKind = args.Kind;
        detail.Handler = new TestNavigationHandler();
        Assert.Equal(ViewNavigationKind.Pushed, detailKind);
        Success(await ((Leaf)detail.ViewModel).Navigation.NavigateBack(Token));
        Assert.Equal([ViewNavigationKind.Covered, ViewNavigationKind.Returned], parentEvents);
        Assert.Equal(compatibility ? [] : new[] { ViewNavigationKind.Covered, ViewNavigationKind.Returned }, childEvents);
    }

    [Fact]
    public async Task Deferred_view_notification_never_runs_after_dismissal_and_callback_failure_does_not_break_navigation()
    {
        var root = await Open<Leaf>(); var old = root.Current!.View; var count = 0;
        old.NavigatedTo += (_, _) => count++;
        Success(await factory.SetRoot<Tabs>(root.Window, cancellationToken: Token));
        AttachDesktop(root.Window, old);
        Assert.Equal(0, count);
        var current = root.Host.CurrentContentNavigation!.Current!.View;
        current.NavigatedTo += (_, _) => throw new InvalidOperationException("geometry");
        AttachDesktop(root.Window, current);
        Assert.False(current.ViewModel.IsDismissed);
        Success(await ((Leaf)current.ViewModel).Navigation.NavigateTo<Detail>(cancellationToken: Token));
    }

    [Fact]
    public Task Raw_popup_cancellation_keeps_observation_until_actual_close_in_the_origin_window() => OnRootUI(async () =>
    {
        CompatibilityProfile();
        var other = await Open<ProfileTabs>(); var root = await Open<ProfileTabs>();
        var shell = (ProfileTabsView)root.Current!.View; var origin = (ProfileLeafView)shell.CurrentTab!.View!;
        AttachDesktop(root.Window, shell, origin);
        var navigationNotifications = 0;
        origin.NavigatedTo += (_, _) => navigationNotifications++;
        origin.NavigatedFrom += (_, _) => navigationNotifications++;
        var popup = new Popup<string> { Content = new Label { Text = "Raw popup" } };
        var opened = RootSignal(); popup.Opened += (_, _) => opened.TrySetResult();
        using var cancellation = new CancellationTokenSource();
        var showing = origin.ShowPopupAsync(popup, token: cancellation.Token);
        await Settle(opened.Task);
        Assert.Single(origin.ModalStarts); Assert.Single(shell.ModalStarts);
        Assert.Same(root.Window, shell.ModalStarts[0].Window);
        Assert.Same(origin, shell.ModalStarts[0].Origin);
        Assert.Empty(((ProfileTabsView)other.Current!.View).ModalStarts);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Settle(showing));
        Assert.Empty(shell.ModalEnds); Assert.Single(root.Window.Navigation.ModalStack);
        await Settle(popup.CloseAsync("done", Token));
        Assert.Equal(shell.ModalStarts, shell.ModalEnds);
        Assert.Equal(origin.ModalStarts, origin.ModalEnds);
        Assert.Equal(0, navigationNotifications);
    });

    [Fact]
    public Task Registered_and_nested_popups_balance_the_same_observers() => OnRootUI(async () =>
    {
        var root = await Open<ProfileTabs>(); var shell = (ProfileTabsView)root.Current!.View;
        var origin = (ProfileLeafView)shell.CurrentTab!.View!;
        var firstSignal = NextPopup();
        var firstShowing = origin.ViewModel.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token);
        var first = await Opened(firstSignal);
        var secondSignal = NextPopup();
        var secondShowing = first.ViewModel.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token);
        var second = await Opened(secondSignal);
        Assert.Equal(2, shell.ModalStarts.Count); Assert.Equal(2, origin.ModalStarts.Count);
        Success(await second.ViewModel.Navigation.ClosePopup("inner", Token)); await Settle(secondShowing);
        Assert.Single(shell.ModalEnds);
        Success(await first.ViewModel.Navigation.ClosePopup("outer", Token)); await Settle(firstShowing);
        Assert.Equal(2, shell.ModalEnds.Count);
        Assert.Equal(2, shell.ModalEnds.Distinct().Count());
    });

    [Fact]
    public Task Modal_observer_failure_balances_entered_observers_without_presenting() => OnRootUI(async () =>
    {
        var root = await Open<ProfileTabs>(); var shell = (ProfileTabsView)root.Current!.View;
        var origin = (ProfileLeafView)shell.CurrentTab!.View!;
        AttachDesktop(root.Window, shell, origin); shell.RejectModal = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => origin.ShowPopupAsync(new Popup(), token: Token));
        Assert.Empty(root.Window.Navigation.ModalStack);
        Assert.Single(shell.ModalStarts); Assert.Equal(shell.ModalStarts, shell.ModalEnds);
        Assert.Single(origin.ModalStarts); Assert.Equal(origin.ModalStarts, origin.ModalEnds);
    });

    [Fact]
    public Task Window_destruction_balances_popup_observation_once() => OnRootUI(async () =>
    {
        var root = await Open<ProfileTabs>(); var shell = (ProfileTabsView)root.Current!.View;
        var signal = NextPopup();
        var showing = shell.ViewModel.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token);
        await Opened(signal);
        ((IWindow)root.Window).Destroying();
        await Settle(root.Host.Completion); await Settle(showing);
        Assert.Single(shell.ModalStarts); Assert.Equal(shell.ModalStarts, shell.ModalEnds);
    });

    [Fact]
    public async Task Detached_and_hidden_views_cannot_present_raw_popups()
    {
        CompatibilityProfile();
        var root = await Open<ProfileTabs>(); var shell = (ProfileTabsView)root.Current!.View;
        var first = (ProfileLeafView)shell.CurrentTab!.View!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.ShowPopupAsync(new Popup(), token: Token));
        AttachDesktop(root.Window, shell, first);
        Success(await shell.ViewModel.Navigation.Select("two", cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.ShowPopupAsync(new Popup(), token: Token));
        Assert.Empty(shell.ModalStarts);
    }

    [Fact]
    public async Task Alert_observation_uses_the_same_chain_and_window_close_is_idempotent()
    {
        var root = await Open<ProfileTabs>(); var shell = (ProfileTabsView)root.Current!.View;
        var origin = (ProfileLeafView)shell.CurrentTab!.View!;
        using var observation = ModalPresentationScope.Begin(origin.ViewModel, root.Window, ModalPresentationKind.Alert);
        Assert.Equal(ModalPresentationKind.Alert, Assert.Single(shell.ModalStarts).Kind);
        ((IWindow)root.Window).Destroying(); await root.Host.Completion;
        observation!.Dispose();
        Assert.Single(shell.ModalEnds); Assert.Single(origin.ModalEnds);
    }

    [Fact]
    public async Task Root_recovery_preserves_the_live_root_on_preparation_failure_and_cleans_failed_activation()
    {
        var calls = 0;
        var root = await OpenRecovering<Leaf>(result =>
        {
            calls++; Assert.True(result.HasCommitted);
            Assert.All(services.GetRequiredService<RootActions>().Created, model => Assert.True(model.Resource.Disposed));
            return new Label { Text = "Retry" };
        });
        var original = root.Current!.ViewModel;
        var preparing = await factory.SetRoot<Failing>(root.Window, cancellationToken: Token);
        Assert.False(preparing.HasCommitted); Assert.False(original.IsDismissed); Assert.Equal(0, calls);
        services.GetRequiredService<RootActions>().Appear = _ => throw new InvalidOperationException("activation");
        var result = await factory.SetRoot<ApplicationRoot>(root.Window, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, result.Status); Assert.True(result.HasCommitted);
        Assert.Equal("Retry", Assert.IsType<Label>(Assert.IsType<ContentPage>(root.Window.Page).Content).Text);
        Assert.Null(root.Host.CurrentRoot); Assert.Equal(1, calls); Assert.True(original.IsDismissed);
        services.GetRequiredService<RootActions>().Appear = null;
        Success(await factory.SetRoot<Leaf>(root.Window, cancellationToken: Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_cleanup_errors_keep_a_healthy_replacement_and_a_failing_fallback_factory_uses_safe_content(bool fromEntry)
    {
        var calls = 0;
        var root = await OpenRecovering<ApplicationRoot>(_ => { calls++; throw new InvalidOperationException("fallback"); });
        var actions = services.GetRequiredService<RootActions>();
        actions.Cleanup = _ => throw new InvalidOperationException("cleanup");
        var cleaned = fromEntry
            ? await ((ApplicationRoot)root.Current!.ViewModel).Navigation.SetRoot<Leaf>(cancellationToken: Token)
            : await factory.SetRoot<Leaf>(root.Window, cancellationToken: Token);
        Assert.NotEmpty(cleaned.CleanupErrors); Assert.Equal(0, calls);
        Assert.IsType<Leaf>(root.Host.CurrentContentNavigation!.Current!.ViewModel);
        actions.Cleanup = null; actions.Appear = _ => throw new InvalidOperationException("activation");
        var failed = await factory.SetRoot<ApplicationRoot>(root.Window, cancellationToken: Token);
        Assert.Equal(1, calls); Assert.Contains(failed.CleanupErrors, error => error.Message == "fallback");
        Assert.IsType<Label>(Assert.IsType<ContentPage>(root.Window.Page).Content);
        Assert.True(failed.HasCommitted);
    }

    [Fact]
    public async Task Entry_root_replacement_also_uses_the_window_recovery_policy()
    {
        var root = await OpenRecovering<Leaf>(_ => new Label { Text = "Entry fallback" });
        services.GetRequiredService<RootActions>().Appear = _ => throw new InvalidOperationException("activation");
        var result = await ((Leaf)root.Current!.ViewModel).Navigation.SetRoot<ApplicationRoot>(cancellationToken: Token);
        Assert.True(result.HasCommitted); Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.Equal("Entry fallback", Assert.IsType<Label>(Assert.IsType<ContentPage>(root.Window.Page).Content).Text);
    }

    [Fact]
    public async Task Configured_window_applies_settings_before_open_and_closure_waits_for_cleanup()
    {
        var order = new List<string>();
        var result = await factory.OpenWindowCore<ApplicationRoot>(window =>
        { order.Add("configure"); window.Title = "Guidelines"; window.Width = 500; window.MinimumWidth = window.MaximumWidth = 500; },
            null, null, Token, window =>
            {
                order.Add("open"); Assert.Equal("Guidelines", window.Title); Assert.Equal(500, window.MaximumWidth);
                application.Next = () => window; ((IApplication)application).CreateWindow(null);
            }, window => ((IWindow)window).Destroying());
        var host = MauiNavigationHostFactory.GetOrCreateHost(result.Window!); hosts.Add(host);
        Success(result.Navigation); Assert.Equal(["configure", "open"], order); Assert.False(result.Closed.IsCompleted);
        var entered = RootSignal(); var release = RootSignal();
        services.GetRequiredService<RootActions>().Cleanup = async _ => { entered.SetResult(); await release.Task; };
        ((IWindow)result.Window!).Destroying(); await Settle(entered.Task);
        Assert.False(result.Closed.IsCompleted);
        release.SetResult(); await Settle(result.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configured_window_failure_releases_the_created_window_and_scopes(bool failDuringOpen)
    {
        var result = await factory.OpenWindowCore<ApplicationRoot>(_ =>
        { if (!failDuringOpen) throw new InvalidOperationException("configure"); }, null, null, Token,
            _ => throw new InvalidOperationException("open"), window => ((IWindow)window).Destroying());
        Assert.Equal(NavigationStatus.Failed, result.Navigation.Status);
        Assert.NotNull(result.Window); await Settle(result.Closed);
        Assert.All(services.GetRequiredService<RootActions>().Created, model => Assert.True(model.Resource.Disposed));
    }

    [Fact]
    public async Task Window_closed_during_initialization_finishes_without_a_live_candidate()
    {
        var release = RootSignal(); services.GetRequiredService<RootActions>().Before = _ => release.Task;
        var result = await factory.OpenWindowCore<ApplicationRoot>(_ => { }, null, null, Token, window =>
        { ((IWindow)window).Destroying(); release.SetResult(); }, window => ((IWindow)window).Destroying());
        Assert.False(result.Navigation.IsSuccess); await Settle(result.Closed);
        Assert.All(services.GetRequiredService<RootActions>().Created, model => Assert.True(model.Resource.Disposed));
    }

    [Fact]
    public async Task Cancelling_a_configured_window_open_closes_it_and_awaits_its_owned_cleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var release = RootSignal(); services.GetRequiredService<RootActions>().Before = _ => release.Task;
        var closes = 0;
        var result = await factory.OpenWindowCore<ApplicationRoot>(_ => { }, null, null, cancellation.Token,
            _ => cancellation.Cancel(), window =>
            { closes++; ((IWindow)window).Destroying(); release.SetResult(); });
        Assert.Equal(NavigationStatus.Cancelled, result.Navigation.Status);
        Assert.Equal(1, closes); Assert.NotNull(result.Window); await Settle(result.Closed);
        Assert.All(services.GetRequiredService<RootActions>().Created, model => Assert.True(model.Resource.Disposed));
    }

    [Fact]
    public async Task Failed_window_initialization_still_returns_the_window_and_its_actual_lifetime()
    {
        services.GetRequiredService<RootActions>().Appear = _ => throw new InvalidOperationException("activation");
        var result = await factory.OpenWindowCore<ApplicationRoot>(_ => { }, null,
            new() { RootFailureContentFactory = _ => new Label { Text = "Recover" } }, Token,
            window => { application.Next = () => window; ((IApplication)application).CreateWindow(null); },
            window => ((IWindow)window).Destroying());
        var host = MauiNavigationHostFactory.GetOrCreateHost(result.Window!); hosts.Add(host);
        Assert.False(result.Navigation.IsSuccess); Assert.NotNull(result.Window); Assert.False(result.Closed.IsCompleted);
        Assert.Equal("Recover", Assert.IsType<Label>(Assert.IsType<ContentPage>(result.Window.Page).Content).Text);
        ((IWindow)result.Window).Destroying(); await Settle(result.Closed);
    }

    private async Task<NavigationContext> OpenRecovering<T>(Func<NavigationResult, View> failure) where T : ViewModelBase
    {
        application.Next = () => factory.CreateWindow<T>(null, new WindowBootstrapOptions { RootFailureContentFactory = failure });
        var window = (Window)((IApplication)application).CreateWindow(null);
        var host = MauiNavigationHostFactory.GetOrCreateHost(window); hosts.Add(host);
        Success(await factory.WaitForInitializationAsync(window, Token));
        return host.CurrentContentNavigation!;
    }

    private static void AttachDesktop(Window window, params ViewBase[] views)
    {
        window.Handler ??= new DesktopWindowHandler();
        foreach (var view in views) view.Handler ??= new TestNavigationHandler();
    }

    private sealed class DesktopWindowHandler : IElementHandler
    {
        public object PlatformView { get; } = new();
        public IWindow VirtualView { get; private set; } = null!;
        IElement IElementHandler.VirtualView => VirtualView;
        public IMauiContext MauiContext { get; private set; } = new MauiContext(new ServiceCollection().BuildServiceProvider());
        public void SetMauiContext(IMauiContext context) => MauiContext = context;
        public void SetVirtualView(IElement view) => VirtualView = (IWindow)view;
        public void UpdateValue(string property) { }
        public void DisconnectHandler() { }
        public void Invoke(string command, object? args) { }
    }
}

public sealed class DesktopRegistrationTests
{
    [Fact]
    public void Root_and_unowned_scoped_navigation_resolution_fail_with_actionable_guidance()
    {
        var builder = MauiApp.CreateBuilder().UseMVVMCompass(_ => { });
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Contains("registered navigation entry", Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<INavigationService>()).Message);
        using var scope = provider.CreateScope();
        Assert.Contains("MauiNavigationHostFactory", Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<INavigationService>()).Message);
    }

    [Fact]
    public void Runtime_registration_shares_validation_lifetimes_and_freezing_with_typed_registration()
    {
        var builder = MauiApp.CreateBuilder().UseMVVMCompass(pairs => pairs.Add(typeof(RuntimeModel), typeof(RuntimeView)));
        var registration = (NavigationRegistrationBuilder)builder.Services.Single(item => item.ServiceType == typeof(NavigationRegistrationBuilder)).ImplementationInstance!;
        Assert.Equal(typeof(RuntimeView), registration[typeof(RuntimeModel)]);
        Assert.Equal(ServiceLifetime.Scoped, builder.Services.Single(item => item.ServiceType == typeof(RuntimeModel)).Lifetime);
        Assert.Equal(ServiceLifetime.Transient, builder.Services.Single(item => item.ServiceType == typeof(RuntimeView)).Lifetime);
        Assert.Throws<ArgumentException>(() => registration.Add<RuntimeModel, RuntimeView>());
        Assert.Throws<ArgumentException>(() => registration.Add(typeof(string), typeof(RuntimeView)));
        Assert.Throws<ArgumentException>(() => registration.Add(typeof(RuntimeModel), typeof(string)));
        using var provider = builder.Services.BuildServiceProvider();
        foreach (var initializer in provider.GetServices<IMauiInitializeService>()) initializer.Initialize(provider);
        Assert.Throws<InvalidOperationException>(() => builder.AddViewModelViewPair(typeof(RuntimeModel), typeof(RuntimeView)));
    }

    public sealed class RuntimeModel(INavigationService navigation) : ViewModelBase
    { public INavigationService Navigation { get; } = navigation; }
    public sealed class RuntimeView(RuntimeModel model) : ViewBase<RuntimeModel>(model);
}
