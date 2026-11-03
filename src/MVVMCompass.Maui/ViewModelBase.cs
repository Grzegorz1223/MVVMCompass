using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MVVMCompass.Interfaces;
using MVVMCompass.Core;
using System.Windows.Input;

namespace MVVMCompass
{
    /// <summary>Observable model with navigation guards, container composition, lifecycle callbacks and an exactly-once permanent lifetime.</summary>
    public abstract partial class ViewModelBase : ObservableObject, IViewModelLifecycle, INavigationLifetimeFinalizer
    {
        // ── Pending-parent ambient context ──────────────────────────
        // Allows child tab VMs to access their ParentViewModel during
        // construction, before the registered child view is created.
        private static readonly AsyncLocal<PendingParentScope?> _pendingParentViewModel = new();

        /// <summary>Constructs a model with the supplied parent available during its constructor, restoring the previous parent even on failure.</summary>
        /// <remarks>For composition roots and isolated tests. The factory must construct a fresh model synchronously.
        /// This does not create navigation entries, DI scopes, activation or lifetime ownership. Registered navigation supplies its own parent.</remarks>
        public static TViewModel ConstructWithParent<TViewModel>(ViewModelBase parent, Func<TViewModel> create)
            where TViewModel : ViewModelBase
        {
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(create);
            ObjectDisposedException.ThrowIf(parent.IsDismissed, parent);
            if (Services.NavigationEntryScope.IsConstructing)
                throw new InvalidOperationException("Registered navigation supplies the constructor parent; do not override it.");
            using var scope = SetPendingParent(parent);
            return create() ?? throw new InvalidOperationException("The model factory returned null.");
        }

        /// <summary>
        /// Sets the ambient parent that will be auto-assigned to any
        /// <see cref="ViewModelBase"/> constructed while the scope is active.
        /// Dispose the returned handle to clear the ambient value.
        /// </summary>
        internal static IDisposable SetPendingParent(ViewModelBase parent)
        {
            var scope = new PendingParentScope(parent, _pendingParentViewModel.Value);
            _pendingParentViewModel.Value = scope;
            return scope;
        }

        private sealed class PendingParentScope(ViewModelBase parent, PendingParentScope? previous) : IDisposable
        {
            internal ViewModelBase? Parent = parent;
            public void Dispose()
            {
                // Captured execution contexts share this frame. Clear the reference so work
                // that outlives construction cannot inherit or retain a stale parent.
                if (Interlocked.Exchange(ref Parent, null) == null) return;
                _pendingParentViewModel.Value = previous;
            }
        }
        // ────────────────────────────────────────────────────────────

        /// <summary>Creates a permanent lifetime and captures any parent supplied by navigation during child construction.</summary>
        protected ViewModelBase()
        {
            Lifetime = new NavigationLifetime(static model => ((ViewModelBase)model!).DismissCoreAsync(), this);
            Services.NavigationEntryScope.Constructed(this);
            if (_pendingParentViewModel.Value is { } scope && Volatile.Read(ref scope.Parent) is { } parent)
            {
                ParentViewModel = parent;
            }
        }

        internal NavigationLifetime Lifetime { get; }

        /// <summary>Gets navigation bound to this custom screen's origin, or null outside a custom content host.</summary>
        internal ScreenNavigator? Navigator { get; set; }

        /// <summary>Gets explicit child and resource ownership for permanent cleanup.</summary>
        public NavigationOwnershipNode Ownership => Lifetime.Ownership;


        private List<Action>? subscriptionCleanup;
        private int viewDetached;
        private TaskCompletionSource? viewDetachmentSource;
        private Task? dismissalCompletion;

        internal Action RegisterSubscriptionCleanup(Action unsubscribe)
        {
            var released = 0;
            void Release()
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                subscriptionCleanup?.Remove(Release);
                unsubscribe();
            }
            if (IsDismissed) Release();
            else (subscriptionCleanup ??= []).Add(Release);
            return Release;
        }

        /// <summary>Cancellation signaled when this instance is permanently dismissed.</summary>
        protected CancellationToken LifetimeToken => Lifetime.Token;

        /// <summary>Whether this instance has been marked for permanent dismissal.</summary>
        public bool IsDismissed => Lifetime.IsDismissed;

        /// <summary>Gets whether this model is permanently removed, including replacement of its host.</summary>
        public bool IsPermanentlyDismissed => IsDismissed || IsHostReplaced;

