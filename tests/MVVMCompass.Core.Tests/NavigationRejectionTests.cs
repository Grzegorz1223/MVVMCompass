namespace MVVMCompass.Core.Tests;

public sealed class NavigationRejectionTests
{
    [Theory]
    [InlineData(NavigationStatus.GuardRejected)]
    [InlineData(NavigationStatus.InvalidOrigin)]
    public async Task Adapter_rejection_abandons_owned_candidates_without_committing(NavigationStatus status)
    {
        var coordinator = new NavigationCoordinator();
        var cleaned = 0;
        var entry = coordinator.CreateEntry(new object(), () => { cleaned++; return Task.CompletedTask; });
        var outcome = await coordinator.RunAsync(new NavigationRequest<int>(42), context =>
        {
            context.Own(entry);
            context.Reject(status);
            return Task.FromResult(false);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(status, outcome.Status);
        Assert.False(outcome.HasCommitted);
        Assert.Null(outcome.Error);
        Assert.Equal(1, cleaned);
        Assert.Equal(DismissalReason.PreparationFailed, entry.Lifetime.Reason);
    }

    [Fact]
    public async Task Rejection_cannot_undo_commitment()
    {
        var outcome = await new NavigationCoordinator().RunAsync(new NavigationRequest<int>(0), context =>
        {
            context.BeginCommit();
            context.Reject(NavigationStatus.GuardRejected);
            return Task.FromResult(false);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(NavigationStatus.Failed, outcome.Status);
        Assert.True(outcome.HasCommitted);
        Assert.IsType<InvalidOperationException>(outcome.Error);
    }
}
