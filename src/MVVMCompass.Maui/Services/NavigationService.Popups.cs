using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;

namespace MVVMCompass.Services;

internal sealed partial class NavigationService
{
    private Popup? popup;
    private ViewModelBase? popupModel;
    private bool closingPopup;
    internal void BindPopup(NavigationContext owner, Popup view, ViewModelBase model)
    { context = owner; popup = view; popupModel = model; }

    public Task<PopupNavigationResult<TResult>> DisplayPopup<TViewModel, TResult>(Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase => context == null
        ? Task.FromException<PopupNavigationResult<TResult>>(new InvalidOperationException("A popup requires an active navigation origin."))
        : context.Window.Dispatcher.DispatchAsync(async () =>
    {
        if (!Valid) throw new InvalidOperationException("A popup requires an active navigation origin.");
        if (context!.Host.IsContentCallback) throw new InvalidOperationException("Present popups outside initialization and navigation callbacks.");
        return await context.Host.PresentRegisteredPopupAsync<TResult>(screen!.ViewModel,
            token => registry.PreparePopupAsync<TViewModel, TResult>(context, parameters, token), cancellationToken);
    });

    public Task<NavigationResult> ClosePopup<TResult>(TResult result, CancellationToken cancellationToken = default) =>
        popup is Popup<TResult> typed ? ClosePopupCore(() => typed.CloseAsync(result, cancellationToken), cancellationToken)
            : Task.FromResult(new NavigationResult(NavigationStatus.InvalidOrigin));

    public Task<NavigationResult> ClosePopup(CancellationToken cancellationToken = default) =>
        popup != null ? ClosePopupCore(() => popup.CloseAsync(cancellationToken), cancellationToken) : Task.FromResult(Invalid);

    private async Task<NavigationResult> ClosePopupCore(Func<Task> close, CancellationToken token)
    {
        if (popup == null || context == null || popupModel == null) return Invalid;
        return await context.Window.Dispatcher.DispatchAsync(async () =>
        {
            if (popupModel.IsDismissed || !PopupOwnership.IsTop(context.Window, popup)) return Invalid;
            if (closingPopup) return new(NavigationStatus.Reentrant);
            closingPopup = true;
            try
            {
                token.ThrowIfCancellationRequested();
                await close();
                return new NavigationResult(NavigationStatus.Completed, true);
            }
            catch (PopupGuardRejectedException) { return new(NavigationStatus.GuardRejected); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(NavigationStatus.Cancelled); }
            catch (Exception error) { return new(NavigationStatus.Failed, popupModel.IsDismissed, error); }
            finally { closingPopup = false; }
        });
    }
}

internal sealed class PopupGuardRejectedException : InvalidOperationException;