        /// <summary>Ends this instance's lifetime; all callers await the same cleanup.</summary>
        public Task DismissAsync()
        {
            var pending = Lifetime.DismissAsync();
            // Nested callbacks join their enclosing batch. Waiting for that batch here
            // would await the very callback that is currently executing.
            if (NavigationCallbackScope.IsCleaning || NavigationCallbackScope.IsWithinLifetime(Lifetime)) return pending;
            if (Volatile.Read(ref dismissalCompletion) is { } existing) return existing;
            if (pending.IsCompleted && Volatile.Read(ref viewDetached) != 0) return pending;
            var completed = AwaitDetachmentAsync(pending);
            return Interlocked.CompareExchange(ref dismissalCompletion, completed, null) ?? completed;
        }

        private async Task AwaitDetachmentAsync(Task pending)
        {
            try { await pending; }
            finally { await ((INavigationLifetimeFinalizer)this).ViewDetachment; }
        }

        Task INavigationLifetimeFinalizer.ViewDetachment
        {
            get
            {
                if (Volatile.Read(ref viewDetached) != 0) return Task.CompletedTask;
                var signal = Volatile.Read(ref viewDetachmentSource);
                if (signal == null)
                {
                    var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    signal = Interlocked.CompareExchange(ref viewDetachmentSource, created, null) ?? created;
                }
                if (Volatile.Read(ref viewDetached) != 0) signal.TrySetResult();
                return signal.Task;
            }
        }

        private Task DismissCoreAsync() => AfterDismissed();

        void INavigationLifetimeFinalizer.DetachView()
        {
            try { DetachViewEventHandlers(); }
            finally
            {
                ClearViewEventHandlers();
                Volatile.Write(ref viewDetached, 1);
                Volatile.Read(ref viewDetachmentSource)?.TrySetResult();
            }
        }

        /// <summary>
        /// Releases view connections after every terminal callback in the outgoing batch has finished,
        /// including failed callbacks. Retained deactivation does not invoke this hook.
        /// </summary>
        /// <remarks>Override to release application subscriptions, and call the base implementation.
        /// Await the navigation operation for completion of the whole batch's view detachment.</remarks>
        protected internal virtual void DetachViewEventHandlers() => ClearViewEventHandlers();

        private void ClearViewEventHandlers()
        {
            foreach (var unsubscribe in subscriptionCleanup?.ToArray() ?? [])
                try { unsubscribe(); }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Subscription cleanup"); }
            Navigator = null;
            DisplayToastEvent = null;
            SendCustomActionEvent = null;
            ShowLoadingEvent = null;
            HideLoadingEvent = null;
            NotifyLanguageChangeEvent = null;
            ParentViewModel = null;
            CustomActionDispatcher = action => action();
        }

        internal Func<Func<Task<object?>>, Task<object?>> CustomActionDispatcher { get; set; } = action => action();

        /// <summary>Raised to request a toast from the associated view.</summary>
        public event Func<ToastEventArgs, Task>? DisplayToastEvent;

        /// <summary>Raised to request an application-defined action and await its result.</summary>
        public event Func<CustomActionEventArgs, Task<object?>>? SendCustomActionEvent;

        /// <summary>Raised to acquire a loading presentation and its cleanup handle.</summary>
        public event Func<LoadingType, IDisposable?>? ShowLoadingEvent;

        /// <summary>Raised to hide the current loading presentation.</summary>
        public event Action? HideLoadingEvent;

        /// <summary>Raised to refresh the associated view's localized content.</summary>
        public event Func<bool>? NotifyLanguageChangeEvent;

        /// <summary>
        /// When this VM is a child tab, holds a reference to the parent tabbed VM.
        /// Set automatically by the navigation framework during tab creation.
        /// </summary>
        public ViewModelBase? ParentViewModel { get; internal set; }

        /// <summary>
        /// True when the page hosting this view model is being replaced wholesale — logout, forced logout,
        /// re-login, update — so this instance will never appear again.
        /// </summary>
        /// <remarks>
        /// <see cref="AfterDismissed"/> serves several different situations, and a view model cannot tell them
        /// apart from its own state. It used to infer "real dismissal" from flags the OUTGOING tab set on the
        /// SHARED parent (<c>NoDismiss</c>, and the since-removed <c>IsNavigating</c>) — flags a tab switch
        /// leaves behind and a host replacement never resets, because it does not go through
        /// <c>CanNavigate</c>. That made teardown depend on which tab the user happened to be on. This is set by
        /// the navigation layer, which is the only thing that actually knows.
        ///
        /// Named for what it actually means, deliberately narrower than "this is the last dismissal": it is set
        /// ONLY when the host page is swapped out. Popping a page with <c>NavigateBack</c> /
        /// <c>NavigateBackToRoot</c> also dismisses a view model for good and does NOT set this, so a page view
        /// model must not use it as a general "am I done" signal — that cleanup would silently never run.
        /// </remarks>
        private bool hostReplaced;
        /// <summary>Gets whether permanent cleanup was caused by replacement of the owning root. Other permanent removals leave this false.</summary>
        public bool IsHostReplaced { get => hostReplaced || Lifetime.Reason == DismissalReason.RootReplaced; private set => hostReplaced = value; }

