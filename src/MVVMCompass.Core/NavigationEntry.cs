namespace MVVMCompass.Core;

/// <summary>The presentation state of a portable navigation entry.</summary>
public enum NavigationEntryState
{
    /// <summary>The entry is owned but has not been activated.</summary>
    Prepared,
    /// <summary>The entry can originate operations in its coordinator.</summary>
    Active,
    /// <summary>The retained entry has released its active-view resources.</summary>
    Inactive,
    /// <summary>The entry is terminal, including while cleanup is running.</summary>
    Dismissed
}

/// <summary>An explicit, portable owner of one view model and its lifetime.</summary>
public abstract class NavigationEntry : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycleGate = new(1);
    private readonly INavigationAware? aware;
    private readonly Func<Task>? cleanup;
    private int state;
    private int activationVersion;
    private bool initialized;
    private bool initializationAttempted;

    internal NavigationEntry(Guid ownerId, object viewModel, Func<Task>? cleanup)
    {
        OwnerId = ownerId;
        ViewModel = viewModel;
        aware = viewModel as INavigationAware;
        this.cleanup = cleanup;
        Lifetime = new NavigationLifetime(CleanupAsync, viewModel);
    }

    internal NavigationEntry(Guid ownerId, object viewModel, NavigationLifetime lifetime)
    {
        OwnerId = ownerId;
        ViewModel = viewModel;
        Lifetime = lifetime;
        if (lifetime.Ownership.Owner != null && !ReferenceEquals(lifetime.Ownership.Owner, viewModel))
            throw new InvalidOperationException("The lifetime belongs to another view model.");
        lifetime.Ownership.Owner = viewModel;
        // The existing adapter owns its callbacks as well as its terminal cleanup.
        // Entry state and typed initialization remain portable; do not invoke them twice.
    }

    internal Guid OwnerId { get; }
    internal int ActivationVersion => Volatile.Read(ref activationVersion);

    /// <summary>Gets a stable identity distinct from the destination's type or value.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Gets the owned view model; the coordinator never resolves services implicitly.</summary>
    public object ViewModel { get; }

    /// <summary>Gets the entry's permanent lifetime and completion state.</summary>
    public NavigationLifetime Lifetime { get; }

    /// <summary>Gets explicit child and resource ownership, shared with the permanent lifetime.</summary>
    public NavigationOwnershipNode Ownership => Lifetime.Ownership;

    /// <summary>Gets whether the entry is prepared, active, retained or terminal.</summary>
    public NavigationEntryState State => Lifetime.IsDismissed
        ? NavigationEntryState.Dismissed : (NavigationEntryState)Volatile.Read(ref state);

    /// <summary>Initializes typed data once. A failed attempt must be abandoned, not retried.</summary>
    public async Task InitializeAsync<TParameter>(TParameter parameter, CancellationToken cancellationToken = default)
    {
        Lifetime.ThrowIfExecutingCallback();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime.Token);
        await lifecycleGate.WaitAsync(linked.Token);
        try
        {
            using var callback = Lifetime.EnterCallback();
            ThrowIfDismissed();
            if (State != NavigationEntryState.Prepared || initializationAttempted)
                throw new InvalidOperationException("An entry can be initialized once, before activation.");
            initializationAttempted = true;
            if (ViewModel is not INavigationInitializable<TParameter> initializable)
                throw new InvalidOperationException("The view model does not accept this navigation parameter type.");
            await initializable.InitializeAsync(parameter, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            initialized = true;
        }
        finally { lifecycleGate.Release(); }
    }

    /// <summary>Activates or reactivates an entry. Repeated activation while active is a no-op.</summary>
    public async Task ActivateAsync()
    {
        Lifetime.ThrowIfExecutingCallback();
        await lifecycleGate.WaitAsync(Lifetime.Token);
        try
        {
            using var callback = Lifetime.EnterCallback();
            ThrowIfDismissed();
            if (State == NavigationEntryState.Active) return;
            if (initializationAttempted && !initialized)
                throw new InvalidOperationException("An entry with failed initialization must be abandoned.");
            if (aware != null) await aware.ActivateAsync(Lifetime.Token);
            ThrowIfDismissed();
            Interlocked.Increment(ref activationVersion);
            Volatile.Write(ref state, (int)NavigationEntryState.Active);
        }
        finally { lifecycleGate.Release(); }
    }

    /// <summary>Releases active resources without cancelling or disposing the retained entry.</summary>
    public async Task DeactivateAsync()
    {
        Lifetime.ThrowIfExecutingCallback();
        await lifecycleGate.WaitAsync();
        try
        {
            using var callback = Lifetime.EnterCallback();
            if (State != NavigationEntryState.Active) return;
            Interlocked.Increment(ref activationVersion);
            Volatile.Write(ref state, (int)NavigationEntryState.Inactive);
            if (aware != null) await aware.DeactivateAsync();
        }
        finally { lifecycleGate.Release(); }
    }

    /// <summary>Ends ownership once; concurrent callers await the same cleanup task.</summary>
    public Task DismissAsync(DismissalReason reason = DismissalReason.Removed) => Lifetime.DismissAsync(reason);

    /// <summary>Abandons a prepared entry, or removes a previously activated entry.</summary>
    public ValueTask DisposeAsync() => new(DismissAsync(State == NavigationEntryState.Prepared
        ? DismissalReason.PreparationFailed : DismissalReason.Removed));

    private void ThrowIfDismissed()
    {
        if (Lifetime.IsDismissed) throw new InvalidOperationException("The navigation entry has been dismissed.");
    }

    private async Task CleanupAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            List<Exception> errors = [];
            try { if (aware != null) await aware.DismissAsync(Lifetime.Reason); }
            catch (Exception error) { errors.Add(error); }
            try { if (cleanup != null) await cleanup(); }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("Navigation entry cleanup failed.", errors);
        }
        finally { lifecycleGate.Release(); }
    }
}

/// <summary>An entry with a strongly typed view model.</summary>
public sealed class NavigationEntry<TViewModel> : NavigationEntry where TViewModel : class
{
    internal NavigationEntry(Guid ownerId, TViewModel viewModel, Func<Task>? cleanup) : base(ownerId, viewModel, cleanup) { }

    internal NavigationEntry(Guid ownerId, TViewModel viewModel, NavigationLifetime lifetime) : base(ownerId, viewModel, lifetime) { }

    /// <summary>Gets the view model with its original type.</summary>
    public new TViewModel ViewModel => (TViewModel)base.ViewModel;
}
