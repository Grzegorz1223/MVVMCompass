using MVVMCompass.Core;

namespace MVVMCompass;

/// <summary>A retained tab or flyout item. Identity survives selection and stack navigation.</summary>
internal sealed class MauiNavigationItem
{
    internal MauiNavigationItem(VisualElement container, VisualElement view, VisualElement anchor,
        object identity, NavigationEntry? entry, MauiNavigationPage owner)
    { Container = container; View = view; Anchor = anchor; Identity = identity; Entry = entry; Owner = owner; }

    /// <summary>Gets the containing tab or flyout.</summary>
    public VisualElement Container { get; internal set; }
    /// <summary>Gets the retained visual, including an extracted custom-tab page or view.</summary>
    public VisualElement View { get; }
    /// <summary>Gets the model owner, or null for a native item without its own model.</summary>
    public NavigationEntry? Entry { get; }
    internal VisualElement Anchor { get; }
    internal object Identity { get; }
    internal MauiNavigationPage Owner { get; set; }
}
