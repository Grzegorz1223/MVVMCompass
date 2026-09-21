using MVVMCompass.Core;

namespace MVVMCompass;

internal sealed partial class NavigationContext
{
    internal NavigationContext? ParentContext { get; private set; }
    internal NavigationScreenEntry? ContainerScreen { get; private set; }
    internal MauiNavigationHost Host => owner;
    internal NavigationContext RootContext => ParentContext?.RootContext ?? this;
    internal NavigationContext Deepest => Current?.Children?.Deepest ?? this;
    private bool embeddedFlyoutOpen;
    internal bool HasOpenFlyout => IsFlyoutOpen || Current?.Children?.HasOpenFlyout == true;
    private ViewModelBase? notifiedSelection;
    private async Task NotifySelectionAsync()
    {
        var selected = active?.Stack.FirstOrDefault()?.ViewModel;
        if (ContainerScreen == null || selected == null || ReferenceEquals(notifiedSelection, selected)) return;
        notifiedSelection = selected;
        await ContainerScreen.ViewModel.OnActiveTabChanged(selected);
    }

    internal IEnumerable<NavigationContext> ActiveChain()
    {
        yield return this;
        if (Current?.Children is { } child) foreach (var context in child.ActiveChain()) yield return context;
    }

    internal bool ContainsActiveOrigin(NavigationEntry origin) => IsActive &&
        ActiveChain().Any(context => ReferenceEquals(context.Current?.Entry, origin));

    internal NavigationContext BackContext() => RootContext.ActiveChain().LastOrDefault(context => context.IsFlyoutOpen)
        ?? RootContext.ActiveChain().LastOrDefault(context => context.LocalCanGoBack) ?? RootContext;

    internal NavigationContext CreateChild(NavigationScreenEntry screen, NavigationDefinition definition)
    {
        var child = new NavigationContext(owner, definition, false) { ParentContext = this, ContainerScreen = screen };
        child.Attach(screen.Entry);
        screen.Entry.Ownership.RegisterCleanup(() => ((INavigationAware)child).DismissAsync(screen.Entry.Lifetime.Reason));
        return child;
    }

    internal Task InitializeContentsAsync(CancellationToken token) =>
        ((INavigationInitializable<NavigationDefinition>)this).InitializeAsync(Definition, token);

    private static IEnumerable<NavigationScreenEntry> GuardBranch(NavigationScreenEntry screen)
    {
        if (screen.Children?.Current is { } current)
            foreach (var child in GuardBranch(current)) yield return child;
        yield return screen;
    }

    private static IEnumerable<NavigationScreenEntry> EndingBranch(NavigationScreenEntry screen) =>
        (screen.Children?.AllScreens() ?? []).Append(screen);

    internal IEnumerable<NavigationScreenEntry> AllScreens() => ownedScreens.SelectMany(screen =>
        (screen.Children?.AllScreens() ?? []).Append(screen));

    internal NavigationContext? FindContainer(string id)
    {
        for (var context = this; context != null; context = context.ParentContext)
            if (context.items.ContainsKey(id)) return context;
        return null;
    }

    internal IReadOnlyList<NavigationItemContext> Matching(Type type) => items.Values.Where(item => item.Destination.Item?.ViewModelType == type).ToArray();

    internal async Task<NavigationResult> ReconcileAsync(NavigationDefinition definition, NavigationRequestOptions options, CancellationToken token)
    {
        var result = await RunAsync(options, async operation =>
        {
            if (definition.Presentation != Definition.Presentation) throw new InvalidOperationException("The container kind cannot change.");
            var desired = definition.Destinations.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var obsolete = destinations.Values.Where(state => !desired.TryGetValue(state.Definition.Id, out var next)
                || next.Item?.ViewModelType != state.Definition.Item?.ViewModelType).ToArray();
            var removed = CurrentFirst(obsolete.SelectMany(state => state.Stack)).ToArray();
            var targetId = active != null && !obsolete.Contains(active) && desired[active.Definition.Id].Item?.IsEnabled != false
                ? active.Definition.Id : definition.InitialDestinationId!;
            var changesActive = active?.Definition.Id != targetId || active != null && obsolete.Contains(active);
            await GuardAsync(removed.SelectMany(EndingBranch).Concat(changesActive && Current != null ? [Current] : []), operation);
            var nextState = destinations.TryGetValue(targetId, out var retained) && !obsolete.Contains(retained) ? retained : new DestinationState(desired[targetId]);
            var candidate = nextState.Stack.Count == 0
                ? await PrepareAsync(desired[targetId].Factory!, null, entry => operation.Own(entry), operation.CancellationToken) : null;
            operation.BeginCommit();
            if (changesActive && Current != null) await DeactivateAsync(Current);
            foreach (var state in obsolete) { destinations.Remove(state.Definition.Id); items.Remove(state.Definition.Id); }
            Definition = definition;
            foreach (var destination in definition.Destinations)
            {
                if (!destinations.TryGetValue(destination.Id, out var state)) destinations.Add(destination.Id, destination.Id == targetId ? nextState : new(destination));
                else state.Definition = destination;
                if (!items.TryGetValue(destination.Id, out var item))
                    items.Add(destination.Id, item = new(destination, new Command(async () => await ReportAsync(SelectAsync(destination.Id,
                        options: new() { Origin = Current?.Entry, RejectIfBusy = true })), () => IsActive && !IsNavigating &&
                            items.TryGetValue(destination.Id, out var currentItem) && currentItem.IsEnabled && currentItem.IsVisible)));
                item.Destination = destination; item.IsVisible = destination.Item?.IsVisible ?? true; item.IsEnabled = destination.Item?.IsEnabled ?? true;
                item.RefreshMetadata();
            }
            active = nextState;
            if (candidate != null) active.Stack.Add(candidate);
            menu.ReplaceWith(HasFlyout ? definition.Destinations.Select(destination => items[destination.Id]) : []);
            foreach (var screen in removed) screen.Entry.Lifetime.MarkDismissed(DismissalReason.Removed);
            Publish();
            try { if (changesActive || candidate != null) await View.Presenter.PresentAsync(Current!.View, false); }
            finally
            {
                try { await DismissAsync(removed, DismissalReason.Removed); }
                finally { if (Current != null) await ActivateAsync(Current); }
            }
            await NotifySelectionAsync();
            return Current;
        }, token);
        return NavigationResult.From(result);
    }
}
