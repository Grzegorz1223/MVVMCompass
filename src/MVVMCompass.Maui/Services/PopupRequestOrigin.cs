using CommunityToolkit.Maui.Views;
using MVVMCompass.Core;

namespace MVVMCompass.Services;

internal sealed class PopupRequestOrigin(NavigationContext context, NavigationScreenEntry? screen, Popup? popup, ViewModelBase model)
{
    internal ViewModelBase Model { get; } = model;
    internal int Version { get; } = screen?.Entry.State == NavigationEntryState.Prepared
        ? screen.Entry.ActivationVersion + 1 : screen?.Entry.ActivationVersion ?? 0;

    internal PopupRequestStatus? Ended => context.Host.IsClosed ? PopupRequestStatus.WindowClosed : Model.Lifetime.Reason switch
    {
        DismissalReason.None => null,
        DismissalReason.PreparationFailed => PopupRequestStatus.PreparationFailed,
        DismissalReason.RootReplaced => PopupRequestStatus.RootReplaced,
        DismissalReason.WindowClosed => PopupRequestStatus.WindowClosed,
        _ => PopupRequestStatus.InvalidOrigin
    };

    // A selected screen remains eligible behind a popup. An actual deactivation invalidates its
    // generation even if rapid navigation activates that same retained instance again.
    internal bool IsCurrent
    {
        get
        {
            if (Ended != null || context.IsClosed) return false;
            if (popup != null) return PopupOwnership.IsPresented(context.Window, popup);
            var root = context.RootContext;
            var visible = context.Window.Navigation.ModalStack.LastOrDefault(page => !PopupOwnership.IsPopupPage(page)) ?? context.Window.Page;
            return screen?.Entry.State == NavigationEntryState.Active && screen.Entry.ActivationVersion == Version
                && root.ScopeEntry.State == NavigationEntryState.Active && visible != null && root.OwnsPage(visible)
                && root.ActiveChain().Any(item => ReferenceEquals(item.Current, screen));
        }
    }

    internal bool CanPresent => IsCurrent && (popup != null ? PopupOwnership.IsTop(context.Window, popup)
        : !PopupOwnership.HasSessions(context.Window));
}
