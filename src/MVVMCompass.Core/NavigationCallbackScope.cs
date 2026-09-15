namespace MVVMCompass.Core;

// Logical scopes follow asynchronous lifecycle work. The synchronous scope survives
// CancellationToken.Register restoring the execution context captured at registration.
// Keep it thread-local: an independent caller must still be able to await cleanup.
internal sealed class NavigationCallbackScope : IDisposable
{
    private static readonly AsyncLocal<NavigationCallbackScope?> logical = new();
    [ThreadStatic] private static NavigationCallbackScope? synchronous;
    private readonly NavigationCallbackScope? previous;
    private readonly NavigationCallbackScope? parent;
    private readonly object owner;
    private bool active = true;
    private bool cleanup;
    private ViewDetachmentBatch? viewDetachment;
    private sealed class ViewDetachmentBatch
    {
        internal List<NavigationLifetime>? Finalizers;
        internal List<NavigationLifetime>? Waiters;
    }

    private NavigationCallbackScope(object owner)
    {
        this.owner = owner;
        previous = logical.Value;
        parent = synchronous ?? previous;
        logical.Value = this;
    }

    internal static NavigationCallbackScope Enter(object owner) => new(owner);

    internal static NavigationCallbackScope EnterCleanup(object owner) => new(owner) { cleanup = true };

    internal static bool IsCleaning
    {
        get
        {
            lock (NavigationOwnershipNode.Gate)
                for (var scope = synchronous ?? logical.Value; scope != null; scope = scope.parent)
                    if (Volatile.Read(ref scope.active) && scope.cleanup) return true;
            return false;
        }
    }

    private NavigationCallbackScope CleanupBatch()
    {
        var batch = this;
        for (var scope = parent; scope != null; scope = scope.parent)
            if (Volatile.Read(ref scope.active) && scope.cleanup) batch = scope;
        return batch;
    }

    internal void ObserveViewDetachment(NavigationLifetime lifetime)
    {
        if (!lifetime.HasViewFinalizer) return;
        lock (NavigationOwnershipNode.Gate)
            ((CleanupBatch().viewDetachment ??= new()).Waiters ??= []).Add(lifetime);
    }

    internal void FinishOrDeferViewDetachment(NavigationLifetime lifetime, ref List<Exception>? failures)
    {
        lock (NavigationOwnershipNode.Gate)
        {
            var batch = CleanupBatch();
            if (batch != this || viewDetachment?.Finalizers != null)
            {
                ((batch.viewDetachment ??= new()).Finalizers ??= []).Add(lifetime);
                return;
            }
        }
        // The usual single-model dismissal needs no batch/list allocation.
        try { lifetime.DetachView(); }
        catch (Exception error) { (failures ??= []).Add(error); }
    }

    internal void FinishViewDetachments(ref List<Exception>? failures)
    {
        List<NavigationLifetime>? pending;
        lock (NavigationOwnershipNode.Gate)
        {
            cleanup = false;
            pending = viewDetachment?.Finalizers;
            if (viewDetachment != null) viewDetachment.Finalizers = null;
        }
        if (pending == null) return;
        foreach (var lifetime in pending)
            try { lifetime.DetachView(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        pending.Clear();
    }

    internal async Task WaitForViewDetachmentsAsync()
    {
        List<NavigationLifetime>? pending;
        lock (NavigationOwnershipNode.Gate)
        {
            pending = viewDetachment?.Waiters;
            viewDetachment = null;
        }
        if (pending == null) return;
        // Finish this batch's own hooks before joining overlapping independent batches.
        // Nested cleanup contributes to its outer batch and therefore has no waiters here.
        foreach (var lifetime in pending) await lifetime.ViewDetachment;
        pending.Clear();
    }

    internal static bool IsActive(object owner) => Contains(logical.Value, owner) || Contains(synchronous, owner);

    private static bool Contains(NavigationCallbackScope? scope, object owner)
    {
        for (; scope != null; scope = scope.parent)
            if (Volatile.Read(ref scope.active) && ReferenceEquals(scope.owner, owner)) return true;
        return false;
    }

    internal static bool IsWithinLifetime(NavigationLifetime lifetime) =>
        ContainsLifetime(logical.Value, lifetime) || ContainsLifetime(synchronous, lifetime);

    private static bool ContainsLifetime(NavigationCallbackScope? scope, NavigationLifetime lifetime)
    {
        for (; scope != null; scope = scope.parent)
            if (Volatile.Read(ref scope.active) && scope.owner is NavigationLifetime callback
                && lifetime.Contains(callback)) return true;
        return false;
    }

    internal static CancellationScope ProtectCancellation() => new();

    internal readonly struct CancellationScope : IDisposable
    {
        private readonly NavigationCallbackScope? previous;
        public CancellationScope()
        {
            previous = synchronous;
            synchronous = logical.Value ?? previous;
        }
        public void Dispose() => synchronous = previous;
    }

    public void Dispose()
    {
        Volatile.Write(ref active, false);
        logical.Value = previous;
    }
}
