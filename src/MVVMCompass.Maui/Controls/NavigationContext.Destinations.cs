using MVVMCompass.Core;

namespace MVVMCompass;

internal sealed partial class NavigationContext
{
    /// <summary>Selects the configured default, or the first remaining destination if it was removed.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> SelectDefaultAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) => SelectAsync(DefaultDestinationId, options: options, cancellationToken: cancellationToken);

    /// <summary>Dismisses a destination's entire history. An active destination receives a fresh root; an inactive one recreates its root on selection.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> ResetDestinationAsync(string destinationId,
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default) => RunAsync(options, async operation =>
    {
        var target = Resolve(destinationId);
        var removed = target.Stack.AsEnumerable().Reverse().ToArray();
        if (removed.Length == 0) return Current;
        await GuardAsync(removed.SelectMany(EndingBranch), operation);
        var candidate = target == active
            ? await PrepareAsync(target.Definition.Factory!, null, entry => operation.Own(entry), operation.CancellationToken) : null;
        operation.BeginCommit();
        target.Stack.Clear();
        if (candidate != null) target.Stack.Add(candidate);
        foreach (var screen in removed) screen.Entry.Lifetime.MarkDismissed(DismissalReason.Removed);
        Publish();
        try { if (candidate != null) await View.Presenter.PresentAsync(candidate.View, true); }
        finally
        {
            try { await DismissAsync(removed, DismissalReason.Removed); }
            finally { if (candidate != null) await ActivateAsync(candidate); }
        }
        return Current;
    }, cancellationToken);

    /// <summary>Removes a destination or tab group after guarding its screens, active first then newest first. Removing the active branch selects the supplied enabled replacement or the first enabled leaf in definition order.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> RemoveDestinationAsync(string destinationId,
        string? replacementDestinationId = null, NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) => RunAsync(options, async operation =>
    {
        if (!items.TryGetValue(destinationId, out var item)) throw new ArgumentException("The destination is not registered.", nameof(destinationId));
        var removingIds = NavigationDefinition.Flatten([item.Destination]).Select(destination => destination.Id).ToHashSet(StringComparer.Ordinal);
        var remaining = destinations.Values.Where(destination => !removingIds.Contains(destination.Definition.Id)).ToArray();
        if (remaining.Length == 0) throw new InvalidOperationException("A navigation scope must retain at least one destination.");
        var removesActive = removingIds.Contains(active!.Definition.Id);
        var target = active;
        if (removesActive)
        {
            if (replacementDestinationId == null)
            {
                var replacement = NavigationDefinition.Flatten(Definition.Destinations)
                    .FirstOrDefault(destination => destinations.ContainsKey(destination.Id)
                        && !removingIds.Contains(destination.Id) && items[destination.Id].IsEnabled
                        && items[TopLevel(destination.Id).Id].IsEnabled);
                if (replacement == null) operation.Reject(NavigationStatus.DestinationUnavailable);
                target = destinations[replacement!.Id];
            }
            else
            {
                if (!items.TryGetValue(replacementDestinationId, out var replacement)) operation.Reject(NavigationStatus.DestinationNotFound);
                if (!replacement!.IsEnabled) operation.Reject(NavigationStatus.DestinationUnavailable);
                target = Resolve(replacementDestinationId);
                if (!items[target.Definition.Id].IsEnabled || !items[TopLevel(target.Definition.Id).Id].IsEnabled)
                    operation.Reject(NavigationStatus.DestinationUnavailable);
            }
            if (removingIds.Contains(target.Definition.Id)) throw new ArgumentException("The replacement must remain registered.", nameof(replacementDestinationId));
        }
        var removed = CurrentFirst(destinations.Values.Where(destination => removingIds.Contains(destination.Definition.Id))
            .SelectMany(destination => destination.Stack)).ToArray();
        await GuardAsync(removed.SelectMany(EndingBranch), operation);
        var candidate = removesActive && target.Stack.Count == 0
            ? await PrepareAsync(target.Definition.Factory!, null, entry => operation.Own(entry), operation.CancellationToken) : null;
        operation.BeginCommit();
        if (candidate != null) target.Stack.Add(candidate);
        active = target;
        foreach (var id in removingIds) { destinations.Remove(id); items.Remove(id); selectedChildren.Remove(id); }
        foreach (var top in Definition.Destinations.Where(top => top.Children.Count != 0))
        {
            if (!top.Children.Any(child => items.ContainsKey(child.Id))) items.Remove(top.Id);
            if (selectedChildren.TryGetValue(top.Id, out var childId) && !items.ContainsKey(childId)) selectedChildren.Remove(top.Id);
        }
        foreach (var entry in menu.Where(entry => !items.ContainsKey(entry.Id)).ToArray()) menu.Remove(entry);
        foreach (var screen in removed) screen.Entry.Lifetime.MarkDismissed(DismissalReason.Removed);
        RememberSelection();
        OnPropertyChanged(nameof(Destinations));
        Publish();
        try { if (removesActive) await View.Presenter.PresentAsync(Current!.View, false); }
        finally
        {
            try { await DismissAsync(removed, DismissalReason.Removed); }
            finally { if (removesActive) await ActivateAsync(Current!); }
        }
        return Current;
    }, cancellationToken);

    private string DefaultDestinationId() => Definition.InitialDestinationId is { } initial && items.ContainsKey(initial)
        ? initial : Definition.Destinations.First(destination => items.ContainsKey(destination.Id)).Id;

    private IEnumerable<NavigationScreenEntry> CurrentFirst(IEnumerable<NavigationScreenEntry> screens) =>
        screens.Distinct().OrderByDescending(screen => ReferenceEquals(screen, Current))
            .ThenByDescending(screen => ownedScreens.IndexOf(screen));
}
