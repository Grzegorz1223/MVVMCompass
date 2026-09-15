namespace MVVMCompass.Core;

/// <summary>Admission policy for navigation that does not request model initialization.</summary>
/// <remarks>Use NavigationRequest&lt;TParameter&gt; when the destination requires typed initialization.</remarks>
public sealed record NavigationRequestOptions
{
    /// <summary>Gets the originating entry, or null for an application-owned operation.</summary>
    public NavigationEntry? Origin { get; init; }

    /// <summary>Gets the priority; required operations retain their order relative to one another.</summary>
    public NavigationPriority Priority { get; init; }

    /// <summary>Gets a key for replacing older normal requests with the same key, or null for FIFO.</summary>
    public string? CoalescingKey { get; init; }

    /// <summary>Gets whether to reject an overlapping operation instead of queuing it.</summary>
    public bool RejectIfBusy { get; init; }
}
