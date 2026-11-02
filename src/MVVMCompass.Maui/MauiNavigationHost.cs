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
    private readonly NavigationCoordinator coordinator;
    private readonly CancellationTokenSource closing = new();
    private readonly CancellationToken closingToken;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int closed;
    private bool installing;
    private CancellationTokenSource? activeCancellation;
    internal Func<NavigationResult, View>? RootFailureContentFactory { get; set; }

    internal MauiNavigationHost(Window window)
    {
        Window = window;
        closingToken = closing.Token;
        var dispatcher = window.Dispatcher ?? throw new InvalidOperationException("The window requires an owning MAUI dispatcher.");
        coordinator = new(callback => DispatchNavigationAsync(dispatcher, callback));
        coordinator.Settled += NotifyDeferredPopups;
        window.Destroying += WindowDestroying;
        window.PropertyChanged += WindowPropertyChanged;
        window.ModalPopped += WindowModalPopped;
        window.ModalPopping += ContentModalPopping;
        window.ModalPushed += WindowModalPushed;
    }

    private static Task DispatchNavigationAsync(IDispatcher dispatcher, Func<Task> callback)
    {
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!dispatcher.Dispatch(async () =>
            {
                try { await callback(); dispatched.TrySetResult(); }
                catch (Exception error) { dispatched.TrySetException(error); }
            })) dispatched.TrySetException(new InvalidOperationException("The window dispatcher rejected navigation."));
        }
        catch (Exception error) { dispatched.TrySetException(error); }
        return dispatched.Task;
    }

    /// <summary>Gets the explicitly bound window; requests never select Application.Windows[0].</summary>
    public Window Window { get; }

    /// <summary>Gets the installed owned root, including a candidate whose activation failed.</summary>
    public MauiNavigationRoot? CurrentRoot { get; private set; }

    /// <summary>Gets whether disposal or native window destruction has begun.</summary>
    public bool IsClosed => Volatile.Read(ref closed) != 0;

    /// <summary>Completes after window destruction or disposal has awaited owned roots, popups and abandoned preparation.</summary>
    public Task Completion => completion.Task;

    private Func<RootPreparation, Action<NavigationEntry<NavigationContext>>, Task<Page>> ContentRootFactory(
        Func<NavigationContext> createContext, Func<NavigationContext, Page> createPage) => (preparation, own) =>
    {
        var context = createContext();
        var entry = Claim(context, () => coordinator.CreateEntry(context));
        own(entry);
        var page = createPage(context);
        preparation.Track(page);
        page.BindingContext = context;
        ViewModelTree.AdoptChildren(page, entry.Lifetime);
        return Task.FromResult(page);
    };

    private async Task<NavigationOutcome<MauiNavigationRoot<NavigationContext>>> RunReplacementAsync<TParameter>(
        NavigationRequest<TParameter> request,
        Func<RootPreparation, Action<NavigationEntry<NavigationContext>>, Task<Page>> create,
        Func<NavigationEntry<NavigationContext>, TParameter, CancellationToken, Task> initialize,
        CancellationToken cancellationToken, RootTransitionMode? applicationMode = null, bool submitted = false,
        Func<CancellationToken, Task>? prepare = null)
    {
        if (IsClosed) return new(applicationMode == null ? NavigationStatus.Cancelled : NavigationStatus.InvalidOrigin, null, false);
        if (!submitted && IsContentCallback) return new(NavigationStatus.Reentrant, null, false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closingToken);
        return await (submitted ? coordinator.SubmitAsync(request, ExecuteAsync, linked.Token)
            : coordinator.RunAsync(request, ExecuteAsync, linked.Token));

        async Task<MauiNavigationRoot<NavigationContext>> ExecuteAsync(NavigationOperationContext<TParameter> context)
        {
            using var callbackScope = NavigationCallbackScope.Enter(this);
            using var gate = await RootOperationGate.EnterAsync(Window, context.CancellationToken);
            if (IsClosed) context.Reject(NavigationStatus.InvalidOrigin);
            var oldRoot = Window.Page;
            var oldOwned = CurrentRoot;
            var application = Application.Current;
            var wasRegistered = application?.Windows.Contains(Window) == true;
            using var preparation = new RootPreparation(MauiNavigationHostFactory.ExistingModels(Window));
            NavigationEntry<NavigationContext>? entry = null;
            MauiNavigationRoot<NavigationContext>? candidate = null;
            var candidateCleanupAttempted = false;
            var cleanupErrors = new List<Exception>();
            void RecordCleanup(Exception error) { cleanupErrors.Add(error); context.RecordCleanupError(error); }
            var stage = RootReplacementStage.Commitment;
            var parent = execution.Value;
            var frame = new Execution(this, parent) { Committed = () => context.HasCommitted };
            execution.Value = frame;
            activeCancellation = linked;
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (applicationMode == RootTransitionMode.Normal) await GuardWindowRootAsync(context, oldRoot, oldOwned?.Entry);
                else if (applicationMode == null && CurrentContentNavigation is { } outgoingContent)
                    await outgoingContent.GuardRootAsync(context, request.Origin);
                if (prepare != null) await prepare(context.CancellationToken);
                context.CancellationToken.ThrowIfCancellationRequested();
                var content = await create(preparation, owned => entry = context.Own(owned));
                candidate = new(content, content, entry!);
                TrackRoot(candidate);
                await initialize(entry!, context.Parameter, context.CancellationToken);
                ValidateWindow(oldRoot, application, wasRegistered);
                var outgoing = ViewModelTree.Collect(oldRoot, true);
                preparation.Validate(outgoing);
                context.BeginCommit();
                await DismissTreeAsync(oldRoot, oldOwned?.Entry, outgoing, DismissalReason.RootReplaced,
                    RecordCleanup, dismissModals: applicationMode != null);
                ValidateWindow(oldRoot, application, wasRegistered);
                NativeModalObserver.AttachApplication();
                installing = true;
                try { Window.Page = candidate.Page; }
                finally { installing = false; }
                if (!ReferenceEquals(Window.Page, candidate.Page))
                    throw new InvalidOperationException("The replacement was changed during installation.");
                CurrentRoot = candidate;
                TrackRoot(candidate);
                stage = RootReplacementStage.Activation;
                await entry!.ActivateAsync();
                await ActivateVisibleEntriesAsync();
                ObserveNativeTree();
                if (IsClosed || !ReferenceEquals(Window.Page, candidate.Page) || entry.Lifetime.IsDismissed)
                    throw new InvalidOperationException("The root was removed during activation.");
                // Publish the final entry state before releasing this operation to the next root.
                CurrentContentNavigation?.RefreshState();
                return candidate;
            }
            catch (Exception error)
            {
                var installed = candidate != null && ReferenceEquals(Window.Page, candidate.Page);
                if (installed)
                {
                    CurrentRoot = candidate;
                    TrackRoot(candidate!);
                }
                if (context.HasCommitted)
                {
                    var failure = new RootReplacementException(stage, installed, error);
                    if (RootFailureContentFactory != null && !IsClosed && (installed || ReferenceEquals(Window.Page, oldRoot)))
                    {
                        candidateCleanupAttempted = true;
                        await RecoverCommittedRootAsync(Window.Page!, candidate, entry, preparation.OwnedModels,
                            failure, cleanupErrors, RecordCleanup);
                    }
                    throw failure;
                }
                throw;
            }
            finally
            {
                try
                {
                    if (!candidateCleanupAttempted && (candidate == null || !ReferenceEquals(Window.Page, candidate.Page)))
                        await DismissTreeAsync(candidate?.Page, entry, preparation.OwnedModels, DismissalReason.PreparationFailed,
                            RecordCleanup);
                }
                finally
                {
                    activeCancellation = null;
                    frame.Executing = false;
                    execution.Value = parent;
                }
            }
        }
    }

    private async Task GuardWindowRootAsync<T>(NavigationOperationContext<T> operation, Page? root, NavigationEntry? rootEntry)
    {
        var models = OwnedWithin(root, rootEntry).Select(item => item.Entry.ViewModel).OfType<NavigationContext>()
            .SelectMany(context => context.AllScreens()).Select(screen => screen.ViewModel)
            .Concat(ViewModelTree.Collect(root, true)).Distinct().ToArray();
        foreach (var model in models)
        {
            operation.CancellationToken.ThrowIfCancellationRequested();
            if (model.IsDismissed) continue;
            if (!await model.CanNavigate()) operation.Reject(NavigationStatus.GuardRejected);
            operation.CancellationToken.ThrowIfCancellationRequested();
        }
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
        IEnumerable<ViewModelBase> models, DismissalReason reason, Action<Exception>? recordError = null, bool dismissModals = false)
    {
        var tracked = OwnedWithin(page, entry);
        var owned = models.Where(model => CanCleanModel(model, entry))
            .Concat(tracked.SelectMany(page => OwnedModels(page).Where(model => CanCleanModel(model, page.Entry))))
            .Concat(page == null ? [] : PopupOwnership.ModelsFor(page))
            .Distinct<ViewModelBase>(ReferenceEqualityComparer.Instance).ToArray();
        MarkOwnedPages(tracked, reason);
        if (reason == DismissalReason.RootReplaced)
            foreach (var model in owned) model.MarkHostReplaced();
        var lifetimes = owned.Select(model => model.Lifetime).Concat(tracked.Select(item => item.Entry.Lifetime))
            .Concat(entry == null ? [] : new[] { entry.Lifetime }).Distinct().ToArray();
        foreach (var lifetime in lifetimes) lifetime.MarkDismissed(reason);
        SignalLifetimes(lifetimes);
        Exception? modalError = null;
        if (dismissModals)
        {
            containerMutation++;
            try
            {
                while (Window.Navigation.ModalStack.LastOrDefault() is { } modal)
                {
                    if (PopupOwnership.IsPopupPage(modal)) await PopupOwnership.CloseTopAsync(Window);
                    else await Window.Navigation.PopModalAsync(false);
                    if (Window.Navigation.ModalStack.Contains(modal))
                        throw new InvalidOperationException("The outgoing modal could not be removed during root replacement.");
                }
                await FinishNativeRemovalAsync();
            }
            catch (Exception error) { modalError = error; }
            finally { containerMutation--; }
        }
        if (page != null)
            try { await PopupOwnership.EndForRootAsync(Window, page, reason); }
            catch (Exception error) { recordError?.Invoke(error); NavigationDiagnostics.Report(error, $"Window popup cleanup ({reason})"); }
        try
        {
            await NavigationLifetimeGroup.DismissAsync(lifetimes, reason);
        }
        catch (Exception error) { recordError?.Invoke(error); NavigationDiagnostics.Report(error, $"Window root cleanup ({reason})"); }
        finally
        {
            ForgetPages(tracked);
        }
        foreach (var error in lifetimes.SelectMany(lifetime => lifetime.CancellationErrors))
        { recordError?.Invoke(error); NavigationDiagnostics.Report(error, "Window root cancellation"); }
        if (modalError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(modalError).Throw();
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
            var outgoing = ViewModelTree.Collect(previous.Page, true).Where(model => CanCleanModel(model, previous.Entry))
                .Concat(PopupOwnership.ModelsFor(previous.Page)).Distinct().ToArray();
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
                    NotifyDeferredPopups();
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
                });
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { closing.Dispose(); coordinator.Settled -= NotifyDeferredPopups; }
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
