using MVVMCompass.Core;

namespace MVVMCompass;

public abstract partial class ViewModelBase
{
    internal Services.NavigationService? NavigationBinding { get; set; }
    internal NavigationPresentation? CompositionKind { get; private set; }
    internal IReadOnlyList<NavigationItem> CompositionItems { get; private set; } = [];
    internal bool IsPreparingComposition { get; set; }

    /// <summary>Sets this model's tabs. Initialization stages membership; later calls reconcile retained destinations atomically.</summary>
    protected Task<NavigationResult> SetTabs(IEnumerable<NavigationItem> items, CancellationToken cancellationToken = default) =>
        SetDestinations(items, NavigationPresentation.Tabs, cancellationToken);

    /// <summary>Sets this model's flyout items. Views only supply the visual templates.</summary>
    protected Task<NavigationResult> SetFlyoutItems(IEnumerable<NavigationItem> items, CancellationToken cancellationToken = default) =>
        SetDestinations(items, NavigationPresentation.Flyout, cancellationToken);

    private async Task<NavigationResult> SetDestinations(IEnumerable<NavigationItem> source, NavigationPresentation kind, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        var items = source.ToArray();
        if (items.Length == 0 || items.Any(item => item == null)) throw new ArgumentException("Declare at least one destination.", nameof(source));
        if (items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Destination IDs must be unique within their container. Supply explicit IDs for repeated model types.", nameof(source));
        if (items.Count(item => item.IsInitiallySelected) > 1 || !items.Any(item => item.IsEnabled) ||
            items.Any(item => item.IsInitiallySelected && !item.IsEnabled))
            throw new ArgumentException("Declare an enabled initial destination and at most one explicit initial selection.", nameof(source));
        if (CompositionKind is { } existing && existing != kind)
            throw new InvalidOperationException("A container cannot change between tabs and flyout.");
        if (token.IsCancellationRequested) return new(NavigationStatus.Cancelled);
        var result = IsPreparingComposition ? new NavigationResult(NavigationStatus.Completed) : NavigationBinding == null
            ? new NavigationResult(NavigationStatus.InvalidOrigin) : await NavigationBinding.SetItems(items, kind, token);
        if (result.IsSuccess) { CompositionKind = kind; CompositionItems = Array.AsReadOnly(items); }
        return result;
    }
}
