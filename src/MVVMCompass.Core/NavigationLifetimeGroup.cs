namespace MVVMCompass.Core;

/// <summary>Coordinates terminal cleanup of an already ordered ownership tree.</summary>
public static class NavigationLifetimeGroup
{
    /// <summary>
    /// Marks every distinct lifetime before any callback, then cleans all entries in input order.
    /// All cleanup failures are reported after the remaining entries have been processed.
    /// </summary>
    public static async Task DismissAsync(
        IEnumerable<NavigationLifetime> lifetimes,
        DismissalReason reason = DismissalReason.Removed)
    {
        ArgumentNullException.ThrowIfNull(lifetimes);
        var ordered = lifetimes.Distinct().ToArray();
        foreach (var lifetime in ordered)
            ArgumentNullException.ThrowIfNull(lifetime);
        if (ordered.Length == 0) return;
        // A captured execution context must not retain the whole outgoing model array.
        using var batch = NavigationCallbackScope.EnterCleanup(typeof(NavigationLifetimeGroup));
        foreach (var lifetime in ordered)
            lifetime.MarkDismissed(reason);
        // One explicit ownership tree cannot contain duplicate descendants.
        var signalled = ordered.Length == 1 ? null : new HashSet<NavigationLifetime>();
        foreach (var lifetime in ordered)
            lifetime.SignalCancellation(signalled);

        List<Exception>? failures = null;
        foreach (var lifetime in ordered)
        {
            try
            {
                if (!NavigationCallbackScope.IsWithinLifetime(lifetime)) batch.ObserveViewDetachment(lifetime);
                await lifetime.DismissAsync(reason);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        batch.FinishViewDetachments(ref failures);
        await batch.WaitForViewDetachmentsAsync();

        if (failures is not null)
            throw new AggregateException("One or more navigation entries failed to clean up.", failures);
    }
}
