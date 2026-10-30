using MVVMCompass.Interfaces;

namespace MVVMCompass;

/// <summary>Optional capabilities of the registered destination-scoped navigation service.</summary>
public static class NavigationServiceExtensions
{
    /// <summary>Opens a configured independent window from an active entry, returning its initialization and lifetime.</summary>
    /// <remarks>The distinct name preserves existing OpenNewWindow(null) calls. Configuration sets ordinary Window properties before opening.</remarks>
    public static Task<WindowNavigationResult> OpenWindow<TViewModel>(this INavigationService navigation, Action<Window> configure,
        Dictionary<string, object>? parameters = null, WindowBootstrapOptions? options = null,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(configure);
        return navigation is Services.NavigationService service
            ? service.OpenWindow<TViewModel>(configure, parameters, options, cancellationToken)
            : throw new NotSupportedException("This navigation adapter does not support configured windows. Use MauiNavigationHostFactory.OpenWindow with an explicit application owner.");
    }

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
