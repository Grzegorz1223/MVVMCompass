using MVVMCompass.Core;

namespace MVVMCompass;

public abstract partial class ViewBase
{
    private ViewNavigatedEventArgs? pendingNavigation;
    private Window? navigationWindow;
    private bool navigationReady, navigationDelivered;
    internal bool HasBeenNavigationActive { get; private set; }

    /// <summary>Raised on the UI thread after model activation and native attachment. Attachment can occur after CurrentTabChanged.</summary>
    public event EventHandler<ViewNavigatedEventArgs>? NavigatedTo;
    /// <summary>Raised when an attached, previously activated view is covered or deselected without ending its lifetime.</summary>
    public event EventHandler<ViewNavigatedEventArgs>? NavigatedFrom;
    /// <summary>Handles activation after this view and its window have native handlers. Exceptions are reported through NavigationDiagnostics.</summary>
    protected virtual void OnNavigatedTo(ViewNavigatedEventArgs args) { }
    /// <summary>Handles non-terminal deactivation. Compatibility tabs receive this only for actual tab switches.</summary>
    protected virtual void OnNavigatedFrom(ViewNavigatedEventArgs args) { }

    private void AttachNavigationEvents()
    {
        Loaded += NavigationAttachmentChanged;
        HandlerChanged += NavigationAttachmentChanged;
        ParentChanged += NavigationAttachmentChanged;
        ViewModel.RegisterSubscriptionCleanup(() =>
        {
            Loaded -= NavigationAttachmentChanged;
            HandlerChanged -= NavigationAttachmentChanged;
            ParentChanged -= NavigationAttachmentChanged;
            if (navigationWindow != null) navigationWindow.HandlerChanged -= NavigationAttachmentChanged;
            navigationWindow = null; pendingNavigation = null;
            NavigatedTo = null; NavigatedFrom = null;
        });
    }

    internal void QueueNavigation(ViewNavigationKind kind, Window window)
    {
        if (ViewModel.IsDismissed) return;
        HasBeenNavigationActive = true;
        navigationReady = false;
        if (navigationWindow != window)
        {
            if (navigationWindow != null) navigationWindow.HandlerChanged -= NavigationAttachmentChanged;
            navigationWindow = window;
            window.HandlerChanged += NavigationAttachmentChanged;
        }
        pendingNavigation = new(kind, window);
    }

    internal void FlushNavigation()
    {
        navigationReady = true;
        DeliverNavigation();
    }

    internal void CancelPendingNavigation() => pendingNavigation = null;
    internal void RetryNavigationAttachment() => DeliverNavigation();

    internal void LeaveNavigation(ViewNavigationKind kind, Window window)
    {
        pendingNavigation = null;
        if (!navigationDelivered) return;
        navigationDelivered = false;
        if (ViewModel.IsDismissed || !IsAttachedTo(window)) return;
        var args = new ViewNavigatedEventArgs(kind, window);
        Notify(() => OnNavigatedFrom(args), "View navigated from");
        if (!ViewModel.IsDismissed) Notify(() => NavigatedFrom?.Invoke(this, args), "View NavigatedFrom event");
    }

    private void NavigationAttachmentChanged(object? sender, EventArgs args) => DeliverNavigation();

    internal bool IsAttachedTo(Window window) => ReferenceEquals(Window, window)
        && Handler?.PlatformView != null && window.Handler?.PlatformView != null;

    private void DeliverNavigation()
    {
        if (!navigationReady || pendingNavigation is not { } args || ViewModel.IsDismissed
            || !IsAttachedTo(args.Window) || Navigator == null
            || !Navigator.Context.RootContext.ContainsActiveOrigin(Navigator.Screen.Entry)) return;
        pendingNavigation = null;
        navigationDelivered = true;
        Notify(() => OnNavigatedTo(args), "View navigated to");
        if (!ViewModel.IsDismissed) Notify(() => NavigatedTo?.Invoke(this, args), "View NavigatedTo event");
    }

    private void Notify(Action callback, string operation)
    {
        using var scope = Navigator == null ? null : NavigationCallbackScope.Enter(Navigator.Context.Host);
        try { callback(); }
        catch (Exception error) { NavigationDiagnostics.Report(error, operation); }
    }
}
