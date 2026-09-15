namespace MVVMCompass;

/// <summary>The coordinated container operation that encountered a committed failure.</summary>
public enum MauiNavigationOperation
{
    /// <summary>Pushes a page onto the visible navigation stack.</summary>
    Push,
    /// <summary>Removes the visible stack page or closes its modal at the modal root.</summary>
    Back,
    /// <summary>Removes pages above the visible stack root.</summary>
    PopToRoot,
    /// <summary>Opens a modal presentation, optionally with its own stack.</summary>
    OpenModal,
    /// <summary>Closes the top modal and its owned stack.</summary>
    CloseModal,
    /// <summary>Selects a retained standard or custom tab.</summary>
    SelectTab,
    /// <summary>Selects a retained flyout page.</summary>
    SelectFlyout,
    /// <summary>Rebuilds or extends retained container content.</summary>
    ComposeRetained
}

/// <summary>A container operation failed after commitment; inspect the host's current presentation before recovery.</summary>
public sealed class MauiNavigationException : Exception
{
    internal MauiNavigationException(MauiNavigationOperation operation, bool presentationChanged, Exception inner)
        : base($"{operation} failed after commitment. Presentation changed: {presentationChanged}.", inner)
    { Operation = operation; HasPresentationChanged = presentationChanged; }
    /// <summary>Gets the operation that failed.</summary>
    public MauiNavigationOperation Operation { get; }
    /// <summary>Gets whether the observed native presentation differs from the captured starting state.</summary>
    public bool HasPresentationChanged { get; }
}
