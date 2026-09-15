using MVVMCompass.Interfaces;
using MVVMCompass.Core;

namespace MVVMCompass.Services;

/// <summary>Collects owned view models in child-before-parent order, including retained content.</summary>
internal static class ViewModelTree
{
    internal static List<ViewModelBase> Collect(Element? root, bool includeModals = false)
    {
        if (!includeModals && root is ContentPage { Content: null } && root is not IFlyoutMenuItems && root is not ICustomTabbedViewBase)
        {
            var owned = (root as IHasVM)?.ViewModel;
            var bound = root.BindingContext as ViewModelBase;
            if (owned == null) return bound == null ? [] : [bound];
            return bound == null || ReferenceEquals(owned, bound) ? [owned] : [owned, bound];
        }
        var result = new List<ViewModelBase>();
        var elements = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        HashSet<ViewModelBase>? models = null;
        if (includeModals && root is Page page)
        {
            foreach (var model in PopupOwnership.ModelsFor(page)) Add(model);
            foreach (var modal in page.Navigation.ModalStack.Reverse().ToArray()) Visit(modal);
        }
        Visit(root);
        return result;

        void Add(ViewModelBase? model)
        {
            if (model == null) return;
            if (result.Count < 8)
            {
                foreach (var existing in result) if (ReferenceEquals(existing, model)) return;
                result.Add(model);
            }
            else if ((models ??= new(result, ReferenceEqualityComparer.Instance)).Add(model)) result.Add(model);
        }
        void Visit(Element? element)
        {
            if (element == null) return;
            // Leaf controls cannot form traversal cycles; model deduplication still applies.
            if (element is not (NavigationPage or TabbedPage or FlyoutPage or IFlyoutMenuItems or ICustomTabbedViewBase
                or ContentPage or ContentView or Layout or ScrollView or Border))
            {
                AddBindings(element);
                return;
            }
            if (!elements.Add(element)) return;
            if (element is NavigationPage navigation)
                foreach (var child in navigation.Navigation.NavigationStack.Reverse().ToArray()) Visit(child);
            if (element is TabbedPage tabs)
                foreach (var child in tabs.Children.ToArray()) Visit(child);
            if (element is FlyoutPage flyout)
            {
                Visit(flyout.Detail);
                Visit(flyout.Flyout);
            }
            if (element is IFlyoutMenuItems menu && menu.MenuItems != null)
                foreach (var item in menu.MenuItems.ToArray()) Visit(item.Content);
            if (element is ICustomTabbedViewBase custom)
                foreach (var child in custom.Children.ToArray())
                {
                    Visit(child.View);
                    Visit(child.Content);
                    Add(child.ViewModel);
                }
            if (element is ContentPage contentPage) Visit(contentPage.Content);
            if (element is ContentView contentView) Visit(contentView.Content);
            if (element is Layout layout)
                foreach (var child in layout.Children.OfType<Element>().ToArray()) Visit(child);
            if (element is ScrollView scroll) Visit(scroll.Content);
            if (element is Border border) Visit(border.Content);
            AddBindings(element);
        }
        void AddBindings(Element element)
        {
            if (element is IHasVM hasVm) Add(hasVm.ViewModel);
            // Inherited contexts on layout controls are references, not additional owners.
            if (ReferenceEquals(element, root) || element.IsSet(BindableObject.BindingContextProperty))
                Add(element.BindingContext as ViewModelBase);
        }
    }

    // Structural collection stays separate from explicit ownership: reconciliation must
    // still see that a retained child was removed even until its cleanup detaches its node.
    internal static void AdoptChildren(Element view, NavigationLifetime? lifetime = null)
    {
        var root = lifetime?.Ownership ?? Model(view)?.Ownership;
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        Visit(view, root);
        void Visit(Element element, NavigationOwnershipNode? parent)
        {
            if (!seen.Add(element)) return;
            var node = Model(element)?.Ownership;
            if (node != null && node != parent)
            {
                parent?.Adopt(node);
                parent = node;
            }
            // Stack pages are siblings under the containing owner. Removing a stack's
            // root must not terminate a page promoted to replace it.
            foreach (var child in Children(element)) Visit(child, parent);
        }
    }

    private static ViewModelBase? Model(Element element) => element is IHasVM hasVm ? hasVm.ViewModel
        : element.IsSet(BindableObject.BindingContextProperty) ? element.BindingContext as ViewModelBase : null;

    private static IEnumerable<Element> Children(Element element) => element switch
    {
        NavigationPage stack => stack.Navigation.NavigationStack,
        TabbedPage tabs => tabs.Children,
        FlyoutPage flyout => new[] { flyout.Detail, flyout.Flyout }.OfType<Element>()
            .Concat(flyout.Flyout is IFlyoutMenuItems menu ? menu.MenuItems?.Select(item => item.Content).OfType<Element>() ?? [] : []),
        ICustomTabbedViewBase custom => custom.Children.SelectMany(child => new Element[] { child.View, child.Content }),
        ContentPage page => new[] { page.Content }.OfType<Element>(),
        ContentView content => new[] { content.Content }.OfType<Element>(),
        Layout layout => layout.Children.OfType<Element>(),
        ScrollView scroll => new[] { scroll.Content }.OfType<Element>(),
        Border border => new[] { border.Content }.OfType<Element>(),
        _ => []
    };
}
