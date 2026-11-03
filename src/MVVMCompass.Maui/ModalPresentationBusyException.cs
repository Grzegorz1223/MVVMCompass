namespace MVVMCompass;

/// <summary>A window-level dialog was rejected because a popup or window-level alert is already being presented.</summary>
/// <remarks>The application can choose a toast or retry later. Registered popups can still present their own nested popups.</remarks>
public sealed class ModalPresentationBusyException : InvalidOperationException
{
    /// <summary>Creates a distinguishable overlapping-presentation failure.</summary>
    public ModalPresentationBusyException() : base("This window already has a popup or window-level alert open.") { }
}
