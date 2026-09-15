using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private readonly List<MauiNavigationPage> ownedPages = [];
    private readonly Dictionary<NavigationPage, Page> observedStacks = [];
    private int containerMutation;

    /// <summary>Gets the owned entry at the visible destination. Read on the window dispatcher.</summary>
    public MauiNavigationPage? CurrentPage => VisibleBranch().Reverse()
        .Select(view => ownedPages.LastOrDefault(item => ReferenceEquals(item.Content, view))).FirstOrDefault(item => item != null);

    /// <summary>Gets completion of the most recently observed native removal, including queued cleanup and logical reactivation.</summary>
    public Task NativeNavigationCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Pushes a registered page with dictionary parameters onto the visible originating stack.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TViewModel>>> PushAsync<TViewModel>(
        NavigationRequest<Dictionary<string, object>?> request, bool animated = true, CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase =>
        PresentRegisteredAsync<TViewModel, Dictionary<string, object>?>(request, DictionaryInitializer<TViewModel>, false, false, animated, cancellationToken);

    /// <summary>Pushes a registered page after typed initialization and BeforeFirstShown.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TViewModel>>> PushAsync<TViewModel, TParameter>(
        NavigationRequest<TParameter> request, bool animated = true, CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase, INavigationInitializable<TParameter> =>
        PresentRegisteredAsync<TViewModel, TParameter>(request, (entry, parameter, token) => entry.InitializeAsync(parameter, token),
            false, false, animated, cancellationToken);

    /// <summary>Pushes an ordinary typed model; explicit cleanup owns only resources declared by the caller.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TViewModel>>> PushAsync<TViewModel, TParameter>(
        NavigationRequest<TParameter> request, Func<TViewModel> createViewModel, Func<TViewModel, Page> createPage,
        bool animated = true, Func<TViewModel, Task>? cleanup = null, CancellationToken cancellationToken = default)
        where TViewModel : class, INavigationInitializable<TParameter> =>
        RunPresentationAsync(request, PlainFactory(createViewModel, createPage, cleanup),
            (entry, parameter, token) => entry.InitializeAsync(parameter, token), false, false, animated, cancellationToken);

    /// <summary>Opens a registered modal, optionally with its own navigation stack.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TViewModel>>> OpenModalAsync<TViewModel>(
        NavigationRequest<Dictionary<string, object>?> request, bool navigable = false, bool animated = true,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase =>
        PresentRegisteredAsync<TViewModel, Dictionary<string, object>?>(request, DictionaryInitializer<TViewModel>, true, navigable, animated, cancellationToken);

    /// <summary>Opens a registered modal after typed initialization and BeforeFirstShown.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TViewModel>>> OpenModalAsync<TViewModel, TParameter>(
        NavigationRequest<TParameter> request, bool navigable = false, bool animated = true, CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase, INavigationInitializable<TParameter> =>
        PresentRegisteredAsync<TViewModel, TParameter>(request, (entry, parameter, token) => entry.InitializeAsync(parameter, token),
            true, navigable, animated, cancellationToken);

    /// <summary>Opens a modal for an ordinary typed model, optionally with an independent stack.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TViewModel>>> OpenModalAsync<TViewModel, TParameter>(
        NavigationRequest<TParameter> request, Func<TViewModel> createViewModel, Func<TViewModel, Page> createPage,
        bool navigable = false, bool animated = true, Func<TViewModel, Task>? cleanup = null, CancellationToken cancellationToken = default)
        where TViewModel : class, INavigationInitializable<TParameter> =>
        RunPresentationAsync(request, PlainFactory(createViewModel, createPage, cleanup),
            (entry, parameter, token) => entry.InitializeAsync(parameter, token), true, navigable, animated, cancellationToken);

    /// <summary>Pops the visible stack, or closes its modal when at that modal's root. At the window root this is a no-op.</summary>
    public Task<NavigationOutcome<MauiNavigationPage?>> BackAsync(NavigationRequest<object?>? request = null,
        bool animated = true, CancellationToken cancellationToken = default) =>
        RunRemovalAsync(request ?? new(null), MauiNavigationOperation.Back, animated, cancellationToken);

    /// <summary>Removes all pages above the visible stack's root; a modal remains open.</summary>
    public Task<NavigationOutcome<MauiNavigationPage?>> PopToRootAsync(NavigationRequest<object?>? request = null,
        bool animated = true, CancellationToken cancellationToken = default) =>
        RunRemovalAsync(request ?? new(null), MauiNavigationOperation.PopToRoot, animated, cancellationToken);

    /// <summary>Closes the top modal and its entire owned stack, then reactivates the underlying destination.</summary>
    public Task<NavigationOutcome<MauiNavigationPage?>> CloseModalAsync(NavigationRequest<object?>? request = null,
        bool animated = true, CancellationToken cancellationToken = default) =>
        RunRemovalAsync(request ?? new(null), MauiNavigationOperation.CloseModal, animated, cancellationToken);

    private static async Task DictionaryInitializer<T>(NavigationEntry<T> entry, Dictionary<string, object>? parameter,
        CancellationToken token) where T : ViewModelBase
    {
        if (parameter != null) await entry.ViewModel.GetParameters(parameter);
        token.ThrowIfCancellationRequested();
    }

    private Task<NavigationOutcome<MauiNavigationPage<T>>> PresentRegisteredAsync<T, TParameter>(
        NavigationRequest<TParameter> request, Func<NavigationEntry<T>, TParameter, CancellationToken, Task> initialize,
        bool modal, bool navigable, bool animated, CancellationToken cancellationToken) where T : ViewModelBase =>
        RunPresentationAsync(request, RegisteredFactory<T>(), RegisteredInitializer(initialize), modal, navigable, animated, cancellationToken);

    private Task<NavigationOutcome<MauiNavigationPage<T>>> RunPresentationAsync<T, TParameter>(
        NavigationRequest<TParameter> request, Func<RootPreparation, Action<NavigationEntry<T>>, Task<Page>> create,
        Func<NavigationEntry<T>, TParameter, CancellationToken, Task> initialize,
        bool modal, bool navigable, bool animated, CancellationToken cancellationToken) where T : class =>
        RunContainerAsync(request, modal ? MauiNavigationOperation.OpenModal : MauiNavigationOperation.Push,
            async (context, snapshot, callbacks) =>
            {
                if (!modal && snapshot.Stack == null)
                    throw new InvalidOperationException("The originating destination has no NavigationPage stack.");
                await CheckGuardsAsync(snapshot.Entries.Select(entry => entry.ViewModel), context);
                ValidateSnapshot(snapshot);
                if (modal) await WaitForNativePresentationAsync(snapshot.Modals.LastOrDefault() ?? snapshot.Root, context.CancellationToken);
                using var preparation = new RootPreparation(MauiNavigationHostFactory.ExistingModels(Window), context.CancellationToken);
                NavigationEntry<T>? entry = null;
                MauiNavigationPage<T>? candidate = null;
                try
                {
                    var content = await create(preparation, owned => entry = context.Own(owned));
                    if (!modal && content is NavigationPage)
                        throw new InvalidOperationException("Open a separate navigation stack as a modal or root.");
                    candidate = new(navigable ? new NavigationPage(content) : content, content, entry!, snapshot.Root, modal);
                    TrackPage(candidate);
                    callbacks.Capture(preparation.OwnedModels);
                    if (entry!.ViewModel is ViewModelBase legacy) legacy.IsModal = modal;
                    await initialize(entry, context.Parameter, context.CancellationToken);
                    DiscoverRetained(candidate);
                    preparation.Validate(MauiNavigationHostFactory.ExistingModels(Window));
                    ValidateSnapshot(snapshot);
                    context.BeginCommit();
                    await DeactivateEntriesAsync(modal ? snapshot.Entries : EntriesIn(snapshot.Stack!.CurrentPage));
                    ValidateSnapshot(snapshot);
                    preparation.FinishPreparation();
                    containerMutation++;
                    try
                    {
                        if (modal) await Window.Navigation.PushModalAsync(candidate.Page, animated);
                        else await snapshot.Stack!.PushAsync(candidate.Page, animated);
                        if (modal) await WaitForNativePresentationAsync(candidate.Page, entry.Lifetime.Token);
                    }
                    finally { containerMutation--; }
                    if (!IsPresented(candidate)) throw new InvalidOperationException("The destination was not installed in the captured window.");
                    candidate.WasPresented = true;
                    await entry.ActivateAsync();
                    await preparation.ActivateAsync();
                    DiscoverRetained(candidate);
                    await ActivateVisibleEntriesAsync();
                    BindRetainedContainers();
                    if (!IsPresented(candidate) || entry.Lifetime.IsDismissed || IsClosed)
                        throw new InvalidOperationException("The destination was removed during activation.");
                    return candidate;
                }
                finally
                {
                    if (candidate == null || !IsPresented(candidate))
                    {
                        await DismissTreeAsync(candidate?.Page, entry, preparation.OwnedModels, DismissalReason.PreparationFailed);
                        if (context.HasCommitted && ReferenceEquals(CurrentPage, snapshot.Current) && !snapshot.Current.Entry.Lifetime.IsDismissed)
                            await ActivateVisibleEntriesAsync(notifyRetained: true);
                    }
                }
            }, cancellationToken);

    private Task<NavigationOutcome<MauiNavigationPage?>> RunPageRemovalAsync(NavigationRequest<object?> request,
        MauiNavigationOperation operation, bool animated, CancellationToken cancellationToken) =>
        RunContainerAsync<object?, MauiNavigationPage?>(request, operation, async (context, snapshot, callbacks) =>
        {
            var closeModal = operation == MauiNavigationOperation.CloseModal ||
                (operation == MauiNavigationOperation.Back && snapshot.StackPages.Length <= 1 && snapshot.Modals.Length != 0);
            Page[] removing = closeModal
                ? snapshot.Modals.TakeLast(1).ToArray()
                : operation == MauiNavigationOperation.CloseModal ? []
                : operation == MauiNavigationOperation.PopToRoot ? snapshot.StackPages.Skip(1).Reverse().ToArray()
                : snapshot.StackPages.Length > 1 ? [snapshot.StackPages[^1]] : [];
            if (removing.Length == 0) return snapshot.Current;
            var entries = removing.SelectMany(page => OwnedWithin(page)).Distinct().ToArray();
            await CheckGuardsAsync(entries.Select(item => item.Entry.ViewModel)
                .Concat(removing.SelectMany(page => RetainedWithin(page, OwnedWithin(page))).Where(item => item.Entry != null).Select(item => item.Entry!.ViewModel))
                .Concat(removing.SelectMany(page => ViewModelTree.Collect(page))), context);
            ValidateSnapshot(snapshot);
            context.BeginCommit();
            try
            {
                containerMutation++;
                try
                {
                    if (closeModal) await Window.Navigation.PopModalAsync(animated);
                    else if (operation == MauiNavigationOperation.PopToRoot) await snapshot.Stack!.PopToRootAsync(animated);
                    else await snapshot.Stack!.PopAsync(animated);
                    if (closeModal) await FinishNativeRemovalAsync();
                }
                finally { containerMutation--; }
                if (removing.Any(IsPageInWindow))
                    throw new InvalidOperationException("Native navigation retained a page requested for removal.");
                ValidateWindow(snapshot.Root, snapshot.Application, snapshot.WasRegistered);
            }
            finally
            {
                // A native failure may happen before or after removal. Never terminate a page still presented.
                await DismissRemovedAsync(removing.Where(page => !IsPageInWindow(page)).ToArray());
                if (CurrentPage is { } current && !current.Entry.Lifetime.IsDismissed && !IsClosed)
                    await ActivateVisibleEntriesAsync(notifyRetained: true);
            }
            return CurrentPage;
        }, cancellationToken);

    private async Task<NavigationOutcome<TResult>> RunContainerAsync<TParameter, TResult>(NavigationRequest<TParameter> request,
        MauiNavigationOperation operation,
        Func<NavigationOperationContext<TParameter>, PresentationSnapshot, NavigationCallbackQueue, Task<TResult>> callback,
        CancellationToken cancellationToken)
    {
        var legacyReentrant = RootOperationGate.IsLegacyReentrant(Window);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closingToken);
        return await coordinator.RunAsync(request, async context =>
        {
            using var callbackScope = NavigationCallbackScope.Enter(this);
            if (legacyReentrant) throw new InvalidOperationException("A legacy root callback cannot await container navigation on its window.");
            using var gate = await RootOperationGate.EnterAsync(Window, context.CancellationToken);
            var snapshot = CaptureSnapshot(request.Origin, context);
            using var callbacks = new NavigationCallbackQueue();
            callbacks.Capture(ViewModelTree.Collect(snapshot.Root, true));
            SuppressRetainedCallbacks(callbacks);
            var parent = execution.Value;
            var frame = new Execution(this, parent) { Committed = () => context.HasCommitted };
            execution.Value = frame;
            activeCancellation = linked;
            try
            {
                var result = await callback(context, snapshot, callbacks);
                foreach (var page in ownedPages.ToArray()) DiscoverRetained(page);
                await ActivateVisibleEntriesAsync();
                BindRetainedContainers();
                ObserveNativeTree();
                var completedPresentation = CaptureSnapshot<TParameter>(null, context);
                await callbacks.DrainAsync();
                ValidateSnapshot(completedPresentation);
                return result;
            }
            catch (Exception error)
            {
                if (context.HasCommitted)
                {
                    if (operation is MauiNavigationOperation.SelectTab or MauiNavigationOperation.SelectFlyout && MatchesSnapshot(snapshot))
                    {
                        try { await ActivateVisibleEntriesAsync(notifyRetained: true); }
                        catch (Exception recovery) { error = new AggregateException(error, recovery); }
                    }
                    throw new MauiNavigationException(operation, !MatchesSnapshot(snapshot), error);
                }
                throw;
            }
            finally
            {
                activeCancellation = null;
                frame.Executing = false;
                execution.Value = parent;
            }
        }, linked.Token);
    }

    private static async Task CheckGuardsAsync<TParameter>(IEnumerable<object> models, NavigationOperationContext<TParameter> context)
    {
        foreach (var model in models.Distinct(ReferenceEqualityComparer.Instance))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var allowed = model switch
            {
                INavigationGuard guard => await guard.CanNavigateAsync(context.CancellationToken),
                ViewModelBase legacy => await legacy.CanNavigate(),
                _ => true
            };
            context.CancellationToken.ThrowIfCancellationRequested();
            if (!allowed) context.Reject(NavigationStatus.GuardRejected);
        }
    }

    private PresentationSnapshot CaptureSnapshot<TParameter>(NavigationEntry? origin, NavigationOperationContext<TParameter> context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var root = Window.Page;
        var current = CurrentPage;
        if (IsClosed || root == null || CurrentRoot?.Page != root || CurrentRoot.Entry.Lifetime.IsDismissed || current == null)
            throw new InvalidOperationException("Install an owned window root and navigate from an owned visible destination.");
        var activeEntries = VisibleEntries();
        if (origin != null && !activeEntries.Contains(origin)) context.Reject(NavigationStatus.InvalidOrigin);
        var visible = VisiblePage(root)!;
        var stack = VisibleBranch().OfType<NavigationPage>().LastOrDefault();
        if (stack != null) ObserveStack(stack, root);
        var application = Application.Current;
        return new(root, current, visible, stack, stack?.Navigation.NavigationStack.ToArray() ?? [],
            Window.Navigation.ModalStack.ToArray(), application, application?.Windows.Contains(Window) == true, activeEntries, VisibleBranch());
    }

    private bool MatchesSnapshot(PresentationSnapshot snapshot) => ReferenceEquals(Window.Page, snapshot.Root)
        && VisibleBranch().SequenceEqual(snapshot.Branch)
        && Window.Navigation.ModalStack.SequenceEqual(snapshot.Modals)
        && (snapshot.Stack == null || snapshot.Stack.Navigation.NavigationStack.SequenceEqual(snapshot.StackPages));

    private void ValidateSnapshot(PresentationSnapshot snapshot)
    {
        ValidateWindow(snapshot.Root, snapshot.Application, snapshot.WasRegistered);
        if (!MatchesSnapshot(snapshot)) throw new InvalidOperationException("The originating container changed during navigation preparation.");
    }

    private sealed record PresentationSnapshot(Page Root, MauiNavigationPage Current, Page Visible, NavigationPage? Stack,
        Page[] StackPages, Page[] Modals, Application? Application, bool WasRegistered, NavigationEntry[] Entries, VisualElement[] Branch);

    private Page? VisiblePage(Page? root) => Branch(Window.Navigation.ModalStack.LastOrDefault() ?? root).OfType<Page>().LastOrDefault();

    private MauiNavigationPage? FindOwnedPage(Page? page)
    {
        for (Element? element = page; element is not null and not Microsoft.Maui.Controls.Window; element = element.Parent)
            if (ownedPages.LastOrDefault(item => ReferenceEquals(item.Content, element)) is { } found) return found;
        return null;
    }

    private void TrackRoot<T>(MauiNavigationRoot<T> root) where T : class
    {
        var existing = ownedPages.FirstOrDefault(item => ReferenceEquals(item.Entry, root.Entry));
        if (existing == null)
            TrackPage(new MauiNavigationPage<T>(root.Page, root.Content, root.Entry, root.Page, false));
        else if (ReferenceEquals(Window.Page, root.Page)) existing.WasPresented = true;
    }

    private void TrackPage(MauiNavigationPage page)
    {
        page.WasPresented = IsPresented(page);
        ownedPages.Add(page);
        DiscoverRetained(page);
        if (page.IsModal) NativeNavigationObserver.OwnModal(page.Page);
        foreach (var stack in StacksWithin(page.Page))
            ObserveStack(stack, page.Root);
    }

    private void ObserveStack(NavigationPage stack, Page root)
    {
        if (!observedStacks.TryAdd(stack, root)) return;
        NativeNavigationObserver.Attach(stack, NativePagesRemovedAsync);
        stack.Pushed += StackPushed;
    }

    private static IEnumerable<NavigationPage> StacksWithin(Page? page)
    {
        if (page is NavigationPage stack)
        {
            yield return stack;
            foreach (var nested in stack.Navigation.NavigationStack.SelectMany(StacksWithin)) yield return nested;
        }
        else if (page is TabbedPage tabs)
        {
            foreach (var nested in tabs.Children.SelectMany(StacksWithin)) yield return nested;
        }
        else if (page is FlyoutPage flyout)
            foreach (var nested in StacksWithin(flyout.Detail)) yield return nested;
    }

    private static bool ContainsPage(Page? parent, Page child) => parent != null &&
        (ReferenceEquals(parent, child) || parent switch
        {
            NavigationPage stack => stack.Navigation.NavigationStack.Any(page => ContainsPage(page, child)),
            TabbedPage tabs => tabs.Children.Any(page => ContainsPage(page, child)),
            FlyoutPage flyout => ContainsPage(flyout.Detail, child) || ContainsPage(flyout.Flyout, child)
                || (flyout.Flyout is IFlyoutMenuItems menu && menu.MenuItems != null && menu.MenuItems.Any(item => item.Content is Page retained && ContainsPage(retained, child))),
            ICustomTabbedViewBase custom => custom.Children.Any(item => item.View is Page retained && ContainsPage(retained, child)),
            _ => false
        });

    private bool IsPageInWindow(Page page) => ContainsPage(Window.Page, page) ||
        Window.Navigation.ModalStack.Any(modal => ContainsPage(modal, page));

    private bool IsPresented(MauiNavigationPage page) => ReferenceEquals(Window.Page, page.Root) && IsPageInWindow(page.Page);

    private MauiNavigationPage[] OwnedWithin(Page? page, NavigationEntry? entry = null)
    {
        var root = ownedPages.FirstOrDefault(item => ReferenceEquals(item.Entry, entry) && ReferenceEquals(item.Page, item.Root))?.Root;
        return ownedPages.AsEnumerable().Reverse().Where(item => ReferenceEquals(item.Root, page) ||
            ReferenceEquals(item.Root, root) || ContainsPage(page, item.Page) || ReferenceEquals(item.Entry, entry)).ToArray();
    }

    private void ForgetPages(IEnumerable<MauiNavigationPage> pages)
    {
        foreach (var page in pages)
        {
            ownedPages.Remove(page);
            if (page.IsModal) NativeNavigationObserver.ReleaseModal(page.Page);
            foreach (var stack in observedStacks.Where(pair =>
                (ReferenceEquals(page.Page, page.Root) && ReferenceEquals(pair.Value, page.Root)) || ContainsPage(page.Page, pair.Key))
                .Select(pair => pair.Key).ToArray())
                if (observedStacks.Remove(stack))
                {
                    stack.Pushed -= StackPushed;
                    NativeNavigationObserver.Detach(stack);
                }
        }
    }

    private void MarkOwnedPages(IEnumerable<MauiNavigationPage> pages, DismissalReason reason)
    {
        foreach (var page in pages)
        {
            foreach (var item in retainedItems.Where(item => item.Owner == page)) item.Entry?.Lifetime.MarkDismissed(reason);
            foreach (var model in ViewModelTree.Collect(page.Content).Where(model => CanCleanLegacy(model)))
            {
                if (reason == DismissalReason.RootReplaced) model.MarkHostReplaced();
                model.Lifetime.MarkDismissed(reason);
            }
            page.Entry.Lifetime.MarkDismissed(reason);
        }
    }

    private async Task DismissRemovedAsync(Page[] removed)
    {
        MarkOwnedPages(removed.SelectMany(page => OwnedWithin(page)), DismissalReason.Back);
        foreach (var model in removed.SelectMany(page => ViewModelTree.Collect(page)).Where(model => CanCleanLegacy(model))) model.Lifetime.MarkDismissed(DismissalReason.Back);
        foreach (var page in removed) await DismissTreeAsync(page, null, ViewModelTree.Collect(page), DismissalReason.Back);
    }

    private Task NativePagesRemovedAsync(IReadOnlyList<Page> removed)
    {
        if (containerMutation != 0 || IsClosed || InHostCallback()) return Task.CompletedTask;
        var pages = removed.Where(page => !IsPageInWindow(page)).ToArray();
        foreach (var page in nativeDepartures.Where(departing => removed.Any(page => ContainsPage(page, departing))).ToArray())
            EndNativeDeparture(page);
        var owned = pages.SelectMany(page => OwnedWithin(page)).Distinct().ToArray();
        var models = pages.SelectMany(page => ViewModelTree.Collect(page)).Where(model => CanCleanLegacy(model)).Distinct().ToArray();
        MarkOwnedPages(owned, DismissalReason.Back);
        foreach (var model in models) model.Lifetime.MarkDismissed(DismissalReason.Back);
        var lifetimes = OwnedLifetimes(owned)
            .Concat(models.Select(model => model.Lifetime)).Distinct().ToArray();
        CancelExternalPreparation();
        SignalLifetimes(lifetimes);
        return ScheduleNativeReconciliation();
    }

    private void SignalLifetimes(IEnumerable<NavigationLifetime> lifetimes)
    {
        var countKnown = lifetimes.TryGetNonEnumeratedCount(out var count);
        if (countKnown && count == 0) return;
        using var coordinatorCallback = coordinator.EnterCallback();
        using var hostCallback = NavigationCallbackScope.Enter(this);
        var signalled = countKnown && count == 1 ? null : new HashSet<NavigationLifetime>();
        foreach (var lifetime in lifetimes) lifetime.SignalCancellation(signalled);
    }

    private IEnumerable<NavigationLifetime> OwnedLifetimes(IEnumerable<MauiNavigationPage> pages) =>
        pages.SelectMany(page => retainedItems.Where(item => item.Owner == page && item.Entry != null)
            .Select(item => item.Entry!.Lifetime)
            .Concat(ViewModelTree.Collect(page.Content).Where(model => CanCleanLegacy(model)).Select(model => model.Lifetime))
            .Append(page.Entry.Lifetime));

    private void StackPushed(object? sender, NavigationEventArgs args)
    { NativeChanged(); }

    private void WindowModalPushed(object? sender, ModalPushedEventArgs args)
    {
        if (IsClosed || InHostCallback() || containerMutation != 0 || PopupOwnership.IsPopupPage(args.Modal)) return;
        try { RefreshNativeOwnership(); }
        catch (Exception error) { NavigationDiagnostics.Report(error, "Native modal adoption"); }
        NativeChanged();
    }

    private async void WindowModalPopped(object? sender, ModalPoppedEventArgs args)
    {
        if (!ownedPages.Any(item => item.IsModal && ReferenceEquals(item.Page, args.Modal))) return;
        try { await NativePagesRemovedAsync([args.Modal]); }
        catch (Exception error) { NavigationDiagnostics.Report(error, "Owned native modal removal"); }
    }
}
