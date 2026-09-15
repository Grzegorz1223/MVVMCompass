using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private readonly List<MauiNavigationItem> retainedItems = [];
    private readonly Dictionary<VisualElement, RetainedContainer> retainedContainers = [];

    /// <summary>Gets the deepest owned entry on the selected visible branch. Read on the window dispatcher.</summary>
    public NavigationEntry? CurrentEntry => CurrentContentNavigation?.Deepest.Current?.Entry ?? VisibleEntries().LastOrDefault();

    /// <summary>Gets completion of queued native tab selections, including guards and lifecycle callbacks.</summary>
    public Task NativeSelectionCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Gets retained items in declaration order, including hidden custom tabs and flyout popup items.</summary>
    public IReadOnlyList<MauiNavigationItem> GetItems(VisualElement container) =>
        retainedItems.Where(item => ReferenceEquals(item.Container, container) && item.Identity is not MenuOwner && IsRetainedMember(item)).ToArray();

    /// <summary>Selects a standard or custom tab by index, preserving duplicate model types and hidden tabs.</summary>
    public Task<NavigationOutcome<MauiNavigationItem>> SelectTabAsync(VisualElement container,
        NavigationRequest<int> request, CancellationToken cancellationToken = default) =>
        RunSelectionAsync(container, request, items => request.Parameter >= 0 && request.Parameter < items.Count
            ? items[request.Parameter] : throw new ArgumentOutOfRangeException(nameof(request)), null, cancellationToken);

    /// <summary>Selects a retained flyout page by its first matching metadata ID. Popup helpers remain available on the menu.</summary>
    public Task<NavigationOutcome<MauiNavigationItem>> SelectFlyoutItemAsync(FlyoutPage container,
        NavigationRequest<string> request, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default) =>
        RunSelectionAsync(container, request, items => items.FirstOrDefault(item => item.Identity is FlyoutMenuItem menu && menu.Id == request.Parameter)
            ?? throw new InvalidOperationException("No retained flyout item has the requested ID."), parameters, cancellationToken);

    private Task<NavigationOutcome<MauiNavigationItem>> RunSelectionAsync<T>(VisualElement container, NavigationRequest<T> request,
        Func<IReadOnlyList<MauiNavigationItem>, MauiNavigationItem> resolve, Dictionary<string, object>? parameters,
        CancellationToken cancellationToken) =>
        RunContainerAsync(request, container is FlyoutPage ? MauiNavigationOperation.SelectFlyout : MauiNavigationOperation.SelectTab,
            async (context, snapshot, callbacks) =>
            {
                if (!retainedContainers.TryGetValue(container, out var state) || !VisibleBranch().Contains(container))
                    context.Reject(NavigationStatus.InvalidOrigin);
                var target = resolve(GetItems(container));
                if (container is FlyoutPage && target.View is not Page)
                    throw new InvalidOperationException("Select a flyout page here; use the preserved menu popup helper for a View item.");
                if (container is ICustomTabbedViewBase { IsTabBarEnabled: false }) context.Reject(NavigationStatus.GuardRejected);
                if (ReferenceEquals(SelectedIdentity(container), target.Identity) && parameters == null)
                {
                    if (container is FlyoutPage same && !((IFlyoutPageController)same).ShouldShowSplitMode)
                        same.IsPresented = false;
                    return target;
                }
                var oldBranch = EntriesIn(SelectedVisual(container));
                await CheckGuardsAsync(oldBranch.Select(entry => entry.ViewModel), context);
                ValidateSnapshot(snapshot);
                context.BeginCommit();
                await DeactivateEntriesAsync(oldBranch);
                ValidateSnapshot(snapshot);
                SuppressRetainedCallbacks(callbacks);
                try
                {
                    if (parameters != null && target.Entry?.ViewModel is ViewModelBase legacy)
                        await legacy.GetParameters(parameters);
                    ValidateSnapshot(snapshot);
                    state!.Bridge.Applying = true;
                    switch (container)
                    {
                        case TabbedPage tabs: tabs.CurrentPage = (Page)target.View; break;
                        case FlyoutPage flyout:
                            foreach (var item in GetItems(container))
                                if (item.Identity is FlyoutMenuItem menu) menu.IsSelected = ReferenceEquals(item, target);
                            flyout.Detail = (Page)target.View;
                            if (!((IFlyoutPageController)flyout).ShouldShowSplitMode)
                                flyout.IsPresented = false;
                            break;
                        case ICustomTabbedViewBase custom:
                            if (!await custom.SwitchToAsync(GetItems(container).ToList().IndexOf(target)))
                                throw new InvalidOperationException("The custom tab could not be selected.");
                            break;
                        default: throw new InvalidOperationException("The destination is not a selectable container.");
                    }
                }
                finally
                {
                    state!.Bridge.Applying = false;
                    state.Selected = SelectedIdentity(container);
                }
                await ActivateVisibleEntriesAsync(notifyRetained: true, alreadyNotified: container is ICustomTabbedViewBase ? target.Entry : null);
                return target;
            }, cancellationToken);

    private static VisualElement? SelectedVisual(VisualElement container) => container switch
    {
        TabbedPage tabs => tabs.CurrentPage,
        ICustomTabbedViewBase custom => custom.CurrentTab?.View,
        FlyoutPage flyout => flyout.Detail,
        NavigationPage stack => stack.CurrentPage,
        _ => null
    };

    private static object? SelectedIdentity(VisualElement container) => container switch
    {
        ICustomTabbedViewBase custom => custom.CurrentTab,
        FlyoutPage { Flyout: IFlyoutMenuItems menu } flyout => menu.MenuItems?.FirstOrDefault(item => ReferenceEquals(item.Content, flyout.Detail)),
        _ => SelectedVisual(container)
    };

    private static IEnumerable<VisualElement> Branch(VisualElement? visual)
    {
        var seen = new HashSet<VisualElement>(ReferenceEqualityComparer.Instance);
        while (visual != null && seen.Add(visual))
        {
            yield return visual;
            visual = SelectedVisual(visual);
        }
    }

    private VisualElement[] VisibleBranch() => Branch(Window.Navigation.ModalStack.LastOrDefault() ?? Window.Page).ToArray();
    private NavigationEntry[] EntriesIn(VisualElement? visual) => EntriesOn(Branch(visual));
    private NavigationEntry[] VisibleEntries() => EntriesOn(VisibleBranch());
    private NavigationEntry[] EntriesOn(IEnumerable<VisualElement> branch) => branch
        .SelectMany(view => ownedPages.Where(item => ReferenceEquals(item.Content, view)).Select(item => item.Entry)
            .Concat(retainedItems.Where(item => ReferenceEquals(item.Anchor, view) && item.Entry != null).Select(item => item.Entry!)))
        .Distinct().ToArray();

    private static async Task DeactivateEntriesAsync(IEnumerable<NavigationEntry> entries)
    {
        foreach (var entry in entries.Reverse().Distinct())
        {
            if (entry.State != NavigationEntryState.Active) continue;
            await entry.DeactivateAsync();
            if (entry.ViewModel is ViewModelBase legacy) await legacy.DeactivateRetainedAsync();
        }
    }

    private async Task ActivateVisibleEntriesAsync(bool notifyRetained = false, NavigationEntry? alreadyNotified = null)
    {
        foreach (var entry in VisibleEntries())
        {
            if (entry.State is NavigationEntryState.Active or NavigationEntryState.Dismissed) continue;
            var initialFlyout = entry.State == NavigationEntryState.Prepared && retainedItems.Any(item => item.Entry == entry && item.Container is FlyoutPage);
            await entry.ActivateAsync();
            if ((notifyRetained || initialFlyout) && entry != alreadyNotified && retainedItems.Any(item => item.Entry == entry)
                && entry.ViewModel is ViewModelBase legacy)
                await legacy.Appearing();
        }
        CurrentContentNavigation?.RefreshState();
    }

    private void SuppressRetainedCallbacks(NavigationCallbackQueue callbacks)
    {
        foreach (var model in retainedItems.Select(item => item.Entry?.ViewModel).OfType<ViewModelBase>())
        {
            callbacks.Suppress(model, "Appearing");
            if (retainedItems.Any(item => item.Entry?.ViewModel == model && item.Container is ICustomTabbedViewBase))
                callbacks.Suppress(model, "NavigatedTo");
        }
    }

    private void DiscoverRetained(MauiNavigationPage owner)
    {
        var seen = new HashSet<VisualElement>(ReferenceEqualityComparer.Instance);
        Visit(owner.Content);
        void Visit(VisualElement visual)
        {
            if (!seen.Add(visual)) return;
            // A pushed/modal container owns its own retained descendants, even when a root
            // scan encounters that container in a nested navigation stack.
            if (ownedPages.Any(page => page != owner && ReferenceEquals(page.Content, visual))) return;
            switch (visual)
            {
                case NavigationPage stack:
                    ObserveStack(stack, owner.Root);
                    foreach (var page in stack.Navigation.NavigationStack) Visit(page);
                    break;
                case TabbedPage tabs:
                    foreach (var page in tabs.Children) Add(tabs, page, page);
                    break;
                case ICustomTabbedViewBase custom:
                    foreach (var child in custom.Children) Add(visual, child.View, child);
                    break;
                case FlyoutPage flyout:
                    if (flyout.Flyout is IFlyoutMenuItems menu && menu.MenuItems != null)
                        foreach (var item in menu.MenuItems)
                            if (item.Content != null) Add(flyout, item.Content, item);
                    else if (flyout.Detail != null) Visit(flyout.Detail);
                    if (flyout.Flyout != null) Add(flyout, flyout.Flyout,
                        retainedItems.FirstOrDefault(item => item.View == flyout.Flyout)?.Identity ?? new MenuOwner());
                    break;
            }
        }
        void Add(VisualElement container, VisualElement view, object identity)
        {
            var existingItem = retainedItems.FirstOrDefault(item => ReferenceEquals(item.Identity, identity) && item.View == view);
            if (existingItem != null) { existingItem.Container = container; existingItem.Owner = owner; }
            else
            {
                var anchor = view is NavigationPage navigation && view is not IHasVM
                    ? navigation.RootPage : view;
                var model = anchor is IHasVM hasVm ? hasVm.ViewModel : anchor.BindingContext;
                NavigationEntry? entry = ownedPages.FirstOrDefault(item => item.Content == anchor)?.Entry
                    ?? retainedItems.FirstOrDefault(item => item.Anchor == anchor)?.Entry;
                if (entry == null && model != null && !ReferenceEquals(owner.Entry.ViewModel, model))
                {
                    if (retainedItems.Any(item => ReferenceEquals(item.Entry?.ViewModel, model)))
                        throw new InvalidOperationException("Each retained child requires a distinct view model.");
                    if (model is ViewModelBase legacy)
                    {
                        legacy.RetainedViewLifecycleBehavior = options.RetainedViewLifecycleBehavior;
                        entry = Claim(legacy, () => coordinator.AttachEntry(legacy, legacy.Lifetime));
                        if (identity is not MenuOwner) RetainedNavigationBridge.OwnLifecycle(legacy, container is ICustomTabbedViewBase);
                    }
                    else entry = Claim(model, () => coordinator.CreateEntry(model));
                }
                if (entry != null && entry.Ownership.Parent == null)
                {
                    var parentEntry = ownedPages.FirstOrDefault(item => item.Content == container)?.Entry
                        ?? retainedItems.FirstOrDefault(item => item.Anchor == container)?.Entry ?? owner.Entry;
                    if (entry != parentEntry) parentEntry.Ownership.Adopt(entry.Ownership);
                }
                retainedItems.Add(new(container, view, anchor, identity, entry, owner));
            }
            Visit(view);
        }
    }

    private void BindRetainedContainers()
    {
        foreach (var container in retainedItems.Select(item => item.Container).Distinct())
        {
            if (retainedContainers.ContainsKey(container)) continue;
            var state = new RetainedContainer(container);
            state.Bridge = new()
            {
                SelectTab = async index => Accepted(await SelectTabAsync(container, new(index))),
                SelectFlyout = async (item, parameter) =>
                {
                    var result = await RunSelectionAsync(container, new NavigationRequest<object?>(null),
                        items => items.FirstOrDefault(candidate => ReferenceEquals(candidate.Identity, item))
                            ?? throw new InvalidOperationException("The menu item is no longer owned."), parameter, CancellationToken.None);
                    Accepted(result);
                }
            };
            retainedContainers.Add(container, state);
            RetainedNavigationBridge.Bind(container, state.Bridge);
            if (container is FlyoutPage { Flyout: { } menu })
            { state.Menu = menu; RetainedNavigationBridge.Bind(menu, state.Bridge); }
            if (container is TabbedPage tabs)
            {
                state.Changed = (_, _) =>
                {
                    if (state.Bridge.Applying || state.Bridge.Composing || IsClosed) return;
                    var selected = tabs.CurrentPage;
                    if (ReferenceEquals(selected, state.Selected)) return;
                    if (state.Selected is not Page accepted || !tabs.Children.Contains(accepted)
                        || !GetItems(tabs).Any(item => ReferenceEquals(item.View, selected)))
                    {
                        state.Selected = selected;
                        NativeChanged();
                        return;
                    }
                    state.Bridge.Applying = true;
                    try { tabs.CurrentPage = state.Selected as Page; }
                    finally { state.Bridge.Applying = false; }
                    var index = GetItems(tabs).ToList().FindIndex(item => ReferenceEquals(item.View, selected));
                    var task = QueueNativeSelectionAsync(tabs, index, state);
                    NativeSelectionCompletion = NativeSelectionCompletion.IsCompleted ? task : Task.WhenAll(NativeSelectionCompletion, task);
                };
                tabs.CurrentPageChanged += state.Changed;
            }
        }
    }

    private Task QueueNativeSelectionAsync(TabbedPage tabs, int index, RetainedContainer state)
    {
        async Task SelectAsync()
        {
            var result = await SelectTabAsync(tabs, new(index) { CoalescingKey = state.Key });
            if (result.Status == NavigationStatus.Failed) NavigationDiagnostics.Report(result.Error!, "Native tab selection");
        }
        for (var frame = execution.Value; frame != null; frame = frame.Parent)
            if (frame.Host == this && frame.Executing)
            {
                using (ExecutionContext.SuppressFlow()) return Task.Run(SelectAsync);
            }
        return SelectAsync();
    }

    private static bool Accepted<T>(NavigationOutcome<T> result)
    {
        if (result.Status == NavigationStatus.Failed) throw result.Error!;
        return result.Status == NavigationStatus.Completed;
    }

    private MauiNavigationItem[] RetainedWithin(Page? page, IEnumerable<MauiNavigationPage> owners) => retainedItems
        .Where(item => owners.Contains(item.Owner) || item.View is Page child && ContainsPage(page, child)) .Reverse().ToArray();

    private void ForgetRetained(IEnumerable<MauiNavigationItem> items)
    {
        foreach (var item in items)
        {
            retainedItems.Remove(item);
            if (item.Entry?.ViewModel is ViewModelBase legacy && !retainedItems.Any(kept => kept.Entry == item.Entry)) RetainedNavigationBridge.ReleaseLifecycle(legacy);
        }
        foreach (var pair in retainedContainers.Where(pair => !retainedItems.Any(item => item.Container == pair.Key)).ToArray())
        {
            if (pair.Key is TabbedPage tabs) tabs.CurrentPageChanged -= pair.Value.Changed;
            if (pair.Key is IRetainedTabMaintenance custom) custom.ReleaseSubscriptions();
            RetainedNavigationBridge.Release(pair.Key);
            if (pair.Value.Menu != null) RetainedNavigationBridge.Release(pair.Value.Menu);
            retainedContainers.Remove(pair.Key);
        }
    }

    private sealed class MenuOwner;
    private sealed class RetainedContainer(VisualElement view)
    {
        internal RetainedNavigationBridge Bridge = null!;
        internal object? Selected = SelectedIdentity(view);
        internal EventHandler? Changed;
        internal VisualElement? Menu;
        internal string Key = $"native-tab-{Guid.NewGuid()}";
    }
}
