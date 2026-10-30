namespace MVVMCompass;

/// <summary>Controls construction, detail stacks and retained lifecycle for one tab container.</summary>
public sealed record TabNavigationOptions
{
    internal static TabNavigationOptions Default { get; } = new();
    /// <summary>Gets when tab roots are constructed. Lazy creation remains the default.</summary>
    public TabInitialization Initialization { get; init; }
    /// <summary>Gets where details opened from this container or its selected branch are pushed.</summary>
    public TabDetailNavigation DetailNavigation { get; init; }
    /// <summary>Gets the retained callback contract. Terminal ownership is identical in either mode.</summary>
    public TabLifecycle Lifecycle { get; init; }

    internal void Validate()
    {
        if (!Enum.IsDefined(Initialization) || !Enum.IsDefined(DetailNavigation) || !Enum.IsDefined(Lifecycle))
            throw new ArgumentException("Unknown tab navigation option.");
    }
}

/// <summary>When a declared tab receives its owned model, view and initialization callback.</summary>
public enum TabInitialization
{
    /// <summary>Construct a destination on its first selection.</summary>
    Lazy,
    /// <summary>Initialize every declared root, including hidden and disabled items, in declaration order. Only the selected root appears.</summary>
    Eager
}

/// <summary>Placement of details opened from a tab container's active branch.</summary>
public enum TabDetailNavigation
{
    /// <summary>Keep details inside the selected tab and retain independent tab histories.</summary>
    WithinTab,
    /// <summary>Push onto the stack containing the nearest configured tab container, covering its entire visual surface.</summary>
    AboveTabs
}

/// <summary>Callbacks for a retained tab when its selection or visibility changes.</summary>
public enum TabLifecycle
{
    /// <summary>Notify retained screens through Deactivated and reactivate them through NavigatedTo and Appearing.</summary>
    Standard,
    /// <summary>Call AfterDismissed on an actual tab switch without ending ownership. Covering the container calls its Disappearing and retains child activation.</summary>
    TabSwitchCompatibility
}
