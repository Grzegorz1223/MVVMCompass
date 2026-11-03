using System.ComponentModel;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private readonly Dictionary<VisualElement, Action> nativeSubscriptions = [];
    private readonly HashSet<Page> nativeDepartures = [];
    private TaskCompletionSource nativeSettled = CompletedSignal();
    private Task? nativeReconciliation;
    private readonly object nativeScheduleGate = new();
    private long nativeRevision;
    private bool nativeAdmissionStarted;
    private int nativeConfirmationQueued;

    /// <summary>Reconciles completed native changes and awaits owned cleanup and activation. Call on the window dispatcher after a native batch.</summary>
    public Task ReconcileNativeAsync()
    {
        if (InHostCallback()) return Task.FromException(new InvalidOperationException("Reconcile native navigation outside this host's callbacks."));
        return IsClosed ? Completion : ScheduleNativeReconciliation();
    }

    private bool InHostCallback()
    {
        if (NavigationCallbackScope.IsActive(this)) return true;
        for (var frame = execution.Value; frame != null; frame = frame.Parent)
            if (frame.Host == this && frame.Executing) return true;
        return false;
    }

    private static TaskCompletionSource CompletedSignal()
    { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); signal.SetResult(); return signal; }

    private Task ScheduleNativeReconciliation()
    {
        lock (nativeScheduleGate)
        {
            nativeRevision++;
            if (nativeReconciliation is { IsCompleted: false })
            {
                if (nativeAdmissionStarted) QueueNativeConfirmation();
                return NativeNavigationCompletion = nativeReconciliation;
            }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            NativeNavigationCompletion = nativeReconciliation = completion.Task;
            // QueueBoundaryAsync supplies the deferred, context-isolated boundary.
            // Starting this waiter needs no additional worker-thread round trip.
            if (ExecutionContext.IsFlowSuppressed()) _ = DrainNativeChangesAsync(completion);
            else using (ExecutionContext.SuppressFlow()) _ = DrainNativeChangesAsync(completion);
            return completion.Task;
        }
    }

    private async Task DrainNativeChangesAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                await nativeSettled.Task;
                long processed;
                lock (nativeScheduleGate) processed = nativeRevision;
                try { await QueueBoundaryAsync(async () =>
                {
                    using var gate = await RootOperationGate.EnterAsync(Window, CancellationToken.None);
                    if (nativeDepartures.Count == 0) await ReconcileNativeCoreAsync();
                }, required: false, beforeAdmission: () =>
                {
                    lock (nativeScheduleGate) nativeAdmissionStarted = true;
                    if (coordinator.IsBusy) ConfirmNativeRemovals();
                }); }
                finally { lock (nativeScheduleGate) nativeAdmissionStarted = false; }
                lock (nativeScheduleGate)
                {
                    if (processed != nativeRevision || nativeDepartures.Count != 0) continue;
                    completion.TrySetResult();
                    if (ReferenceEquals(nativeReconciliation, completion.Task)) nativeReconciliation = null;
                    return;
                }
            }
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally { NotifyDeferredPopups(); }
    }

    private void QueueNativeConfirmation()
    {
        if (Interlocked.Exchange(ref nativeConfirmationQueued, 1) != 0) return;
        var context = SynchronizationContext.Current;
        if (ExecutionContext.IsFlowSuppressed()) Post();
        else using (ExecutionContext.SuppressFlow()) Post();
        void Post()
        {
            if (context != null && !Window.Dispatcher.IsDispatchRequired)
                context.Post(_ => ObserveFailure(ConfirmAsync()), null);
            else ObserveFailure(Task.Run(ConfirmAsync));
        }
        async Task ConfirmAsync()
        {
            try
            {
                await nativeSettled.Task;
                await Window.Dispatcher.DispatchAsync(ConfirmNativeRemovals);
            }
            finally { Interlocked.Exchange(ref nativeConfirmationQueued, 0); }
        }
    }

    private void ConfirmNativeRemovals()
    {
        if (IsClosed || CurrentRoot == null || Window.Page != CurrentRoot.Page) return;
        var removed = ownedPages.Where(page => page.WasPresented && !IsPageInWindow(page.Page)).ToArray();
        MarkOwnedPages(removed, DismissalReason.Removed);
        SignalLifetimes(OwnedLifetimes(removed));
    }

    private void NativeChanged()
    {
        if (IsClosed || InHostCallback() || containerMutation != 0) return;
        CancelExternalPreparation();
        ObserveFailure(ScheduleNativeReconciliation());
    }

    private static async void ObserveFailure(Task task)
    {
        try { await task; }
        catch (Exception error) { NavigationDiagnostics.Report(error, "Native reconciliation"); }
    }

    private void NativeNavigating(object? sender, NavigatingFromEventArgs args)
    {
        if (sender is not Page page || InHostCallback() || containerMutation != 0 || IsClosed) return;
        if (nativeDepartures.Add(page) && nativeDepartures.Count == 1)
            nativeSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NativeChanged();
    }

    private void NativeNavigated(object? sender, NavigatedFromEventArgs args)
    {
        if (sender is Page page) EndNativeDeparture(page);
        NativeChanged();
    }

    private void EndNativeDeparture(Page page)
    {
        if (nativeDepartures.Remove(page) && nativeDepartures.Count == 0) nativeSettled.TrySetResult();
    }

    private void NativeChildChanged(object? sender, ElementEventArgs args)
    {
        if (args.Element is not Page || InHostCallback() || containerMutation != 0 || IsClosed) return;
        NativeChanged();
    }

    private void NativePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if ((sender is NavigationPage && args.PropertyName is nameof(NavigationPage.CurrentPage) or nameof(NavigationPage.RootPage))
            || args.PropertyName is nameof(FlyoutPage.Detail) or nameof(FlyoutPage.Flyout))
            NativeChanged();
    }

    private void ObserveNativeTree()
    {
        if (CurrentRoot == null || Window.Page != CurrentRoot.Page) return;
        var reachable = NativeTree();
        PruneNativeSubscriptions(reachable);
        foreach (var view in reachable)
        {
            if (!nativeSubscriptions.ContainsKey(view))
            {
                if (view is Page page)
                {
                    page.NavigatingFrom += NativeNavigating;
                    page.NavigatedFrom += NativeNavigated;
                }
                if (view is NavigationPage or TabbedPage)
                {
                    view.ChildAdded += NativeChildChanged;
                    view.ChildRemoved += NativeChildChanged;
                }
                view.PropertyChanged += NativePropertyChanged;
                nativeSubscriptions.Add(view, () =>
                {
                    if (view is Page page)
                    {
                        page.NavigatingFrom -= NativeNavigating;
                        page.NavigatedFrom -= NativeNavigated;
                        EndNativeDeparture(page);
                    }
                    view.ChildAdded -= NativeChildChanged;
                    view.ChildRemoved -= NativeChildChanged;
                    view.PropertyChanged -= NativePropertyChanged;
                });
            }
        }
    }

    private HashSet<VisualElement> NativeTree()
    {
        var result = new HashSet<VisualElement>(ReferenceEqualityComparer.Instance);
        Visit(Window.Page);
        foreach (var modal in Window.Navigation.ModalStack.ToArray()) if (!PopupOwnership.IsPopupPage(modal)) Visit(modal);
        return result;
        void Visit(VisualElement? view)
        {
            if (view == null || !result.Add(view)) return;
            switch (view)
            {
                case NavigationPage stack:
                    foreach (var page in stack.Navigation.NavigationStack.ToArray()) Visit(page);
                    break;
                case TabbedPage tabs:
                    foreach (var page in tabs.Children.ToArray()) Visit(page);
                    break;
                case FlyoutPage flyout: Visit(flyout.Flyout); Visit(flyout.Detail); break;
            }
        }
    }

    private async Task ReconcileNativeCoreAsync()
    {
        using var callbackScope = NavigationCallbackScope.Enter(this);
        if (IsClosed || CurrentRoot == null || Window.Page != CurrentRoot.Page) return;
        var parent = execution.Value;
        var frame = new Execution(this, parent) { Committed = () => true };
        execution.Value = frame;
        try
        {
            var removed = ownedPages.Where(page => page.WasPresented && !IsPageInWindow(page.Page)).Reverse().ToArray();
            MarkOwnedPages(removed, DismissalReason.Removed);
            SignalLifetimes(OwnedLifetimes(removed));
            var visible = NativeVisibleEntries();
            foreach (var page in ownedPages.Except(removed).Reverse().ToArray())
                if (page.Entry.State == NavigationEntryState.Active && !visible.Contains(page.Entry))
                    try { await page.Entry.DeactivateAsync(); }
                    catch (Exception error) { NavigationDiagnostics.Report(error, "Native deactivation"); }
            foreach (var page in removed)
                await DismissTreeAsync(page.Page, page.Entry, OwnedModels(page), DismissalReason.Removed);
            PruneNativeSubscriptions(NativeTree());
            try { await ActivateVisibleEntriesAsync(); }
            catch (Exception error) { NavigationDiagnostics.Report(error, "Native activation"); }
            ObserveNativeTree();
        }
        finally { frame.Executing = false; execution.Value = parent; }
    }

    private void PruneNativeSubscriptions(HashSet<VisualElement> reachable)
    {
        // ChildRemoved precedes transition completion. Keep the outgoing page's
        // NavigatedFrom subscription until MAUI confirms completion (including cancellation).
        foreach (var pair in nativeSubscriptions.Where(pair => !reachable.Contains(pair.Key)
            && (reachable.Count == 0 || pair.Key is not Page page || !nativeDepartures.Contains(page))).ToArray())
        { pair.Value(); nativeSubscriptions.Remove(pair.Key); }
    }
}
