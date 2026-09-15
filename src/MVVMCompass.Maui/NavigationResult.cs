using MVVMCompass.Core;

namespace MVVMCompass;

/// <summary>The outcome of navigation, without exposing views or engine entries.</summary>
public sealed record NavigationResult
{
    /// <summary>Creates an outcome, also usable by application test doubles.</summary>
    public NavigationResult(NavigationStatus status, bool hasCommitted = false, Exception? error = null,
        IReadOnlyList<Exception>? cleanupErrors = null)
    { Status = status; HasCommitted = hasCommitted; Error = error; CleanupErrors = cleanupErrors ?? []; }
    /// <summary>Gets the terminal status.</summary>
    public NavigationStatus Status { get; }
    /// <summary>Gets whether navigation completed successfully.</summary>
    public bool IsSuccess => Status == NavigationStatus.Completed;
    /// <summary>Gets whether navigation crossed its commitment boundary.</summary>
    public bool HasCommitted { get; }
    /// <summary>Gets a preparation, guard, or presentation error.</summary>
    public Exception? Error { get; }
    /// <summary>Gets terminal cleanup errors.</summary>
    public IReadOnlyList<Exception> CleanupErrors { get; }
    internal static NavigationResult From<T>(NavigationOutcome<T> result) => new(result.Status, result.HasCommitted, result.Error, result.CleanupErrors);
}
