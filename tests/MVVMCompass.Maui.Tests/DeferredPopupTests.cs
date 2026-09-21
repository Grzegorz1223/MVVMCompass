using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    private static TaskCompletionSource<PopupView> NextPopup() => PopupView.OpenedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<PopupView> Opened(TaskCompletionSource<PopupView> signal) => Settle(signal.Task);

    [Fact]
    public Task Cancelling_a_queued_warning_does_not_wait_for_an_unfinished_guard() => OnRootUI(async () =>
    {
        var context = await Open<Leaf>(); var leaf = (Leaf)context.Current!.ViewModel;
        var entered = RootSignal(); var release = RootSignal(); var opened = NextPopup();
        using var cancellation = new CancellationTokenSource(); PopupRequest<string?>? request = null;
        leaf.Guard = async () =>
        {
            request = leaf.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: cancellation.Token);
            entered.TrySetResult(); await release.Task; return false;
        };
        var navigation = leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        cancellation.Cancel();
        Assert.Equal(PopupRequestStatus.Cancelled, (await Settle(request!.Completion)).Status);
        Assert.False(navigation.IsCompleted); Assert.False(opened.Task.IsCompleted);
        release.TrySetResult(); Assert.Equal(NavigationStatus.GuardRejected, (await navigation).Status);
    });

    [Fact]
    public async Task Dispatcher_refusal_completes_a_deferred_request_with_an_explicit_failure()
    {
        var dispatcher = new RejectingRootDispatcher(); DispatcherProvider.SetCurrent(dispatcher);
        try
        {
            var context = await Open<Leaf>(); dispatcher.Reject = true;
            var request = ((Leaf)context.Current!.ViewModel).Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token);
            var result = await Settle(request.Completion);
            Assert.True(request.IsAccepted); Assert.Equal(PopupRequestStatus.Failed, result.Status);
            Assert.False(result.WasPresented); Assert.IsType<InvalidOperationException>(result.Error);
        }
        finally { dispatcher.Reject = false; TestDispatcher.Initialize(); }
    }

    [Fact]
    public Task Deferred_requests_are_isolated_to_their_origin_window() => OnRootUI(async () =>
    {
        var first = await Open<Leaf>(); var second = await Open<Leaf>(); var opened = NextPopup();
        var request = ((Leaf)second.Current!.ViewModel).Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token);
        var popup = await Opened(opened);
        Assert.Empty(first.Window.Navigation.ModalStack); Assert.Single(second.Window.Navigation.ModalStack);
        Success(await factory.SetRoot<Tabs>(first.Window, options: Enforced, cancellationToken: Token));
        Assert.False(request.Completion.IsCompleted); Assert.False(popup.ViewModel.IsDismissed);
        Success(await popup.ViewModel.Navigation.ClosePopup(Token));
        Assert.Equal(PopupRequestStatus.Completed, (await Settle(request.Completion)).Status);
    });

    [Fact]
    public Task Eligible_screen_requests_preserve_order_and_snapshot_parameters() => OnRootUI(async () =>
    {
        var context = await Open<Leaf>(); var origin = (Leaf)context.Current!.ViewModel;
        var signals = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<PopupView>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var order = new List<string>();
        services.GetRequiredService<PopupActions>().Opened = view =>
        { order.Add(view.ViewModel.Filter); signals[int.Parse(view.ViewModel.Filter)].TrySetResult(view); };
        var requests = new List<PopupRequest<string?>>();
        for (var i = 0; i < 3; i++)
        {
            var parameters = new Dictionary<string, object> { ["filter"] = i.ToString() };
            requests.Add(origin.Navigation.RequestPopup<PopupModel, string?>(parameters, cancellationToken: Token));
            parameters.Clear();
        }
        for (var i = 0; i < 3; i++)
        {
            var popup = await Settle(signals[i].Task);
            Assert.Single(context.Window.Navigation.ModalStack);
            Success(await popup.ViewModel.Navigation.ClosePopup(i.ToString(), Token));
            Assert.Equal(i.ToString(), (await Settle(requests[i].Completion)).PopupResult!.Result);
        }
        Assert.Equal(["0", "1", "2"], order);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Deferred_popup_from_preparation_or_activation_waits_for_navigation(bool activation, bool replaceRoot) => OnRootUI(async () =>
    {
        var context = await Open<Leaf>();
        var entered = RootSignal(); var release = RootSignal(); var opened = NextPopup();
        PopupRequest<string?>? request = null;
        async Task Callback(ApplicationRoot model)
        {
            if (request != null) return;
            request = model.Navigation.RequestPopup<PopupModel, string?>(new() { ["filter"] = "warning" }, cancellationToken: Token);
            entered.TrySetResult();
            Assert.True(request.IsAccepted);
            Assert.False(request.Completion.IsCompleted);
            Assert.True(context.Host.IsContentCallback, "Activation must remain in the host callback scope.");
            await Assert.ThrowsAsync<InvalidOperationException>(() => model.Navigation.DisplayPopup<PopupModel, string?>(cancellationToken: Token));
            await release.Task;
        }
        var actions = services.GetRequiredService<RootActions>();
        if (activation) actions.Appear = Callback; else actions.Before = Callback;
        var navigation = replaceRoot ? factory.SetRoot<ApplicationRoot>(context.Window, cancellationToken: Token)
            : ((Leaf)context.Current!.ViewModel).Navigation.NavigateTo<ApplicationRoot>(cancellationToken: Token);
        await Task.WhenAny(entered.Task, navigation).WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.False(navigation.IsCompleted, navigation.IsCompleted ? $"Navigation finished before activation: {navigation.Result.Status}: {navigation.Result.Error}" : "");
        Assert.False(opened.Task.IsCompleted); Assert.False(navigation.IsCompleted);
        release.TrySetResult(); Success(await navigation.WaitAsync(TimeSpan.FromSeconds(3), Token));
        await Task.WhenAny(opened.Task, request!.Completion).WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.False(request.Completion.IsCompleted, request.Completion.IsCompleted ? $"Unexpected early completion: {request.Completion.Result.Status}: {request.Completion.Result.Error}" : "");
        var popup = await opened.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.Equal("warning", popup.ViewModel.Filter);
        Assert.False(request!.Completion.IsCompleted);
        Assert.False(actions.Created.Last().IsBusy);
        Success(await popup.ViewModel.Navigation.ClosePopup<string?>(null, Token));
        var result = await Settle(request.Completion);
        Assert.Equal(PopupRequestStatus.Completed, result.Status); Assert.True(result.WasPresented);
        Assert.True(result.PopupResult!.HasResult); Assert.Null(result.PopupResult.Result);
        Assert.True(popup.ViewModel.Resource.Disposed); Assert.Equal(1, popup.ViewModel.Dismissals);
    });

    [Theory]
    [InlineData("app")]
    [InlineData("toolbar")]
    [InlineData("native")]
    [InlineData("root")]
    [InlineData("flyout")]
    public Task Rejected_guard_can_explain_after_every_navigation_entry_point(string path) => OnRootUI(async () =>
    {
        var context = await Open<Flyout>();
        var parent = (Leaf)context.Current!.ViewModel;
        Success(await parent.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var origin = (Leaf)context.Deepest.Current!.ViewModel;
        var opened = NextPopup(); PopupRequest<string?>? request = null;
        var exited = false;
        origin.Guard = () =>
        {
            request = origin.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token);
            Assert.False(opened.Task.IsCompleted); exited = true; return Task.FromResult(false);
        };
        switch (path)
        {
            case "app": Assert.Equal(NavigationStatus.GuardRejected, (await origin.Navigation.NavigateBack(Token)).Status); break;
            case "toolbar": await context.ExecuteLeadingAsync(); break;
            case "native": Assert.True(context.RequestPlatformBack()); await context.NativeBackCompletion; break;
            case "root": Assert.Equal(NavigationStatus.GuardRejected, (await factory.SetRoot<Leaf>(context.Window, cancellationToken: Token)).Status); break;
            case "flyout": context.ActiveChain().SelectMany(item => item.Destinations).Single(item => item.Id == "other").SelectCommand.Execute(null); break;
        }
        var popup = await Opened(opened);
        Assert.True(exited); Assert.False(origin.IsDismissed);
        Success(await popup.ViewModel.Navigation.ClosePopup("explanation", Token));
        Assert.Equal("explanation", (await Settle(request!.Completion)).PopupResult!.Result);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Abandoned_destination_never_presents_its_queued_popup(bool superseded) => OnRootUI(async () =>
    {
        var context = await Open<Leaf>(); var opened = NextPopup();
        PopupRequest<string?>? request = null; RootTransitionHandle? replacement = null;
        var actions = services.GetRequiredService<RootActions>();
        actions.Before = model =>
        {
            request = model.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token);
            if (superseded)
            {
                replacement = factory.RequestRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token);
                return Task.CompletedTask;
            }
            throw new InvalidOperationException("failed destination preparation");
        };
        var result = await ((Leaf)context.Current!.ViewModel).Navigation.NavigateTo<ApplicationRoot>(cancellationToken: Token);
        Assert.Equal(superseded ? NavigationStatus.Superseded : NavigationStatus.Failed, result.Status);
        if (replacement != null) Success(await Settle(replacement.Completion));
        Assert.Equal(PopupRequestStatus.PreparationFailed, (await Settle(request!.Completion)).Status);
        Assert.False(opened.Task.IsCompleted); Assert.True(actions.Created.Single().Resource.Disposed);
    });

    [Fact]
    public Task Successful_departure_invalidates_a_guard_warning_even_when_the_retained_origin_returns() => OnRootUI(async () =>
    {
        var context = await Open<Leaf>(); var leaf = (Leaf)context.Current!.ViewModel;
        var opened = NextPopup(); PopupRequest<string?>? request = null;
        leaf.Guard = () => { request = leaf.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token); return Task.FromResult(true); };
        Success(await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        Success(await ((Leaf)context.Current!.ViewModel).Navigation.NavigateBack(Token));
        Assert.Same(leaf, context.Current.ViewModel);
        Assert.Equal(PopupRequestStatus.InvalidOrigin, (await Settle(request!.Completion)).Status);
        Assert.False(opened.Task.IsCompleted);
    });

    [Fact]
    public Task Navigation_cancellation_does_not_cancel_the_still_active_origins_explanation() => OnRootUI(async () =>
    {
        var context = await Open<Leaf>(); var leaf = (Leaf)context.Current!.ViewModel;
        using var navigationCancellation = new CancellationTokenSource();
        var opened = NextPopup(); PopupRequest<string?>? request = null;
        leaf.Guard = () =>
        {
            request = leaf.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: Token);
            navigationCancellation.Cancel(); return Task.FromResult(false);
        };
        Assert.Equal(NavigationStatus.Cancelled, (await leaf.Navigation.NavigateTo<Detail>(cancellationToken: navigationCancellation.Token)).Status);
        var popup = await Opened(opened); Success(await popup.ViewModel.Navigation.ClosePopup(Token));
        Assert.Equal(PopupRequestStatus.Completed, (await Settle(request!.Completion)).Status);
    });

    [Theory]
    [InlineData("caller")]
    [InlineData("root")]
    [InlineData("window")]
    public Task Queued_popup_behind_an_open_popup_settles_without_stale_presentation(string end) => OnRootUI(async () =>
    {
        var context = await Open<Leaf>(); var leaf = (Leaf)context.Current!.ViewModel;
        var (cover, coveringResult) = await OpenPopup(leaf); var next = NextPopup();
        using var cancellation = new CancellationTokenSource();
        var request = leaf.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: cancellation.Token);
        switch (end)
        {
            case "caller": cancellation.Cancel(); break;
            case "root": Success(await factory.SetRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token)); break;
            case "window": await context.Host.DisposeAsync(); break;
        }
        var result = await Settle(request.Completion);
        Assert.Equal(end switch { "caller" => PopupRequestStatus.Cancelled, "root" => PopupRequestStatus.RootReplaced, _ => PopupRequestStatus.WindowClosed }, result.Status);
        Assert.False(result.WasPresented); Assert.False(next.Task.IsCompleted);
        if (end == "caller") Success(await cover.ViewModel.Navigation.ClosePopup(Token));
        await Settle(coveringResult);
    });

    [Fact]
    public Task Screen_requests_wait_for_popup_cleanup_and_duplicate_keys_share_one_result() => OnRootUI(async () =>
    {
        var context = await Open<Leaf>(); var leaf = (Leaf)context.Current!.ViewModel;
        var (cover, coveringResult) = await OpenPopup(leaf); var next = NextPopup();
        var request = leaf.Navigation.RequestPopup<PopupModel, string?>(new() { ["filter"] = "first" }, "warning", Token);
        var duplicate = leaf.Navigation.RequestPopup<PopupModel, string?>(new() { ["filter"] = "second" }, "warning", new CancellationToken(true));
        Assert.Same(request, duplicate);
        var entered = RootSignal(); var release = RootSignal();
        services.GetRequiredService<PopupActions>().Cleanup = async _ => { entered.TrySetResult(); await release.Task; };
        var closing = cover.ViewModel.Navigation.ClosePopup(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token); Assert.False(next.Task.IsCompleted);
        release.TrySetResult(); Success(await closing); await coveringResult;
        services.GetRequiredService<PopupActions>().Cleanup = null;
        var popup = await Opened(next); Assert.Equal("first", popup.ViewModel.Filter);
        Success(await popup.ViewModel.Navigation.ClosePopup("once", Token));
        Assert.Same(await Settle(request.Completion), await Settle(duplicate.Completion));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Popup_initialization_and_rejected_popup_guards_can_request_nested_explanations(bool guard) => OnRootUI(async () =>
    {
        var context = await Open<Leaf>(); var leaf = (Leaf)context.Current!.ViewModel;
        PopupRequest<string?>? request = null;
        var actions = services.GetRequiredService<PopupActions>();
        var childOpened = new TaskCompletionSource<PopupView>(TaskCreationOptions.RunContinuationsAsynchronously);
        actions.Opened = view => { if (view.ViewModel.Filter == "child") childOpened.TrySetResult(view); };
        if (!guard) actions.Before = model =>
        {
            actions.Before = null;
            request = model.Navigation.RequestPopup<PopupModel, string?>(new() { ["filter"] = "child" }, cancellationToken: Token);
            return Task.CompletedTask;
        };
        var (parent, parentResult) = await OpenPopup(leaf);
        if (guard)
        {
            parent.ViewModel.Guard = () => { request = parent.ViewModel.Navigation.RequestPopup<PopupModel, string?>(new() { ["filter"] = "child" }, cancellationToken: Token); return Task.FromResult(false); };
            Assert.Equal(NavigationStatus.GuardRejected, (await parent.ViewModel.Navigation.ClosePopup(Token)).Status);
        }
        var child = await Settle(childOpened.Task);
        Assert.Equal(2, context.Window.Navigation.ModalStack.Count);
        Success(await child.ViewModel.Navigation.ClosePopup(Token));
        Assert.Equal(PopupRequestStatus.Completed, (await Settle(request!.Completion)).Status);
        parent.ViewModel.Guard = null;
        Success(await parent.ViewModel.Navigation.ClosePopup(Token)); await Settle(parentResult);
    });

    [Fact]
    public Task Cancelling_a_presented_deferred_result_preserves_visible_popup_ownership() => OnRootUI(async () =>
    {
        var context = await Open<Leaf>(); var leaf = (Leaf)context.Current!.ViewModel; var opened = NextPopup();
        using var cancellation = new CancellationTokenSource();
        var request = leaf.Navigation.RequestPopup<PopupModel, string?>(cancellationToken: cancellation.Token);
        var popup = await Opened(opened); cancellation.Cancel();
        var result = await Settle(request.Completion);
        Assert.Equal(PopupRequestStatus.Cancelled, result.Status); Assert.True(result.WasPresented);
        Assert.False(popup.ViewModel.IsDismissed); Assert.False(popup.ViewModel.Resource.Disposed);
        Assert.Single(context.Window.Navigation.ModalStack);
        Success(await popup.ViewModel.Navigation.ClosePopup("later", Token));
        Assert.True(popup.ViewModel.Resource.Disposed); Assert.Equal(1, popup.ViewModel.Dismissals);
        Assert.Same(result, await request.Completion);
    });

    public sealed class PopupLifecycleActions
    {
        internal Func<LifecyclePopupModel, Task>? Before;
        internal LifecyclePopupModel? Created;
        internal TaskCompletionSource<LifecyclePopupView> Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class LifecyclePopupModel(INavigationService navigation, Resource resource, PopupLifecycleActions actions) : ViewModelBase
    {
        internal readonly INavigationService Navigation = navigation;
        internal readonly Resource Resource = resource;
        internal readonly List<string> Events = [];
        internal bool Subscribed;
        public override Task GetParameters(Dictionary<string, object> parameters) { Events.Add("parameters"); return Task.CompletedTask; }
        public override async Task BeforeFirstShown()
        {
            actions.Created = this; Events.Add("initialize"); Subscribed = true;
            Ownership.RegisterCleanup(() => { Assert.True(Subscribed); Subscribed = false; Events.Add("release"); return Task.CompletedTask; });
            if (actions.Before != null) await actions.Before(this);
        }
        public override Task Appearing() { Events.Add("appearing"); return Task.CompletedTask; }
        public override Task Deactivated() { Events.Add("deactivated"); return Task.CompletedTask; }
    }
    public sealed class LifecyclePopupView : PopupViewBase<LifecyclePopupModel, string?>
    {
        public LifecyclePopupView(LifecyclePopupModel model, PopupLifecycleActions actions) : base(model)
        { Opened += (_, _) => { model.Events.Add("opened"); actions.Opened.TrySetResult(this); }; }
    }

    [Theory]
    [InlineData("result")]
    [InlineData("back")]
    [InlineData("root")]
    [InlineData("window")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public Task Registered_popup_subscription_lifecycle_uses_preparation_and_exactly_once_owned_cleanup(string end) => OnRootUI(async () =>
    {
        var context = await Open<Leaf>(); var leaf = (Leaf)context.Current!.ViewModel;
        var actions = services.GetRequiredService<PopupLifecycleActions>();
        using var cancellation = new CancellationTokenSource();
        if (end is "failed" or "cancelled") actions.Before = _ =>
        {
            if (end == "failed") throw new InvalidOperationException("after subscribing");
            cancellation.Cancel(); return Task.CompletedTask;
        };
        var request = leaf.Navigation.RequestPopup<LifecyclePopupModel, string?>(new() { ["value"] = 1 }, cancellationToken: cancellation.Token);
        if (end is not ("failed" or "cancelled"))
        {
            var popup = await Settle(actions.Opened.Task);
            Assert.True(popup.ViewModel.Subscribed); Assert.Equal(["parameters", "initialize", "opened"], popup.ViewModel.Events);
            switch (end)
            {
                case "result": Success(await popup.ViewModel.Navigation.ClosePopup("OK", Token)); break;
                case "back": Assert.True(context.RequestPlatformBack()); await context.NativeBackCompletion; break;
                case "root": Success(await factory.SetRoot<Tabs>(context.Window, options: Enforced, cancellationToken: Token)); break;
                case "window": await context.Host.DisposeAsync(); break;
            }
        }
        var result = await Settle(request.Completion); var model = actions.Created!;
        Assert.Equal(end switch { "failed" => PopupRequestStatus.PreparationFailed, "cancelled" => PopupRequestStatus.Cancelled, _ => PopupRequestStatus.Completed }, result.Status);
        Assert.False(model.Subscribed); Assert.True(model.Resource.Disposed);
        Assert.Equal(1, model.Events.Count(item => item == "deactivated")); Assert.Equal(1, model.Events.Count(item => item == "release"));
        Assert.DoesNotContain("appearing", model.Events);
        await model.DismissAsync(); Assert.Equal(1, model.Events.Count(item => item == "release"));
    });
}
