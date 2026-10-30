using MVVMCompass.Core;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private readonly List<MauiNavigationPage> ownedPages = [];
    private int containerMutation;

    internal MauiNavigationPage? CurrentPage => ownedPages.LastOrDefault(item =>
        ReferenceEquals(item.Page, Window.Navigation.ModalStack.LastOrDefault() ?? Window.Page));
    internal NavigationEntry? CurrentEntry => CurrentContentNavigation?.Deepest.Current?.Entry;
    internal Task NativeNavigationCompletion { get; private set; } = Task.CompletedTask;

    internal IReadOnlyList<NavigationOwnershipNode> OwnershipRoots
    {
        get
        {
            var nodes = AllEntries().Select(entry => entry.Ownership)
                .Concat(Window.Page == null ? [] : PopupOwnership.ModelsFor(Window.Page).Select(model => model.Ownership))
                .Where(node => !node.Lifetime.IsDismissed).Distinct().ToHashSet();
            return nodes.Where(node => !HasSelectedAncestor(node)).ToArray();
            bool HasSelectedAncestor(NavigationOwnershipNode node)
            {
                for (var parent = node.Parent; parent != null; parent = parent.Parent)
                    if (nodes.Contains(parent)) return true;
                return false;
            }
        }
    }

    private void TrackRoot<T>(MauiNavigationRoot<T> root) where T : class
    {
        var existing = ownedPages.FirstOrDefault(item => ReferenceEquals(item.Entry, root.Entry));
        if (existing == null) TrackPage(new MauiNavigationPage<T>(root.Page, root.Content, root.Entry, root.Page, false));
        else if (ReferenceEquals(Window.Page, root.Page)) existing.WasPresented = true;
    }

    private void TrackPage(MauiNavigationPage page)
    {
        page.WasPresented = IsPageInWindow(page.Page);
        ownedPages.Add(page);
        if (page.IsModal) NativeModalObserver.OwnModal(page.Page);
    }

    private static bool ContainsPage(Page? parent, Page child) => parent != null &&
        (ReferenceEquals(parent, child) || parent switch
        {
            NavigationPage stack => stack.Navigation.NavigationStack.Any(page => ContainsPage(page, child)),
            TabbedPage tabs => tabs.Children.Any(page => ContainsPage(page, child)),
            FlyoutPage flyout => ContainsPage(flyout.Detail, child) || ContainsPage(flyout.Flyout, child),
            _ => false
        });

    private bool IsPageInWindow(Page page) => ContainsPage(Window.Page, page) ||
        Window.Navigation.ModalStack.Any(modal => ContainsPage(modal, page));

    private MauiNavigationPage[] OwnedWithin(Page? page, NavigationEntry? entry = null)
    {
        var root = ownedPages.FirstOrDefault(item => ReferenceEquals(item.Entry, entry) && ReferenceEquals(item.Page, item.Root))?.Root;
        return ownedPages.AsEnumerable().Reverse().Where(item => ReferenceEquals(item.Root, page) ||
            ReferenceEquals(item.Root, root) || ContainsPage(page, item.Page) || ReferenceEquals(item.Entry, entry)).ToArray();
    }

    private void ForgetPages(IEnumerable<MauiNavigationPage> pages)
    {
        foreach (var page in pages)
        {
            ownedPages.Remove(page);
            if (page.IsModal) NativeModalObserver.ReleaseModal(page.Page);
        }
    }

    private static IEnumerable<ViewModelBase> OwnedModels(MauiNavigationPage page) =>
        (page.Entry.ViewModel is NavigationContext context ? context.AllScreens().Select(screen => screen.ViewModel) : [])
            .Concat(ViewModelTree.Collect(page.Content)).Distinct<ViewModelBase>(ReferenceEqualityComparer.Instance);

    private void MarkOwnedPages(IEnumerable<MauiNavigationPage> pages, DismissalReason reason)
    {
        foreach (var page in pages)
        {
            foreach (var model in OwnedModels(page).Where(model => CanCleanModel(model)))
            {
                if (reason == DismissalReason.RootReplaced) model.MarkHostReplaced();
                model.Lifetime.MarkDismissed(reason);
            }
            page.Entry.Lifetime.MarkDismissed(reason);
        }
    }

    private void SignalLifetimes(IEnumerable<NavigationLifetime> lifetimes)
    {
        var countKnown = lifetimes.TryGetNonEnumeratedCount(out var count);
        if (countKnown && count == 0) return;
        using var coordinatorCallback = coordinator.EnterCallback();
        using var hostCallback = NavigationCallbackScope.Enter(this);
        var signalled = countKnown && count == 1 ? null : new HashSet<NavigationLifetime>();
        foreach (var lifetime in lifetimes) lifetime.SignalCancellation(signalled);
    }

    private IEnumerable<NavigationLifetime> OwnedLifetimes(IEnumerable<MauiNavigationPage> pages) =>
        pages.SelectMany(page => OwnedModels(page).Where(model => CanCleanModel(model))
            .Select(model => model.Lifetime).Append(page.Entry.Lifetime));

    private NavigationEntry[] AllEntries() => ownedPages.SelectMany(page =>
        (page.Entry.ViewModel is NavigationContext context ? context.AllScreens().Select(screen => (NavigationEntry)screen.Entry) : [])
            .Append(page.Entry)).Distinct().ToArray();

    // A model claimed by another host must never be adopted or cleaned by this one.
    internal static bool IsClaimedModel(ViewModelBase model)
    { lock (claims) return claims.TryGetValue(model, out _); }

    private bool CanCleanModel(ViewModelBase model, NavigationEntry? candidate = null)
    {
        lock (claims) return !claims.TryGetValue(model, out var lifetime) || candidate?.Lifetime == lifetime
            || AllEntries().Any(entry => entry.Lifetime == lifetime);
    }

    private NavigationEntry[] NativeVisibleEntries()
    {
        var visible = Window.Navigation.ModalStack.LastOrDefault(page => !PopupOwnership.IsPopupPage(page)) ?? Window.Page;
        return ownedPages.Where(item => item.Page == visible).Select(item => item.Entry).ToArray();
    }

    private async Task ActivateVisibleEntriesAsync()
    {
        foreach (var entry in NativeVisibleEntries())
            if (entry.State is NavigationEntryState.Prepared or NavigationEntryState.Inactive) await entry.ActivateAsync();
        CurrentContentNavigation?.RefreshState();
    }

    private void WindowModalPushed(object? sender, ModalPushedEventArgs args)
    {
        if (PopupOwnership.IsPopupPage(args.Modal)) return;
        NativeChanged();
    }

    private async void WindowModalPopped(object? sender, ModalPoppedEventArgs args)
    {
        if (containerMutation != 0 || IsClosed || InHostCallback() || PopupOwnership.IsPopupPage(args.Modal)) return;
        try
        {
            var removed = OwnedWithin(args.Modal);
            foreach (var page in nativeDepartures.Where(page => ContainsPage(args.Modal, page)).ToArray()) EndNativeDeparture(page);
            MarkOwnedPages(removed, DismissalReason.Back);
            CancelExternalPreparation();
            SignalLifetimes(OwnedLifetimes(removed));
            await ScheduleNativeReconciliation();
        }
        catch (Exception error) { NavigationDiagnostics.Report(error, "Owned native modal removal"); }
    }
}
