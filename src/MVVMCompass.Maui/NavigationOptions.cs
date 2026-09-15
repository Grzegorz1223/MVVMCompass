namespace MVVMCompass;

/// <summary>Controls compatibility behavior for retained views.</summary>
internal sealed class NavigationOptions
{
    /// <summary>Uses one asynchronously disposed DI scope per resolved navigation destination. Defaults to the existing shared-provider behavior.</summary>
    public bool UseEntryScopes { get; set; }

    /// <summary>Gets or sets the callback used when leaving a view that remains available.</summary>
    public RetainedViewLifecycleBehavior RetainedViewLifecycleBehavior { get; set; }
        = RetainedViewLifecycleBehavior.Deactivate;
}

/// <summary>Defines how retained tabs and flyout entries notify their view models.</summary>
internal enum RetainedViewLifecycleBehavior
{
    /// <summary>Calls Deactivated without ending the instance's lifetime.</summary>
    Deactivate,
    /// <summary>Calls AfterDismissed without ending the lifetime, preserving legacy profile callbacks.</summary>
    LegacyAfterDismissed
}
