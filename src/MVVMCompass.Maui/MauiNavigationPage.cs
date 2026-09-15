using MVVMCompass.Core;

namespace MVVMCompass;

/// <summary>A root, stack page or modal owned by one explicit window host.</summary>
internal abstract class MauiNavigationPage
{
    internal MauiNavigationPage(Page page, Page content, NavigationEntry entry, Page root, bool isModal)
    { Page = page; Content = content; Entry = entry; Root = root; IsModal = isModal; }

    /// <summary>Gets the presented page, including an optional modal navigation wrapper.</summary>
    public Page Page { get; }
    /// <summary>Gets the model's identity, retained activation state and permanent lifetime.</summary>
    public NavigationEntry Entry { get; }
    /// <summary>Gets whether this entry owns a modal presentation.</summary>
    public bool IsModal { get; }
    internal Page Content { get; }
    internal Page Root { get; }
    internal bool WasPresented { get; set; }
}

/// <summary>An owned MAUI page with a strongly typed view model.</summary>
internal sealed class MauiNavigationPage<TViewModel> : MauiNavigationPage where TViewModel : class
{
    internal MauiNavigationPage(Page page, Page content, NavigationEntry<TViewModel> entry, Page root, bool isModal)
        : base(page, content, entry, root, isModal) { }
    /// <summary>Gets the entry with its original model type.</summary>
    public new NavigationEntry<TViewModel> Entry => (NavigationEntry<TViewModel>)base.Entry;
}
