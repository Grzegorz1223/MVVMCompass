namespace MVVMCompass;

/// <summary>A ViewModel-owned destination. IDs are unique within its containing tabs or flyout.</summary>
public sealed class NavigationItem
{
    /// <summary>Describes a destination without constructing its view or model.</summary>
    public NavigationItem(Type viewModelType, string title, string? id = null,
        Dictionary<string, object>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(viewModelType);
        ArgumentNullException.ThrowIfNull(title);
        if (!typeof(ViewModelBase).IsAssignableFrom(viewModelType) || viewModelType.IsAbstract || viewModelType.ContainsGenericParameters)
            throw new ArgumentException("A destination must be a concrete ViewModelBase type.", nameof(viewModelType));
        if (id != null && (string.IsNullOrWhiteSpace(id) || id.Contains('/')))
            throw new ArgumentException("Use a nonempty ID without '/'.", nameof(id));
        ViewModelType = viewModelType; Title = title; Id = id ?? viewModelType.FullName ?? viewModelType.Name;
        Parameters = parameters == null ? null : new System.Collections.ObjectModel.ReadOnlyDictionary<string, object>(new Dictionary<string, object>(parameters));
    }

    /// <summary>Gets the registered model type.</summary>
    public Type ViewModelType { get; }
    /// <summary>Gets its stable identity within the owning container.</summary>
    public string Id { get; }
    /// <summary>Gets the accessible display label.</summary>
    public string Title { get; }
    /// <summary>Gets the parameters applied when this destination is created.</summary>
    public IReadOnlyDictionary<string, object>? Parameters { get; }
    /// <summary>Gets the selected icon.</summary>
    public string? SelectedIcon { get; init; }
    /// <summary>Gets the unselected icon.</summary>
    public string? UnselectedIcon { get; init; }
    /// <summary>Gets whether this is the initial destination. At most one item may be selected initially.</summary>
    public bool IsInitiallySelected { get; init; }
    /// <summary>Gets selector visibility. Hidden destinations remain available to explicit selection.</summary>
    public bool IsVisible { get; init; } = true;
    /// <summary>Gets selection availability, including explicit selection.</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>The edge occupied by a tab strip.</summary>
public enum TabBarPosition
{
    /// <summary>Horizontal tabs above the body.</summary>
    Top,
    /// <summary>Horizontal tabs below the body.</summary>
    Bottom,
    /// <summary>Vertical tabs to the left of the body.</summary>
    Left,
    /// <summary>Vertical tabs to the right of the body.</summary>
    Right
}

/// <summary>How visible tabs divide the space left after shared strip content.</summary>
public enum TabItemSizing
{
    /// <summary>Natural item sizes, with scrolling on overflow.</summary>
    Content,
    /// <summary>Equal shares of the available width or height.</summary>
    Equal
}

/// <summary>Placement of persistent shared content relative to the destination body.</summary>
public enum SharedContentPosition
{
    /// <summary>Above the body.</summary>
    Top,
    /// <summary>Below the body.</summary>
    Bottom
}
