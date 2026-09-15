using MVVMCompass.Core;
using static MVVMCompass.Core.Tests.NavigationEntryTests;

namespace MVVMCompass.Core.Tests;

public sealed class NavigationCoordinatorTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Done<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Ordinary_requests_execute_in_arrival_order_and_return_typed_values()
    {
        var coordinator = new NavigationCoordinator();
        var release = Signal();
        var order = new List<int>();
        var first = coordinator.RunAsync(new NavigationRequest<int>(1), async context =>
        {
            order.Add(context.Parameter);
            await release.Task;
            return "first";
        }, TestContext.Current.CancellationToken);
        var second = coordinator.RunAsync(new NavigationRequest<int>(2), context => { order.Add(context.Parameter); return Task.FromResult("second"); }, TestContext.Current.CancellationToken);
        var third = coordinator.RunAsync(new NavigationRequest<int>(3), context => { order.Add(context.Parameter); return Task.FromResult("third"); }, TestContext.Current.CancellationToken);
        Assert.Equal([1], order);
        release.SetResult();
        Assert.Equal("first", (await Done(first)).Value);
        Assert.Equal("second", (await Done(second)).Value);
        Assert.Equal("third", (await Done(third)).Value);
        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public async Task Cancelled_queued_work_completes_without_waiting_for_the_active_callback()
    {
        var coordinator = new NavigationCoordinator();
        var release = Signal();
        var first = coordinator.RunAsync(new NavigationRequest<int>(0), async _ => { await release.Task; return 0; }, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var queued = coordinator.RunAsync<int, int>(new NavigationRequest<int>(1), _ => throw new InvalidOperationException("must not run"), cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(NavigationStatus.Cancelled, (await Done(queued)).Status);
        Assert.False(first.IsCompleted);
        release.SetResult();
        Assert.True((await Done(first)).IsSuccess);
    }

    [Fact]
    public async Task Already_cancelled_required_work_does_not_supersede_an_existing_operation()
    {
        var coordinator = new NavigationCoordinator();
        var release = Signal();
        var first = coordinator.RunAsync(new NavigationRequest<int>(0), async context => { await release.Task; context.BeginCommit(); return 0; }, TestContext.Current.CancellationToken);
        var required = await coordinator.RunAsync(new NavigationRequest<int>(1) { Priority = NavigationPriority.Required }, _ => Task.FromResult(1), new CancellationToken(true));
        Assert.Equal(NavigationStatus.Cancelled, required.Status);
        release.SetResult();
        Assert.True((await Done(first)).IsSuccess);
    }

    [Fact]
    public async Task Cancellation_abandons_owned_candidates_and_reports_throwing_token_callbacks()
    {
        var coordinator = new NavigationCoordinator();
        using var cancellation = new CancellationTokenSource();
        var candidate = coordinator.CreateEntry(new Model());
        var operation = coordinator.RunAsync<int, int>(new(0), async context =>
        {
            context.Own(candidate);
            using var registration = context.CancellationToken.Register(() => throw new InvalidOperationException("token callback"));
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return 0;
        }, cancellation.Token);
        cancellation.Cancel();
        var result = await Done(operation);
        Assert.Equal(NavigationStatus.Cancelled, result.Status);
        Assert.Equal("token callback", Assert.Single(result.CleanupErrors).Message);
        Assert.Equal(DismissalReason.PreparationFailed, candidate.Lifetime.Reason);
        Assert.Equal(1, candidate.ViewModel.Dismissals);
        Assert.True((await coordinator.RunAsync(new NavigationRequest<int>(1), _ => Task.FromResult(1), TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Required_requests_supersede_uncommitted_normal_work_and_keep_required_arrival_order()
    {
        var coordinator = new NavigationCoordinator();
        var release = Signal();
        var order = new List<string>();
        var candidate = coordinator.CreateEntry(new Model());
        var normal = coordinator.RunAsync(new NavigationRequest<int>(0), async context =>
        {
            context.Own(candidate);
            await release.Task; // Simulate preparation that does not cooperate with cancellation.
            context.BeginCommit();
            order.Add("obsolete commit");
            return 0;
        }, TestContext.Current.CancellationToken);
        var queued = coordinator.RunAsync(new NavigationRequest<int>(1), _ => { order.Add("obsolete queue"); return Task.FromResult(1); }, TestContext.Current.CancellationToken);
        var blocked = coordinator.RunAsync(new NavigationRequest<string>("blocked") { Priority = NavigationPriority.Required }, context => { order.Add(context.Parameter); context.BeginCommit(); return Task.FromResult(2); }, TestContext.Current.CancellationToken);
        var login = coordinator.RunAsync(new NavigationRequest<string>("login") { Priority = NavigationPriority.Required }, context => { order.Add(context.Parameter); context.BeginCommit(); return Task.FromResult(3); }, TestContext.Current.CancellationToken);
        var later = coordinator.RunAsync(new NavigationRequest<string>("later"), context => { order.Add(context.Parameter); return Task.FromResult(4); }, TestContext.Current.CancellationToken);
        Assert.Equal(NavigationStatus.Superseded, (await Done(queued)).Status);
        Assert.False(blocked.IsCompleted);
        release.SetResult();
        Assert.Equal(NavigationStatus.Superseded, (await Done(normal)).Status);
        Assert.True((await Done(blocked)).IsSuccess);
        Assert.True((await Done(login)).IsSuccess);
        Assert.True((await Done(later)).IsSuccess);
        Assert.Equal(["blocked", "login", "later"], order);
        Assert.Equal(DismissalReason.PreparationFailed, candidate.Lifetime.Reason);
    }

    [Fact]
    public async Task Commitment_finishes_despite_caller_or_origin_cancellation_and_required_work_waits()
    {
        var coordinator = new NavigationCoordinator();
        await using var origin = coordinator.CreateEntry(new object());
        await origin.ActivateAsync();
        using var cancellation = new CancellationTokenSource();
        var release = Signal();
        var captured = CancellationToken.None;
        var first = coordinator.RunAsync(new NavigationRequest<int>(0) { Origin = origin }, async context =>
        {
            captured = context.CancellationToken;
            context.BeginCommit();
            await release.Task;
            return 7;
        }, cancellation.Token);
        cancellation.Cancel();
        await origin.DismissAsync();
        var required = coordinator.RunAsync(new NavigationRequest<int>(1) { Priority = NavigationPriority.Required }, _ => Task.FromResult(1), TestContext.Current.CancellationToken);
        Assert.False(captured.IsCancellationRequested);
        Assert.False(required.IsCompleted);
        release.SetResult();
        var completed = await Done(first);
        Assert.True(completed.IsSuccess);
        Assert.True(completed.HasCommitted);
        Assert.Equal(7, completed.Value);
        Assert.True((await Done(required)).IsSuccess);
    }

    [Fact]
    public async Task Coalescing_replaces_matching_normal_requests_without_losing_unrelated_work()
    {
        var coordinator = new NavigationCoordinator();
        var release = Signal();
        var values = new List<int>();
        var first = coordinator.RunAsync(new NavigationRequest<int>(1) { CoalescingKey = "menu" }, async context => { await release.Task; context.BeginCommit(); return 1; }, TestContext.Current.CancellationToken);
        var other = coordinator.RunAsync(new NavigationRequest<int>(2) { CoalescingKey = "other" }, context => { values.Add(context.Parameter); return Task.FromResult(2); }, TestContext.Current.CancellationToken);
        var middle = coordinator.RunAsync(new NavigationRequest<int>(3) { CoalescingKey = "menu" }, _ => Task.FromResult(3), TestContext.Current.CancellationToken);
        var newest = coordinator.RunAsync(new NavigationRequest<int>(4) { CoalescingKey = "menu" }, context => { values.Add(context.Parameter); return Task.FromResult(4); }, TestContext.Current.CancellationToken);
        Assert.Equal(NavigationStatus.Superseded, (await Done(middle)).Status);
        release.SetResult();
        Assert.Equal(NavigationStatus.Superseded, (await Done(first)).Status);
        Assert.True((await Done(other)).IsSuccess);
        Assert.True((await Done(newest)).IsSuccess);
        Assert.Equal([2, 4], values);
    }

    [Fact]
    public async Task Dismissed_origins_cancel_queued_and_active_work()
    {
        var coordinator = new NavigationCoordinator();
        var origin = coordinator.CreateEntry(new object());
        await origin.ActivateAsync();
        var active = coordinator.RunAsync(new NavigationRequest<int>(0) { Origin = origin }, async context => { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); return 0; }, TestContext.Current.CancellationToken);
        var queued = coordinator.RunAsync(new NavigationRequest<int>(1) { Origin = origin }, _ => Task.FromResult(1), TestContext.Current.CancellationToken);
        await origin.DismissAsync();
        Assert.Equal(NavigationStatus.InvalidOrigin, (await Done(active)).Status);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await Done(queued)).Status);
        var late = await coordinator.RunAsync(new NavigationRequest<int>(2) { Origin = origin }, _ => Task.FromResult(2), TestContext.Current.CancellationToken);
        Assert.Equal(NavigationStatus.InvalidOrigin, late.Status);
    }

    [Fact]
    public async Task Foreign_and_inactive_origins_cannot_submit_work()
    {
        var coordinator = new NavigationCoordinator();
        await using var local = coordinator.CreateEntry(new object());
        await using var foreign = new NavigationCoordinator().CreateEntry(new object());
        await foreign.ActivateAsync();
        Assert.Equal(NavigationStatus.InvalidOrigin, (await coordinator.RunAsync(new NavigationRequest<int>(0) { Origin = local }, _ => Task.FromResult(0), TestContext.Current.CancellationToken)).Status);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await coordinator.RunAsync(new NavigationRequest<int>(1) { Origin = foreign }, _ => Task.FromResult(1), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Reactivating_a_retained_entry_does_not_revalidate_work_from_its_previous_activation()
    {
        var coordinator = new NavigationCoordinator();
        await using var origin = coordinator.CreateEntry(new object());
        await origin.ActivateAsync();
        var release = Signal();
        var active = coordinator.RunAsync(new NavigationRequest<int>(0), async _ => { await release.Task; return 0; }, TestContext.Current.CancellationToken);
        var queued = coordinator.RunAsync(new NavigationRequest<int>(1) { Origin = origin }, _ => Task.FromResult(1), TestContext.Current.CancellationToken);
        await origin.DeactivateAsync();
        await origin.ActivateAsync();
        release.SetResult();
        Assert.True((await Done(active)).IsSuccess);
        Assert.Equal(NavigationStatus.InvalidOrigin, (await Done(queued)).Status);
        Assert.True((await coordinator.RunAsync(new NavigationRequest<int>(2) { Origin = origin }, _ => Task.FromResult(2), TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Same_coordinator_and_cross_coordinator_cycles_are_rejected_without_deadlocking()
    {
        var first = new NavigationCoordinator();
        var second = new NavigationCoordinator();
        var result = await Done(first.RunAsync(new NavigationRequest<int>(0), async _ =>
        {
            var nested = await first.RunAsync(new NavigationRequest<int>(1), _ => Task.FromResult(1), TestContext.Current.CancellationToken);
            Assert.Equal(NavigationStatus.Reentrant, nested.Status);
            var cycle = await second.RunAsync(new NavigationRequest<int>(2), async _ =>
                await first.RunAsync(new NavigationRequest<int>(3), _ => Task.FromResult(3), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.Equal(NavigationStatus.Reentrant, cycle.Value!.Status);
            return 4;
        }, TestContext.Current.CancellationToken));
        Assert.Equal(4, result.Value);
    }

    [Fact]
    public async Task Cross_coordinator_cycles_are_rejected_when_the_dispatcher_does_not_flow_execution_context()
    {
        var token = TestContext.Current.CancellationToken;
        var first = new NavigationCoordinator();
        var second = new NavigationCoordinator(callback =>
        {
            using (ExecutionContext.SuppressFlow()) return Task.Run(callback, token);
        });
        var outcome = await Done(first.RunAsync(new NavigationRequest<int>(0), async _ =>
        {
            var child = await second.RunAsync(new NavigationRequest<int>(1), async _ =>
                await first.RunAsync(new NavigationRequest<int>(2), _ => Task.FromResult(2), token), token);
            Assert.Equal(NavigationStatus.Reentrant, child.Value!.Status);
            return 3;
        }, token));
        Assert.True(outcome.IsSuccess, outcome.Error?.ToString());
    }

    [Fact]
    public async Task Deferred_callbacks_are_allowed_after_their_captured_execution_frame_has_finished()
    {
        var coordinator = new NavigationCoordinator();
        var release = Signal();
        Task<NavigationOutcome<int>>? deferred = null;
        await coordinator.RunAsync(new NavigationRequest<int>(0), _ =>
        {
            deferred = Task.Run(async () => { await release.Task; return await coordinator.RunAsync(new NavigationRequest<int>(1), _ => Task.FromResult(1), TestContext.Current.CancellationToken); }, TestContext.Current.CancellationToken);
            return Task.FromResult(0);
        }, TestContext.Current.CancellationToken);
        release.SetResult();
        Assert.True((await Done(deferred!)).IsSuccess);
    }

    [Fact]
    public async Task Abandonment_marks_every_candidate_then_cleans_in_reverse_order_despite_failures()
    {
        var coordinator = new NavigationCoordinator();
        var order = new List<int>();
        NavigationEntry<object>[] entries = [];
        entries = Enumerable.Range(0, 3).Select(index => coordinator.CreateEntry(new object(), async () =>
        {
            Assert.All(entries, entry => Assert.Equal(NavigationEntryState.Dismissed, entry.State));
            order.Add(index);
            var nested = await coordinator.RunAsync(new NavigationRequest<int>(0), _ => Task.FromResult(0), TestContext.Current.CancellationToken);
            Assert.Equal(NavigationStatus.Reentrant, nested.Status);
            if (index == 2) throw new InvalidOperationException("cleanup");
        })).ToArray();
        var original = new ArgumentException("preparation");
        var result = await coordinator.RunAsync<int, int>(new(0), context =>
        {
            foreach (var entry in entries) context.Own(entry);
            context.Own(entries[0]);
            throw original;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.Same(original, result.Error);
        Assert.Single(result.CleanupErrors);
        Assert.Equal([2, 1, 0], order);
    }

    [Fact]
    public async Task Typed_preparation_teardown_commitment_and_activation_transfer_ownership()
    {
        var coordinator = new NavigationCoordinator();
        var old = coordinator.CreateEntry(new Model());
        await old.ActivateAsync();
        var result = await coordinator.RunAsync(new NavigationRequest<Input>(new("new-document")) { Origin = old }, async context =>
        {
            var next = context.Own(coordinator.CreateEntry(new Model()));
            await next.InitializeAsync(context.Parameter, context.CancellationToken);
            Assert.Equal(NavigationEntryState.Active, old.State);
            context.BeginCommit();
            await old.DismissAsync(DismissalReason.RootReplaced);
            await next.ActivateAsync();
            return next;
        }, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.True(result.HasCommitted);
        Assert.Equal("new-document", result.Value!.ViewModel.Value);
        Assert.Equal(NavigationEntryState.Active, result.Value.State);
        Assert.Equal(DismissalReason.RootReplaced, old.Lifetime.Reason);
        await result.Value.DisposeAsync();
    }

    [Fact]
    public async Task Failure_after_commitment_retains_the_candidate_for_explicit_host_recovery()
    {
        var coordinator = new NavigationCoordinator();
        var candidate = coordinator.CreateEntry(new Model());
        var error = new InvalidOperationException("presentation");
        var result = await coordinator.RunAsync<int, int>(new(0), context =>
        {
            context.Own(candidate);
            context.BeginCommit();
            throw error;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.True(result.HasCommitted);
        Assert.Same(error, result.Error);
        Assert.False(candidate.Lifetime.IsDismissed);
        await candidate.DisposeAsync();
        Assert.True((await coordinator.RunAsync(new NavigationRequest<int>(1), _ => Task.FromResult(1), TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Successful_uncommitted_work_releases_temporary_entries_before_completing()
    {
        var coordinator = new NavigationCoordinator();
        var entry = coordinator.CreateEntry(new Model());
        var result = await coordinator.RunAsync(new NavigationRequest<int>(0), context => { context.Own(entry); return Task.FromResult(7); }, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
        Assert.Equal(DismissalReason.PreparationFailed, entry.Lifetime.Reason);
    }

    [Fact]
    public async Task Cleanup_failure_cannot_be_reported_as_success()
    {
        var coordinator = new NavigationCoordinator();
        var entry = coordinator.CreateEntry(new object(), () => throw new InvalidOperationException("cleanup"));
        var result = await coordinator.RunAsync(new NavigationRequest<int>(0), context => { context.Own(entry); return Task.FromResult(7); }, TestContext.Current.CancellationToken);
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.Single(result.CleanupErrors);
    }

    [Fact]
    public async Task Expired_context_cannot_commit_or_take_new_ownership_and_its_token_remains_readable()
    {
        var coordinator = new NavigationCoordinator();
        NavigationOperationContext<int>? captured = null;
        await coordinator.RunAsync(new NavigationRequest<int>(0), context => { captured = context; return Task.FromResult(0); }, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => captured!.BeginCommit());
        Assert.False(captured!.CancellationToken.IsCancellationRequested);
        await using var candidate = coordinator.CreateEntry(new object());
        Assert.Throws<InvalidOperationException>(() => captured.Own(candidate));
    }

    [Fact]
    public async Task Busy_rejection_is_an_explicit_alternative_to_queueing()
    {
        var coordinator = new NavigationCoordinator();
        var release = Signal();
        var active = coordinator.RunAsync(new NavigationRequest<int>(0), async _ => { await release.Task; return 0; }, TestContext.Current.CancellationToken);
        var rejected = await coordinator.RunAsync(new NavigationRequest<int>(1) { RejectIfBusy = true }, _ => Task.FromResult(1), TestContext.Current.CancellationToken);
        Assert.Equal(NavigationStatus.Busy, rejected.Status);
        release.SetResult();
        await Done(active);
        Assert.True((await coordinator.RunAsync(new NavigationRequest<int>(2) { RejectIfBusy = true }, _ => Task.FromResult(2), TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task Dispatcher_wraps_the_callback_and_automatic_cleanup_and_failures_do_not_poison_the_queue()
    {
        var calls = 0;
        var inside = false;
        var coordinator = new NavigationCoordinator(async callback =>
        {
            if (++calls == 1) throw new InvalidOperationException("dispatcher unavailable");
            inside = true;
            try { await callback(); }
            finally { inside = false; }
        });
        var failure = await coordinator.RunAsync(new NavigationRequest<int>(0), _ => Task.FromResult(0), TestContext.Current.CancellationToken);
        Assert.Equal("dispatcher unavailable", failure.Error!.Message);
        var entry = coordinator.CreateEntry(new object(), () => { Assert.True(inside); return Task.CompletedTask; });
        var next = await coordinator.RunAsync(new NavigationRequest<int>(1), async context =>
        {
            context.Own(entry);
            await Task.Yield();
            Assert.True(inside);
            return 1;
        }, TestContext.Current.CancellationToken);
        Assert.True(next.IsSuccess);
        Assert.False(inside);
    }

    [Fact]
    public async Task Concurrent_submitters_never_execute_callbacks_at_the_same_time()
    {
        var coordinator = new NavigationCoordinator();
        var executing = 0;
        var operations = Enumerable.Range(0, 32).Select(value => Task.Run(() => coordinator.RunAsync(new NavigationRequest<int>(value), async context =>
        {
            Assert.Equal(1, Interlocked.Increment(ref executing));
            await Task.Yield();
            Assert.Equal(0, Interlocked.Decrement(ref executing));
            return context.Parameter;
        }, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.ToString()));
        Assert.Equal(Enumerable.Range(0, 32), results.Select(result => result.Value).Order());
    }
}
