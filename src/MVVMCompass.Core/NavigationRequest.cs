namespace MVVMCompass.Core;

/// <summary>Determines how an operation competes with work that has not committed.</summary>
public enum NavigationPriority
{
    /// <summary>Runs in arrival order unless cancelled or superseded.</summary>
    Normal,
    /// <summary>Supersedes uncommitted normal work and runs before queued normal work.</summary>
    Required
}

/// <summary>Typed operation data and explicit admission policy.</summary>
public sealed record NavigationRequest<TParameter>(TParameter Parameter)
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

/// <summary>The terminal result of an operation, separate from its returned value.</summary>
public enum NavigationStatus
{
    /// <summary>The operation completed successfully.</summary>
    Completed,
    /// <summary>The caller cancelled before commitment.</summary>
    Cancelled,
    /// <summary>Newer or required work replaced this uncommitted operation.</summary>
    Superseded,
    /// <summary>The origin is inactive, dismissed or belongs to another coordinator.</summary>
    InvalidOrigin,
    /// <summary>A callback tried to await work on its own executing coordinator.</summary>
    Reentrant,
    /// <summary>The caller requested rejection while the coordinator was occupied.</summary>
    Busy,
    /// <summary>The operation or its automatic cleanup failed.</summary>
    Failed,
    /// <summary>An asynchronous navigation guard refused the uncommitted operation.</summary>
    GuardRejected,
    /// <summary>No matching destination exists in the origin's containers.</summary>
    DestinationNotFound,
    /// <summary>The destination is disabled or cannot currently be selected.</summary>
    DestinationUnavailable,
    /// <summary>More than one destination matches; select by an explicit ID.</summary>
    AmbiguousDestination
}

/// <summary>A typed result with commitment and diagnostic information.</summary>
public sealed class NavigationOutcome<TResult>
{
    internal NavigationOutcome(NavigationStatus status, TResult? value, bool hasCommitted,
        Exception? error = null, IReadOnlyList<Exception>? cleanupErrors = null)
    {
        Status = status;
        Value = value;
        HasCommitted = hasCommitted;
        Error = error;
        CleanupErrors = cleanupErrors ?? Array.Empty<Exception>();
    }

    /// <summary>Gets the operation's terminal status.</summary>
    public NavigationStatus Status { get; }
    /// <summary>Gets whether the operation completed successfully.</summary>
    public bool IsSuccess => Status == NavigationStatus.Completed;
    /// <summary>Gets the returned value on success, or its default value otherwise.</summary>
    public TResult? Value { get; }
    /// <summary>Gets whether the irreversible boundary was crossed, including on failure.</summary>
    public bool HasCommitted { get; }
    /// <summary>Gets the primary operation error, if any.</summary>
    public Exception? Error { get; }
    /// <summary>Gets failures from automatic entry cleanup or cancellation callbacks.</summary>
    public IReadOnlyList<Exception> CleanupErrors { get; }
}
