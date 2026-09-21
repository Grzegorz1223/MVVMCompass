namespace MVVMCompass.Core;

/// <summary>The lifetime and ownership boundary of one executing operation.</summary>
public sealed class NavigationOperationContext<TParameter>
{
    private readonly NavigationCoordinator.Operation operation;

    internal NavigationOperationContext(TParameter parameter, NavigationCoordinator.Operation operation)
    {
        Parameter = parameter;
        this.operation = operation;
    }

    /// <summary>Gets the request's typed data.</summary>
    public TParameter Parameter { get; }

    /// <summary>Gets cooperative cancellation that is disabled after commitment.</summary>
    public CancellationToken CancellationToken => operation.Token;

    /// <summary>Gets whether the operation crossed its irreversible boundary.</summary>
    public bool HasCommitted => operation.Coordinator.IsCommitted(operation);

    internal void RecordCleanupError(Exception error) => operation.Diagnostics.Add(error);

    /// <summary>Registers a prepared candidate for automatic abandonment unless commitment succeeds.</summary>
    public NavigationEntry<TViewModel> Own<TViewModel>(NavigationEntry<TViewModel> entry) where TViewModel : class
    {
        operation.Coordinator.Own(operation, entry);
        return entry;
    }

    /// <summary>
    /// Checks cancellation and origin validity, then makes the remaining work non-cancellable.
    /// Call immediately before outgoing teardown; the host takes ownership of registered candidates.
    /// </summary>
    public void BeginCommit() => operation.Coordinator.BeginCommit(operation);

    /// <summary>
    /// Ends uncommitted work with GuardRejected or InvalidOrigin. Prepared candidates are abandoned.
    /// Cancellation and supersession retain precedence over an adapter's rejection.
    /// </summary>
    public void Reject(NavigationStatus status) => operation.Coordinator.Reject(operation, status);
}
