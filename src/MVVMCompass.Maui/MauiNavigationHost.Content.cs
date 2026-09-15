using MVVMCompass.Core;
using MVVMCompass.Services;
using Microsoft.Maui.Dispatching;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private async Task<NavigationOutcome<MauiNavigationPage?>> RunRemovalAsync(NavigationRequest<object?> request,
        MauiNavigationOperation operation, bool animated, CancellationToken cancellationToken)
    {
        if (IsContentCallback) return new(NavigationStatus.Reentrant, null, false);
        NavigationContext? scope = null;
        await DispatchContentAsync(() => scope = CurrentContentNavigation);
        if (scope == null) return await RunPageRemovalAsync(request, operation, animated, cancellationToken);
        var options = new NavigationRequestOptions { Origin = request.Origin, Priority = request.Priority,
            CoalescingKey = request.CoalescingKey, RejectIfBusy = request.RejectIfBusy };
        var result = await (operation switch
        {
            MauiNavigationOperation.Back => scope.BackAsync(options, cancellationToken),
            MauiNavigationOperation.PopToRoot => scope.PopToRootAsync(options, cancellationToken),
            _ => scope.CloseModalAsync(options, cancellationToken)
        });
        MauiNavigationPage? page = null;
        if (result.IsSuccess) await DispatchContentAsync(() => page = CurrentPage);
        return new(result.Status, page, result.HasCommitted, result.Error, result.CleanupErrors);
    }

    /// <summary>Gets the custom content scope at the visible window or modal root.</summary>
    public NavigationContext? CurrentContentNavigation
    {
        get
        {
            var visible = Window.Navigation.ModalStack.LastOrDefault() ?? Window.Page;
            return ownedPages.Select(item => item.Entry.ViewModel).OfType<NavigationContext>()
                .LastOrDefault(context => !context.IsClosed && visible != null && context.OwnsPage(visible));
        }
    }

    /// <summary>Installs a persistent custom toolbar and view-based stacks in this window.</summary>
    public async Task<NavigationOutcome<MauiNavigationRoot<NavigationContext>>> ReplaceContentRootAsync(
        NavigationRequest<NavigationDefinition> request, Action<NavigationView>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parameter);
        var result = await RunReplacementAsync(request,
            PlainFactory(() => new NavigationContext(this, request.Parameter, false), context =>
            {
                var page = context.CreatePage();
                configure?.Invoke(context.View);
                return page;
            }, null),
            async (entry, definition, token) =>
            {
                entry.ViewModel.Attach(entry);
                await entry.InitializeAsync(definition, token);
            }, false, cancellationToken);
        CurrentContentNavigation?.RefreshState();
        return result;
    }

    internal NavigationEntry<ViewModelBase> ClaimContentScreen(ViewModelBase model) =>
        Claim(model, () => coordinator.AttachEntry(model, model.Lifetime));

    internal NavigationEntry<NavigationContext> CreateContentScope(NavigationContext context) =>
        Claim(context, () => coordinator.CreateEntry(context));

    internal Task DispatchContentAsync(Action callback) => Window.Dispatcher.DispatchAsync(callback);
    internal bool IsContentCallback => InHostCallback() || NavigationCallbackScope.IsActive(coordinator);

    internal async Task<NavigationOutcome<T>> RunContentOperationAsync<T>(NavigationContext scope,
        NavigationRequestOptions? options, Func<NavigationOperationContext<object?>, Task<T>> callback, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closingToken);
        var request = new NavigationRequest<object?>(null)
        {
            Origin = options?.Origin, Priority = options?.Priority ?? NavigationPriority.Normal,
            CoalescingKey = options?.CoalescingKey, RejectIfBusy = options?.RejectIfBusy ?? false
        };
        return await coordinator.RunAsync(request, async operation =>
        {
            using var callbacks = NavigationCallbackScope.Enter(this);
            using var gate = await RootOperationGate.EnterAsync(Window, operation.CancellationToken);
            if (IsClosed || scope.IsClosed) operation.Reject(NavigationStatus.InvalidOrigin);
            var parent = execution.Value;
            var frame = new Execution(this, parent) { Committed = () => operation.HasCommitted };
            execution.Value = frame;
            activeCancellation = linked;
            try { return await callback(operation); }
            finally
            {
                activeCancellation = null; frame.Executing = false; execution.Value = parent;
                ObserveNativeTree();
            }
        }, linked.Token);
    }

    internal async Task PresentContentModalAsync(NavigationContext context, NavigationEntry<NavigationContext> entry)
    {
        var page = context.PresentedPage;
        // The custom scope supplies its own guarded dismissal gesture. A native sheet must not dismiss first.
        Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page.SetModalPresentationStyle(page.On<Microsoft.Maui.Controls.PlatformConfiguration.iOS>(),
            Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.UIModalPresentationStyle.FullScreen);
        var owned = new MauiNavigationPage<NavigationContext>(page, page, entry, Window.Page!, true);
        TrackPage(owned);
        try
        {
            containerMutation++;
            await WaitForNativePresentationAsync(Window.Navigation.ModalStack.LastOrDefault() ?? Window.Page!, entry.Lifetime.Token);
            await Window.Navigation.PushModalAsync(page, context.View.Presenter.AnimateNavigation);
            await WaitForNativePresentationAsync(page, entry.Lifetime.Token);
            owned.WasPresented = Window.Navigation.ModalStack.Contains(page);
            if (!owned.WasPresented) throw new InvalidOperationException("The modal was not installed.");
        }
        finally
        {
            containerMutation--;
            if (!Window.Navigation.ModalStack.Contains(page)) ForgetPages([owned]);
        }
    }

    private void ContentModalPopping(object? sender, ModalPoppingEventArgs args)
    {
        if (containerMutation != 0 || IsClosed || CurrentContentNavigation is not { IsModal: true } context ||
            !context.OwnsPage(args.Modal)) return;
        args.Cancel = true;
        context.RequestPlatformModalClose();
    }

    internal async Task CloseContentModalAsync(NavigationContext context)
    {
        var page = context.PresentedPage;
        if (!ReferenceEquals(Window.Navigation.ModalStack.LastOrDefault(), page))
            throw new InvalidOperationException("Only the visible modal can be closed.");
        try
        {
            containerMutation++;
            await Window.Navigation.PopModalAsync(context.View.Presenter.AnimateNavigation);
            await FinishNativeRemovalAsync();
        }
        finally { containerMutation--; }
        if (Window.Navigation.ModalStack.Contains(page)) throw new InvalidOperationException("The platform retained the modal.");
        await DismissTreeAsync(page, context.ScopeEntry, ViewModelTree.Collect(page), DismissalReason.Back);
        await ActivateVisibleEntriesAsync();
        CurrentContentNavigation?.RefreshState();
    }
}
