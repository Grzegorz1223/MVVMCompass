using MVVMCompass.Core;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private readonly object deferredPopupGate = new();
    private List<DeferredPopup>? deferredPopups;
    private TaskCompletionSource? deferredPopupChanged;
    private bool drainingDeferredPopups;

    internal PopupRequest<TResult> EnqueuePopup<TViewModel, TResult>(PopupRequestOrigin origin,
        Func<CancellationToken, Task<View>> prepare, string? key, CancellationToken token) where TViewModel : ViewModelBase
    {
        var start = false;
        DeferredPopup<TResult> request;
        lock (deferredPopupGate)
        {
            if (key != null && deferredPopups?.OfType<DeferredPopup<TResult>>().FirstOrDefault(item =>
                item.Key == key && item.PopupType == typeof(TViewModel) && ReferenceEquals(item.Origin.Model, origin.Model)
                && item.Origin.Version == origin.Version) is { } existing) return existing.Handle;
            if (IsClosed || token.IsCancellationRequested)
            {
                var rejected = new PopupRequest<TResult>(false);
                rejected.Complete(new(IsClosed ? PopupRequestStatus.WindowClosed : PopupRequestStatus.Cancelled));
                return rejected;
            }
            request = new(this, origin, typeof(TViewModel), prepare, key, token);
            (deferredPopups ??= []).Add(request);
            request.Bind();
            NotifyDeferredPopups();
            if (!drainingDeferredPopups) { drainingDeferredPopups = true; start = true; }
        }
        if (start)
        {
            // Admission may occur inside an awaited callback. Only the host gates establish when
            // the request may run; a captured ExecutionContext must never carry that callback in.
            if (ExecutionContext.IsFlowSuppressed()) _ = Task.Run(DrainDeferredPopupsAsync);
            else using (ExecutionContext.SuppressFlow()) _ = Task.Run(DrainDeferredPopupsAsync);
        }
        return request.Handle;
    }

    internal void NotifyDeferredPopups()
    {
        lock (deferredPopupGate)
        {
            if (deferredPopups is not { Count: > 0 }) return;
            deferredPopupChanged?.TrySetResult();
            deferredPopupChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private bool DeferredPresentationSettled => !coordinator.IsBusy && nativeReconciliation is not { IsCompleted: false }
        && nativeDepartures.Count == 0 && !ownedPages.Select(item => item.Entry.ViewModel).OfType<NavigationContext>().Any(item => item.IsNavigating);

    private async Task DrainDeferredPopupsAsync()
    {
        while (true)
        {
            Task changed;
            lock (deferredPopupGate)
            {
                if (deferredPopups is not { Count: > 0 }) { drainingDeferredPopups = false; deferredPopupChanged = null; return; }
                changed = deferredPopupChanged!.Task;
            }
            try
            {
                await DispatchNavigationAsync(Window.Dispatcher, async () =>
                {
                    DeferredPopup[] requests;
                    lock (deferredPopupGate) requests = deferredPopups!.Where(item => !item.Started).ToArray();
                    // Cancellation of a queued request never needs to wait for a dialog or a guard.
                    foreach (var request in requests)
                        if (IsClosed || request.Token.IsCancellationRequested)
                            request.Complete(IsClosed ? PopupRequestStatus.WindowClosed : PopupRequestStatus.Cancelled);
                    if (IsClosed || !DeferredPresentationSettled) return;
                    using var gate = RootOperationGate.TryEnter(Window);
                    if (gate == null || !DeferredPresentationSettled) return;
                    foreach (var request in requests)
                    {
                        if (request.Finished) continue;
                        if (request.Origin.Ended is { } ended) { request.Complete(ended); continue; }
                        if (!request.Origin.IsCurrent) { request.Complete(PopupRequestStatus.InvalidOrigin); continue; }
                        if (!request.Origin.CanPresent) continue;
                        if (request.Token.IsCancellationRequested) { request.Complete(PopupRequestStatus.Cancelled); continue; }
                        request.Started = true;
                        using var callback = NavigationCallbackScope.Enter(this);
                        await request.StartAsync();
                        // Preserve ordering among eligible requests. Popup-origin requests can nest;
                        // screen-origin requests wait until all covering popup cleanup has ended.
                        break;
                    }
                });
            }
            catch (Exception error)
            {
                DeferredPopup[] pending;
                lock (deferredPopupGate) pending = deferredPopups!.Where(item => !item.Started).ToArray();
                foreach (var request in pending) request.Complete(IsClosed ? PopupRequestStatus.WindowClosed : PopupRequestStatus.Failed, error);
            }
            await changed;
        }
    }

    private void RemoveDeferredPopup(DeferredPopup request)
    {
        lock (deferredPopupGate)
        {
            deferredPopups!.Remove(request);
            deferredPopupChanged?.TrySetResult();
            if (deferredPopups.Count > 0) deferredPopupChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        request.Release();
    }

    private abstract class DeferredPopup(MauiNavigationHost host, PopupRequestOrigin origin, Type popupType, string? key, CancellationToken token)
    {
        protected MauiNavigationHost Host { get; } = host;
        internal PopupRequestOrigin Origin { get; } = origin;
        internal Type PopupType { get; } = popupType;
        internal string? Key { get; } = key;
        internal CancellationToken Token { get; } = token;
        internal bool Started;
        internal bool Finished;
        private CancellationTokenRegistration cancelled;
        private CancellationTokenRegistration dismissed;
        internal void Bind()
        {
            cancelled = Token.UnsafeRegister(static state => ((MauiNavigationHost)state!).NotifyDeferredPopups(), Host);
            dismissed = Origin.Model.Lifetime.Token.UnsafeRegister(static state => ((MauiNavigationHost)state!).NotifyDeferredPopups(), Host);
        }
        internal void Release() { cancelled.Dispose(); dismissed.Dispose(); }
        internal abstract Task StartAsync();
        internal abstract void Complete(PopupRequestStatus status, Exception? error = null);
    }

    private sealed class DeferredPopup<TResult>(MauiNavigationHost host, PopupRequestOrigin origin, Type popupType,
        Func<CancellationToken, Task<View>> prepare, string? key, CancellationToken token) : DeferredPopup(host, origin, popupType, key, token)
    {
        internal PopupRequest<TResult> Handle { get; } = new(true);
        private bool wasPresented;
        internal override async Task StartAsync()
        {
            try
            {
                var showing = await Host.StartRegisteredPopupAsync<TResult>(Origin.Model, prepare, () => Origin.CanPresent,
                    Token, joinCurrentOperation: true, onPresented: () => wasPresented = true);
                _ = ObserveAsync(showing);
            }
            catch (Exception error) { Failed(error); }
            finally { Host.NotifyDeferredPopups(); }
        }
        private async Task ObserveAsync(Task<PopupNavigationResult<TResult>> showing)
        {
            try { Finish(new(PopupRequestStatus.Completed, wasPresented, await showing)); }
            catch (Exception error) { Failed(error); }
        }
        private void Failed(Exception error) => Complete(Token.IsCancellationRequested ? PopupRequestStatus.Cancelled
            : Origin.Ended ?? (wasPresented ? PopupRequestStatus.Failed : PopupRequestStatus.PreparationFailed), error);
        internal override void Complete(PopupRequestStatus status, Exception? error = null) => Finish(new(status, wasPresented, error: error));
        private void Finish(PopupRequestResult<TResult> result)
        {
            if (Finished) return;
            Finished = true;
            Host.RemoveDeferredPopup(this);
            Handle.Complete(result);
        }
    }
}
