namespace MVVMCompass.Interfaces;

/// <summary>Optional destination-scoped capability for requesting popups after navigation settles.</summary>
public interface IDeferredPopupNavigationService
{
    /// <summary>Accepts a popup request without waiting for presentation or user interaction.</summary>
    /// <remarks>
    /// May be called during initialization, activation or guards. Return from the callback before
    /// awaiting the handle's completion. A nonempty key shares an outstanding request for the same
    /// origin activation, popup type and result type; its first parameters and cancellation token win.
    /// Cancellation before presentation prevents opening; afterwards it stops the result wait and
    /// leaves the visible popup owned until actual dismissal.
    /// </remarks>
    PopupRequest<TResult> RequestPopup<TViewModel, TResult>(Dictionary<string, object>? parameters = null,
        string? requestKey = null, CancellationToken cancellationToken = default) where TViewModel : ViewModelBase;
}
