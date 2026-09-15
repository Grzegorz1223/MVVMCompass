using System.Runtime.CompilerServices;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;

namespace MVVMCompass.Services;

/// <summary>Observes native removals that can bypass ILegacyNavigationService methods.</summary>
internal sealed class NativeNavigationObserver
{
    private static readonly ConditionalWeakTable<NavigationPage, NativeNavigationObserver> observers = new();
    private static readonly ConditionalWeakTable<Page, object> ownedModals = new();
    private Func<IReadOnlyList<Page>, Task>? onRemoved;
    private readonly NavigationPage navigation;
    private readonly List<Page> pendingRemoved = [];
    private Task completion = Task.CompletedTask;
    private readonly object removalGate = new();
    private TaskCompletionSource? removalBatch;
    private readonly List<ViewModelBase> confirmedModels = [];
    private bool confirming, draining;
    private HashSet<Page>? legacyPages;
    private readonly HashSet<Page> departures = [];
    private TaskCompletionSource? settled;
    private readonly EventHandler<NavigatingFromEventArgs> navigating;
    private readonly EventHandler<NavigatedFromEventArgs> navigated;

    private NativeNavigationObserver(NavigationPage navigation, Func<IReadOnlyList<Page>, Task>? onRemoved = null)
    {
        this.navigation = navigation;
        this.onRemoved = onRemoved;
        navigating = Navigating;
        navigated = Navigated;
        navigation.Popped += Removed;
        navigation.PoppedToRoot += Removed;
        navigation.ChildRemoved += ChildRemoved;
        if (onRemoved == null)
        {
            navigation.ChildAdded += ChildAdded;
            foreach (var page in navigation.Navigation.NavigationStack) ObservePage(page);
        }
    }

    internal static void Attach(NavigationPage navigation) => observers.GetValue(navigation, page => new(page));

    internal static void Attach(NavigationPage navigation, Func<IReadOnlyList<Page>, Task> onRemoved)
    {
        var observer = observers.GetValue(navigation, page => new(page, onRemoved));
        observer.onRemoved = onRemoved;
        observer.DetachLegacyPages();
    }

    internal static void OwnModal(Page page) => ownedModals.GetValue(page, _ => new());
    internal static void ReleaseModal(Page page) => ownedModals.Remove(page);
    internal static Task Completion(NavigationPage page) => observers.TryGetValue(page, out var observer) ? observer.completion : Task.CompletedTask;

    internal static void Detach(NavigationPage navigation)
    {
        if (!observers.TryGetValue(navigation, out var observer)) return;
        navigation.Popped -= observer.Removed;
        navigation.PoppedToRoot -= observer.Removed;
        navigation.ChildRemoved -= observer.ChildRemoved;
        observer.DetachLegacyPages();
        observers.Remove(navigation);
    }

    internal static void AttachApplication()
    {
        if (Application.Current is not { } application) return;
        application.ModalPopped -= ModalPopped;
        application.ModalPopped += ModalPopped;
    }

    private async void Removed(object? sender, NavigationEventArgs args)
    {
        // MAUI supplies the removed pages, so no competing copy of its navigation stack is needed.
        var removed = args is PoppedToRootEventArgs toRoot
            ? toRoot.PoppedPages.Reverse().ToArray()
            : new[] { args.Page };
        try
        {
            if (onRemoved != null) { await onRemoved(removed); return; }
            foreach (var page in removed) EndDeparture(page);
            var models = RemovedModels(removed);
            SignalModels(models);
            bool post;
            lock (removalGate)
            {
                // Popped confirms these pages. Reuse their models when the deferred
                // batch runs instead of walking the same visual trees a second time.
                foreach (var page in removed) pendingRemoved.Remove(page);
                confirmedModels.AddRange(models);
                if (removalBatch == null)
                {
                    removalBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    completion = removalBatch.Task;
                }
                post = !confirming && !draining;
                if (post) confirming = true;
            }
            if (post) PostConfirmation();
        }
        catch (Exception exception) { NavigationDiagnostics.Report(exception, "Native navigation removal"); }
    }

    private void ChildRemoved(object? sender, ElementEventArgs args)
    {
        if (onRemoved != null || args.Element is not Page page) return;
        try { QueueLegacyRemoval([page]); }
        catch (Exception error) { NavigationDiagnostics.Report(error, "Native stack edit"); }
    }

    private void ChildAdded(object? sender, ElementEventArgs args)
    { if (args.Element is Page page) ObservePage(page); }

    private void ObservePage(Page page)
    {
        if (!(legacyPages ??= []).Add(page)) return;
        page.NavigatingFrom += navigating;
        page.NavigatedFrom += navigated;
    }

    private void Navigating(object? sender, NavigatingFromEventArgs args)
    { if (sender is Page page) departures.Add(page); }

    private void Navigated(object? sender, NavigatedFromEventArgs args)
    { if (sender is Page page) EndDeparture(page); }

