using CommunityToolkit.Maui.Core;
using MVVMCompass.Core;

namespace MVVMCompass;

/// <summary>A popup result delivered after its owned cleanup has completed.</summary>
public sealed record PopupNavigationResult<TResult>(TResult? Result, bool WasDismissedByTappingOutsideOfPopup,
    DismissalReason Reason) : IPopupResult<TResult>
{
    /// <summary>Gets whether the popup explicitly returned a value, including null or a value type's default.</summary>
    public bool HasResult { get; init; }
}

/// <summary>The native window retained a popup requested for closure.</summary>
public sealed class PopupCloseRejectedException : InvalidOperationException
{
    internal PopupCloseRejectedException() : base("Native navigation retained the popup. Its ownership is still active; close can be retried.") { }
}
