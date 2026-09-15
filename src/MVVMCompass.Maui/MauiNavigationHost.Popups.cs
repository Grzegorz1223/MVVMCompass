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

    internal async Task<PopupNavigationResult<TResult>> PresentRegisteredPopupAsync<TResult>(ViewModelBase origin,
        Func<CancellationToken, Task<View>> prepare, CancellationToken token)
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
            return await Window.Dispatcher.DispatchAsync(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, closingToken);
                var content = CurrentContentNavigation;
                View? popup = null;
                try
                {
                    popup = await prepare(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    if (content == null || CurrentContentNavigation != content || !content.AllScreens().Any(item => item.ViewModel == origin && content.ContainsActiveOrigin(item.Entry)))
                        throw new InvalidOperationException("The popup origin changed during preparation.");
                    var showing = PopupOwnership.ShowAsync<TResult>(Window, popup, origin, token);
                    FinishPreparation();
                    return await showing;
                }
                catch
                {
                    if (popup != null && popup.Parent == null)
                        await MVVMCompass.Core.NavigationLifetimeGroup.DismissAsync(ViewModelTree.Collect(popup).Select(model => model.Lifetime), MVVMCompass.Core.DismissalReason.PreparationFailed);
                    throw;
                }
                finally { FinishPreparation(); }
            });
        }
        finally { FinishPreparation(); }
    }

    private Task WaitForPopupPreparationsAsync()
    {
        lock (popupPreparationGate) return Task.WhenAll(popupPreparations.ToArray());
    }

    /// <summary>Closes the top popup on this window, awaiting its terminal cleanup. A covering ordinary modal is preserved.</summary>
    public Task ClosePopupAsync(CancellationToken cancellationToken = default) =>
        Window.Dispatcher.DispatchAsync(() => PopupOwnership.CloseTopAsync(Window, cancellationToken));
}
