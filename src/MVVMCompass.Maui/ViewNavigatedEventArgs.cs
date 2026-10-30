namespace MVVMCompass;

/// <summary>The logical transition associated with an attached view notification.</summary>
public enum ViewNavigationKind
{
    /// <summary>The window's root was installed.</summary>
    RootInstalled,
    /// <summary>A fresh detail or modal screen was presented.</summary>
    Pushed,
    /// <summary>An existing screen became visible again.</summary>
    Returned,
    /// <summary>A retained destination was selected, including its initial selection.</summary>
    TabSelected,
    /// <summary>Another retained destination was selected.</summary>
    TabDeselected,
    /// <summary>A detail or navigation modal covered this screen.</summary>
    Covered
}

/// <summary>An attached view's navigation notification. Toolkit popups and alerts use IModalPresentationObserver instead.</summary>
public sealed class ViewNavigatedEventArgs : EventArgs
{
    internal ViewNavigatedEventArgs(ViewNavigationKind kind, Window window) { Kind = kind; Window = window; }
    /// <summary>Gets the transition that activated or deactivated the view.</summary>
    public ViewNavigationKind Kind { get; }
    /// <summary>Gets the owning window with a native handler.</summary>
    public Window Window { get; }
}
