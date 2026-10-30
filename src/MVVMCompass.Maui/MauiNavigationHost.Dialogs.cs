using CommunityToolkit.Maui;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private bool windowAlertPresenting;

    internal Task<PopupNavigationResult<TResult>> PresentWindowPopupAsync<TResult>(MauiNavigationRoot<NavigationContext> root,
        View popup, IPopupOptions? options, CancellationToken token)
    {
        if (IsContentCallback)
            throw new InvalidOperationException("Present popups outside navigation callbacks, or use RequestPopup for a registered popup.");
        return Window.Dispatcher.DispatchAsync(async () =>
        {
            if (IsContentCallback)
                throw new InvalidOperationException("Present popups outside navigation callbacks, or use RequestPopup for a registered popup.");
            ValidateWindowDialog(root);
            NavigationContext? presentationContext = null;
            return await await StartRegisteredPopupAsync<TResult>(root.Entry.ViewModel.StackRoot!.ViewModel,
                _ => Task.FromResult(popup), () => { presentationContext = ValidateWindowDialog(root); return true; }, token, options: options,
                observe: () => ModalPresentationScope.BeginWindow(presentationContext!, root.Entry.ViewModel, ModalPresentationKind.Popup));
        });
    }

    internal Task<TResult> PresentWindowAlertAsync<TResult>(MauiNavigationRoot<NavigationContext> root,
        Func<Page, Task<TResult>> present, CancellationToken token)
    {
        // A native dispatcher need not flow ExecutionContext. Preserve the existing guard
        // scope (including its active flag) so its alert does not acquire the same gate twice.
        var callbackContext = IsContentCallback ? ExecutionContext.Capture() : null;
        return Window.Dispatcher.DispatchAsync(() =>
        {
            if (callbackContext == null) return PresentAsync();
            Task<TResult>? result = null;
            ExecutionContext.Run(callbackContext, _ => result = PresentAsync(), null);
            return result!;
        });

        async Task<TResult> PresentAsync()
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, closingToken, root.Entry.Lifetime.Token);
            // Alerts can be awaited by guards already holding the window gate. Only native installation
            // holds the gate; the interaction wait must not prevent logout or window cleanup.
            var showing = await RunPopupOperationAsync(() =>
            {
                wait.Token.ThrowIfCancellationRequested();
                var context = ValidateWindowDialog(root);
                windowAlertPresenting = true;
                try
                {
                    var observation = ModalPresentationScope.BeginWindow(context, root.Entry.ViewModel, ModalPresentationKind.Alert);
                    return Task.FromResult(ObserveAsync(observation, context.HandledPage));
                }
                catch { windowAlertPresenting = false; throw; }
            }, wait.Token, joinCurrentOperation: true);
            return await showing.WaitAsync(wait.Token);

            async Task<TResult> ObserveAsync(ModalPresentationScope observation, Page page)
            {
                try { return await present(page); }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Window alert presentation"); throw; }
                finally { observation.Dispose(); windowAlertPresenting = false; NotifyDeferredPopups(); }
            }
        }
    }

    private NavigationContext ValidateWindowDialog(MauiNavigationRoot<NavigationContext> root)
    {
        ObjectDisposedException.ThrowIf(IsClosed, Window);
        if (!ReferenceEquals(CurrentRoot, root) || root.Entry.Lifetime.IsDismissed)
            throw new InvalidOperationException("The window root changed before the dialog could be presented.");
        if (windowAlertPresenting || PopupOwnership.HasSessions(Window)
            || Window.Navigation.ModalStack.LastOrDefault() is { } modal && PopupOwnership.IsPopupPage(modal))
            throw new ModalPresentationBusyException();
        var context = CurrentContentNavigation;
        if (context is not { IsActive: true } || context.Deepest.Current is not { } current)
            throw new InvalidOperationException("The window has no active navigation page for dialog presentation.");
        if (!current.View.IsAttachedTo(Window) || context.HandledPage.Handler?.PlatformView == null)
            throw new InvalidOperationException("Dialog presentation requires the active view, page and window to have native handlers.");
        return context;
    }
}
