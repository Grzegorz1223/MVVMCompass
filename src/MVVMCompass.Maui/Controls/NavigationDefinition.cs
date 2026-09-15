namespace MVVMCompass;

/// <summary>The destination selector displayed around a custom content stack.</summary>
public enum NavigationPresentation
{
    /// <summary>A single stack without a destination selector.</summary>
    Plain,
    /// <summary>A horizontal tab selector.</summary>
    Tabs,
    /// <summary>A vertical destination rail.</summary>
    Rail,
    /// <summary>A flyout menu surrounding a persistent custom detail host.</summary>
    Flyout
}

/// <summary>Immutable configuration of a custom navigation scope.</summary>
internal sealed class NavigationDefinition
{
    /// <summary>Creates a scope. Destination IDs must be unique throughout its tree.</summary>
    public NavigationDefinition(IEnumerable<NavigationDestination> destinations,
        NavigationPresentation presentation = NavigationPresentation.Plain, string? initialDestinationId = null)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        if (!Enum.IsDefined(presentation)) throw new ArgumentOutOfRangeException(nameof(presentation));
        Destinations = Array.AsReadOnly(destinations.ToArray());
        if (Destinations.Count == 0 || Destinations.Any(item => item is null))
            throw new ArgumentException("Declare at least one destination.", nameof(destinations));
        if (presentation == NavigationPresentation.Plain && Destinations.Count != 1)
            throw new ArgumentException("A plain scope contains one destination.", nameof(destinations));
        if (presentation != NavigationPresentation.Flyout && Destinations.Any(item => item.Children.Count != 0))
            throw new ArgumentException("Tab groups are supported inside a flyout scope.", nameof(destinations));
        Presentation = presentation;
        InitialDestinationId = initialDestinationId;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Flatten(Destinations))
            if (!ids.Add(item.Id)) throw new ArgumentException($"Duplicate destination ID '{item.Id}'.", nameof(destinations));
        if (initialDestinationId != null && !ids.Contains(initialDestinationId))
            throw new ArgumentException("The initial destination is not registered.", nameof(initialDestinationId));
    }

    /// <summary>Gets the declared top-level destinations.</summary>
    public IReadOnlyList<NavigationDestination> Destinations { get; }
    /// <summary>Gets the selector layout.</summary>
    public NavigationPresentation Presentation { get; }
    /// <summary>Gets the initial destination ID, or null for the first declared destination.</summary>
    public string? InitialDestinationId { get; }

    internal static IEnumerable<NavigationDestination> Flatten(IEnumerable<NavigationDestination> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children)) yield return child;
        }
    }
}

/// <summary>A named screen stack or a group of tab stacks. Factories create fresh screen owners.</summary>
internal sealed class NavigationDestination
{
    internal NavigationItem? Item { get; init; }
    /// <summary>Creates a screen destination.</summary>
    public NavigationDestination(string id, string title, ScreenFactory factory, string? icon = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        Id = id; Title = title; Factory = factory; Icon = icon;
    }

    /// <summary>Creates a group of tab destinations, for example inside a flyout.</summary>
    public NavigationDestination(string id, string title, IEnumerable<NavigationDestination> tabs, string? icon = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(tabs);
        Id = id; Title = title; Icon = icon;
        Children = Array.AsReadOnly(tabs.ToArray());
        if (Children.Count == 0 || Children.Any(item => item is null || item.Children.Count != 0))
            throw new ArgumentException("A tab group must contain one or more screen destinations.", nameof(tabs));
    }

    /// <summary>Gets the stable ID, independent of view-model type.</summary>
    public string Id { get; }
    /// <summary>Gets the selector label.</summary>
    public string Title { get; }
    /// <summary>Gets an optional image source name.</summary>
    public string? Icon { get; }
    /// <summary>Gets the root factory, or null for a tab group.</summary>
    public ScreenFactory? Factory { get; }
    /// <summary>Gets the group's tab destinations.</summary>
    public IReadOnlyList<NavigationDestination> Children { get; } = Array.Empty<NavigationDestination>();
}
