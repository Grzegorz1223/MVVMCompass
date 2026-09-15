namespace MVVMCompass.Core;

/// <summary>The reason an owned navigation entry permanently ends.</summary>
public enum DismissalReason
{
    /// <summary>The entry has not been dismissed.</summary>
    None,
    /// <summary>The entry was removed from its navigation host.</summary>
    Removed,
    /// <summary>The entry was removed by back navigation.</summary>
    Back,
    /// <summary>The entry's root was replaced.</summary>
    RootReplaced,
    /// <summary>The entry's owning window closed.</summary>
    WindowClosed,
    /// <summary>The dialog completed or was dismissed.</summary>
    DialogClosed,
    /// <summary>A candidate was abandoned before successful activation and presentation.</summary>
    PreparationFailed
}
