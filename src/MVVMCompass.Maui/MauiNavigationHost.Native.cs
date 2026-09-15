using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private readonly Dictionary<VisualElement, Action> nativeSubscriptions = [];
    private readonly Dictionary<INotifyCollectionChanged, Action> nativeCollections = [];
    private readonly HashSet<Page> nativeDepartures = [];
    private readonly List<NativeCallback> nativeCallbacks = [];
    private TaskCompletionSource nativeSettled = CompletedSignal();
    private Task? nativeReconciliation;
    private readonly object nativeScheduleGate = new();
    private readonly object ownershipDiscoveryGate = new();
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
        if (IsClosed || nativeDepartures.Count != 0 || CurrentRoot == null || Window.Page != CurrentRoot.Page) return;
        // This runs after the native batch on the window dispatcher, independently of
        // coordinator cleanup. A transient removal/reinsertion is still reachable.
        var reachable = NativeTree();
        // Preparation owns candidates before installation. Absence only confirms removal
        // for destinations that have actually been presented in this window.
        var removedPages = ownedPages.Where(page => page.WasPresented && !reachable.Contains(page.Content)).ToArray();
        var removedItems = retainedItems.Where(item => item.Owner.WasPresented && (!reachable.Contains(item.Container) || !IsRetainedMember(item)
            && !(item.Container is FlyoutPage flyout && flyout.Detail == item.View))).ToArray();
        var surviving = ownedPages.Except(removedPages).Select(page => page.Entry.Lifetime)
            .Concat(retainedItems.Except(removedItems).Where(item => item.Entry != null).Select(item => item.Entry!.Lifetime))
            .Concat(ViewModelTree.Collect(Window.Page, true).Select(model => model.Lifetime)).ToHashSet();
        var removed = OwnedLifetimes(removedPages).Concat(removedItems.SelectMany(item =>
            ViewModelTree.Collect(item.View).Select(model => model.Lifetime)
                .Concat(item.Entry == null ? [] : new[] { item.Entry.Lifetime })))
            .Where(lifetime => !surviving.Contains(lifetime)).Distinct().ToArray();
        foreach (var lifetime in removed) lifetime.MarkDismissed(DismissalReason.Removed);
        SignalLifetimes(removed);
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

    private bool DeferNativeLifecycle(ViewModelBase model, Func<Task> callback, string operation)
    {
        if (InHostCallback() || IsClosed) return false;
        if (nativeDepartures.Count == 0 && nativeReconciliation == null
            && NativeVisibleEntries().Any(entry => ReferenceEquals(entry.ViewModel, model) && entry.State == NavigationEntryState.Active))
            return false;
        lock (nativeScheduleGate) nativeCallbacks.Add(new(model, callback, operation));
        ObserveFailure(ScheduleNativeReconciliation());
        return true;
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
        // Claim newly inserted models before MAUI sends their first appearance callback.
        try { RefreshNativeOwnership(); }
        catch (Exception error) { NavigationDiagnostics.Report(error, "Native page adoption"); }
        NativeChanged();
    }

    private void NativePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if ((sender is NavigationPage && args.PropertyName is nameof(NavigationPage.CurrentPage) or nameof(NavigationPage.RootPage))
            || args.PropertyName is nameof(FlyoutPage.Detail) or nameof(FlyoutPage.Flyout) or nameof(IFlyoutMenuItems.MenuItems))
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
            if (view is IFlyoutMenuItems { MenuItems: { } items } && !nativeCollections.ContainsKey(items))
            {
                NotifyCollectionChangedEventHandler changed = (_, _) => NativeChanged();
                items.CollectionChanged += changed;
                nativeCollections.Add(items, () => items.CollectionChanged -= changed);
            }
        }
        foreach (var entry in AllEntries())
            if (entry.ViewModel is ViewModelBase model)
                model.NativeLifecycleObserver = (callback, operation) => DeferNativeLifecycle(model, callback, operation);
    }

    private NavigationEntry[] AllEntries() => ownedPages.Select(page => page.Entry)
        .Concat(retainedItems.Where(item => item.Entry != null).Select(item => item.Entry!)).Distinct().ToArray();

    // Invalid native edits must never transfer teardown responsibility for another host's VM.
    internal static bool IsClaimedModel(ViewModelBase model)
    { lock (claims) return claims.TryGetValue(model, out _); }

    internal bool OwnsPopupView(View view, ViewModelBase model) => retainedItems.Any(item => item.View == view
        && ReferenceEquals(item.Entry?.ViewModel, model) && IsRetainedMember(item));

    private bool CanCleanLegacy(ViewModelBase model, NavigationEntry? candidate = null)
    {
        lock (claims) return !claims.TryGetValue(model, out var lifetime) || candidate?.Lifetime == lifetime
            || AllEntries().Any(entry => entry.Lifetime == lifetime);
    }

    private static bool IsToolkitPopup(Page page) => PopupOwnership.IsPopupPage(page);

    private HashSet<VisualElement> NativeTree()
    {
        var result = new HashSet<VisualElement>(ReferenceEqualityComparer.Instance);
        Visit(Window.Page);
        foreach (var modal in Window.Navigation.ModalStack.ToArray()) if (!IsToolkitPopup(modal)) Visit(modal);
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
                case ICustomTabbedViewBase tabs:
                    foreach (var child in tabs.Children.ToArray()) Visit(child.View);
                    break;
                case FlyoutPage flyout: Visit(flyout.Flyout); Visit(flyout.Detail); break;
            }
            if (view is IFlyoutMenuItems { MenuItems: { } items })
                foreach (var item in items.ToArray()) Visit(item.Content);
        }
    }

    private void RefreshNativeOwnership()
    {
        // Synchronous native notifications can overlap queued discovery in headless
        // dispatchers. Keep the ownership check, claim and registration indivisible.
        lock (ownershipDiscoveryGate)
        {
            if (IsClosed || CurrentRoot == null || Window.Page != CurrentRoot.Page) return;
            var existingEntries = AllEntries().ToHashSet();
            foreach (var owner in ownedPages.ToArray())
                if (IsPageInWindow(owner.Page)) DiscoverRetained(owner);
            foreach (var item in retainedItems.Where(item => item.Entry != null && !existingEntries.Contains(item.Entry)))
                if (item.Entry!.ViewModel is ViewModelBase legacy && item.Anchor is Page page && page is IHasVM)
                    composition.WireRoot(page, legacy);
            // Claim the modal wrapper before visiting its inner stack, preserving IsModal
            // and the application-level observer's single-owner exclusion.
            foreach (var modal in Window.Navigation.ModalStack.ToArray())
                if (!IsToolkitPopup(modal)) AdoptNativePage(modal, true);
            foreach (var stack in NativeTree().OfType<NavigationPage>().ToArray())
            {
                ObserveStack(stack, CurrentRoot.Page);
                foreach (var page in stack.Navigation.NavigationStack.ToArray()) AdoptNativePage(page, false);
            }
            foreach (var flyout in NativeTree().OfType<FlyoutPage>().ToArray())
                if (flyout.Detail != null) AdoptNativePage(flyout.Detail, false);
            ObserveNativeTree();
        }
    }

    private void AdoptNativePage(Page page, bool modal)
    {
        var anchor = page is NavigationPage stack && page is not IHasVM ? stack.RootPage : page;
        if (anchor == null || ownedPages.Any(item => item.Page == page || item.Content == anchor)
            || retainedItems.Any(item => item.View == page || item.Anchor == anchor)) return;
        object model = anchor is IHasVM hasVm ? hasVm.ViewModel
            : anchor.IsSet(BindableObject.BindingContextProperty) && anchor.BindingContext != null ? anchor.BindingContext : anchor;
        NavigationEntry entry;
        if (model is ViewModelBase legacy)
        {
            entry = Claim(legacy, () => coordinator.AttachEntry(legacy, legacy.Lifetime));
            legacy.IsModal = modal;
            if (anchor is IHasVM) composition.WireRoot(anchor, legacy);
        }
        else entry = Claim(model, () => coordinator.CreateEntry(model));
        TrackPage(new NativePage(page, anchor, entry, CurrentRoot!.Page, modal));
    }

    private async Task ReconcileNativeCoreAsync()
    {
        using var callbackScope = NavigationCallbackScope.Enter(this);
        NativeCallback[] callbacks;
        // Taking a batch and clearing it must be atomic with concurrent native callbacks.
        // Otherwise callbacks can be lost or List<T>.ToArray can expose cleared slots.
        lock (nativeScheduleGate)
        {
            callbacks = nativeCallbacks.ToArray();
            nativeCallbacks.Clear();
        }
        if (IsClosed || CurrentRoot == null || Window.Page != CurrentRoot.Page) return;
        var parent = execution.Value;
        var frame = new Execution(this, parent) { Committed = () => true };
        execution.Value = frame;
        try
        {
            RefreshNativeOwnership();
            var reachable = NativeTree();
            var removedPages = ownedPages.Where(page => !reachable.Contains(page.Content)).Reverse().ToArray();
            var removedItems = retainedItems.Where(item => !reachable.Contains(item.Container) || !IsRetainedMember(item)
                && !(item.Container is FlyoutPage flyout && flyout.Detail == item.View)).Reverse().ToArray();
            var survivingEntries = ownedPages.Except(removedPages).Select(page => page.Entry)
                .Concat(retainedItems.Except(removedItems).Where(item => item.Entry != null).Select(item => item.Entry!)).ToHashSet();
            var removedEntries = removedPages.Select(page => page.Entry).Concat(removedItems.Where(item => item.Entry != null).Select(item => item.Entry!))
                .Where(entry => !survivingEntries.Contains(entry)).Distinct().ToArray();
            var survivingLifetimes = survivingEntries.Select(entry => entry.Lifetime)
                .Concat(ViewModelTree.Collect(Window.Page, true).Select(model => model.Lifetime)).ToHashSet();
            var lifetimes = removedPages.SelectMany(page => removedItems.Where(item => item.Owner == page)
                    .SelectMany(ItemLifetimes).Concat(ViewModelTree.Collect(page.Content).Select(model => model.Lifetime)).Append(page.Entry.Lifetime))
                .Concat(removedItems.SelectMany(ItemLifetimes)).Concat(removedEntries.Select(entry => entry.Lifetime))
                .Where(lifetime => !survivingLifetimes.Contains(lifetime)).Distinct().ToArray();
            foreach (var lifetime in lifetimes) lifetime.MarkDismissed(DismissalReason.Removed);
            SignalLifetimes(lifetimes);
            // Disappearance belongs to the departing presentation; activation belongs after cleanup.
            await DrainNativeCallbacksAsync(callbacks.Where(item => item.Operation == "Disappearing"), allowMarked: true);
            foreach (var entry in AllEntries().Where(entry => !removedEntries.Contains(entry) && !NativeVisibleEntries().Contains(entry)).Reverse())
                try { await DeactivateEntriesAsync([entry]); }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Native deactivation"); }
            try { await NavigationLifetimeGroup.DismissAsync(lifetimes, DismissalReason.Removed); }
            catch (Exception error) { NavigationDiagnostics.Report(error, "Native ownership cleanup"); }
            foreach (var error in lifetimes.SelectMany(lifetime => lifetime.CancellationErrors))
                NavigationDiagnostics.Report(error, "Native lifetime cancellation");
            foreach (var page in removedPages)
            {
                ownedPages.Remove(page);
                if (page.IsModal) NativeNavigationObserver.ReleaseModal(page.Page);
            }
            ForgetRetained(removedItems);
            PruneNativeSubscriptions(reachable);
            PromoteNativeRoot();
            RefreshRetainedBindings();
            var activated = NativeVisibleEntries().Where(entry => entry.State is NavigationEntryState.Prepared or NavigationEntryState.Inactive).ToArray();
            foreach (var entry in activated)
            {
                try
                {
                    await entry.ActivateAsync();
                    if (entry.ViewModel is ViewModelBase legacy) await legacy.Appearing();
                }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Native activation"); }
            }
            await DrainNativeCallbacksAsync(callbacks.Where(item => item.Operation != "Disappearing"
                && !(item.Operation == "Appearing" && activated.Any(entry => ReferenceEquals(entry.ViewModel, item.Model)))));
        }
        finally { frame.Executing = false; execution.Value = parent; }

        static IEnumerable<NavigationLifetime> ItemLifetimes(MauiNavigationItem item) =>
            ViewModelTree.Collect(item.View).Select(model => model.Lifetime)
                .Concat(item.Entry == null ? [] : new[] { item.Entry.Lifetime });
    }

    private NavigationEntry[] NativeVisibleEntries() => EntriesOn(Branch(Window.Navigation.ModalStack.ToArray().LastOrDefault(page => page != null && !IsToolkitPopup(page)) ?? Window.Page));

    private async Task DrainNativeCallbacksAsync(IEnumerable<NativeCallback> callbacks, bool allowMarked = false)
    {
        var delivered = new HashSet<(ViewModelBase, string)>();
        foreach (var item in callbacks)
        {
            if ((!allowMarked && item.Model.IsDismissed) || !delivered.Add((item.Model, item.Operation))) continue;
            if (item.Operation is "Appearing" or "NavigatedTo" && !NativeVisibleEntries().Any(entry => ReferenceEquals(entry.ViewModel, item.Model))) continue;
            try { await item.Callback(); }
            catch (Exception error) { NavigationDiagnostics.Report(error, $"Native {item.Operation}"); }
        }
    }

    private void PromoteNativeRoot()
    {
        if (Window.Page is not NavigationPage stack || CurrentRoot?.Page != stack) return;
        var owner = ownedPages.FirstOrDefault(page => page.Content == stack.RootPage)
            ?? ownedPages.FirstOrDefault(page => page.Page == stack.RootPage);
        if (owner == null || owner.Entry == CurrentRoot.Entry) return;
        CurrentRoot = new NativeRoot(stack, owner.Content, owner.Entry);
        composition.SetCurrentRoot(owner.Content);
    }

    private void PruneNativeSubscriptions(HashSet<VisualElement> reachable)
    {
        // ChildRemoved precedes transition completion. Keep the outgoing page's
        // NavigatedFrom subscription until MAUI confirms completion (including cancellation).
        foreach (var pair in nativeSubscriptions.Where(pair => !reachable.Contains(pair.Key)
            && (reachable.Count == 0 || pair.Key is not Page page || !nativeDepartures.Contains(page))).ToArray())
        { pair.Value(); nativeSubscriptions.Remove(pair.Key); }
        var collections = reachable.OfType<IFlyoutMenuItems>().Select(menu => menu.MenuItems).ToArray();
        foreach (var pair in nativeCollections.Where(pair => !collections.Contains(pair.Key)).ToArray())
        { pair.Value(); nativeCollections.Remove(pair.Key); }
        foreach (var stack in observedStacks.Keys.Where(stack => !reachable.Contains(stack)).ToArray())
        {
            stack.Pushed -= StackPushed;
            NativeNavigationObserver.Detach(stack);
            observedStacks.Remove(stack);
        }
    }

    private void RefreshRetainedBindings()
    {
        foreach (var pair in retainedContainers)
        {
            pair.Value.Selected = SelectedIdentity(pair.Key);
            if (pair.Key is FlyoutPage flyout)
            {
                if (pair.Value.Menu != flyout.Flyout)
                {
                    if (pair.Value.Menu != null) RetainedNavigationBridge.Release(pair.Value.Menu);
                    pair.Value.Menu = flyout.Flyout;
                    if (flyout.Flyout != null) RetainedNavigationBridge.Bind(flyout.Flyout, pair.Value.Bridge);
                }
                foreach (var item in GetItems(flyout))
                    if (item.Identity is FlyoutMenuItem menu) menu.IsSelected = item.View == flyout.Detail;
            }
        }
        BindRetainedContainers();
    }

    private static bool IsRetainedMember(MauiNavigationItem item) => item.Container switch
    {
        TabbedPage tabs => tabs.Children.Contains(item.View),
        ICustomTabbedViewBase tabs => tabs.Children.Any(child => ReferenceEquals(child, item.Identity)),
        FlyoutPage flyout when item.Identity is MenuOwner => flyout.Flyout == item.View,
        FlyoutPage { Flyout: IFlyoutMenuItems { MenuItems: { } items } } => items.Any(menu => ReferenceEquals(menu, item.Identity) && menu.Content == item.View),
        _ => false
    };

    private sealed record NativeCallback(ViewModelBase Model, Func<Task> Callback, string Operation);
    private sealed class NativePage(Page page, Page content, NavigationEntry entry, Page root, bool modal)
        : MauiNavigationPage(page, content, entry, root, modal);
    private sealed class NativeRoot(Page page, Page content, NavigationEntry entry) : MauiNavigationRoot(page, content, entry);
}
