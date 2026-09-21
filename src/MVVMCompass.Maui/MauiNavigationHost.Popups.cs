using Microsoft.Maui.Dispatching;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private readonly object popupPreparationGate = new();
    private readonly HashSet<Task> popupPreparations = [];

    /// <summary>Presents a registered popup on this window and awaits its result and owned cleanup.</summary>
    /// <remarks>Popup duration does not hold the operation queue. Cancellation stops the result wait; a presented popup remains owned until closed.</remarks>
    public Task<PopupNavigationResult<TResult>> DisplayPopupAsync<TViewModel, TResult>(ViewModelBase? origin = null,
        Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default) where TViewModel : ViewModelBase
    {
        if (IsClosed) return Task.FromException<PopupNavigationResult<TResult>>(new ObjectDisposedException(nameof(MauiNavigationHost)));
        if (InHostCallback()) return Task.FromException<PopupNavigationResult<TResult>>(new InvalidOperationException("Present a popup outside this host's lifecycle callbacks."));
        var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (popupPreparationGate)
        {
            if (IsClosed) return Task.FromException<PopupNavigationResult<TResult>>(new ObjectDisposedException(nameof(MauiNavigationHost)));
            popupPreparations.Add(preparation.Task);
        }
        return PresentAsync();

        async Task<PopupNavigationResult<TResult>> PresentAsync()
        {
            try
            {
                return await Window.Dispatcher.DispatchAsync(async () =>
                {
                    var parent = execution.Value;
                    var frame = new Execution(this, parent);
                    execution.Value = frame;
                    try
                    {
                        closingToken.ThrowIfCancellationRequested();
                        if (origin != null && !ViewModelTree.Collect(Window.Page, true).Contains(origin))
                            throw new InvalidOperationException("The popup origin belongs to another presentation.");
                        return await composition.DisplayPopupResultAsync<TViewModel, TResult>(origin, parameters,
                            cancellationToken, closingToken, () =>
                            {
                                frame.Executing = false;
                                Prepared();
                            });
                    }
                    finally { frame.Executing = false; execution.Value = parent; }
                });
            }
            finally { Prepared(); }
        }

        void Prepared()
        {
            lock (popupPreparationGate)
            {
                popupPreparations.Remove(preparation.Task);
                preparation.TrySetResult();
            }
        }
    }

    internal Task<PopupNavigationResult<TResult>> PresentRegisteredPopupAsync<TResult>(ViewModelBase origin,
        Func<CancellationToken, Task<View>> prepare, Func<bool> validOrigin, CancellationToken token) =>
        Window.Dispatcher.DispatchAsync(async () => await await StartRegisteredPopupAsync<TResult>(origin, prepare, validOrigin, token));

    // Returns after native installation; its inner task represents interaction and terminal cleanup.
    private async Task<Task<PopupNavigationResult<TResult>>> StartRegisteredPopupAsync<TResult>(ViewModelBase origin,
        Func<CancellationToken, Task<View>> prepare, Func<bool> validOrigin, CancellationToken token,
        bool joinCurrentOperation = false, Action? onPresented = null)
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
            try
            {
                return await RunPopupOperationAsync(async () =>
                {
                    if (!validOrigin()) throw new InvalidOperationException("The popup origin is no longer active.");
                    popup = await prepare(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    if (!validOrigin()) throw new InvalidOperationException("The popup origin changed during preparation.");
                    var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var result = PopupOwnership.ShowAsync<TResult>(Window, popup, origin, token, presented);
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
                if (popup != null && !PopupOwnership.IsPresented(Window, popup))
                    await RunPopupOperationAsync(async () =>
                    {
                        await MVVMCompass.Core.NavigationLifetimeGroup.DismissAsync(ViewModelTree.Collect(popup).Select(model => model.Lifetime), MVVMCompass.Core.DismissalReason.PreparationFailed);
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
