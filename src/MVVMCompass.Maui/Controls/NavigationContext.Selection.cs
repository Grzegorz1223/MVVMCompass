using MVVMCompass.Core;

namespace MVVMCompass;

internal sealed partial class NavigationContext
{
    internal Task<NavigationOutcome<NavigationScreenEntry?>> SelectPathAsync(string[] path, Dictionary<string, object>? parameters,
        NavigationRequestOptions options, CancellationToken token) => RunAsync<NavigationScreenEntry?>(options, async operation =>
    {
        var staged = new List<(NavigationContext Context, DestinationState Target, NavigationScreenEntry? Candidate)>();
        var destinationContext = this;
        for (var index = 0; index < path.Length; index++)
        {
            if (!destinationContext.items.TryGetValue(path[index], out var metadata)) operation.Reject(NavigationStatus.DestinationNotFound);
            if (!metadata!.IsEnabled) operation.Reject(NavigationStatus.DestinationUnavailable);
            var target = destinationContext.Resolve(path[index]);
            var previous = destinationContext.Current;
            if (previous != null && (target != destinationContext.active || index == path.Length - 1 && parameters != null))
                await GuardAsync([previous], operation);
            var candidate = target.Stack.Count == 0
                ? await destinationContext.PrepareAsync(target.Definition.Factory!, index == path.Length - 1 ? parameters : null,
                    entry => operation.Own(entry), operation.CancellationToken) : null;
            staged.Add((destinationContext, target, candidate));
            if (index < path.Length - 1)
            {
                var container = candidate ?? target.Stack[0];
                if (container.Children == null) operation.Reject(NavigationStatus.DestinationNotFound);
                // A qualified path addresses the container root and restores its retained active branch.
                if (target.Stack.Count > 1) operation.Reject(NavigationStatus.DestinationUnavailable);
                destinationContext = container.Children!;
            }
        }
        if (staged.All(step => step.Target == step.Context.active && step.Candidate == null) && parameters == null)
        { foreach (var step in staged) step.Context.SetFlyout(false); return Deepest.Current; }
        operation.BeginCommit();
        var last = staged[^1];
        if (last.Candidate == null && parameters != null) await last.Target.Stack[0].ViewModel.GetParameters(parameters);
        foreach (var step in staged)
        {
            if (step.Context.Current != null) await step.Context.DeactivateAsync(step.Context.Current);
            if (step.Candidate != null) step.Target.Stack.Add(step.Candidate);
            step.Context.active = step.Target;
            step.Context.RememberSelection();
            step.Context.SetFlyout(false);
            await step.Context.View.Presenter.PresentAsync(step.Context.Current!.View, false);
        }
        foreach (var step in staged) step.Context.Publish();
        await ActivateAsync(Current!);
        foreach (var step in staged) await step.Context.NotifySelectionAsync();
        return Deepest.Current;
    }, token);

    private async Task<NavigationScreenEntry?> PresentPreparedModalAsync(NavigationScreenEntry screen,
        ScreenFactory factory, NavigationOperationContext<object?> operation)
    {
        var definition = new NavigationDefinition([new("modal", screen.View.Title ?? screen.ViewModel.GetType().Name, factory)]);
        var modal = new NavigationContext(owner, definition, true);
        var entry = owner.CreateContentScope(modal);
        operation.Own(entry); modal.Attach(entry); modal.CreatePage();
        ScopeEntry.Ownership.Detach(screen.Entry.Ownership);
        entry.Ownership.Adopt(screen.Entry.Ownership);
        ownedScreens.Remove(screen); modal.ownedScreens.Add(screen);
        modal.active = modal.Resolve("modal"); modal.active.Stack.Add(screen);
        screen.View.Navigator = new(modal, screen); screen.ViewModel.Navigator = screen.View.Navigator;
        screen.ViewModel.NavigationBinding?.Bind(modal, screen);
        if (screen.Children != null) screen.Children.ParentContext = modal;
        modal.View.Presenter.Install(screen.View);
        operation.BeginCommit();
        try
        {
            await RootContext.ScopeEntry.DeactivateAsync();
            await owner.PresentContentModalAsync(modal, entry);
            await entry.ActivateAsync(); modal.Publish();
            return screen;
        }
        catch (Exception error)
        {
            await RecoverUnpresentedModalAsync(modal, entry, error);
            throw;
        }
    }

    private async Task RecoverUnpresentedModalAsync(NavigationContext modal,
        NavigationEntry<NavigationContext> entry, Exception originalError)
    {
        if (Window.Navigation.ModalStack.Contains(modal.PresentedPage)) return;
        List<Exception> errors = [originalError];
        try { await entry.DismissAsync(DismissalReason.PreparationFailed); }
        catch (Exception error) { errors.Add(error); }
        // Cleanup failure must not leave the still-visible root disabled.
        try
        {
            if (!RootContext.IsClosed)
            {
                await RootContext.ScopeEntry.ActivateAsync();
                RootContext.RefreshState();
            }
        }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 1) throw new AggregateException("Modal presentation and recovery failed.", errors);
    }

    internal async Task GuardRootAsync<T>(NavigationOperationContext<T> operation, NavigationEntry? origin)
    {
        if (!IsActive || origin != null && !ContainsActiveOrigin(origin)) operation.Reject(NavigationStatus.InvalidOrigin);
        foreach (var screen in AllScreens().OrderByDescending(item => item.Entry.State == NavigationEntryState.Active))
        {
            operation.CancellationToken.ThrowIfCancellationRequested();
            if (!await screen.ViewModel.CanNavigate()) operation.Reject(NavigationStatus.GuardRejected);
            operation.CancellationToken.ThrowIfCancellationRequested();
        }
    }
}
