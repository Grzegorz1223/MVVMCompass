using MVVMCompass.Interfaces;

namespace MVVMCompass;

/// <summary>Optional capabilities of the registered destination-scoped navigation service.</summary>
public static class NavigationServiceExtensions
{
    /// <summary>Requests a popup after navigation settles, without awaiting user interaction.</summary>
    /// <remarks>Return from initialization, activation or guard callbacks before awaiting Completion.</remarks>
    /// <exception cref="NotSupportedException">The supplied adapter does not forward deferred popup requests.</exception>
    public static PopupRequest<TResult> RequestPopup<TViewModel, TResult>(this INavigationService navigation,
        Dictionary<string, object>? parameters = null, string? requestKey = null, CancellationToken cancellationToken = default)
        where TViewModel : ViewModelBase
    {
        ArgumentNullException.ThrowIfNull(navigation);
        return navigation is IDeferredPopupNavigationService deferred
            ? deferred.RequestPopup<TViewModel, TResult>(parameters, requestKey, cancellationToken)
            : throw new NotSupportedException("This navigation service does not implement IDeferredPopupNavigationService.");
    }

    /// <summary>Closes the enclosing drawer, preserving destination selection and history.</summary>
    /// <exception cref="NotSupportedException">The supplied implementation does not support flyout closure.</exception>
    public static Task<NavigationResult> CloseFlyout(this INavigationService navigation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        return navigation is IFlyoutNavigationService flyout ? flyout.CloseFlyout(cancellationToken)
            : throw new NotSupportedException("This navigation service does not implement IFlyoutNavigationService.");
    }
}