    private void EndDeparture(Page page)
    {
        if (departures.Remove(page) && departures.Count == 0)
        { settled?.TrySetResult(); settled = null; }
    }

    private void DetachLegacyPages()
    {
        navigation.ChildAdded -= ChildAdded;
        if (legacyPages != null)
            foreach (var page in legacyPages)
            { page.NavigatingFrom -= navigating; page.NavigatedFrom -= navigated; }
        legacyPages?.Clear(); departures.Clear();
        settled?.TrySetResult(); settled = null;
    }

    private void QueueLegacyRemoval(IEnumerable<Page> removed)
    {
        lock (removalGate)
        {
            pendingRemoved.AddRange(removed);
            if (removalBatch == null)
            {
                removalBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
                completion = removalBatch.Task;
            }
            if (confirming) return;
            confirming = true;
        }
        PostConfirmation();
    }

    private List<ViewModelBase> RemovedModels(IReadOnlyList<Page> pages)
    {
        var stack = navigation.Navigation.NavigationStack;
        if (pages.Count == 1) return stack.Contains(pages[0]) ? [] : ViewModelTree.Collect(pages[0]);
        var models = new List<ViewModelBase>();
        foreach (var page in pages)
            if (!stack.Contains(page)) models.AddRange(ViewModelTree.Collect(page));
        return models;
    }

    private static void SignalModels(List<ViewModelBase> models)
    {
        if (models.Count == 0) return;
        foreach (var model in models) model.Lifetime.MarkDismissed(DismissalReason.Back);
        var signalled = models.Count == 1 ? null : new HashSet<NavigationLifetime>();
        foreach (var model in models) model.Lifetime.SignalCancellation(signalled);
    }

    private void PostConfirmation()
    {
        var context = SynchronizationContext.Current;
        if (ExecutionContext.IsFlowSuppressed()) Post();
        else using (ExecutionContext.SuppressFlow()) Post();
        void Post()
        {
            // Confirm after the native batch; cancellation must not wait for an older cleanup.
            if (context != null && !navigation.Dispatcher.IsDispatchRequired)
                context.Post(static state => _ = ((NativeNavigationObserver)state!).ConfirmRemovalsAsync(), this);
            else _ = Task.Run(ConfirmRemovalsAsync);
        }
    }

    private async Task ConfirmRemovalsAsync()
    {
        try
        {
            await navigation.Dispatcher.DispatchAsync(async () =>
            {
                if (departures.Count != 0)
                    await (settled ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                Page[] batch;
                lock (removalGate)
                {
                    batch = pendingRemoved.Count == 0 ? [] : pendingRemoved.Count == 1
                        ? [pendingRemoved[0]] : pendingRemoved.Distinct().ToArray();
                    pendingRemoved.Clear();
                }
                var models = batch.Length == 0 ? null : RemovedModels(batch);
                if (models != null) SignalModels(models);
                bool start, again;
                lock (removalGate)
                {
                    if (models != null) confirmedModels.AddRange(models);
                    again = pendingRemoved.Count != 0;
                    confirming = again;
                    start = !draining;
                    if (start) draining = true;
                }
                if (again) PostConfirmation();
                if (start) _ = DrainRemovalsAsync();
            });
        }
        catch (Exception error) { FailRemoval(error); }
    }

    private async Task DrainRemovalsAsync()
    {
        try
        {
            while (true)
            {
                ViewModelBase[] models;
                lock (removalGate)
                {
                    if (confirmedModels.Count == 0)
                    {
                        draining = false;
                        if (!confirming && pendingRemoved.Count == 0)
                        {
                            removalBatch!.TrySetResult();
                            removalBatch = null;
                        }
                        return;
                    }
                    models = confirmedModels.ToArray();
                    confirmedModels.Clear();
                }
                await navigation.Dispatcher.DispatchAsync(() => LegacyNavigationService.DismissViewModelsAsync(models, DismissalReason.Back));
                if (legacyPages != null)
                    foreach (var page in legacyPages.Where(page => !departures.Contains(page)
                        && !navigation.Navigation.NavigationStack.Contains(page)).ToArray())
                    {
                        page.NavigatingFrom -= navigating; page.NavigatedFrom -= navigated;
                        legacyPages.Remove(page);
                    }
            }
        }
        catch (Exception error) { FailRemoval(error); }
    }

    private void FailRemoval(Exception error)
    {
        lock (removalGate)
        {
            removalBatch?.TrySetException(error);
            removalBatch = null; confirming = false; draining = false;
        }
        NavigationDiagnostics.Report(error, "Native stack cleanup");
    }

    private static async void ModalPopped(object? sender, ModalPoppedEventArgs args)
    {
        if (ownedModals.TryGetValue(args.Modal, out _)) return;
        try
        {
            await LegacyNavigationService.DismissViewModelsAsync(ViewModelTree.Collect(args.Modal).Where(model => !MauiNavigationHost.IsClaimedModel(model)), DismissalReason.Back);
        }
        catch (Exception exception) { NavigationDiagnostics.Report(exception, "Native modal removal"); }
    }
}
