namespace MVVMCompass;

/// <summary>Observes modal UI presented by this view or its descendants through MVVMCompass.</summary>
public interface IModalPresentationObserver
{
    /// <summary>Called before presentation, closest observer first. Throwing prevents presentation and still balances entered observers.</summary>
    void OnModalPresenting(ModalPresentation presentation);
    /// <summary>Called once for each entered presentation when it ends or fails. Cancelling a popup result wait does not end its presentation.</summary>
    void OnModalDismissed(ModalPresentation presentation);
}

/// <summary>The kind of modal UI being observed.</summary>
public enum ModalPresentationKind
{
    /// <summary>A native informational alert or confirmation.</summary>
    Alert,
    /// <summary>A Toolkit popup.</summary>
    Popup
}

/// <summary>The stable identity and origin of one modal presentation.</summary>
public sealed class ModalPresentation
{
    internal ModalPresentation(ModalPresentationKind kind, VisualElement origin, Window window)
    { Kind = kind; Origin = origin; Window = window; }
    /// <summary>Gets the modal kind.</summary>
    public ModalPresentationKind Kind { get; }
    /// <summary>Gets the presenting view.</summary>
    public VisualElement Origin { get; }
    /// <summary>Gets the presenting view's own window.</summary>
    public Window Window { get; }
}
