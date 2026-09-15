using MVVMCompass.Core;

namespace MVVMCompass;

/// <summary>A root owned by an explicit MAUI window host.</summary>
internal abstract class MauiNavigationRoot
{
    internal MauiNavigationRoot(Page page, Page content, NavigationEntry entry)
    {
        Page = page;
        Content = content;
        Entry = entry;
    }

    /// <summary>Gets the installed page, including an optional NavigationPage wrapper.</summary>
    public Page Page { get; }

    /// <summary>Gets the portable identity and lifetime of the root view model.</summary>
    public NavigationEntry Entry { get; }

    internal Page Content { get; }
}

/// <summary>A window root with a strongly typed view model entry.</summary>
internal sealed class MauiNavigationRoot<TViewModel> : MauiNavigationRoot where TViewModel : class
{
    internal MauiNavigationRoot(Page page, Page content, NavigationEntry<TViewModel> entry) : base(page, content, entry) { }

    /// <summary>Gets the entry with its original view model type.</summary>
    public new NavigationEntry<TViewModel> Entry => (NavigationEntry<TViewModel>)base.Entry;
}
