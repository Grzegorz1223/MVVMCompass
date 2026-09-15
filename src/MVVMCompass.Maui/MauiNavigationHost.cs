using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass;

/// <summary>Coordinates roots, stacks, modals and ownership on one MAUI window dispatcher.</summary>
internal sealed partial class MauiNavigationHost : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<object, NavigationLifetime> claims = new();
    private static readonly AsyncLocal<Execution?> execution = new();
    private readonly IViewLocator locator;
    private readonly NavigationOptions options;
    private IServiceScopeFactory? scopeFactory;
    private readonly LegacyNavigationService composition;
    private readonly NavigationCoordinator coordinator;
    private readonly CancellationTokenSource closing = new();
    private readonly CancellationToken closingToken;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int closed;
    private bool installing;
    private CancellationTokenSource? activeCancellation;

    internal MauiNavigationHost(Window window, IViewLocator locator, NavigationOptions options, IServiceScopeFactory? scopeFactory = null)
    {
        Window = window;
        closingToken = closing.Token;
        this.locator = locator;
        this.options = options;
        this.scopeFactory = scopeFactory;
        composition = new(locator, options, window) { ResolveOwnedView = ResolveOwnedView, ComposeRetained = ComposeRetainedAsync };
        var dispatcher = window.Dispatcher ?? throw new InvalidOperationException("The window requires an owning MAUI dispatcher.");
        coordinator = new(callback => dispatcher.DispatchAsync(callback));
        window.Destroying += WindowDestroying;
        window.PropertyChanged += WindowPropertyChanged;
        window.ModalPopped += WindowModalPopped;
        window.ModalPopping += ContentModalPopping;
        window.ModalPushed += WindowModalPushed;
    }

    /// <summary>Gets the explicitly bound window; requests never select Application.Windows[0].</summary>
    public Window Window { get; }

    /// <summary>Gets the installed owned root, including a candidate whose activation failed.</summary>
    public MauiNavigationRoot? CurrentRoot { get; private set; }

    /// <summary>Gets whether disposal or native window destruction has begun.</summary>
    public bool IsClosed => Volatile.Read(ref closed) != 0;

    /// <summary>Completes after window destruction or disposal has awaited owned roots, popups and abandoned preparation.</summary>
    public Task Completion => completion.Task;

    internal void ValidateConfiguration(IViewLocator expectedLocator, NavigationOptions expectedOptions, IServiceScopeFactory? expectedScopeFactory)
    {
        if (!ReferenceEquals(locator, expectedLocator) || !ReferenceEquals(options, expectedOptions)
            || scopeFactory != null && expectedScopeFactory != null && !ReferenceEquals(scopeFactory, expectedScopeFactory))
            throw new InvalidOperationException("This window already belongs to another navigation configuration.");
        // The original constructor does not specify a scope provider. DI may supply it later.
        scopeFactory ??= expectedScopeFactory;
    }

    /// <summary>Replaces a registered legacy root using dictionary parameters and its existing lifecycle.</summary>
    public Task<NavigationOutcome<MauiNavigationRoot<TViewModel>>> ReplaceRootAsync<TViewModel>(
        NavigationRequest<Dictionary<string, object>?> request, bool navigable = false,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase =>
        ReplaceRegisteredAsync<TViewModel, Dictionary<string, object>?>(request,
            async (entry, parameter, token) =>
            {
                if (parameter != null) await entry.ViewModel.GetParameters(parameter);
                token.ThrowIfCancellationRequested();
            }, navigable, cancellationToken);

    /// <summary>Replaces a registered legacy root using typed initialization before BeforeFirstShown.</summary>
    public Task<NavigationOutcome<MauiNavigationRoot<TViewModel>>> ReplaceRootAsync<TViewModel, TParameter>(
        NavigationRequest<TParameter> request, bool navigable = false,
        CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase, INavigationInitializable<TParameter> =>
        ReplaceRegisteredAsync<TViewModel, TParameter>(request,
            (entry, parameter, token) => entry.InitializeAsync(parameter, token), navigable, cancellationToken);

    /// <summary>
    /// Replaces a root using an ordinary view model and page factories. The entry owns the model
    /// before page construction and typed initialization; explicit cleanup releases owned resources.
    /// </summary>
    public Task<NavigationOutcome<MauiNavigationRoot<TViewModel>>> ReplaceRootAsync<TViewModel, TParameter>(
        NavigationRequest<TParameter> request, Func<TViewModel> createViewModel, Func<TViewModel, Page> createPage,
        bool navigable = false, Func<TViewModel, Task>? cleanup = null, CancellationToken cancellationToken = default)
        where TViewModel : class, INavigationInitializable<TParameter>
    {
        return RunReplacementAsync(request, PlainFactory(createViewModel, createPage, cleanup),
            (entry, parameter, token) => entry.InitializeAsync(parameter, token), navigable, cancellationToken);
    }

    private Task<NavigationOutcome<MauiNavigationRoot<TViewModel>>> ReplaceRegisteredAsync<TViewModel, TParameter>(
        NavigationRequest<TParameter> request,
        Func<NavigationEntry<TViewModel>, TParameter, CancellationToken, Task> initialize,
        bool navigable, CancellationToken cancellationToken) where TViewModel : ViewModelBase =>
        RunReplacementAsync(request, RegisteredFactory<TViewModel>(), RegisteredInitializer(initialize), navigable, cancellationToken);

    private Func<RootPreparation, Action<NavigationEntry<TViewModel>>, Task<Page>> RegisteredFactory<TViewModel>()
        where TViewModel : ViewModelBase => async (preparation, own) =>
        {
            var visual = await NavigationViewFactory.CreateAsync<TViewModel>(locator, preparation.CancellationToken);
            preparation.Track(visual);
            if (visual is not Page page || visual is not IHasVM { ViewModel: TViewModel model })
                throw new InvalidOperationException("A registered destination must be a Page with the requested IHasVM view model.");
            own(Claim(model, () => coordinator.AttachEntry(model, model.Lifetime)));
            composition.WireRoot(page, model);
            return page;
        };

    private static Func<NavigationEntry<TViewModel>, TParameter, CancellationToken, Task> RegisteredInitializer<TViewModel, TParameter>(
        Func<NavigationEntry<TViewModel>, TParameter, CancellationToken, Task> initialize) where TViewModel : ViewModelBase =>
        async (entry, parameter, token) =>
        {
            await initialize(entry, parameter, token);
            token.ThrowIfCancellationRequested();
            await entry.ViewModel.BeforeFirstShown();
            token.ThrowIfCancellationRequested();
        };

    private Func<RootPreparation, Action<NavigationEntry<TViewModel>>, Task<Page>> PlainFactory<TViewModel>(
        Func<TViewModel> createViewModel, Func<TViewModel, Page> createPage, Func<TViewModel, Task>? cleanup) where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(createViewModel);
        ArgumentNullException.ThrowIfNull(createPage);
        return (preparation, own) =>
        {
            var model = createViewModel() ?? throw new InvalidOperationException("The view model factory returned null.");
            if (model is ViewModelBase)
                throw new InvalidOperationException("Use registered overloads for ViewModelBase and its existing lifecycle.");
            var entry = Claim(model, () => coordinator.CreateEntry(model, cleanup == null ? null : () => cleanup(model)));
            own(entry);
            var page = createPage(model) ?? throw new InvalidOperationException("The page factory returned null.");
            if (page.Parent != null || ReferenceEquals(page, Window.Page))
                throw new InvalidOperationException("Navigation factories must return detached pages.");
            if (page.BindingContext != null && !ReferenceEquals(page.BindingContext, model))
                throw new InvalidOperationException("The page must bind to the supplied view model.");
            preparation.Track(page);
            page.BindingContext = model;
            ViewModelTree.AdoptChildren(page, entry.Lifetime);
            return Task.FromResult(page);
        };
    }

    private async Task<NavigationOutcome<MauiNavigationRoot<TViewModel>>> RunReplacementAsync<TViewModel, TParameter>(
        NavigationRequest<TParameter> request,
        Func<RootPreparation, Action<NavigationEntry<TViewModel>>, Task<Page>> create,
        Func<NavigationEntry<TViewModel>, TParameter, CancellationToken, Task> initialize,
        bool navigable, CancellationToken cancellationToken) where TViewModel : class
    {
        var legacyReentrant = RootOperationGate.IsLegacyReentrant(Window);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closingToken);
        return await coordinator.RunAsync(request, async context =>
        {
            using var callbackScope = NavigationCallbackScope.Enter(this);
            if (legacyReentrant) throw new InvalidOperationException("A legacy root callback cannot await another root on its window.");
            using var gate = await RootOperationGate.EnterAsync(Window, context.CancellationToken);
            var oldRoot = Window.Page;
            var oldOwned = CurrentRoot;
            var application = Application.Current;
            var wasRegistered = application?.Windows.Contains(Window) == true;
            using var preparation = new RootPreparation(MauiNavigationHostFactory.ExistingModels(Window), context.CancellationToken);
            NavigationEntry<TViewModel>? entry = null;
            MauiNavigationRoot<TViewModel>? candidate = null;
            var stage = RootReplacementStage.Commitment;
            var parent = execution.Value;
            var frame = new Execution(this, parent) { Committed = () => context.HasCommitted };
            execution.Value = frame;
            activeCancellation = linked;
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (CurrentContentNavigation is { } outgoingContent) await outgoingContent.GuardRootAsync(context, request.Origin);
                var content = await create(preparation, owned => entry = context.Own(owned));
                candidate = new(navigable ? new NavigationPage(content) : content, content, entry!);
                TrackRoot(candidate);
                await initialize(entry!, context.Parameter, context.CancellationToken);
                DiscoverRetained(ownedPages.Single(page => page.Entry == entry));
                ValidateWindow(oldRoot, application, wasRegistered);
                var outgoing = ViewModelTree.Collect(oldRoot, true);
                preparation.Validate(outgoing);
                context.BeginCommit();
                await DismissTreeAsync(oldRoot, oldOwned?.Entry, outgoing, DismissalReason.RootReplaced);
                ValidateWindow(oldRoot, application, wasRegistered);
                preparation.FinishPreparation();
                NativeNavigationObserver.AttachApplication();
                if (candidate.Page is NavigationPage navigation) NativeNavigationObserver.Attach(navigation);
                installing = true;
                try { Window.Page = candidate.Page; }
                finally { installing = false; }
                if (!ReferenceEquals(Window.Page, candidate.Page))
                    throw new InvalidOperationException("The replacement was changed during installation.");
                CurrentRoot = candidate;
                TrackRoot(candidate);
                composition.SetCurrentRoot(content);
                stage = RootReplacementStage.Activation;
                await entry!.ActivateAsync();
                await preparation.ActivateAsync();
                DiscoverRetained(ownedPages.Single(page => page.Entry == entry));
                await ActivateVisibleEntriesAsync();
                BindRetainedContainers();
                ObserveNativeTree();
                if (IsClosed || !ReferenceEquals(Window.Page, candidate.Page) || entry.Lifetime.IsDismissed)
                    throw new InvalidOperationException("The root was removed during activation.");
                return candidate;
            }
            catch (Exception error)
            {
                var installed = candidate != null && ReferenceEquals(Window.Page, candidate.Page);
                if (installed)
                {
                    CurrentRoot = candidate;
                    TrackRoot(candidate!);
                    composition.SetCurrentRoot(candidate!.Content);
                }
                if (context.HasCommitted) throw new RootReplacementException(stage, installed, error);
                throw;
            }
            finally
            {
                try
                {
                    if (candidate == null || !ReferenceEquals(Window.Page, candidate.Page))
                        await DismissTreeAsync(candidate?.Page, entry, preparation.OwnedModels, DismissalReason.PreparationFailed);
                }
                finally
                {
                    activeCancellation = null;
                    frame.Executing = false;
                    execution.Value = parent;
                }
            }
        }, linked.Token);
    }

    private void ValidateWindow(Page? expected, Application? application, bool wasRegistered)
    {
        if (IsClosed || !ReferenceEquals(Window.Page, expected) ||
            (wasRegistered && (!ReferenceEquals(Application.Current, application) || !application!.Windows.Contains(Window))))
            throw new InvalidOperationException("The target window changed. The request cannot replace its current root.");
    }

    private static NavigationEntry<T> Claim<T>(T model, Func<NavigationEntry<T>> create) where T : class
    {
        lock (claims)
        {
            if (claims.TryGetValue(model, out _))
                throw new InvalidOperationException("Each navigation entry requires a fresh, unowned view model.");
            var entry = create();
            claims.Add(model, entry.Lifetime);
            return entry;
        }
    }

    private async Task DismissTreeAsync(Page? page, NavigationEntry? entry,
        IEnumerable<ViewModelBase> models, DismissalReason reason)
    {
        var tracked = OwnedWithin(page, entry);
        var retained = RetainedWithin(page, tracked);
        var owned = models.Concat(retained.Select(item => item.Entry?.ViewModel).OfType<ViewModelBase>())
            .Where(model => CanCleanLegacy(model, entry)).Distinct<ViewModelBase>(ReferenceEqualityComparer.Instance).ToArray();
        MarkOwnedPages(tracked, reason);
        if (reason == DismissalReason.RootReplaced)
            foreach (var model in owned) model.MarkHostReplaced();
        var attachedModels = tracked.SelectMany(item => ViewModelTree.Collect(item.Content)).ToHashSet();
        var lifetimes = owned.Where(model => !attachedModels.Contains(model)).Select(model => model.Lifetime).Concat(tracked.SelectMany(pageOwner => retained.Where(item => item.Owner == pageOwner && item.Entry != null).Select(item => item.Entry!.Lifetime)
                .Concat(ViewModelTree.Collect(pageOwner.Content).Where(model => CanCleanLegacy(model, entry)).Select(model => model.Lifetime)).Append(pageOwner.Entry.Lifetime)))
            .Concat(retained.Where(item => item.Entry != null).Select(item => item.Entry!.Lifetime))
            .Concat(owned.Select(model => model.Lifetime))
            .Concat(entry == null ? [] : new[] { entry.Lifetime }).Distinct().ToArray();
        foreach (var lifetime in lifetimes) lifetime.MarkDismissed(reason);
        SignalLifetimes(lifetimes);
        if (page != null)
            try { await PopupOwnership.EndForRootAsync(Window, page, reason); }
            catch (Exception error) { NavigationDiagnostics.Report(error, $"Window popup cleanup ({reason})"); }
        try
        {
            await NavigationLifetimeGroup.DismissAsync(lifetimes, reason);
        }
        catch (Exception error) { NavigationDiagnostics.Report(error, $"Window root cleanup ({reason})"); }
        finally
        {
            composition.UnwireScopedSubscriptions(owned.Concat(tracked.SelectMany(item => ViewModelTree.Collect(item.Content))));
            ForgetRetained(retained);
            ForgetPages(tracked);
            if (page is NavigationPage navigation) NativeNavigationObserver.Detach(navigation);
        }
        foreach (var error in lifetimes.SelectMany(lifetime => lifetime.CancellationErrors))
            NavigationDiagnostics.Report(error, "Window root cancellation");
    }

    private async void WindowPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        try
        {
            if (args.PropertyName != nameof(Window.Page) || installing || IsClosed) return;
            var previous = CurrentRoot;
            if (previous == null || ReferenceEquals(Window.Page, previous.Page))
            {
                CancelExternalPreparation();
                return;
            }
            // Native/external replacement is already irreversible. End origin validity immediately.
            var outgoing = ViewModelTree.Collect(previous.Page, true);
            foreach (var model in outgoing)
            {
                model.MarkHostReplaced();
                model.Lifetime.MarkDismissed(DismissalReason.RootReplaced);
            }
            previous.Entry.Lifetime.MarkDismissed(DismissalReason.RootReplaced);
            MarkOwnedPages(OwnedWithin(previous.Page, previous.Entry), DismissalReason.RootReplaced);
            CancelExternalPreparation();
            SignalLifetimes(OwnedLifetimes(OwnedWithin(previous.Page, previous.Entry))
                .Concat(outgoing.Select(model => model.Lifetime)).Append(previous.Entry.Lifetime));
            PruneNativeSubscriptions([]);
            await QueueBoundaryAsync(async () =>
            {
                using var gate = await RootOperationGate.EnterAsync(Window, CancellationToken.None);
                await DismissTreeAsync(previous.Page, previous.Entry, outgoing, DismissalReason.RootReplaced);
                if (ReferenceEquals(CurrentRoot, previous))
                {
                    CurrentRoot = null;
                    composition.SetCurrentRoot(null);
                }
            }, required: false);
        }
        catch (Exception error) { NavigationDiagnostics.Report(error, "External window root replacement"); }
    }

    private void CancelExternalPreparation()
    {
        // Cancels only the executing request. Queued required work keeps its order, and Core
        // shields a committed request even though this caller-side source becomes cancelled.
        if (activeCancellation == null) return;
        using var coordinatorCallback = coordinator.EnterCallback();
        using var hostCallback = NavigationCallbackScope.Enter(this);
        using var cancellationCallbacks = NavigationCallbackScope.ProtectCancellation();
        try { activeCancellation?.Cancel(); }
        catch (AggregateException error) { NavigationDiagnostics.Report(error, "External root preparation cancellation"); }
    }

    private async void WindowDestroying(object? sender, EventArgs args)
    {
        try { await CloseAsync(DismissalReason.WindowClosed); }
        catch (Exception error) { NavigationDiagnostics.Report(error, "Navigation window destruction"); }
    }

    /// <summary>Ends this host and awaits cleanup. Call outside navigation lifecycle callbacks.</summary>
    public ValueTask DisposeAsync()
    {
        if (RootOperationGate.IsLegacyReentrant(Window))
            return ValueTask.FromException(new InvalidOperationException("Dispose the host outside legacy root callbacks."));
        if (InHostCallback())
            return ValueTask.FromException(new InvalidOperationException("Dispose the host outside its navigation callbacks."));
        return new(CloseAsync(DismissalReason.Removed));
    }

    private Task CloseAsync(DismissalReason reason)
    {
        if (Interlocked.CompareExchange(ref closed, 1, 0) != 0) return Completion;
        _ = FinishAsync();
        return Completion;

        async Task FinishAsync()
        {
            try
            {
                await Window.Dispatcher.DispatchAsync(() =>
                {
                    using var coordinatorCallback = coordinator.EnterCallback();
                    using var hostCallback = NavigationCallbackScope.Enter(this);
                    var presented = ownedPages.Where(page => IsPageInWindow(page.Page)).ToArray();
                    var lifetimes = OwnedLifetimes(presented).ToArray();
                    MarkOwnedPages(presented, reason);
                    using var cancellationCallbacks = NavigationCallbackScope.ProtectCancellation();
                    try { closing.Cancel(); }
                    catch (AggregateException error) { NavigationDiagnostics.Report(error, "Navigation window cancellation"); }
                    SignalLifetimes(lifetimes);
                });
                await WaitForPopupPreparationsAsync();
                await QueueBoundaryAsync(async () =>
                {
                    Window.Destroying -= WindowDestroying;
                    Window.PropertyChanged -= WindowPropertyChanged;
                    Window.ModalPopped -= WindowModalPopped;
                    Window.ModalPopping -= ContentModalPopping;
                    Window.ModalPushed -= WindowModalPushed;
                    using var gate = await RootOperationGate.EnterAsync(Window, CancellationToken.None);
                    if (CurrentRoot is { } root)
                        await DismissTreeAsync(root.Page, root.Entry, ViewModelTree.Collect(root.Page, true), reason);
                    try { await PopupOwnership.EndForWindowAsync(Window, reason); }
                    catch (Exception error) { NavigationDiagnostics.Report(error, $"Window popup cleanup ({reason})"); }
                    CurrentRoot = null;
                    PruneNativeSubscriptions([]);
                    lock (nativeScheduleGate) nativeCallbacks.Clear();
                    composition.SetCurrentRoot(null);
                });
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { closing.Dispose(); }
        }
    }

    private Task QueueBoundaryAsync(Func<Task> callback, bool required = true, Action? beforeAdmission = null)
    {
        // A native event can be raised inside the active operation. It must enqueue after that
        // operation, without inheriting its logical reentrancy frame or blocking its callback.
        var synchronization = SynchronizationContext.Current;
        if (synchronization != null && !Window.Dispatcher.IsDispatchRequired)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ExecutionContext.IsFlowSuppressed()) Post();
            else using (ExecutionContext.SuppressFlow()) Post();
            return signal.Task;

            void Post() => synchronization.Post(async _ =>
            {
                try { await EnqueueAsync(); signal.TrySetResult(); }
                catch (OperationCanceledException error) { signal.TrySetCanceled(error.CancellationToken); }
                catch (Exception error) { signal.TrySetException(error); }
            }, null);
        }
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(EnqueueAsync);
        using (ExecutionContext.SuppressFlow()) return Task.Run(EnqueueAsync);

        async Task EnqueueAsync()
            {
                if (beforeAdmission != null) await Window.Dispatcher.DispatchAsync(beforeAdmission);
                while (true)
                {
                    var outcome = await coordinator.RunAsync(new NavigationRequest<int>(0)
                        { Priority = required ? NavigationPriority.Required : NavigationPriority.Normal },
                        async context =>
                        {
                            using var callbackScope = NavigationCallbackScope.Enter(this);
                            context.BeginCommit(); await callback(); return true;
                        });
                    // Housekeeping cannot be lost when a required request overtakes a normal boundary.
                    if (outcome.Status == NavigationStatus.Superseded) continue;
                    if (!outcome.IsSuccess)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outcome.Error
                            ?? new InvalidOperationException($"Window cleanup was rejected: {outcome.Status}.")).Throw();
                    return;
                }
            }
    }

    private sealed class Execution(MauiNavigationHost host, Execution? parent)
    {
        internal MauiNavigationHost Host { get; } = host;
        internal Execution? Parent { get; } = parent;
        internal Func<bool>? Committed;
        internal volatile bool Executing = true;
    }
}
