using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class WindowNavigationHostTests : IAsyncDisposable
{
    private readonly Application? previousApplication = Application.Current;
    private readonly TestApplication application = new();
    private readonly List<MauiNavigationHost> hosts = [];
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        try { foreach (var host in hosts) await host.DisposeAsync(); }
        finally { Application.Current = previousApplication; }
    }

    [Fact]
    public async Task Typed_bootstrap_targets_an_unregistered_window_and_shares_the_legacy_lifetime()
    {
        var old = new Probe();
        var window = new Window(new ProbePage(old));
        var model = new Probe();
        var page = new ProbePage(model);
        model.Before = () => { Assert.False(old.IsDismissed); ((IPageController)page).SendAppearing(); return Task.CompletedTask; };
        model.Appear = () => { Assert.True(old.IsDismissed); Assert.Same(page, window.Page); return Task.CompletedTask; };
        var host = Host(window, _ => page);
        Assert.Empty(application.Windows);
        var outcome = await host.ReplaceRootAsync<Probe, string>(new("bootstrap"), cancellationToken: TestToken);
        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.HasCommitted);
        Assert.Same(page, outcome.Value!.Page);
        Assert.Same(outcome.Value, host.CurrentRoot);
        Assert.Same(model.Lifetime, outcome.Value.Entry.Lifetime);
        Assert.Equal(NavigationEntryState.Active, outcome.Value.Entry.State);
        Assert.Equal("bootstrap", model.Parameter);
        Assert.Equal(1, model.BeforeCalls);
        // MAUI only sends Appearing after a window belongs to the application hierarchy.
        Assert.Equal(0, model.Appearances);
        Assert.Empty(application.Windows);
        application.Register(window);
        ((IPageController)page).SendAppearing();
        Assert.Equal(1, model.Appearances);
    }

    [Fact]
    public async Task Required_authorization_abandons_an_uninstalled_bootstrap_candidate()
    {
        var old = new Probe();
        var window = new Window(new ProbePage(old));
        var started = Signal();
        var bootstrap = new Probe { Initialize = async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
        var authorized = new Probe();
        var models = new Queue<Probe>([bootstrap, authorized]);
        var host = Host(window, _ => new ProbePage(models.Dequeue()));
        var pending = host.ReplaceRootAsync<Probe, string>(new("bootstrap"), cancellationToken: TestToken);
        await Await(started.Task);
        Assert.False(old.IsDismissed);
        var required = host.ReplaceRootAsync<Probe, string>(new("authorized") { Priority = NavigationPriority.Required }, cancellationToken: TestToken);
        Assert.Equal(NavigationStatus.Superseded, (await Await(pending)).Status);
        Assert.True((await Await(required)).IsSuccess);
        Assert.Equal(DismissalReason.PreparationFailed, bootstrap.Lifetime.Reason);
        Assert.Equal(1, bootstrap.Dismissals);
        Assert.Equal(0, bootstrap.BeforeCalls);
        Assert.Equal(0, bootstrap.Appearances);
        Assert.Null(bootstrap.PendingRootPreparation);
        Assert.Same(authorized, host.CurrentRoot!.Entry.ViewModel);
    }

    [Fact]
    public async Task Required_requests_wait_for_committed_cleanup_and_keep_their_order()
    {
        var entered = Signal();
        var release = Signal();
        var old = new Probe { Cleanup = async () => { entered.SetResult(); await release.Task; } };
        var window = application.Add(new ProbePage(old));
        var parameters = new List<string>();
        var host = Host(window, _ => new ProbePage(new Probe { Before = () => { parameters.Add("prepared"); return Task.CompletedTask; } }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var normal = host.ReplaceRootAsync<Probe, string>(new("normal"), cancellationToken: cancellation.Token);
        await Await(entered.Task);
        var first = host.ReplaceRootAsync<Probe, string>(new("first") { Priority = NavigationPriority.Required }, cancellationToken: TestToken);
        var second = host.ReplaceRootAsync<Probe, string>(new("second") { Priority = NavigationPriority.Required }, cancellationToken: TestToken);
        cancellation.Cancel();
        try
        {
            Assert.Single(parameters);
            Assert.False(normal.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("normal", (await Await(normal)).Value!.Entry.ViewModel.Parameter);
        var firstResult = await Await(first);
        var secondResult = await Await(second);
        Assert.Equal("first", firstResult.Value!.Entry.ViewModel.Parameter);
        Assert.Equal("second", secondResult.Value!.Entry.ViewModel.Parameter);
        Assert.True(firstResult.Value.Entry.Lifetime.IsDismissed);
        Assert.Same(secondResult.Value, host.CurrentRoot);
    }

    [Fact]
    public async Task Menu_rebuilds_coalesce_before_resolution()
    {
        var entered = Signal();
        var release = Signal();
        var first = new Probe { Initialize = async _ => { entered.SetResult(); await release.Task; } };
        var resolutions = 0;
        var host = Host(new(new ContentPage()), _ => new ProbePage(++resolutions == 1 ? first : new Probe()));
        var blocking = host.ReplaceRootAsync<Probe, string>(new("required") { Priority = NavigationPriority.Required }, cancellationToken: TestToken);
        await Await(entered.Task);
        var stale = host.ReplaceRootAsync<Probe, string>(new("old menu") { CoalescingKey = "menu" }, cancellationToken: TestToken);
        var latest = host.ReplaceRootAsync<Probe, string>(new("latest menu") { CoalescingKey = "menu" }, cancellationToken: TestToken);
        try { Assert.Equal(NavigationStatus.Superseded, (await Await(stale)).Status); }
        finally { release.TrySetResult(); }
        Assert.True((await Await(blocking)).IsSuccess);
        Assert.Equal("latest menu", (await Await(latest)).Value!.Entry.ViewModel.Parameter);
        Assert.Equal(2, resolutions);
    }

    [Fact]
    public async Task Explicit_windows_are_independent_and_reject_foreign_and_obsolete_origins()
    {
        var firstWindow = application.Add(new ContentPage());
        var secondWindow = application.Add(new ContentPage());
        var firstHost = Host(firstWindow, _ => new ProbePage(new Probe()));
        var secondResolutions = 0;
        var secondHost = Host(secondWindow, _ => { secondResolutions++; return new ProbePage(new Probe()); });
        var root = (await firstHost.ReplaceRootAsync<Probe, string>(new("first"), cancellationToken: TestToken)).Value!;
        var foreign = await secondHost.ReplaceRootAsync<Probe, string>(new("foreign") { Origin = root.Entry }, cancellationToken: TestToken);
        Assert.Equal(NavigationStatus.InvalidOrigin, foreign.Status);
        Assert.Equal(0, secondResolutions);
        var second = await secondHost.ReplaceRootAsync<Probe, string>(new("second"), cancellationToken: TestToken);
        Assert.True(second.IsSuccess);
        Assert.Same(root.Page, firstWindow.Page);
        await firstHost.ReplaceRootAsync<Probe, string>(new("replacement") { Origin = root.Entry }, cancellationToken: TestToken);
        var stale = await firstHost.ReplaceRootAsync<Probe, string>(new("late callback") { Origin = root.Entry }, cancellationToken: TestToken);
        Assert.Equal(NavigationStatus.InvalidOrigin, stale.Status);
        Assert.Same(second.Value!.Page, secondWindow.Page);
    }

    [Fact]
    public async Task Direct_legacy_dismissal_invalidates_the_portable_origin()
    {
        var host = Host(new(new ContentPage()), _ => new ProbePage(new Probe()));
        var root = (await host.ReplaceRootAsync<Probe, string>(new("live"), cancellationToken: TestToken)).Value!;
        await root.Entry.ViewModel.DismissAsync();
        var rejected = await host.ReplaceRootAsync<Probe, string>(new("late") { Origin = root.Entry }, cancellationToken: TestToken);
        Assert.Equal(NavigationStatus.InvalidOrigin, rejected.Status);
        Assert.True(root.Entry.Lifetime.Token.IsCancellationRequested);
        Assert.Equal(1, root.Entry.ViewModel.Dismissals);
    }

    [Theory]
    [InlineData(false, (int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData(false, (int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    [InlineData(true, (int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData(true, (int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public async Task Retained_children_keep_profiles_and_activate_after_old_ownership_ends(bool standard, int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var old = new Probe();
        var window = new Window(new ProbePage(old));
        var parent = new Probe();
        var child = new Probe { Appear = () => { Assert.True(old.IsDismissed); return Task.CompletedTask; } };
        var hidden = new Probe();
        var children = new Queue<Probe>([child, hidden]);
        parent.Before = () => parent.AddTabs(new(typeof(Child)), new(typeof(Child), hideInTabBar: true));
        var host = Host(window, type => type == typeof(Child) ? new ProbePage(children.Dequeue())
            : standard ? new StandardHost(parent) : new CustomHost(parent), new() { RetainedViewLifecycleBehavior = profile });
        var outcome = await host.ReplaceRootAsync<Probe>(new(null), cancellationToken: TestToken);
        Assert.True(outcome.IsSuccess);
        Assert.Equal(1, child.Appearances);
        Assert.Equal(0, hidden.Appearances);
        Assert.Equal(profile, child.RetainedViewLifecycleBehavior);
        Assert.Equal(profile, hidden.RetainedViewLifecycleBehavior);
        Assert.False(hidden.IsDismissed);
        var order = new List<string>();
        child.Cleanup = () => { Assert.True(parent.IsDismissed); Assert.True(hidden.IsDismissed); order.Add("child"); return Task.CompletedTask; };
        hidden.Cleanup = () => { Assert.True(parent.IsDismissed); order.Add("hidden"); return Task.CompletedTask; };
        parent.Cleanup = () => { order.Add("parent"); return Task.CompletedTask; };
        await host.DisposeAsync();
        Assert.Equal(3, order.Count);
        Assert.Equal("parent", order[^1]);
        Assert.Equal(1, hidden.Dismissals);
        Assert.Equal(DismissalReason.Removed, hidden.Lifetime.Reason);
    }

    [Fact]
    public async Task Plain_factory_roots_have_typed_data_activation_and_explicit_cleanup()
    {
        var window = new Window(new ContentPage());
        var host = Host(window, _ => throw new InvalidOperationException());
        var model = new PlainModel();
        var cleanups = 0;
        var outcome = await host.ReplaceRootAsync(new NavigationRequest<string>("document"), () => model,
            vm => new ContentPage { BindingContext = vm }, navigable: true,
            cleanup: _ => { cleanups++; return Task.CompletedTask; }, cancellationToken: TestToken);
        Assert.True(outcome.IsSuccess);
        Assert.IsType<NavigationPage>(window.Page);
        Assert.Equal("document", model.Parameter);
        Assert.Equal(1, model.Activations);
        Assert.Same(model, outcome.Value!.Entry.ViewModel);
        await host.DisposeAsync();
        Assert.Equal(1, model.Dismissals);
        Assert.Equal(1, cleanups);
        Assert.True(outcome.Value.Entry.Lifetime.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_blocked_window_does_not_block_another_windows_root()
    {
        var entered = Signal();
        var first = Host(new(new ContentPage()), _ => new ProbePage(new Probe
            { Initialize = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } }));
        var second = Host(new(new ContentPage()), _ => new ProbePage(new Probe()));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var pending = first.ReplaceRootAsync<Probe, string>(new("pending"), cancellationToken: cancellation.Token);
        await Await(entered.Task);
        try
        {
            var installed = await Await(second.ReplaceRootAsync<Probe, string>(new("independent"), cancellationToken: TestToken));
            Assert.True(installed.IsSuccess);
            Assert.False(pending.IsCompleted);
        }
        finally { cancellation.Cancel(); }
        Assert.Equal(NavigationStatus.Cancelled, (await Await(pending)).Status);
    }

    [Fact]
    public async Task Failed_tab_composition_cleans_attached_and_unattached_candidates_before_the_parent()
    {
        var old = new Probe();
        var parent = new Probe();
        var first = new Probe();
        var failure = new TestFailure();
        var second = new Probe { Before = () => throw failure };
        var order = new List<string>();
        first.Cleanup = () => { Assert.True(parent.IsDismissed); order.Add("first"); return Task.CompletedTask; };
        second.Cleanup = () => { Assert.True(first.IsDismissed); Assert.True(parent.IsDismissed); order.Add("second"); return Task.CompletedTask; };
        parent.Cleanup = () => { order.Add("parent"); return Task.CompletedTask; };
        parent.Before = () => parent.AddTabs(new(typeof(Child)), new(typeof(Child)));
        var children = new Queue<Probe>([first, second]);
        var host = Host(new(new ProbePage(old)), type => type == typeof(Child) ? new ProbePage(children.Dequeue()) : new CustomHost(parent));
        var failed = await host.ReplaceRootAsync<Probe>(new(null), cancellationToken: TestToken);
        Assert.Same(failure, failed.Error);
        Assert.False(failed.HasCommitted);
        Assert.False(old.IsDismissed);
        Assert.Equal(["second", "first", "parent"], order);
        Assert.All(new[] { parent, first, second }, model =>
        {
            Assert.Equal(1, model.Dismissals);
            Assert.Equal(0, model.Appearances);
            Assert.Null(model.PendingRootPreparation);
            Assert.Equal(DismissalReason.PreparationFailed, model.Lifetime.Reason);
        });
    }

    [Fact]
    public async Task A_retained_child_in_an_unregistered_window_cannot_be_reused_as_another_root()
    {
        var parent = new Probe();
        var child = new Probe();
        parent.Before = () => parent.AddTabs(new TabModel(typeof(Child)));
        var first = Host(new(new ContentPage()), type => type == typeof(Child) ? new ProbePage(child) : new CustomHost(parent));
        Assert.True((await first.ReplaceRootAsync<Probe>(new(null), cancellationToken: TestToken)).IsSuccess);
        var second = Host(new(new ContentPage()), _ => new ProbePage(child));
        var rejected = await second.ReplaceRootAsync<Probe>(new(null), cancellationToken: TestToken);
        Assert.Equal(NavigationStatus.Failed, rejected.Status);
        Assert.False(parent.IsDismissed);
        Assert.False(child.IsDismissed);
        Assert.Equal(0, child.Dismissals);
        Assert.Empty(application.Windows);
    }

    [Fact]
    public async Task Native_activation_callbacks_during_installation_are_awaited_and_report_committed_failure()
    {
        var old = new Probe();
        var window = application.Add(new ProbePage(old));
        var entered = Signal();
        var release = Signal();
        var failure = new TestFailure();
        var model = new Probe { Appear = async () => { Assert.True(old.IsDismissed); entered.SetResult(); await release.Task; throw failure; } };
        var page = new ProbePage(model);
        var host = Host(window, _ => page);
        void Appearing(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        { if (args.PropertyName == nameof(Window.Page)) ((IPageController)page).SendAppearing(); }
        window.PropertyChanged += Appearing;
        try
        {
            var pending = host.ReplaceRootAsync<Probe, string>(new("activate"), cancellationToken: TestToken);
            await Await(entered.Task);
            Assert.False(pending.IsCompleted);
            release.TrySetResult();
            var failed = await Await(pending);
            var error = Assert.IsType<RootReplacementException>(failed.Error);
            Assert.Equal(RootReplacementStage.Activation, error.Stage);
            Assert.Same(failure, error.InnerException);
            Assert.True(error.IsReplacementPresented);
            Assert.False(model.IsDismissed);
        }
        finally { release.TrySetResult(); window.PropertyChanged -= Appearing; }
    }

    [Fact]
    public async Task Page_factory_failure_cleans_the_owned_plain_model_without_changing_the_window()
    {
        var old = new ProbePage(new Probe());
        var host = Host(new(old), _ => throw new InvalidOperationException());
        var model = new PlainModel();
        var error = new TestFailure();
        var outcome = await host.ReplaceRootAsync(new NavigationRequest<string>("failed"), () => model,
            _ => throw error, cancellationToken: TestToken);
        Assert.Equal(NavigationStatus.Failed, outcome.Status);
        Assert.Same(error, outcome.Error);
        Assert.False(outcome.HasCommitted);
        Assert.Same(old, host.Window.Page);
        Assert.False(old.ViewModel.IsDismissed);
        Assert.Equal(1, model.Dismissals);
        Assert.Equal(DismissalReason.PreparationFailed, model.Reason);
    }

    [Fact]
    public async Task Activation_failure_retains_installed_ownership_until_a_fresh_recovery_root()
    {
        var host = Host(new(new ContentPage()), _ => new ProbePage(new Probe()));
        var error = new TestFailure();
        var model = new PlainModel { Activation = () => throw error };
        var failed = await host.ReplaceRootAsync(new NavigationRequest<string>("failed"), () => model,
            _ => new ContentPage(), cancellationToken: TestToken);
        var replacement = Assert.IsType<RootReplacementException>(failed.Error);
        Assert.Equal(NavigationStatus.Failed, failed.Status);
        Assert.True(failed.HasCommitted);
        Assert.True(replacement.IsReplacementPresented);
        Assert.Equal(RootReplacementStage.Activation, replacement.Stage);
        Assert.Same(error, replacement.InnerException);
        Assert.Same(model, host.CurrentRoot!.Entry.ViewModel);
        Assert.False(host.CurrentRoot.Entry.Lifetime.IsDismissed);
        Assert.True((await host.ReplaceRootAsync<Probe, string>(new("recover") { Priority = NavigationPriority.Required }, cancellationToken: TestToken)).IsSuccess);
        Assert.Equal(1, model.Dismissals);
        Assert.Equal(DismissalReason.RootReplaced, model.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Installation_failure_reports_observed_page_identity_and_cleans_only_uninstalled_candidates(bool afterAssignment)
    {
        var old = new Probe();
        var window = new Window(new ProbePage(old));
        var candidate = new Probe();
        var host = Host(window, _ => new ProbePage(candidate));
        var failure = new TestFailure();
        Microsoft.Maui.Controls.PropertyChangingEventHandler changing = (_, args) => { if (args.PropertyName == nameof(Window.Page)) throw failure; };
        System.ComponentModel.PropertyChangedEventHandler changed = (_, args) => { if (args.PropertyName == nameof(Window.Page)) throw failure; };
        if (afterAssignment) window.PropertyChanged += changed;
        else window.PropertyChanging += changing;
        NavigationOutcome<MauiNavigationRoot<Probe>> outcome;
        try { outcome = await host.ReplaceRootAsync<Probe, string>(new("new"), cancellationToken: TestToken); }
        finally { window.PropertyChanging -= changing; window.PropertyChanged -= changed; }
        var error = Assert.IsType<RootReplacementException>(outcome.Error);
        Assert.True(outcome.HasCommitted);
        Assert.Equal(RootReplacementStage.Commitment, error.Stage);
        Assert.Equal(afterAssignment, error.IsReplacementPresented);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(!afterAssignment, candidate.IsDismissed);
        Assert.True(old.IsDismissed);
    }

    [Fact]
    public async Task External_replacement_during_preparation_is_never_overwritten()
    {
        var old = new Probe();
        var window = new Window(new ProbePage(old));
        var external = new ContentPage();
        var candidate = new Probe { Before = () => { window.Page = external; return Task.CompletedTask; } };
        var host = Host(window, _ => new ProbePage(candidate));
        var outcome = await host.ReplaceRootAsync<Probe, string>(new("candidate"), cancellationToken: TestToken);
        Assert.Equal(NavigationStatus.Cancelled, outcome.Status);
        Assert.False(outcome.HasCommitted);
        Assert.Same(external, window.Page);
        Assert.True(candidate.IsDismissed);
        Assert.False(old.IsDismissed);
    }

    [Fact]
    public async Task External_replacement_invalidates_a_tracked_origin_and_awaits_its_cleanup()
    {
        var cleaned = Signal();
        var host = Host(new(new ContentPage()), _ => new ProbePage(new Probe { Cleanup = () => { cleaned.TrySetResult(); return Task.CompletedTask; } }));
        var root = (await host.ReplaceRootAsync<Probe, string>(new("owned"), cancellationToken: TestToken)).Value!;
        var external = new ContentPage();
        host.Window.Page = external;
        Assert.Equal(NavigationEntryState.Dismissed, root.Entry.State);
        Assert.True(root.Entry.ViewModel.IsHostReplaced);
        var rejected = await host.ReplaceRootAsync<Probe, string>(new("late") { Origin = root.Entry }, cancellationToken: TestToken);
        Assert.Equal(NavigationStatus.InvalidOrigin, rejected.Status);
        await Await(cleaned.Task);
        Assert.Same(external, host.Window.Page);
        Assert.Equal(1, root.Entry.ViewModel.Dismissals);
    }

    [Theory]
    [InlineData(NavigationPriority.Normal)]
    [InlineData(NavigationPriority.Required)]
    public async Task External_replacement_cancels_active_preparation_before_queued_cleanup(NavigationPriority priority)
    {
        var entered = Signal();
        var cleaned = Signal();
        var mutationFinished = Signal();
        var old = new Probe { Cleanup = () => { cleaned.TrySetResult(); return Task.CompletedTask; } };
        var candidate = new Probe { Initialize = async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { await mutationFinished.Task; }
        } };
        var models = new Queue<Probe>([old, candidate, new Probe()]);
        var host = Host(new(new ContentPage()), _ => new ProbePage(models.Dequeue()));
        var root = (await host.ReplaceRootAsync<Probe, string>(new("old"), cancellationToken: TestToken)).Value!;
        var pending = host.ReplaceRootAsync<Probe, string>(new("pending") { Priority = priority, Origin = root.Entry }, cancellationToken: TestToken);
        await Await(entered.Task);
        // Required work queued behind another required request must survive the external change.
        var next = priority == NavigationPriority.Required
            ? host.ReplaceRootAsync<Probe, string>(new("next") { Priority = NavigationPriority.Required }, cancellationToken: TestToken) : null;
        var external = new ContentPage();
        // The headless dispatcher has no UI synchronization context. Native continuations
        // cannot overlap a synchronous Window.Page setter; model that boundary explicitly.
        try { host.Window.Page = external; }
        finally { mutationFinished.TrySetResult(); }
        Assert.Equal(NavigationStatus.Cancelled, (await Await(pending)).Status);
        Assert.Equal(DismissalReason.PreparationFailed, candidate.Lifetime.Reason);
        await Await(cleaned.Task);
        Assert.Equal(1, old.Dismissals);
        if (next != null)
        {
            var installed = await Await(next);
            Assert.True(installed.IsSuccess, $"{installed.Status}: {installed.Error}");
            Assert.Same(installed.Value!.Page, host.Window.Page);
        }
        else Assert.Same(external, host.Window.Page);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_window_destruction_cancels_preparation_or_cleans_an_installed_root(bool preparing)
    {
        var entered = Signal();
        var model = new Probe();
        if (preparing) model.Initialize = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var host = Host(new(new ContentPage()), _ => new ProbePage(model));
        var pending = host.ReplaceRootAsync<Probe, string>(new("window"), cancellationToken: TestToken);
        if (preparing) await Await(entered.Task);
        else Assert.True((await Await(pending)).IsSuccess);
        ((IWindow)host.Window).Destroying();
        await Await(host.Completion);
        Assert.True(host.IsClosed);
        Assert.Equal(preparing ? NavigationStatus.Cancelled : NavigationStatus.Completed, (await Await(pending)).Status);
        Assert.Equal(1, model.Dismissals);
        Assert.Equal(preparing ? DismissalReason.PreparationFailed : DismissalReason.WindowClosed, model.Lifetime.Reason);
        Assert.Null(host.CurrentRoot);
        var later = await host.ReplaceRootAsync<Probe, string>(new("after close"), cancellationToken: TestToken);
        Assert.Equal(NavigationStatus.Cancelled, later.Status);
    }

    [Fact]
    public async Task Reentrant_root_calls_are_rejected_and_disposal_cannot_deadlock_its_own_callback()
    {
        var model = new Probe();
        var host = Host(new(new ContentPage()), _ => new ProbePage(model));
        model.Before = async () =>
        {
            var nested = await host.ReplaceRootAsync<Probe, string>(new("nested"), cancellationToken: TestToken);
            Assert.Equal(NavigationStatus.Reentrant, nested.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.DisposeAsync());
            Assert.False(host.IsClosed);
        };
        Assert.True((await Await(host.ReplaceRootAsync<Probe, string>(new("outer"), cancellationToken: TestToken))).IsSuccess);
    }

    [Fact]
    public async Task Legacy_root_calls_reject_overlap_and_cannot_deadlock_by_awaiting_the_host()
    {
        var window = application.Add(new ContentPage());
        var locator = new Locator(_ => new ProbePage(new Probe()));
        var host = Track(new MauiNavigationHostFactory(locator, new()).ForWindow(window));
        var legacy = new LegacyNavigationService(locator);
        var model = new Probe();
        locator.Factory = _ => new ProbePage(model);
        model.Before = async () =>
        {
            var nested = await host.ReplaceRootAsync<Probe, string>(new("nested"), cancellationToken: TestToken);
            Assert.Equal(NavigationStatus.Failed, nested.Status);
            Assert.IsType<InvalidOperationException>(nested.Error);
        };
        await Await(legacy.PresentAsMainPage<Probe>());
        var entered = Signal();
        var pendingModel = new Probe { Initialize = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
        locator.Factory = _ => new ProbePage(pendingModel);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var pending = host.ReplaceRootAsync<Probe, string>(new("pending"), cancellationToken: cancellation.Token);
        await Await(entered.Task);
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => legacy.PresentAsMainPage<Probe>()); }
        finally { cancellation.Cancel(); }
        Assert.Equal(NavigationStatus.Cancelled, (await Await(pending)).Status);
        Assert.Same(model, ((IHasVM)window.Page!).ViewModel);
        Assert.False(model.IsDismissed);
    }

    [Fact]
    public async Task Reusing_a_live_model_in_another_window_is_rejected_without_dismissing_it()
    {
        var model = new PlainModel();
        var first = Host(new(new ContentPage()), _ => throw new InvalidOperationException());
        var second = Host(new(new ContentPage()), _ => throw new InvalidOperationException());
        var installed = await first.ReplaceRootAsync(new NavigationRequest<string>("first"), () => model, _ => new ContentPage(), cancellationToken: TestToken);
        var rejected = await second.ReplaceRootAsync(new NavigationRequest<string>("second"), () => model, _ => new ContentPage(), cancellationToken: TestToken);
        Assert.Equal(NavigationStatus.Failed, rejected.Status);
        Assert.False(installed.Value!.Entry.Lifetime.IsDismissed);
        Assert.Equal(0, model.Dismissals);
        Assert.Same(installed.Value.Page, first.Window.Page);
    }

    [Fact]
    public void Builder_registers_a_factory_and_each_window_has_one_configuration()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMVVMCompass(pairs => pairs.Add<Probe, ProbePage>());
        using var services = builder.Services.BuildServiceProvider();
        var factory = services.GetRequiredService<MauiNavigationHostFactory>();
        var window = new Window(new ContentPage());
        var host = Track(factory.ForWindow(window));
        Assert.Same(host, factory.ForWindow(window));
        Assert.Throws<InvalidOperationException>(() => new MauiNavigationHostFactory(new Locator(_ => new ContentPage()), new()).ForWindow(window));
    }

    [Fact]
    public async Task Disposed_hosts_release_their_native_page_event_subscriptions()
    {
        var window = application.Add(new ContentPage());
        var page = new ProbePage(new Probe());
        var locator = new Locator(_ => page);
        var host = Track(new MauiNavigationHostFactory(locator, new()).ForWindow(window));
        Assert.True((await host.ReplaceRootAsync<Probe>(new(null), cancellationToken: TestToken)).IsSuccess);
        ((IPageController)page).SendAppearing();
        Assert.True(locator.Lookups > 0);
        await host.DisposeAsync();
        var lookups = locator.Lookups;
        ((IPageController)page).SendDisappearing();
        ((IPageController)page).SendAppearing();
        Assert.Equal(lookups, locator.Lookups);
    }

    private MauiNavigationHost Host(Window window, Func<Type, VisualElement> factory, NavigationOptions? options = null) =>
        Track(new MauiNavigationHostFactory(new Locator(factory), options ?? new()).ForWindow(window));
    private MauiNavigationHost Track(MauiNavigationHost host) { hosts.Add(host); return host; }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
    private static Task<T> Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestToken);
    private sealed class TestFailure : Exception;
    private sealed class TestApplication : Application
    {
        private Page? next;
        private Window? nextWindow;
        internal Window Add(Page page) { next = page; return (Window)((IApplication)this).CreateWindow(null); }
        internal void Register(Window window) { nextWindow = window; ((IApplication)this).CreateWindow(null); nextWindow = null; }
        protected override Window CreateWindow(IActivationState? activationState) => nextWindow ?? new(next!);
    }
    private sealed class Child : Probe;
    public class Probe : ViewModelBase, INavigationInitializable<string>
    {
        internal string? Parameter;
        internal int BeforeCalls, Appearances, Dismissals;
        internal Func<CancellationToken, Task>? Initialize;
        internal Func<Task>? Before, Appear, Cleanup;
        public async Task InitializeAsync(string parameter, CancellationToken cancellationToken)
        { Parameter = parameter; if (Initialize != null) await Initialize(cancellationToken); }
        public override Task BeforeFirstShown() { BeforeCalls++; return Before?.Invoke() ?? Task.CompletedTask; }
        public override Task Appearing() { Appearances++; return Appear?.Invoke() ?? Task.CompletedTask; }
        public override Task AfterDismissed() { Dismissals++; return Cleanup?.Invoke() ?? Task.CompletedTask; }
        internal Task AddTabs(params TabModel[] tabs) => AddTabbedViewModels(new(tabs, this));
    }
    public sealed class ProbePage : ContentPage, IHasVM
    {
        public ProbePage(Probe model) { ViewModel = model; BindingContext = model; Content = new Label(); }
        public ViewModelBase ViewModel { get; }
    }
    private sealed class StandardHost(Probe model) : TabbedPage, IHasVM { public ViewModelBase ViewModel { get; } = model; }
    private sealed class CustomHost(Probe model) : CustomTabbedViewBase<Probe>(model)
    {
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => true;
        protected override IDisposable ShowLoading(LoadingType loadingType) => new EmptyDisposable();
        protected override void HideLoading() { }
        private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    }
    private sealed class PlainModel : INavigationInitializable<string>, INavigationAware
    {
        internal string? Parameter;
        internal int Activations, Dismissals;
        internal DismissalReason Reason;
        internal Func<Task>? Activation;
        public Task InitializeAsync(string parameter, CancellationToken cancellationToken) { Parameter = parameter; return Task.CompletedTask; }
        public Task ActivateAsync(CancellationToken lifetimeToken) { Activations++; return Activation?.Invoke() ?? Task.CompletedTask; }
        public Task DismissAsync(DismissalReason reason) { Dismissals++; Reason = reason; return Task.CompletedTask; }
    }
    private sealed class Locator(Func<Type, VisualElement> factory) : IViewLocator
    {
        internal Func<Type, VisualElement> Factory = factory;
        internal int Lookups;
        public void Initialize(Dictionary<Type, Type> registerPairs) { }
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => Factory(typeof(T));
        public VisualElement CreateAndBindVEFor(Type type) => Factory(type);
        public Type FindVEForViewModel(Type viewModelType) => typeof(ProbePage);
        public Type FindViewModelForVE(Type page) { Lookups++; return typeof(Probe); }
    }
}
