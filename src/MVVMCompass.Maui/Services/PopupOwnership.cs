using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Services;

internal interface INativePopupClose
{
    Task CloseNativeAsync(CancellationToken cancellationToken);
}

/// <summary>Joins Toolkit results, native removal and VM cleanup without holding the window operation queue.</summary>
internal static class PopupOwnership
{
    private static readonly ConditionalWeakTable<View, Session> sessions = new();
    private static readonly ConditionalWeakTable<Window, WindowSessions> windows = new();
    private static readonly ConditionalWeakTable<Page, List<Session>> roots = new();
    private static readonly AsyncLocal<Session?> cleaning = new();

    internal static Task NativeBackCompletion(Popup popup) => sessions.TryGetValue(popup, out var session) ? session.NativeBackCompletion : Task.CompletedTask;

    internal static bool HasGuardedPopup(Window window) => GuardedTop(window) != null;
    internal static bool RequestPlatformBack(Window window, out Task completion)
    {
        var session = GuardedTop(window);
        if (session == null) { completion = Task.CompletedTask; return false; }
        session.RequestNativeBack(); completion = session.NativeBackCompletion; return true;
    }
    private static Session? GuardedTop(Window window) => windows.TryGetValue(window, out var state)
        ? state.Active.LastOrDefault(session => session.Page == window.Navigation.ModalStack.LastOrDefault()
            && session.Models.Any(model => model.NavigationBinding != null && !model.IsDismissed)) : null;

    internal static bool IsTop(Window window, Popup popup) => sessions.TryGetValue(popup, out var session)
        && session.Page != null && ReferenceEquals(window.Navigation.ModalStack.LastOrDefault(), session.Page);

    internal static bool IsPopupPage(Page page) => page != null && page.GetType().Assembly == typeof(Popup).Assembly
        && (page.GetType().Name == "PopupPage" || page.GetType().Name.StartsWith("PopupPage`", StringComparison.Ordinal));

    internal static async Task<PopupNavigationResult<TResult>> ShowAsync<TResult>(Window window, View view,
        ViewModelBase? origin = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (origin?.IsDismissed == true) throw new InvalidOperationException("A dismissed view model cannot present a popup.");
        var session = Create(window, view, origin);
        return await session.ShowAsync<TResult>(cancellationToken);
    }

    internal static void Observe(Popup popup)
    {
        // Also covers callers that use the Toolkit extensions directly on PopupViewBase.
        EventHandler? opened = null;
        opened = (_, _) =>
        {
            try
            {
                if (sessions.TryGetValue(popup, out _)) return;
                var page = NativePage(popup);
                if (page?.Window is not { } window) return;
                var session = Create(window, popup, null);
                session.Attach(page);
            }
            catch (Exception error) { NavigationDiagnostics.Report(error, "Popup ownership"); }
        };
        popup.Opened += opened;
        if (popup is IHasVM hasVm) hasVm.ViewModel.RegisterSubscriptionCleanup(() => popup.Opened -= opened);
    }

    internal static async Task CloseAsync(Popup popup, Func<Task> close, CancellationToken token, bool hasResult = false)
    {
        if (!sessions.TryGetValue(popup, out var session)) { await CloseUntrackedAsync(popup, close, token); return; }
        var previous = session.HasResult; session.HasResult = hasResult;
        try { await session.CloseAsync(close, token); }
        catch { session.HasResult = previous; throw; }
    }

    private static async Task CloseUntrackedAsync(Popup popup, Func<Task> close, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await close();
        await NavigationLifetimeGroup.DismissAsync(ViewModelTree.Collect(popup).Select(model => model.Lifetime), DismissalReason.DialogClosed);
    }

    internal static async Task CloseTopAsync(Window window, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var top = window.Navigation.ModalStack.LastOrDefault();
        if (top == null) return;
        if (!IsPopupPage(top)) throw new PopupBlockedException(top);
        var session = ForWindow(window).Active.LastOrDefault(item => item.Page == top);
        if (session == null && FindPopup(top) is { } popup)
        { session = Create(window, popup, null); session.Attach(top); }
        if (session == null) throw new InvalidOperationException("The native popup content cannot be resolved.");
        await session.CloseAsync(() => session.CloseNativeAsync(token), token);
    }

    internal static IEnumerable<ViewModelBase> ModelsFor(Page root)
    {
        // Root replacement can detach the page from its window before cleanup queries it.
        // Index the captured root rather than rediscovering a window through the visual tree.
        if (!roots.TryGetValue(root, out var active)) return [];
        lock (active)
            return active.Count == 0 ? [] : active.AsEnumerable().Reverse().SelectMany(item => item.Models).Distinct().ToArray();
    }

