using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private readonly object popupPreparationGate = new();
    private readonly HashSet<Task> popupPreparations = [];

    internal Task<PopupNavigationResult<TResult>> PresentPopupAsync<TResult>(ViewModelBase origin,
        Func<CancellationToken, Task<View>> prepare, Func<bool> validOrigin, CancellationToken token,
        CommunityToolkit.Maui.IPopupOptions? options = null, bool ownsPreparation = false) =>
        Window.Dispatcher.DispatchAsync(async () => await await StartPopupAsync<TResult>(origin, prepare, validOrigin, token,
            options: options, ownsPreparation: ownsPreparation));

    // Returns after native installation; its inner task represents interaction and terminal cleanup.
    private async Task<Task<PopupNavigationResult<TResult>>> StartPopupAsync<TResult>(ViewModelBase origin,
        Func<CancellationToken, Task<View>> prepare, Func<bool> validOrigin, CancellationToken token,
        bool joinCurrentOperation = false, Action? onPresented = null, CommunityToolkit.Maui.IPopupOptions? options = null,
        Func<ModalPresentationScope>? observe = null, bool ownsPreparation = false)
    {
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (popupPreparationGate)
        {
            if (IsClosed) throw new ObjectDisposedException(nameof(MauiNavigationHost));
            popupPreparations.Add(prepared.Task);
        }
        void FinishPreparation() { lock (popupPreparationGate) { popupPreparations.Remove(prepared.Task); prepared.TrySetResult(); } }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closingToken, origin.Lifetime.Token);
            View? popup = null;
            ViewModelBase? preparedModel = null;
            try
            {
                return await RunPopupOperationAsync(async () =>
                {
                    if (!validOrigin()) throw new InvalidOperationException("The popup origin is no longer active.");
                    popup = await prepare(linked.Token);
                    if (ownsPreparation && popup is IHasVM candidate) preparedModel = candidate.ViewModel;
                    linked.Token.ThrowIfCancellationRequested();
                    if (!validOrigin()) throw new InvalidOperationException("The popup origin changed during preparation.");
                    var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var result = PopupOwnership.ShowAsync<TResult>(Window, popup, origin, token, presented, options, observe, preparedModel);
                    try
                    {
                        await Task.WhenAny(presented.Task, result);
                        if (result.IsCompleted) await result;
                        return result;
                    }
                    finally
                    {
                        if (presented.Task.IsCompletedSuccessfully || PopupOwnership.IsPresented(Window, popup)) onPresented?.Invoke();
                    }
                }, linked.Token, joinCurrentOperation);
            }
            catch
            {
                // A rejected raw popup has not transferred ownership. Registered preparation
                // owns its model and explicit children, never arbitrary binding contexts.
                if (preparedModel != null && popup != null && !PopupOwnership.IsPresented(Window, popup))
                    await RunPopupOperationAsync(async () =>
                    {
                        await preparedModel.Lifetime.DismissAsync(DismissalReason.PreparationFailed);
                        return true;
                    }, joinCurrentOperation: true);
                throw;
            }
        }
        finally { FinishPreparation(); }
    }

    // Popup preparation, native presentation and close guards share the window root gate.
    // The result wait is outside it, so a visible popup never blocks application root changes.
    internal async Task<T> RunPopupOperationAsync<T>(Func<Task<T>> callback, CancellationToken token = default,
        bool joinCurrentOperation = false)
    {
        try
        {
            if (joinCurrentOperation && InHostCallback()) return await callback();
            using var gate = await RootOperationGate.EnterAsync(Window, token);
            using var callbacks = MVVMCompass.Core.NavigationCallbackScope.Enter(this);
            return await callback();
        }
        finally { NotifyDeferredPopups(); }
    }

    private Task WaitForPopupPreparationsAsync()
    {
        lock (popupPreparationGate) return Task.WhenAll(popupPreparations.ToArray());
    }

    /// <summary>Closes the top popup on this window, awaiting its terminal cleanup. A covering ordinary modal is preserved.</summary>
    public Task ClosePopupAsync(CancellationToken cancellationToken = default) =>
        Window.Dispatcher.DispatchAsync(() => PopupOwnership.CloseTopAsync(Window, cancellationToken));
}