        /// <summary>Records that this view model's host page is being replaced. One-way.</summary>
        internal void MarkHostReplaced() => IsHostReplaced = true;

        /// <summary>Stores the busy state used by commands and activity indicators.</summary>
        protected bool _isBusy = false;
        /// <summary>Gets or sets the observable busy state used by commands and activity indicators.</summary>
        public virtual bool IsBusy
        {
            get { return _isBusy; }
            set { SetProperty(ref _isBusy, value); }
        }

        private bool _isModal;
        /// <summary>Gets or sets whether this destination uses modal presentation.</summary>
        public bool IsModal
        {
            get { return _isModal; }
            set { SetProperty(ref _isModal, value); }
        }

        /// <summary>Indicates the preserved popup-return state for presentation callbacks.</summary>
        public bool IsComingFromPopup = false;

        /// <summary>
        /// True while a CommunityToolkit popup is displayed over this page. Derived by
        /// the popup ownership tracker, so views no
        /// longer have to hand-signal "a popup is up" at every ShowPopup call site.
        /// </summary>
        public bool IsPopupOpen { get; set; }

        /// <summary>Creates a command that marks this model busy during execution and restores its busy state even when the action fails.</summary>
        protected virtual ICommand CreateCommand(Action action)
        {
            return new Command(() =>
            {
                IsBusy = true;
                try { action(); }
                finally { IsBusy = false; }
            }
            , () => !IsBusy);
        }

        /// <summary>Creates a command that marks this model busy during execution and restores its busy state even when the action fails.</summary>
        protected virtual ICommand CreateCommand(Func<Task> action, bool isBusy = false)
        {
            return new AsyncRelayCommand(async () =>
            {
                IsBusy = true;
                try { await action(); }
                finally { IsBusy = isBusy; }
            });
        }

        /// <summary>Handles or dispatches a toast request and awaits its presentation callback.</summary>
        protected virtual async Task DisplayToast(ToastEventArgs args)
        {
            if (DisplayToastEvent != null)
            {
                foreach (Func<ToastEventArgs, Task> handler in DisplayToastEvent.GetInvocationList())
                    await handler(args);
            }
        }

        /// <summary>Handles or dispatches an application-defined action and returns its result, which may be null.</summary>
        protected virtual async Task<object?> SendCustomAction(CustomActionEventArgs args)
        {
            if (SendCustomActionEvent != null)
            {
                var handlers = SendCustomActionEvent.GetInvocationList();
                return await CustomActionDispatcher(async () =>
                {
                    object? result = null;
                    foreach (Func<CustomActionEventArgs, Task<object?>> handler in handlers)
                        result = await handler(args);
                    return result;
                });
            }
            else
            {
                return null;
            }
        }

        /// <summary>Shows the requested loading presentation and returns its cleanup handle; an unhandled model request returns null.</summary>
        protected virtual IDisposable? ShowLoading(LoadingType loadingType)
        {
            if (ShowLoadingEvent != null)
            {
                return ShowLoadingEvent.Invoke(loadingType);
            }
            else
            {
                return null;
            }
        }

        /// <summary>Hides the active loading presentation.</summary>
        protected virtual void HideLoading()
        {
            if (HideLoadingEvent != null)
            {
                HideLoadingEvent.Invoke();
            }
        }

        /// <summary>Requests a localization refresh and returns whether the view handled it.</summary>
        protected virtual bool NotifyLanguageChange()
        {
            if (NotifyLanguageChangeEvent != null)
            {
                return NotifyLanguageChangeEvent.Invoke();
            }

            return false;
        }

        /// <inheritdoc />
        public virtual Task GetParameters(Dictionary<string, object> parameters)
        {
            return Task.CompletedTask;
        }