    internal static async Task EndForRootAsync(Window window, Page root, DismissalReason reason)
    {
        if (!windows.TryGetValue(window, out var state)) return;
        var owned = state.Active.Where(item => item.Root == root).Reverse().ToArray();
        foreach (var item in owned) item.Mark(reason);
        foreach (var item in owned) item.SignalCancellation();
        List<Exception> errors = [];
        foreach (var item in owned)
            try { await item.EndAsync(reason); }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException(errors);
    }

    internal static async Task EndForWindowAsync(Window window, DismissalReason reason)
    {
        if (!windows.TryGetValue(window, out var state)) return;
        var owned = state.Active.Reverse().ToArray();
        foreach (var session in owned) session.Mark(reason);
        foreach (var session in owned) session.SignalCancellation();
        List<Exception> errors = [];
        foreach (var session in owned)
            try { await session.EndAsync(reason); }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException(errors);
    }

    internal static bool DeferLifecycle(ViewModelBase model, Func<Task> callback, string operation)
    {
        if (operation is not ("Appearing" or "NavigatedTo" or "Loaded")) return false;
        foreach (var state in windows.Select(pair => pair.Value))
            if (state.Active.LastOrDefault(item => item.Popping && item.ReturnModels.Contains(model)) is { } session)
            { session.Callbacks.Add((model, callback)); return true; }
        return false;
    }

    private static Session Create(Window window, View view, ViewModelBase? origin)
    {
        if (sessions.TryGetValue(view, out _)) throw new InvalidOperationException("This popup already has an active presentation.");
        var models = ViewModelTree.Collect(view).ToArray();
        if (models.Any(model => model.IsDismissed)) throw new InvalidOperationException("A popup presentation requires fresh, live view models.");
        if (models.Any(model => MauiNavigationHost.IsClaimedModel(model)
                && MauiNavigationHostFactory.Find(window)?.OwnsPopupView(view, model) != true))
            throw new InvalidOperationException("A popup cannot borrow a model owned by another navigation entry.");
        var state = ForWindow(window);
        if (windows.SelectMany(pair => pair.Value.Active).Any(item => item.Models.Intersect(models).Any()))
            throw new InvalidOperationException("A popup model already belongs to another presentation.");
        var session = new Session(state, view, origin, models);
        sessions.Add(view, session);
        state.Add(session);
        return session;
    }

    private static WindowSessions ForWindow(Window window) => windows.GetValue(window, value => new(value));

    private static Page? NativePage(Element view)
    {
        for (Element? current = view; current != null; current = current.Parent)
            if (current is Page page && IsPopupPage(page)) return page;
        return null;
    }

    private static Popup? FindPopup(Element element)
    {
        if (element is Popup popup) return popup;
        if (element is IVisualTreeElement visual)
            foreach (var child in visual.GetVisualChildren().OfType<Element>())
                if (FindPopup(child) is { } found) return found;
        return null;
    }

