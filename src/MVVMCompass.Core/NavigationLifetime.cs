namespace MVVMCompass.Core;

/// <summary>
/// Owns permanent cancellation and exactly-once asynchronous cleanup independently of any UI framework.
/// Temporary deactivation must not dismiss this lifetime.
/// </summary>
public sealed class NavigationLifetime
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken token;
    private readonly Func<Task>? cleanup;
    private readonly Func<object?, Task>? statefulCleanup;
    private readonly object? owner;
    private NavigationOwnershipNode? ownership;
    private Task? completion;
    private TaskCompletionSource? completionSource;
    private IReadOnlyList<Exception> cancellationErrors = Array.Empty<Exception>();
    private int reason;
    private int started;
    private int cancellationState;
    private TaskCompletionSource? cancellationCompletion;

    /// <summary>Creates an owned lifetime with its terminal cleanup callback.</summary>
    public NavigationLifetime(Func<Task> cleanup) : this(cleanup, null) { }

    /// <summary>Creates a lifetime associated with an inspectable owner.</summary>
    public NavigationLifetime(Func<Task> cleanup, object? owner)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        this.cleanup = cleanup;
        token = cancellation.Token;
        this.owner = owner;
    }

    internal NavigationLifetime(Func<object?, Task> cleanup, object? owner)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        statefulCleanup = cleanup;
        this.owner = owner;
        token = cancellation.Token;
    }

    /// <summary>Gets explicit child and resource ownership for this lifetime.</summary>
    public NavigationOwnershipNode Ownership
    {
        get
        {
            if (Volatile.Read(ref ownership) is { } node) return node;
            lock (NavigationOwnershipNode.Gate)
            {
                if (ownership == null) Volatile.Write(ref ownership, new(this, owner));
                return ownership;
            }
        }
    }

    /// <summary>Gets a token cancelled on confirmed permanent removal, before ordered cleanup; it remains readable afterwards.</summary>
    public CancellationToken Token => token;

    /// <summary>Gets whether the entry is terminal, including the mark-before-cleanup phase.</summary>
    public bool IsDismissed => Volatile.Read(ref reason) != 0;

    /// <summary>Gets the first terminal reason. Later dismissal calls cannot replace it.</summary>
    public DismissalReason Reason => (DismissalReason)Volatile.Read(ref reason);

    /// <summary>Gets cancellation-callback failures, which never prevent terminal cleanup.</summary>
    public IReadOnlyList<Exception> CancellationErrors => Volatile.Read(ref cancellationErrors);

    /// <summary>Marks the entry terminal synchronously, before a whole tree starts asynchronous cleanup.</summary>
    public void MarkDismissed(DismissalReason dismissalReason)
    {
        if (dismissalReason == DismissalReason.None || !Enum.IsDefined(dismissalReason))
            throw new ArgumentOutOfRangeException(nameof(dismissalReason));

        if (IsDismissed) return;
        lock (NavigationOwnershipNode.Gate)
        {
            if (ownership == null) MarkOnly(dismissalReason);
            else ownership.Mark(dismissalReason);
        }
    }

    internal void MarkOnly(DismissalReason dismissalReason) => Interlocked.CompareExchange(ref reason, (int)dismissalReason, 0);

    // Called only after a complete confirmed-removal set has been marked.
    // Cancellation is independent of the ordered cleanup queue.
    internal void SignalCancellation(HashSet<NavigationLifetime>? signalled)
    {
        if (!IsDismissed) throw new InvalidOperationException("Mark confirmed ownership before signalling cancellation.");
        if (Volatile.Read(ref cancellationState) == 2 && Volatile.Read(ref ownership)?.HasChildren != true) return;
        if (signalled != null && !signalled.Add(this)) return;
        using var callback = EnterCallback();
        _ = CancelWork();
        if (Volatile.Read(ref ownership) is { } node)
            foreach (var child in node.Children) child.Lifetime.SignalCancellation(signalled);
    }

    private Task CancelWork()
    {
        if (Volatile.Read(ref cancellationState) == 2) return Task.CompletedTask;
        if (Interlocked.CompareExchange(ref cancellationState, 1, 0) == 0)
        {
            using var callbacks = NavigationCallbackScope.ProtectCancellation();
            try { cancellation.Cancel(); }
            catch (AggregateException exception)
            { Volatile.Write(ref cancellationErrors, exception.Flatten().InnerExceptions); }
            finally
            {
                Volatile.Write(ref cancellationState, 2);
                Volatile.Read(ref cancellationCompletion)?.TrySetResult();
            }
            return Task.CompletedTask;
        }
        if (Volatile.Read(ref cancellationState) == 2) return Task.CompletedTask;
        var signal = Volatile.Read(ref cancellationCompletion);
        if (signal == null)
        {
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal = Interlocked.CompareExchange(ref cancellationCompletion, created, null) ?? created;
        }
        if (Volatile.Read(ref cancellationState) == 2) signal.TrySetResult();
        return signal.Task;
    }

    /// <summary>
    /// Cancels lifetime work and completes cleanup once. Every caller receives the same completion task.
    /// Terminal cleanup cannot be cancelled by a caller after ownership has ended.
    /// </summary>
    public Task DismissAsync(DismissalReason dismissalReason = DismissalReason.Removed)
    {
        if (Volatile.Read(ref completion) is { IsCompleted: true } completed)
        {
            if (dismissalReason == DismissalReason.None || !Enum.IsDefined(dismissalReason))
                throw new ArgumentOutOfRangeException(nameof(dismissalReason));
            return completed;
        }
        if (NavigationCallbackScope.IsWithinLifetime(this))
            return Task.FromException(new InvalidOperationException("A lifecycle callback cannot await dismissal of its own lifetime or an ancestor."));
        MarkDismissed(dismissalReason);
        if (Volatile.Read(ref ownership)?.HasChildren == true) SignalCancellation(null);
        return StartMarkedCleanup();
    }

    private Task StartMarkedCleanup()
    {
        if (Volatile.Read(ref completion) is { } existing) return existing;
        if (Interlocked.CompareExchange(ref started, 1, 0) == 0)
        {
            var finishing = CompleteAsync();
            if (finishing.IsCompletedSuccessfully)
            {
                var error = finishing.Result;
                lock (NavigationOwnershipNode.Gate)
                {
                    if (completion == null)
                        Volatile.Write(ref completion, error == null ? Task.CompletedTask : Task.FromException(error));
                    else if (error == null) completionSource!.TrySetResult();
                    else completionSource!.TrySetException(error);
                    return completion;
                }
            }
            var waiting = ReserveCompletion();
            _ = PublishAsync(finishing, completionSource!);
            return waiting;
        }
        return ReserveCompletion();

        static async Task PublishAsync(ValueTask<Exception?> finishing, TaskCompletionSource waiting)
        {
            try
            {
                var error = await finishing.ConfigureAwait(false);
                if (error == null) waiting.TrySetResult();
                else waiting.TrySetException(error);
            }
            catch (Exception error) { waiting.TrySetException(error); }
        }
    }

    private Task ReserveCompletion()
    {
        // An independent caller can arrive before a synchronous callback returns.
        // Reserve one shared task without blocking it on that callback.
        lock (NavigationOwnershipNode.Gate)
        {
            if (completion == null)
            {
                completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref completion, completionSource.Task);
            }
            return completion;
        }
    }

    internal bool Contains(NavigationLifetime lifetime) => ReferenceEquals(this, lifetime)
        || Volatile.Read(ref ownership)?.Contains(lifetime) == true;

    internal IDisposable EnterCallback() => NavigationCallbackScope.Enter(this);

    internal bool HasViewFinalizer => owner is INavigationLifetimeFinalizer;
    internal Task ViewDetachment => ((INavigationLifetimeFinalizer)owner!).ViewDetachment;

    internal void DetachView()
    {
        if (NavigationCallbackScope.IsActive(this))
        {
            ((INavigationLifetimeFinalizer)owner!).DetachView();
            return;
        }
        using var callback = EnterCallback();
        ((INavigationLifetimeFinalizer)owner!).DetachView();
    }

    internal void ThrowIfExecutingCallback()
    {
        if (NavigationCallbackScope.IsActive(this))
            throw new InvalidOperationException("A lifecycle callback cannot await another lifecycle operation on its own entry.");
    }

    private async ValueTask<Exception?> CompleteAsync()
    {
        List<Exception>? failures = null;
        var node = Volatile.Read(ref ownership);
        using var callback = NavigationCallbackScope.EnterCleanup(this);
        try
        {
            await CancelWork();
            if (node != null)
                foreach (var child in node.Children)
                    try
                    {
                        callback.ObserveViewDetachment(child.Lifetime);
                        await child.Lifetime.StartMarkedCleanup();
                    }
                    catch (Exception error) { (failures ??= []).Add(error); }
            try { await (statefulCleanup != null ? statefulCleanup(owner) : cleanup!()); }
            catch (Exception error) { (failures ??= []).Add(error); }
            if (node != null)
                foreach (var release in node.CleanupCallbacks)
                    try { await release(); }
                    catch (Exception error) { (failures ??= []).Add(error); }
        }
        finally
        {
            if (HasViewFinalizer) callback.FinishOrDeferViewDetachment(this, ref failures);
            node?.Complete();
            cancellation.Dispose();
        }
        callback.FinishViewDetachments(ref failures);
        await callback.WaitForViewDetachmentsAsync();
        return failures == null ? null : failures.Count == 1 ? failures[0]
            : new AggregateException("Navigation ownership cleanup failed.", failures);
    }

}
