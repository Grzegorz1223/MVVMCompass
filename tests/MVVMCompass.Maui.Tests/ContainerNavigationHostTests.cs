using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class ContainerNavigationHostTests : IAsyncDisposable
{
    private readonly Application? previous = Application.Current;
    private readonly TestApplication application = new();
    private readonly List<MauiNavigationHost> hosts = [];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        try { foreach (var host in hosts) await host.DisposeAsync(); }
        finally { Application.Current = previous; }
    }

    [Fact]
    public async Task Push_and_back_retain_and_reactivate_the_underlying_entry()
    {
        var (host, root) = await CreateAsync();
        var model = new Model();
        var detail = await PushAsync(host, model, root.Entry);
        Assert.Equal("detail", model.Parameter);
        Assert.Equal(NavigationEntryState.Inactive, root.Entry.State);
        Assert.False(root.Entry.Lifetime.Token.IsCancellationRequested);
        Assert.Equal(1, root.Entry.ViewModel.Deactivations);
        Assert.Same(detail, host.CurrentPage);
        var back = await host.BackAsync(new(null) { Origin = detail.Entry }, false, Token);
        Assert.True(back.IsSuccess, back.Error?.ToString());
        Assert.Same(root.Entry, back.Value!.Entry);
        Assert.Equal(2, root.Entry.ViewModel.Activations);
        Assert.Equal(1, model.Dismissals);
        Assert.Equal(DismissalReason.Back, model.Reason);
        Assert.True(model.LifetimeToken.IsCancellationRequested);
        Assert.Equal(NavigationStatus.InvalidOrigin,
            (await host.BackAsync(new(null) { Origin = detail.Entry }, false, Token)).Status);
    }

    [Fact]
    public async Task Modal_stacks_route_back_and_close_to_their_own_container()
    {
        var (host, root) = await CreateAsync();
        var underlying = await PushAsync(host, new Model());
        var modal = await ModalAsync(host, new Model(), true);
        var modalStack = (NavigationPage)modal.Page;
        modalStack.Handler = new TestNavigationHandler();
        var detail = await PushAsync(host, new Model(), modal.Entry);
        Assert.Same(detail.Page, modalStack.CurrentPage);
        Assert.Equal(2, ((NavigationPage)root.Page).Navigation.NavigationStack.Count);
        Assert.True((await host.BackAsync(new(null) { Origin = detail.Entry }, false, Token)).IsSuccess);
        Assert.Same(modal, host.CurrentPage);
        var nested = await ModalAsync(host, new Model());
        Assert.True((await host.CloseModalAsync(new(null) { Origin = nested.Entry }, false, Token)).IsSuccess);
        Assert.Equal(1, nested.Entry.ViewModel.Dismissals);
        Assert.Single(host.Window.Navigation.ModalStack);
        Assert.True((await host.BackAsync(new(null) { Origin = modal.Entry }, false, Token)).IsSuccess);
        Assert.Empty(host.Window.Navigation.ModalStack);
        Assert.Same(underlying, host.CurrentPage);
        Assert.Equal(NavigationEntryState.Inactive, root.Entry.State);
    }

    [Fact]
    public async Task Pop_to_root_marks_every_removed_entry_before_ordered_cleanup()
    {
        var (host, root) = await CreateAsync();
        List<string> order = [];
        var first = await PushAsync(host, new Model { Cleanup = () => { order.Add("first"); return Task.CompletedTask; } });
        var entered = Signal();
        var release = Signal();
        var second = await PushAsync(host, new Model { Cleanup = async () =>
        {
            Assert.True(first.Entry.Lifetime.IsDismissed);
            Assert.Equal(NavigationEntryState.Inactive, root.Entry.State);
            order.Add("second"); entered.SetResult(); await release.Task;
        } });
        var pop = host.PopToRootAsync(new(null) { Origin = second.Entry }, false, Token);
        await Await(entered.Task);
        try { Assert.False(pop.IsCompleted); }
        finally { release.SetResult(); }
        Assert.True((await Await(pop)).IsSuccess);
        Assert.Equal(["second", "first"], order);
        Assert.Equal(2, root.Entry.ViewModel.Activations);
        Assert.Equal(NavigationEntryState.Active, root.Entry.State);
    }

    [Fact]
    public async Task Closing_a_modal_dismisses_its_whole_stack_before_underlying_activation()
    {
        var (host, root) = await CreateAsync();
        var modal = await ModalAsync(host, new Model(), true);
        ((NavigationPage)modal.Page).Handler = new TestNavigationHandler();
        var child = await PushAsync(host, new Model());
        root.Entry.ViewModel.Activate = () =>
        {
            Assert.Equal(1, modal.Entry.ViewModel.Dismissals);
            Assert.Equal(1, child.Entry.ViewModel.Dismissals);
            return Task.CompletedTask;
        };
        Assert.True((await host.CloseModalAsync(animated: false, cancellationToken: Token)).IsSuccess);
        Assert.Same(root.Entry, host.CurrentPage!.Entry);
    }

    [Theory]
    [InlineData(MauiNavigationOperation.Push)]
    [InlineData(MauiNavigationOperation.OpenModal)]
    [InlineData(MauiNavigationOperation.Back)]
    [InlineData(MauiNavigationOperation.PopToRoot)]
    [InlineData(MauiNavigationOperation.CloseModal)]
    public async Task Guard_veto_preserves_presentation_and_never_resolves_a_candidate(MauiNavigationOperation operation)
    {
        var (host, root) = await CreateAsync();
        var current = operation == MauiNavigationOperation.CloseModal ? await ModalAsync(host, new Model()) : await PushAsync(host, new Model());
        current.Entry.ViewModel.Guard = _ => Task.FromResult(false);
        var resolutions = 0;
        NavigationStatus status;
        bool committed;
        if (operation is MauiNavigationOperation.Push or MauiNavigationOperation.OpenModal)
        {
            var request = new NavigationRequest<string>("guarded") { Origin = current.Entry };
            Model Create() { resolutions++; return new(); }
            var outcome = operation == MauiNavigationOperation.Push
                ? await host.PushAsync(request, Create, PageFor, false, cancellationToken: Token)
                : await host.OpenModalAsync(request, Create, PageFor, animated: false, cancellationToken: Token);
            status = outcome.Status; committed = outcome.HasCommitted;
        }
        else
        {
            var request = new NavigationRequest<object?>(null) { Origin = current.Entry };
            var outcome = operation switch
            {
                MauiNavigationOperation.Back => await host.BackAsync(request, false, Token),
                MauiNavigationOperation.PopToRoot => await host.PopToRootAsync(request, false, Token),
                _ => await host.CloseModalAsync(request, false, Token)
            };
            status = outcome.Status; committed = outcome.HasCommitted;
        }
        Assert.Equal(NavigationStatus.GuardRejected, status);
        Assert.False(committed);
        Assert.Equal(0, resolutions);
        Assert.Same(current, host.CurrentPage);
        Assert.Equal(0, current.Entry.ViewModel.Dismissals);
        Assert.Equal(NavigationEntryState.Inactive, root.Entry.State);
    }

    [Fact]
    public async Task A_cancelled_guard_and_a_reentrant_guard_do_not_mutate_the_stack()
    {
        var (host, root) = await CreateAsync();
        var entered = Signal();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        root.Entry.ViewModel.Guard = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; };
        var pending = host.PushAsync(new NavigationRequest<string>("cancelled"), () => new Model(), PageFor, false, cancellationToken: cancel.Token);
        await Await(entered.Task);
        cancel.Cancel();
        Assert.Equal(NavigationStatus.Cancelled, (await Await(pending)).Status);
        root.Entry.ViewModel.Guard = async _ =>
        {
            Assert.Equal(NavigationStatus.Reentrant, (await host.BackAsync(cancellationToken: Token)).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await host.DisposeAsync());
            return false;
        };
        var rejected = await host.PushAsync(new NavigationRequest<string>("reentrant"), () => new Model(), PageFor, false, cancellationToken: Token);
        Assert.Equal(NavigationStatus.GuardRejected, rejected.Status);
        Assert.Single(((NavigationPage)root.Page).Navigation.NavigationStack);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_page_factory_or_initialization_releases_only_the_candidate(bool failFactory)
    {
        var (host, root) = await CreateAsync();
        var candidate = new Model { Initialize = _ => throw new TestFailure() };
        var outcome = await host.PushAsync(new NavigationRequest<string>("failure"), () => candidate,
            model => failFactory ? throw new TestFailure() : PageFor(model), false, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, outcome.Status);
        Assert.False(outcome.HasCommitted);
        Assert.Equal(1, candidate.Dismissals);
        Assert.Equal(DismissalReason.PreparationFailed, candidate.Reason);
        Assert.Equal(NavigationEntryState.Active, root.Entry.State);
        Assert.Equal(0, root.Entry.ViewModel.Deactivations);
    }

    [Fact]
    public async Task Required_root_replacement_supersedes_preparing_stack_work()
    {
        var (host, root) = await CreateAsync();
        var entered = Signal();
        var candidate = new Model { Initialize = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
        var pending = host.PushAsync(new NavigationRequest<string>("old"), () => candidate, PageFor, false, cancellationToken: Token);
        await Await(entered.Task);
        var replacement = host.ReplaceRootAsync(new NavigationRequest<string>("required") { Priority = NavigationPriority.Required },
            () => new Model(), PageFor, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Superseded, (await Await(pending)).Status);
        Assert.True((await Await(replacement)).IsSuccess);
        Assert.Equal(1, candidate.Dismissals);
        Assert.Equal(1, root.Entry.ViewModel.Dismissals);
    }

    [Fact]
    public async Task Committed_push_finishes_before_cancellation_and_required_replacement()
    {
        var (host, root) = await CreateAsync();
        var entered = Signal(); var release = Signal();
        root.Entry.ViewModel.Deactivate = async () => { entered.SetResult(); await release.Task; };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var model = new Model();
        var push = host.PushAsync(new NavigationRequest<string>("committed"), () => model, PageFor, false, cancellationToken: cancel.Token);
        await Await(entered.Task);
        cancel.Cancel();
        var required = host.ReplaceRootAsync(new NavigationRequest<string>("required") { Priority = NavigationPriority.Required },
            () => new Model(), PageFor, cancellationToken: Token);
        try { Assert.False(required.IsCompleted); }
        finally { release.SetResult(); }
        Assert.True((await Await(push)).IsSuccess);
        Assert.True((await Await(required)).IsSuccess);
        Assert.Equal(1, model.Activations);
        Assert.Equal(1, model.Dismissals);
    }

    [Fact]
    public async Task Foreign_hidden_and_queued_stale_origins_cannot_route_to_another_container()
    {
        var (first, root) = await CreateAsync();
        var (second, other) = await CreateAsync();
        var foreign = await second.PushAsync(new NavigationRequest<string>("foreign") { Origin = root.Entry },
            () => new Model(), PageFor, false, cancellationToken: Token);
        Assert.Equal(NavigationStatus.InvalidOrigin, foreign.Status);
        var entered = Signal(); var release = Signal();
        var pushing = first.PushAsync(new NavigationRequest<string>("active"),
            () => new Model { Initialize = async _ => { entered.SetResult(); await release.Task; } }, PageFor, false, cancellationToken: Token);
        await Await(entered.Task);
        var stale = first.PushAsync(new NavigationRequest<string>("stale") { Origin = root.Entry }, () => new Model(), PageFor, false, cancellationToken: Token);
        release.SetResult();
        Assert.True((await Await(pushing)).IsSuccess);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await Await(stale)).Status);
        // Even manual portable activation cannot make a covered page the visible routing origin.
        await root.Entry.ActivateAsync();
        Assert.Equal(NavigationStatus.InvalidOrigin,
            (await first.BackAsync(new(null) { Origin = root.Entry }, false, Token)).Status);
        Assert.Same(other.Page, second.Window.Page);
        Assert.Single(((NavigationPage)other.Page).Navigation.NavigationStack);
    }

    [Fact]
    public async Task Selected_nested_navigation_stacks_receive_their_own_pushes()
    {
        var host = Host();
        var left = TestNavigationHandler.Create(new ContentPage());
        var right = TestNavigationHandler.Create(new ContentPage());
        var tabs = new TabbedPage(); tabs.Children.Add(left); tabs.Children.Add(right);
        var root = await host.ReplaceRootAsync(new NavigationRequest<string>("tabs"), () => new Model(), _ => tabs, cancellationToken: Token);
        Assert.True(root.IsSuccess);
        var first = await PushAsync(host, new Model());
        Assert.Same(first.Page, left.CurrentPage);
        Assert.Single(right.Navigation.NavigationStack);
        Assert.True((await host.BackAsync(animated: false, cancellationToken: Token)).IsSuccess);
        tabs.CurrentPage = right;
        var second = await PushAsync(host, new Model());
        Assert.Same(second.Page, right.CurrentPage);
        Assert.Single(left.Navigation.NavigationStack);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_back_and_pop_to_root_cleanup_plain_models_and_reactivate_the_root(bool toRoot)
    {
        var (host, root) = await CreateAsync();
        var detail = await PushAsync(host, new Model());
        MauiNavigationPage<Model>? second = toRoot ? await PushAsync(host, new Model()) : null;
        var reactivated = Signal();
        root.Entry.ViewModel.Activate = () => { reactivated.TrySetResult(); return Task.CompletedTask; };
        if (toRoot) await ((NavigationPage)root.Page).PopToRootAsync(false);
        else await ((NavigationPage)root.Page).PopAsync(false);
        await Await(reactivated.Task);
        await Await(host.NativeNavigationCompletion);
        Assert.Equal(1, detail.Entry.ViewModel.Dismissals);
        if (second != null) Assert.Equal(1, second.Entry.ViewModel.Dismissals);
        Assert.Equal(NavigationEntryState.Active, root.Entry.State);
    }

    [Fact]
    public async Task Native_modal_close_cleans_up_owned_models()
    {
        var (host, root) = await CreateAsync();
        var modal = await ModalAsync(host, new Model());
        var reactivated = Signal();
        root.Entry.ViewModel.Activate = () => { reactivated.TrySetResult(); return Task.CompletedTask; };
        await host.Window.Navigation.PopModalAsync(false);
        await Await(reactivated.Task);
        await Await(host.NativeNavigationCompletion);
        Assert.Equal(1, modal.Entry.ViewModel.Dismissals);
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public async Task Registered_pages_keep_both_retained_profiles_and_await_return_callbacks(int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var models = new Queue<Legacy>([new(), new()]);
        var host = Host(_ => new LegacyPage(models.Dequeue()), new() { RetainedViewLifecycleBehavior = profile });
        var root = (await host.ReplaceRootAsync<Legacy>(new(new() { ["value"] = "root" }), true, Token)).Value!;
        ((NavigationPage)root.Page).Handler = new TestNavigationHandler();
        ((IPageController)root.Page).SendAppearing();
        var detail = await host.PushAsync<Legacy, string>(new("typed"), false, Token);
        Assert.True(detail.IsSuccess, detail.Error?.ToString());
        Assert.Equal("typed", detail.Value!.Entry.ViewModel.Parameter);
        Assert.Equal("root", root.Entry.ViewModel.Parameter);
        Assert.Equal(1, profile == RetainedViewLifecycleBehavior.Deactivate ? root.Entry.ViewModel.Deactivations : root.Entry.ViewModel.Dismissals);
        Assert.False(root.Entry.ViewModel.IsDismissed);
        Assert.False(detail.Value.Entry.ViewModel.IsModal);
        var entered = Signal(); var release = Signal();
        root.Entry.ViewModel.Appear = async () => { entered.TrySetResult(); await release.Task; };
        var back = host.BackAsync(animated: false, cancellationToken: Token);
        await Await(entered.Task);
        try
        {
            Assert.False(back.IsCompleted);
            Assert.True(detail.Value.Entry.ViewModel.IsDismissed);
        }
        finally { release.SetResult(); }
        Assert.True((await Await(back)).IsSuccess);
        Assert.Equal(1, detail.Value.Entry.ViewModel.Dismissals);
        Assert.Equal(NavigationEntryState.Active, root.Entry.State);
    }

    [Fact]
    public async Task Registered_modal_sets_IsModal_and_uses_the_legacy_guard()
    {
        var models = new Queue<Legacy>([new(), new()]);
        var host = Host(_ => new LegacyPage(models.Dequeue()));
        Assert.True((await host.ReplaceRootAsync<Legacy>(new(null), cancellationToken: Token)).IsSuccess);
        var modal = await host.OpenModalAsync<Legacy>(new(new() { ["value"] = "modal" }), animated: false, cancellationToken: Token);
        Assert.True(modal.IsSuccess, modal.Error?.ToString());
        Assert.True(modal.Value!.Entry.ViewModel.IsModal);
        Assert.Equal("modal", modal.Value.Entry.ViewModel.Parameter);
        modal.Value.Entry.ViewModel.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await host.CloseModalAsync(animated: false, cancellationToken: Token)).Status);
        modal.Value.Entry.ViewModel.Allowed = true;
        Assert.True((await host.CloseModalAsync(animated: false, cancellationToken: Token)).IsSuccess);
        Assert.Equal(1, modal.Value.Entry.ViewModel.Dismissals);
    }

    [Fact]
    public async Task Activation_failure_keeps_installed_candidate_for_back_recovery()
    {
        var (host, root) = await CreateAsync();
        var model = new Model { Activate = () => throw new TestFailure() };
        var failed = await host.PushAsync(new NavigationRequest<string>("activation failure"), () => model, PageFor, false, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, failed.Status);
        Assert.True(failed.HasCommitted);
        Assert.True(Assert.IsType<MauiNavigationException>(failed.Error).HasPresentationChanged);
        Assert.Same(model, host.CurrentPage!.Entry.ViewModel);
        Assert.Equal(0, model.Dismissals);
        Assert.True((await host.BackAsync(animated: false, cancellationToken: Token)).IsSuccess);
        Assert.Equal(1, model.Dismissals);
        Assert.Same(root.Entry, host.CurrentPage!.Entry);
    }

    [Fact]
    public async Task Deactivation_failure_abandons_candidate_and_reactivates_the_still_visible_root()
    {
        var (host, root) = await CreateAsync();
        root.Entry.ViewModel.Deactivate = () => throw new TestFailure();
        var model = new Model();
        var failed = await host.PushAsync(new NavigationRequest<string>("failure"), () => model, PageFor, false, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, failed.Status);
        Assert.False(Assert.IsType<MauiNavigationException>(failed.Error).HasPresentationChanged);
        Assert.Equal(1, model.Dismissals);
        Assert.Equal(NavigationEntryState.Active, root.Entry.State);
        Assert.Equal(2, root.Entry.ViewModel.Activations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_replacement_and_host_disposal_clean_every_owned_stack_and_modal_entry(bool dispose)
    {
        var (host, root) = await CreateAsync();
        var detail = await PushAsync(host, new Model());
        var modal = await ModalAsync(host, new Model());
        if (dispose) await host.DisposeAsync();
        else Assert.True((await host.ReplaceRootAsync(new NavigationRequest<string>("replacement"), () => new Model(), PageFor, cancellationToken: Token)).IsSuccess);
        foreach (var model in new[] { root.Entry.ViewModel, detail.Entry.ViewModel, modal.Entry.ViewModel })
        {
            Assert.Equal(1, model.Dismissals);
            Assert.Equal(dispose ? DismissalReason.Removed : DismissalReason.RootReplaced, model.Reason);
        }
    }

    [Fact]
    public async Task Root_back_pop_to_root_and_close_without_a_modal_are_uncommitted_no_ops()
    {
        var (host, root) = await CreateAsync();
        root.Entry.ViewModel.Guard = _ => throw new TestFailure();
        foreach (var outcome in new[] { await host.BackAsync(cancellationToken: Token), await host.PopToRootAsync(cancellationToken: Token), await host.CloseModalAsync(cancellationToken: Token) })
        {
            Assert.True(outcome.IsSuccess);
            Assert.False(outcome.HasCommitted);
            Assert.Same(root.Entry, outcome.Value!.Entry);
        }
    }

    [Fact]
    public async Task Native_modal_veto_never_dismisses_the_still_presented_modal()
    {
        var (host, root) = await CreateAsync();
        var modal = await ModalAsync(host, new Model());
        host.Window.ModalPopping += (_, args) => args.Cancel = true;
        var outcome = await host.CloseModalAsync(animated: false, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, outcome.Status);
        Assert.True(outcome.HasCommitted);
        Assert.False(Assert.IsType<MauiNavigationException>(outcome.Error).HasPresentationChanged);
        Assert.Same(modal, host.CurrentPage);
        Assert.Equal(0, modal.Entry.ViewModel.Dismissals);
        Assert.Equal(NavigationEntryState.Active, modal.Entry.State);
        Assert.Equal(NavigationEntryState.Inactive, root.Entry.State);
    }

    [Fact]
    public async Task Pop_to_root_checks_inactive_owners_and_keeps_the_modal_open()
    {
        var (host, _) = await CreateAsync();
        var modal = await ModalAsync(host, new Model(), true);
        ((NavigationPage)modal.Page).Handler = new TestNavigationHandler();
        var first = await PushAsync(host, new Model());
        var second = await PushAsync(host, new Model());
        first.Entry.ViewModel.Guard = _ => Task.FromResult(false);
        Assert.Equal(NavigationStatus.GuardRejected, (await host.PopToRootAsync(animated: false, cancellationToken: Token)).Status);
        Assert.Same(second, host.CurrentPage);
        first.Entry.ViewModel.Guard = null;
        Assert.True((await host.PopToRootAsync(animated: false, cancellationToken: Token)).IsSuccess);
        Assert.Single(host.Window.Navigation.ModalStack);
        Assert.Same(modal, host.CurrentPage);
        Assert.Equal(1, first.Entry.ViewModel.Dismissals);
        Assert.Equal(1, second.Entry.ViewModel.Dismissals);
    }

    [Fact]
    public async Task External_root_change_cancels_preparation_and_cleans_detached_owned_pages_before_required_recovery()
    {
        var (host, root) = await CreateAsync();
        var detail = await PushAsync(host, new Model());
        var entered = Signal(); var mutated = Signal();
        var candidate = new Model { Initialize = async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { await mutated.Task; }
        } };
        var pending = host.PushAsync(new NavigationRequest<string>("preparing") { Priority = NavigationPriority.Required }, () => candidate, PageFor, false, cancellationToken: Token);
        await Await(entered.Task);
        var replacementModel = new Model { Activate = () =>
        {
            Assert.Equal(1, root.Entry.ViewModel.Dismissals);
            Assert.Equal(1, detail.Entry.ViewModel.Dismissals);
            return Task.CompletedTask;
        } };
        var required = host.ReplaceRootAsync(new NavigationRequest<string>("recovery") { Priority = NavigationPriority.Required },
            () => replacementModel, PageFor, cancellationToken: Token);
        host.Window.Page = new ContentPage();
        mutated.SetResult();
        Assert.Equal(NavigationStatus.Cancelled, (await Await(pending)).Status);
        Assert.True((await Await(required)).IsSuccess);
        Assert.Equal(1, candidate.Dismissals);
    }

    [Fact]
    public async Task Native_back_cancels_required_preparation_and_preserves_queued_required_work()
    {
        var (host, root) = await CreateAsync();
        var detail = await PushAsync(host, new Model());
        var entered = Signal(); var mutated = Signal();
        var candidate = new Model { Initialize = async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { await mutated.Task; }
        } };
        var pending = host.PushAsync(new NavigationRequest<string>("preparing") { Priority = NavigationPriority.Required }, () => candidate, PageFor, false, cancellationToken: Token);
        await Await(entered.Task);
        var required = host.ReplaceRootAsync(new NavigationRequest<string>("surviving") { Priority = NavigationPriority.Required }, () => new Model(), PageFor, cancellationToken: Token);
        await ((NavigationPage)root.Page).PopAsync(false);
        mutated.SetResult();
        Assert.Equal(NavigationStatus.Cancelled, (await Await(pending)).Status);
        Assert.True((await Await(required)).IsSuccess);
        await Await(host.NativeNavigationCompletion);
        Assert.Equal(1, detail.Entry.ViewModel.Dismissals);
        Assert.Equal(1, candidate.Dismissals);
    }

    [Fact]
    public async Task Failure_in_one_terminal_callback_does_not_skip_siblings_or_reactivation()
    {
        var (host, root) = await CreateAsync();
        var first = await PushAsync(host, new Model());
        var second = await PushAsync(host, new Model { Cleanup = () => throw new TestFailure() });
        var diagnostics = new List<Exception>();
        void Record(Exception error, string operation) => diagnostics.Add(error);
        NavigationDiagnostics.Error += Record;
        try
        {
            Assert.True((await host.PopToRootAsync(animated: false, cancellationToken: Token)).IsSuccess);
            Assert.Equal(1, first.Entry.ViewModel.Dismissals);
            Assert.Equal(1, second.Entry.ViewModel.Dismissals);
            Assert.Equal(NavigationEntryState.Active, root.Entry.State);
            Assert.Single(diagnostics);
        }
        finally { NavigationDiagnostics.Error -= Record; }
    }

    [Fact]
    public async Task A_live_model_cannot_be_reused_as_a_pushed_candidate()
    {
        var (host, root) = await CreateAsync();
        var outcome = await host.PushAsync(new NavigationRequest<string>("reuse"), () => root.Entry.ViewModel, PageFor, false, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, outcome.Status);
        Assert.False(outcome.HasCommitted);
        Assert.Equal(0, root.Entry.ViewModel.Dismissals);
        Assert.Equal(NavigationEntryState.Active, root.Entry.State);
    }

    [Fact]
    public async Task Native_callback_root_mutation_is_reported_as_a_committed_failure()
    {
        var rootModel = new Legacy();
        var model = new Legacy();
        var models = new Queue<Legacy>([rootModel, model, new()]);
        var host = Host(_ => new LegacyPage(models.Dequeue()));
        var root = (await host.ReplaceRootAsync<Legacy>(new(null), true, Token)).Value!;
        ((NavigationPage)root.Page).Handler = new TestNavigationHandler();
        ((IPageController)root.Page).SendAppearing();
        model.Appear = () => { host.Window.Page = new ContentPage(); return Task.CompletedTask; };
        var outcome = await host.PushAsync<Legacy>(new(null), false, Token);
        Assert.Equal(NavigationStatus.Failed, outcome.Status);
        Assert.True(outcome.HasCommitted);
        Assert.True(model.IsDismissed);
        Assert.True((await host.ReplaceRootAsync<Legacy>(new(null) { Priority = NavigationPriority.Required }, cancellationToken: Token)).IsSuccess);
        Assert.Equal(1, model.Dismissals);
        Assert.Equal(1, rootModel.Dismissals);
    }

    private MauiNavigationHost Host(Func<Type, VisualElement>? factory = null, NavigationOptions? options = null)
    {
        var window = application.Add();
        var host = new MauiNavigationHostFactory(new Locator(factory ?? (_ => throw new TestFailure())), options ?? new()).ForWindow(window);
        hosts.Add(host); return host;
    }
    private async Task<(MauiNavigationHost Host, MauiNavigationRoot<Model> Root)> CreateAsync()
    {
        var host = Host();
        var outcome = await host.ReplaceRootAsync(new NavigationRequest<string>("root"), () => new Model(), PageFor, true, cancellationToken: Token);
        Assert.True(outcome.IsSuccess, outcome.Error?.ToString());
        ((NavigationPage)outcome.Value!.Page).Handler = new TestNavigationHandler();
        return (host, outcome.Value);
    }
    private static async Task<MauiNavigationPage<Model>> PushAsync(MauiNavigationHost host, Model model, NavigationEntry? origin = null)
    {
        var outcome = await host.PushAsync(new NavigationRequest<string>("detail") { Origin = origin }, () => model, PageFor, false, cancellationToken: Token);
        Assert.True(outcome.IsSuccess, outcome.Error?.ToString()); return outcome.Value!;
    }
    private static async Task<MauiNavigationPage<Model>> ModalAsync(MauiNavigationHost host, Model model, bool navigable = false)
    {
        var outcome = await host.OpenModalAsync(new NavigationRequest<string>("modal"), () => model, PageFor, navigable, false, cancellationToken: Token);
        Assert.True(outcome.IsSuccess, outcome.Error?.ToString()); return outcome.Value!;
    }
    private static Page PageFor(Model model) => new ContentPage { BindingContext = model, Content = new Label() };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private static Task<T> Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    private sealed class TestFailure : Exception;
    private sealed class TestApplication : Application
    {
        internal Window Add() => (Window)((IApplication)this).CreateWindow(null);
        protected override Window CreateWindow(IActivationState? activationState) => new(new ContentPage());
    }
    private sealed class Model : INavigationInitializable<string>, INavigationAware, INavigationGuard
    {
        internal string? Parameter;
        internal int Activations, Deactivations, Dismissals;
        internal CancellationToken LifetimeToken;
        internal DismissalReason Reason;
        internal Func<CancellationToken, Task>? Initialize;
        internal Func<CancellationToken, Task<bool>>? Guard;
        internal Func<Task>? Activate, Deactivate, Cleanup;
        public async Task InitializeAsync(string parameter, CancellationToken cancellationToken)
        { Parameter = parameter; if (Initialize != null) await Initialize(cancellationToken); }
        public Task ActivateAsync(CancellationToken lifetimeToken)
        { LifetimeToken = lifetimeToken; Activations++; return Activate?.Invoke() ?? Task.CompletedTask; }
        public Task DeactivateAsync() { Deactivations++; return Deactivate?.Invoke() ?? Task.CompletedTask; }
        public Task DismissAsync(DismissalReason reason) { Dismissals++; Reason = reason; return Cleanup?.Invoke() ?? Task.CompletedTask; }
        public Task<bool> CanNavigateAsync(CancellationToken cancellationToken) => Guard?.Invoke(cancellationToken) ?? Task.FromResult(true);
    }
    private sealed class Legacy : ViewModelBase, INavigationInitializable<string>
    {
        internal string? Parameter;
        internal int Deactivations, Dismissals;
        internal bool Allowed = true;
        internal Func<Task>? Appear;
        public Task InitializeAsync(string parameter, CancellationToken cancellationToken) { Parameter = parameter; return Task.CompletedTask; }
        public override Task GetParameters(Dictionary<string, object> parameters) { Parameter = (string)parameters["value"]; return Task.CompletedTask; }
        public override Task<bool> CanNavigate() => Task.FromResult(Allowed);
        public override Task Deactivated() { Deactivations++; return Task.CompletedTask; }
        public override Task AfterDismissed() { Dismissals++; return Task.CompletedTask; }
        public override Task Appearing() => Appear?.Invoke() ?? Task.CompletedTask;
    }
    private sealed class LegacyPage(Legacy model) : ContentPage, IHasVM
    { public ViewModelBase ViewModel { get; } = model; }
    private sealed class Locator(Func<Type, VisualElement> create) : IViewLocator
    {
        public void Initialize(Dictionary<Type, Type> pairs) { }
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => create(typeof(T));
        public VisualElement CreateAndBindVEFor(Type type) => create(type);
        public Type FindVEForViewModel(Type type) => typeof(LegacyPage);
        public Type FindViewModelForVE(Type type) => typeof(Legacy);
    }
}