    private sealed class WindowSessions(Window window)
    {
        internal Window Window = window;
        private readonly List<Session> active = [];
        internal Session[] Active { get { lock (active) return active.ToArray(); } }
        internal void Add(Session session)
        {
            lock (active)
            {
                if (active.Count == 0)
                {
                    Window.ModalPopped += Popped;
                    Window.ModalPopping += Popping;
                    Window.Destroying += Destroying;
                    Window.PropertyChanged += Changed;
                }
                active.Add(session);
                if (session.Root is { } root)
                {
                    var rootSessions = roots.GetValue(root, static _ => []);
                    lock (rootSessions) rootSessions.Add(session);
                }
            }
        }
        internal void Remove(Session session)
        {
            lock (active)
            {
                active.Remove(session);
                if (session.Root is { } root && roots.TryGetValue(root, out var rootSessions))
                    lock (rootSessions) rootSessions.Remove(session);
                if (active.Count != 0) return;
                Window.ModalPopped -= Popped;
                Window.ModalPopping -= Popping;
                Window.Destroying -= Destroying;
                Window.PropertyChanged -= Changed;
            }
        }
        private void Popping(object? sender, ModalPoppingEventArgs args)
        {
            var session = Active.LastOrDefault(item => item.Page == args.Modal);
            if (session == null || session.Popping || !session.Models.Any(model => model.NavigationBinding != null && !model.IsDismissed)) return;
            args.Cancel = true;
            session.RequestNativeBack();
        }
        private async void Popped(object? sender, ModalPoppedEventArgs args)
        {
            var session = Active.LastOrDefault(item => item.Page == args.Modal);
            if (session == null || session.Popping) return;
            try { await session.EndAsync(DismissalReason.Back); }
            catch (Exception error) { NavigationDiagnostics.Report(error, "Native popup removal"); }
        }
        private async void Destroying(object? sender, EventArgs args)
        {
            var owned = Active.AsEnumerable().Reverse().ToArray();
            foreach (var session in owned) session.Mark(DismissalReason.WindowClosed);
            foreach (var session in owned) session.SignalCancellation();
            foreach (var session in owned)
                try { await session.EndAsync(DismissalReason.WindowClosed, force: true); }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Popup window destruction"); }
        }
        private async void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(Window.Page)) return;
            var removed = Active.Where(item => item.Root != Window.Page).Reverse().ToArray();
            foreach (var session in removed) session.Mark(DismissalReason.RootReplaced);
            foreach (var session in removed) session.SignalCancellation();
            foreach (var session in removed)
                try { await session.EndAsync(DismissalReason.RootReplaced); }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Popup root replacement"); }
        }
    }

    private sealed class Session
    {
        private readonly WindowSessions state;
        private readonly View view;
        private readonly ViewModelBase? origin;
        private readonly CancellationTokenSource abandoned = new();
        private readonly TaskCompletionSource<DismissalReason> external = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? cleanup, closeAttempt;
        private DismissalReason? terminalReason;
        private bool finished, showing;
        private NavigationProxy? pageNavigation;
        private INavigation? previousNavigation;
        private CloseNavigation? interception;
        internal Page? Page;
        internal Page? Root;
        internal ViewModelBase[] Models;
        internal HashSet<ViewModelBase> ReturnModels = [];
        internal List<(ViewModelBase Model, Func<Task> Callback)> Callbacks = [];
        internal bool Popping;

        internal Session(WindowSessions state, View view, ViewModelBase? origin, ViewModelBase[] models)
        {
            this.state = state; this.view = view; this.origin = origin; Models = models;
            Root = state.Window.Page;
            if (view is Popup popup) popup.Closed += Closed;
        }

        internal Task NativeBackCompletion { get; private set; } = Task.CompletedTask;
        private bool nativeBackRequested, outsideTapRequested, dismissedByOutsideTap;
        private CancellationToken closeToken;
        internal void RequestNativeBack(bool outsideTap = false)
        {
            if (!NativeBackCompletion.IsCompleted) return;
            NativeBackCompletion = CloseAfterEventAsync();
            async Task CloseAfterEventAsync()
            {
                await Task.Yield();
                nativeBackRequested = !outsideTap; outsideTapRequested = outsideTap;
                try { await CloseAsync(() => CloseNativeAsync(CancellationToken.None), CancellationToken.None); }
                catch (PopupGuardRejectedException) { }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Popup Back"); }
                finally { nativeBackRequested = false; outsideTapRequested = false; }
            }
        }

        internal bool HasResult;
        internal async Task<PopupNavigationResult<TResult>> ShowAsync<TResult>(CancellationToken cancellationToken)
        {
            showing = true;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, abandoned.Token);
            try
            {
                if (origin != null) { origin.IsComingFromPopup = true; origin.IsPopupOpen = true; }
                // Toolkit's outside-tap command is async void and cannot await a cancellable
                // guard safely. Registered popups use our gesture and Android Back routing.
                var options = Models.Any(model => model.NavigationBinding != null)
                    ? new CommunityToolkit.Maui.PopupOptions { CanBeDismissedByTappingOutsideOfPopup = false } : null;
                var displayed = new ShowNavigation(this) { Inner = state.Window.Navigation }
                    .ShowPopupAsync<TResult>(view, options, token: wait.Token);
                await Task.WhenAny(displayed, external.Task);
                if (external.Task.IsCompletedSuccessfully)
                {
                    var reason = await external.Task;
                    await CleanupAsync(reason);
                    return new(default, false, reason);
                }
                var result = await displayed;
                await CleanupAsync(DismissalReason.DialogClosed);
                var outside = dismissedByOutsideTap || result.WasDismissedByTappingOutsideOfPopup;
                return new(outside ? default : result.Result,
                    outside, Reason(DismissalReason.DialogClosed)) { HasResult = HasResult && !outside };
            }
            catch (OperationCanceledException) when (external.Task.IsCompletedSuccessfully)
            {
                var reason = await external.Task;
                await CleanupAsync(reason);
                return new(default, false, reason);
            }
            catch
            {
                // Cancelling a result wait does not close the Toolkit popup. Retain its session
                // until actual closure; failed installation releases the candidate immediately.
                if (Page == null || !state.Window.Navigation.ModalStack.Contains(Page))
                    await CleanupAsync(DismissalReason.PreparationFailed);
                else showing = false;
                throw;
            }
            finally { if (cleanup?.IsCompleted == true) Finish(); }
        }

        internal void Attach(Page page)
        {
            Page = page;
            NativeNavigationObserver.OwnModal(page);
            page.ParentChanged += ParentChanged;
            BindNavigation();
            if (view is Popup popup && Models.Any(model => model.NavigationBinding != null)
                && page is ContentPage { Content: Layout layout })
            {
                // Toolkit 15's public visual tree has an overlay followed by the popup border.
                // Keep its layout, shape, accessibility and result machinery; own only dismissal input.
                var overlay = layout.Children.OfType<BoxView>().SingleOrDefault();
                var border = layout.Children.OfType<Border>().SingleOrDefault();
                if (overlay == null || border == null) throw new InvalidOperationException("The Toolkit popup dismissal surface could not be resolved.");
                overlay.GestureRecognizers.Clear();
                var gesture = new TapGestureRecognizer();
                gesture.Tapped += (_, args) =>
                {
                    if (popup.CanBeDismissedByTappingOutsideOfPopup && args.GetPosition(layout) is { } position && !border.Bounds.Contains(position))
                        RequestNativeBack(outsideTap: true);
                };
                overlay.GestureRecognizers.Add(gesture);
            }
        }

        private void ParentChanged(object? sender, EventArgs args) => BindNavigation();

        private void BindNavigation()
        {
            if (Page?.Navigation is not NavigationProxy navigation || navigation.Inner == null || ReferenceEquals(navigation.Inner, interception)) return;
            pageNavigation = navigation;
            previousNavigation = navigation.Inner;
            interception = new CloseNavigation(this) { Inner = previousNavigation };
            navigation.Inner = interception;
        }

        internal Task CloseAsync(Func<Task> close, CancellationToken token)
        {
            if (ReferenceEquals(cleaning.Value, this) || NavigationCallbackScope.IsActive(this)
                || Models.Any(model => NavigationCallbackScope.IsWithinLifetime(model.Lifetime)))
                return Task.FromException(new InvalidOperationException("A popup cannot await its own close from terminal cleanup."));
            token.ThrowIfCancellationRequested();
            if (closeAttempt is { IsCompleted: false }) return closeAttempt.WaitAsync(token);
            if (cleanup != null) return cleanup;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            closeAttempt = signal.Task;
            closeToken = token;
            _ = CloseCoreAsync();
            return signal.Task;
            async Task CloseCoreAsync()
            {
                try { await close(); await CleanupAsync(nativeBackRequested ? DismissalReason.Back : DismissalReason.DialogClosed); signal.TrySetResult(); }
                catch (Exception error) { signal.TrySetException(error); }
                finally { if (cleanup?.IsCompleted == true && !showing) Finish(); }
            }
        }

        internal void Mark(DismissalReason reason)
        {
            terminalReason ??= reason;
            foreach (var model in Models)
            {
                if (reason == DismissalReason.RootReplaced) model.MarkHostReplaced();
                model.Lifetime.MarkDismissed(reason);
            }
        }

        private DismissalReason Reason(DismissalReason fallback) =>
            Models.FirstOrDefault()?.Lifetime.Reason ?? terminalReason ?? fallback;

        internal void SignalCancellation()
        {
            if (Models.Length == 0) return;
            using var callback = NavigationCallbackScope.Enter(this);
            var signalled = Models.Length == 1 ? null : new HashSet<NavigationLifetime>();
            foreach (var model in Models) model.Lifetime.SignalCancellation(signalled);
        }

        internal Task CloseNativeAsync(CancellationToken token)
        {
            var popup = view as Popup ?? (Page == null ? null : FindPopup(Page))
                ?? throw new InvalidOperationException("The popup content cannot be resolved.");
            // Toolkit's INavigation.ClosePopupAsync consults Shell.Current, including
            // in non-Shell multi-window applications. The instance resolves its own page.
            return popup is INativePopupClose native ? native.CloseNativeAsync(token) : popup.CloseAsync(token);
        }

        internal async Task EndAsync(DismissalReason reason, bool force = false)
        {
            if (finished) return;
            try
            {
                if (!force && Page != null && state.Window.Navigation.ModalStack.Contains(Page))
                {
                    if (state.Window.Navigation.ModalStack.LastOrDefault() != Page)
                        throw new PopupBlockedException(state.Window.Navigation.ModalStack[^1]);
                    await CloseAsync(() => CloseNativeAsync(CancellationToken.None), CancellationToken.None);
                }
            }
            finally
            {
                try { await CleanupAsync(reason); }
                finally
                {
                    external.TrySetResult(Reason(reason));
                    if (!finished) abandoned.Cancel();
                    if (!showing) Finish();
                }
            }
        }

        private Task CleanupAsync(DismissalReason reason)
        {
            if (cleanup != null) return cleanup;
            Mark(reason);
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cleanup = signal.Task;
            _ = CleanAsync();
            return signal.Task;
            async Task CleanAsync()
            {
                using var callback = NavigationCallbackScope.Enter(this);
                var previous = cleaning.Value; cleaning.Value = this;
                try
                {
                    await NavigationLifetimeGroup.DismissAsync(Models.Select(model => model.Lifetime), reason);
                    signal.TrySetResult();
                }
                catch (Exception error) { signal.TrySetException(error); }
                finally { cleaning.Value = previous; }
            }
        }

        private async void Closed(object? sender, EventArgs args)
        {
            try { await CleanupAsync(DismissalReason.DialogClosed); }
            catch (Exception error) { NavigationDiagnostics.Report(error, "Popup cleanup"); }
            finally { if (!showing) Finish(); }
        }

        private void Finish()
        {
            if (finished) return;
            finished = true;
            if (view is Popup popup) popup.Closed -= Closed;
            if (Page != null) { Page.ParentChanged -= ParentChanged; NativeNavigationObserver.ReleaseModal(Page); }
            if (pageNavigation != null && ReferenceEquals(pageNavigation.Inner, interception)) pageNavigation.Inner = previousNavigation;
            if (origin != null) { origin.IsPopupOpen = state.Active.Any(item => item != this && item.origin == origin); origin.IsComingFromPopup = origin.IsPopupOpen; }
            sessions.Remove(view);
            state.Remove(this);
            abandoned.Dispose();
            Callbacks.Clear(); ReturnModels.Clear();
        }

        private sealed class ShowNavigation(Session session) : NavigationProxy
        {
            protected override async Task OnPushModal(Page modal, bool animated)
            {
                session.Attach(modal);
                await base.OnPushModal(modal, animated);
                session.BindNavigation();
            }
        }

        private sealed class CloseNavigation(Session session) : NavigationProxy
        {
            protected override async Task<Page> OnPopModal(bool animated)
            {
                using var guardScope = NavigationCallbackScope.Enter(session);
                foreach (var model in session.Models.Where(model => model.NavigationBinding != null && !model.IsDismissed))
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.closeToken, model.Lifetime.Token);
                    bool allowed;
                    try { allowed = await model.CanNavigate().WaitAsync(cancellation.Token); }
                    catch (OperationCanceledException) when (model.IsDismissed)
                    {
                        // Token continuations can run inline inside lifetime cancellation.
                        // Leave that callback before continuing native close and terminal cleanup.
                        await Task.Yield();
                        continue;
                    }
                    if (model.IsDismissed) continue;
                    session.closeToken.ThrowIfCancellationRequested();
                    if (!allowed) throw new PopupGuardRejectedException();
                }
                session.Popping = true;
                session.ReturnModels = ViewModelTree.Collect(session.Root, true).Except(session.Models).ToHashSet();
                try
                {
                    var removed = await base.OnPopModal(animated);
                    if (session.Page != null && Inner.ModalStack.Contains(session.Page)) throw new PopupCloseRejectedException();
                    session.dismissedByOutsideTap = session.outsideTapRequested;
                    // The Toolkit publishes its result only after this native task returns.
                    // Cleanup errors are observed by library awaiters, while allowing Toolkit
                    // to publish Closed/result and release its own navigation semaphore.
                    try { await session.CleanupAsync(session.nativeBackRequested ? DismissalReason.Back : DismissalReason.DialogClosed); }
                    catch (Exception error) { NavigationDiagnostics.Report(error, "Popup cleanup"); }
                    foreach (var item in session.Callbacks.ToArray())
                        if (!item.Model.IsDismissed)
                            try { await item.Callback(); }
                            catch (Exception error) { NavigationDiagnostics.Report(error, "Popup return lifecycle"); }
                    return removed;
                }
                finally { session.Popping = false; session.Callbacks.Clear(); session.ReturnModels.Clear(); }
            }
        }
    }
}
