namespace MVVMCompass.Core;

/// <summary>Serializes host operations, rejects reentrancy and owns pre-commit cancellation policy.</summary>
public sealed class NavigationCoordinator
{
    private static readonly AsyncLocal<ExecutionFrame?> execution = new();
    private readonly object gate = new();
    private readonly List<Operation> pending = [];
    private readonly Func<Func<Task>, Task> dispatch;
    private Operation? active;
    private bool draining;

    /// <summary>Creates a coordinator. UI hosts supply a dispatcher that awaits the complete callback.</summary>
    public NavigationCoordinator(Func<Func<Task>, Task>? dispatch = null) => this.dispatch = dispatch ?? (callback => callback());

    /// <summary>Gets the identity used to reject requests from another host's entries.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    internal bool IsBusy { get { lock (gate) return active != null || pending.Count != 0; } }

    /// <summary>Creates a prepared entry. The caller owns it until an operation explicitly takes ownership.</summary>
    public NavigationEntry<TViewModel> CreateEntry<TViewModel>(TViewModel viewModel, Func<Task>? cleanup = null) where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return new NavigationEntry<TViewModel>(Id, viewModel, cleanup);
    }

    /// <summary>
    /// Attaches entry state to an existing lifetime. The adapter owns all lifecycle callbacks;
    /// INavigationAware is not invoked by this entry. Typed initialization remains available.
    /// </summary>
    public NavigationEntry<TViewModel> AttachEntry<TViewModel>(TViewModel viewModel, NavigationLifetime lifetime) where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(lifetime);
        if (lifetime.IsDismissed) throw new InvalidOperationException("A dismissed lifetime cannot be attached.");
        return new NavigationEntry<TViewModel>(Id, viewModel, lifetime);
    }

    /// <summary>Runs typed work under the request's admission, cancellation and ownership contract.</summary>
    public Task<NavigationOutcome<TResult>> RunAsync<TParameter, TResult>(NavigationRequest<TParameter> request,
        Func<NavigationOperationContext<TParameter>, Task<TResult>> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callback);
        if (!Enum.IsDefined(request.Priority)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.CoalescingKey != null && (string.IsNullOrWhiteSpace(request.CoalescingKey) || request.Priority != NavigationPriority.Normal))
            throw new ArgumentException("Only normal requests can have a nonempty coalescing key.", nameof(request));
        if (NavigationCallbackScope.IsActive(this))
            return Task.FromResult(new NavigationOutcome<TResult>(NavigationStatus.Reentrant, default, false));
        for (var frame = execution.Value; frame != null; frame = frame.Parent)
            if (ReferenceEquals(frame.Coordinator, this) && Volatile.Read(ref frame.Executing))
                return Task.FromResult(new NavigationOutcome<TResult>(NavigationStatus.Reentrant, default, false));
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new NavigationOutcome<TResult>(NavigationStatus.Cancelled, default, false));

        Operation<TParameter, TResult> operation;
        List<Operation> stopped = [];
        var start = false;
        lock (gate)
        {
            if (!ValidOrigin(request.Origin))
                return Task.FromResult(new NavigationOutcome<TResult>(NavigationStatus.InvalidOrigin, default, false));
            if (request.RejectIfBusy && (active != null || pending.Count != 0))
                return Task.FromResult(new NavigationOutcome<TResult>(NavigationStatus.Busy, default, false));
            operation = new Operation<TParameter, TResult>(this, request, callback);
            foreach (var older in pending.Concat(active == null ? [] : new[] { active }).ToArray())
            {
                if (older.Priority != NavigationPriority.Normal) continue;
                if (request.Priority == NavigationPriority.Required ||
                    (request.CoalescingKey != null && request.CoalescingKey == older.CoalescingKey))
                    if (MarkStopped(older, NavigationStatus.Superseded)) stopped.Add(older);
            }
            pending.Add(operation);
            if (!draining) { draining = true; start = true; }
        }
        operation.Bind(cancellationToken, request.Origin?.Lifetime.Token ?? default);
        foreach (var older in stopped) SignalStop(older);
        if (start) _ = DrainAsync();
        return operation.Completion.Task;
    }

    private bool ValidOrigin(NavigationEntry? origin) => origin == null ||
        (origin.OwnerId == Id && origin.State == NavigationEntryState.Active);

    private async Task DrainAsync()
    {
        while (true)
        {
            Operation operation;
            lock (gate)
            {
                if (pending.Count == 0) { draining = false; return; }
                var index = pending.FindIndex(item => item.Priority == NavigationPriority.Required);
                if (index < 0) index = 0;
                operation = pending[index];
                pending.RemoveAt(index);
                active = operation;
            }
            try { await dispatch(operation.ExecuteAsync); }
            catch (Exception error) { await operation.DispatchFailedAsync(error); }
            lock (gate) { active = null; operation.Finished = true; }
            operation.Publish();
        }
    }

    // Called under gate; cancellation callbacks run only after releasing the coordinator lock.
    private bool MarkStopped(Operation operation, NavigationStatus reason)
    {
        if (operation.Committed || operation.Completing || operation.Finished || operation.StopReason != null) return false;
        operation.StopReason = reason;
        operation.RemovedFromQueue = pending.Remove(operation);
        return true;
    }

    private void Stop(Operation operation, NavigationStatus reason)
    {
        lock (gate) { if (!MarkStopped(operation, reason)) return; }
        SignalStop(operation);
    }

    private void SignalStop(Operation operation)
    {
        using var callback = EnterCallback();
        using var cancellation = NavigationCallbackScope.ProtectCancellation();
        try { operation.Cancellation.Cancel(); }
        catch (AggregateException error) { operation.Diagnostics.AddRange(error.Flatten().InnerExceptions); }
        finally { operation.CancellationCompleted.TrySetResult(); }
        if (operation.RemovedFromQueue)
        {
            lock (gate) operation.Finished = true;
            operation.Publish();
        }
    }

    internal bool IsCommitted(Operation operation) { lock (gate) return operation.Committed; }

    internal void Own(Operation operation, NavigationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (gate)
        {
            EnsureExecuting(operation);
            if (operation.Committed || entry.OwnerId != Id || entry.State != NavigationEntryState.Prepared)
                throw new InvalidOperationException("Only prepared entries from this coordinator can be owned before commitment.");
            if (!operation.Entries.Contains(entry)) operation.Entries.Add(entry);
        }
    }

    internal void BeginCommit(Operation operation)
    {
        lock (gate)
        {
            EnsureExecuting(operation);
            if (operation.Committed) return;
            CheckInterruption(operation);
            operation.Committed = true;
        }
    }

    internal void Reject(Operation operation, NavigationStatus status)
    {
        if (status is not (NavigationStatus.GuardRejected or NavigationStatus.InvalidOrigin or NavigationStatus.DestinationNotFound
            or NavigationStatus.DestinationUnavailable or NavigationStatus.AmbiguousDestination))
            throw new ArgumentOutOfRangeException(nameof(status));
        lock (gate)
        {
            EnsureExecuting(operation);
            if (operation.Committed) throw new InvalidOperationException("Committed navigation cannot be rejected.");
            CheckInterruption(operation);
            throw new InterruptedException(status);
        }
    }

    private void EnsureExecuting(Operation operation)
    {
        if (operation.Completing || operation.Finished || !ReferenceEquals(active, operation))
            throw new InvalidOperationException("The navigation operation context is no longer executing.");
    }

    private void CheckInterruption(Operation operation)
    {
        if (operation.StopReason is { } reason) throw new InterruptedException(reason);
        if (!ValidOrigin(operation.Origin) || operation.Origin?.ActivationVersion != operation.OriginVersion) throw new InterruptedException(NavigationStatus.InvalidOrigin);
    }

    private sealed class InterruptedException(NavigationStatus status) : Exception
    {
        internal NavigationStatus Status { get; } = status;
    }

    internal IDisposable EnterCallback() => NavigationCallbackScope.Enter(this);

    internal sealed class ExecutionFrame(NavigationCoordinator coordinator, ExecutionFrame? parent) : IDisposable
    {
        internal NavigationCoordinator Coordinator { get; } = coordinator;
        internal ExecutionFrame? Parent { get; } = parent;
        internal bool Executing = true;
        public void Dispose()
        {
            Volatile.Write(ref Executing, false);
            execution.Value = Parent;
        }
    }

    internal abstract class Operation
    {
        protected Operation(NavigationCoordinator coordinator, NavigationEntry? origin, NavigationPriority priority, string? coalescingKey)
        {
            Coordinator = coordinator;
            Origin = origin;
            OriginVersion = origin?.ActivationVersion;
            Priority = priority;
            CoalescingKey = coalescingKey;
            Token = Cancellation.Token;
        }
        internal NavigationCoordinator Coordinator { get; }
        // Keep logical ancestry even when a UI dispatcher does not flow ExecutionContext.
        internal ExecutionFrame? ParentFrame { get; } = execution.Value;
        internal NavigationEntry? Origin { get; }
        internal int? OriginVersion { get; }
        internal NavigationPriority Priority { get; }
        internal string? CoalescingKey { get; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal CancellationToken Token { get; }
        internal TaskCompletionSource CancellationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<NavigationEntry> Entries { get; } = [];
        internal List<Exception> Diagnostics { get; } = [];
        internal NavigationStatus? StopReason;
        internal bool Committed;
        internal bool Finished;
        internal bool Completing;
        internal bool RemovedFromQueue;
        private CancellationTokenRegistration callerRegistration;
        private CancellationTokenRegistration originRegistration;

        internal void Bind(CancellationToken caller, CancellationToken lifetime)
        {
            var callerBinding = caller.Register(() => Coordinator.Stop(this, NavigationStatus.Cancelled));
            var originBinding = lifetime.Register(() => Coordinator.Stop(this, NavigationStatus.InvalidOrigin));
            lock (Coordinator.gate)
            {
                if (Finished) { callerBinding.Unregister(); originBinding.Unregister(); }
                else { callerRegistration = callerBinding; originRegistration = originBinding; }
            }
        }

        internal void Unbind()
        {
            lock (Coordinator.gate)
            {
                callerRegistration.Unregister();
                originRegistration.Unregister();
            }
            Cancellation.Dispose();
        }

        internal abstract Task ExecuteAsync();
        internal abstract Task DispatchFailedAsync(Exception error);
        internal abstract void Publish();
    }

    private sealed class Operation<TParameter, TResult>(NavigationCoordinator coordinator, NavigationRequest<TParameter> request,
        Func<NavigationOperationContext<TParameter>, Task<TResult>> callback)
        : Operation(coordinator, request.Origin, request.Priority, request.CoalescingKey)
    {
        internal TaskCompletionSource<NavigationOutcome<TResult>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private NavigationOutcome<TResult>? outcome;

        internal override async Task ExecuteAsync()
        {
            using var callbackScope = Coordinator.EnterCallback();
            var previous = execution.Value;
            var frame = new ExecutionFrame(Coordinator, ParentFrame);
            execution.Value = frame;
            TResult? value = default;
            Exception? failure = null;
            var status = NavigationStatus.Completed;
            try
            {
                lock (Coordinator.gate) Coordinator.CheckInterruption(this);
                value = await callback(new NavigationOperationContext<TParameter>(request.Parameter, this));
                lock (Coordinator.gate) { if (!Committed) Coordinator.CheckInterruption(this); }
            }
            catch (InterruptedException error) { status = error.Status; }
            catch (Exception error)
            {
                lock (Coordinator.gate) status = !Committed && StopReason is { } reason ? reason : NavigationStatus.Failed;
                if (error is not OperationCanceledException || status == NavigationStatus.Failed) failure = error;
            }
            lock (Coordinator.gate)
            {
                Completing = true;
                if (!Committed && StopReason is { } reason) status = reason;
            }
            try
            {
                if (StopReason != null) await CancellationCompleted.Task;
                if (!Committed)
                {
                    NavigationLifetime[] abandoned;
                    lock (NavigationOwnershipNode.Gate)
                    {
                        foreach (var entry in Entries) entry.Lifetime.MarkDismissed(DismissalReason.PreparationFailed);
                        // Cleanup detaches descendants. Freeze and retain their diagnostic sources
                        // before any callback runs, including children also owned directly.
                        abandoned = Entries.SelectMany(entry => entry.Ownership.Snapshot())
                            .Select(node => node.Lifetime).Distinct().ToArray();
                    }
                    foreach (var entry in Entries.AsEnumerable().Reverse())
                    {
                        try { await entry.DismissAsync(DismissalReason.PreparationFailed); }
                        catch (Exception error) { Diagnostics.Add(error); }
                    }
                    foreach (var lifetime in abandoned) Diagnostics.AddRange(lifetime.CancellationErrors);
                }
                if (status == NavigationStatus.Completed && Diagnostics.Count != 0) status = NavigationStatus.Failed;
                outcome = new(status, status == NavigationStatus.Completed ? value : default, Committed, failure, Diagnostics.ToArray());
            }
            finally
            {
                Volatile.Write(ref frame.Executing, false);
                execution.Value = previous;
            }
        }

        internal override async Task DispatchFailedAsync(Exception error)
        {
            lock (Coordinator.gate) Completing = true;
            if (StopReason != null) await CancellationCompleted.Task;
            outcome = new(NavigationStatus.Failed, default, Committed, error, Diagnostics.ToArray());
        }

        internal override void Publish()
        {
            outcome ??= new(StopReason ?? NavigationStatus.Failed, default, Committed, cleanupErrors: Diagnostics.ToArray());
            Unbind();
            Completion.TrySetResult(outcome);
        }
    }
}