        /// <summary>Initializes a fresh registered destination after parameters, before its first presentation.</summary>
        /// <remarks>Registered popups use this callback, not page Appearing. Register subscription release
        /// with Ownership.RegisterCleanup so failed preparation also releases it. Return before awaiting
        /// the completion of a deferred popup request.</remarks>
        public virtual Task BeforeFirstShown()
        {
            return Task.CompletedTask;
        }

        /// <summary>Runs terminal cleanup once, including failed registered-popup preparation.</summary>
        /// <remarks>The default calls Deactivated. Overrides should preserve the base call when using that cleanup path.</remarks>
        public virtual Task AfterDismissed() => Deactivated();

        /// <summary>Releases active-view resources while retaining this instance for reuse.</summary>
        public virtual Task Deactivated() => Task.CompletedTask;

        /// <inheritdoc />
        public virtual async Task Appearing()
        {
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public virtual Task Disappearing()
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public virtual Task NavigatedTo()
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public virtual Task Loaded()
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public virtual Task Unloaded()
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public virtual Task<bool> CanNavigate()
        {
            return Task.FromResult(true);
        }

        /// <summary>
        /// Called on the <b>parent</b> tabbed ViewModel after the active child tab changes.
        /// Override to react to tab switches (e.g. update shared UI state).
        /// </summary>
        /// <param name="activeTabViewModel">The ViewModel of the newly active tab.</param>
        public virtual Task OnActiveTabChanged(ViewModelBase activeTabViewModel)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Toast or notification identity, category and optional message.</summary>
    public class ToastEventArgs
    {
        /// <summary>Gets the application-defined identifier for this request.</summary>
        public string Id { get; }

        /// <summary>Gets the requested toast duration or notification category.</summary>
        public ToastType ToastType { get; }

        /// <summary>Gets the optional text supplied for this presentation request.</summary>
        public string? Message { get; }

        /// <summary>Toast or notification identity, category and optional message.</summary>
        public ToastEventArgs(string id, ToastType toastType = ToastType.Default, string? message = null)
        {
            Id = id;
            ToastType = toastType;
            Message = message;
        }
    }

    /// <summary>Application-defined action identity with an optional message or object parameter.</summary>
    public class CustomActionEventArgs
    {
        /// <summary>Gets the application-defined identifier for this request.</summary>
        public string Id { get; }

        /// <summary>Gets the optional text supplied for this presentation request.</summary>
        public string? Message { get; }

        /// <summary>Gets the optional application-defined parameter value.</summary>
        public CustomActionParameter? Parameter { get; }

        /// <summary>Application-defined action identity with an optional message or object parameter.</summary>
        public CustomActionEventArgs(string id)
        {
            Id = id;
        }

        /// <summary>Application-defined action identity with an optional message or object parameter.</summary>
        public CustomActionEventArgs(string id, string message)
        {
            Id = id;
            Message = message;
        }

        /// <summary>Application-defined action identity with an optional message or object parameter.</summary>
        public CustomActionEventArgs(string id, CustomActionParameter parameter)
        {
            Id = id;
            Parameter = parameter;
        }
    }

    /// <summary>Wraps an application-defined value, including null, for a custom action.</summary>
    public class CustomActionParameter
    {
        /// <summary>Gets the optional application-defined parameter value.</summary>
        public object? Parameter { get; }

        /// <summary>Wraps an application-defined value, including null, for a custom action.</summary>
        public CustomActionParameter(object? parameter = null)
        {
            Parameter = parameter;
        }
    }

    /// <summary>Preserved notification categories and short or long toast durations; numeric values are compatibility contracts.</summary>
    public enum ToastType
    {
        /// <summary>Uses the default notification category (numeric value 0).</summary>
        Default,
        /// <summary>Uses the reminder notification category (numeric value 1).</summary>
        Reminder,
        /// <summary>Uses the alarm notification category (numeric value 2).</summary>
        Alarm,
        /// <summary>Uses the incoming-call notification category (numeric value 3).</summary>
        IncomingCall,
        /// <summary>Uses the urgent notification category (numeric value 4).</summary>
        Urgent,
        /// <summary>Uses a short toast duration (numeric value 5).</summary>
        Short,
        /// <summary>Uses a long toast duration (numeric value 6).</summary>
        Long
    }

    /// <summary>Preserved loading presentation categories, including the original login spelling.</summary>
    public enum LoadingType
    {
        /// <summary>Shows the standard loading presentation.</summary>
        Loading,
        /// <summary>Shows the login presentation; the original enum spelling is preserved.</summary>
        LogingIn
    }
}
