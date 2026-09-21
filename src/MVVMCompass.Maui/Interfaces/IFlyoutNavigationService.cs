namespace MVVMCompass.Interfaces;

/// <summary>Optional scoped capability for hiding the enclosing flyout without leaving its destination.</summary>
public interface IFlyoutNavigationService
{
    /// <summary>Closes an open overlay drawer. Closed or pinned drawers complete without commitment.</summary>
    Task<NavigationResult> CloseFlyout(CancellationToken cancellationToken = default);
}
