using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private VisualElement? ResolveOwnedView(ViewModelBase model) => ownedPages
        .FirstOrDefault(page => ReferenceEquals(page.Entry.ViewModel, model))?.Content
        ?? retainedItems.FirstOrDefault(item => ReferenceEquals(item.Entry?.ViewModel, model))?.Anchor;

    private async Task ComposeRetainedAsync(ViewModelBase model, Func<Task> build)
    {
        // BeforeFirstShown on a detached candidate already uses its preparation. Live helpers
        // enter the window queue, and may also run inside an already committed activation.
        for (var frame = execution.Value; frame != null; frame = frame.Parent)
            if (frame.Host == this && frame.Executing && frame.Committed?.Invoke() == true)
            {
                await ComposeCoreAsync(model, build);
                return;
            }
        var result = await RunContainerAsync(new NavigationRequest<object?>(null), MauiNavigationOperation.ComposeRetained,
            async (context, snapshot, callbacks) =>
            {
                var view = ResolveOwnedView(model) ?? throw new InvalidOperationException("The composition owner is no longer owned.");
                await CheckGuardsAsync(EntriesIn(SelectedVisual(view)).Select(entry => entry.ViewModel), context);
                ValidateSnapshot(snapshot);
                // Legacy helpers mutate retained controls. Cancellation ends at this explicit boundary.
                context.BeginCommit();
                await ComposeCoreAsync(model, build);
                return true;
            }, CancellationToken.None);
        if (!Accepted(result)) throw new InvalidOperationException($"Retained composition was rejected: {result.Status}.");
    }

    private async Task ComposeCoreAsync(ViewModelBase model, Func<Task> build)
    {
        var view = ResolveOwnedView(model) ?? throw new InvalidOperationException("The composition owner is no longer owned.");
        if (model.IsDismissed) throw new InvalidOperationException("The composition owner has been dismissed.");
        var owner = ownedPages.FirstOrDefault(page => ReferenceEquals(page.Content, view))
            ?? retainedItems.First(item => ReferenceEquals(item.Anchor, view)).Owner;
        var oldItems = retainedItems.ToArray();
        var oldModels = ViewModelTree.Collect(view).Where(item => !ReferenceEquals(item, model)).ToArray();
        var oldSelection = SelectedVisual(view);
        var oldBranch = EntriesIn(oldSelection);
        var oldTabs = (view as TabbedPage)?.Children.ToArray();
        var oldCount = (view as ICustomTabbedViewBase)?.ChildCount ?? 0;
        var oldMenu = (view as FlyoutPage)?.Flyout;
        var bridge = RetainedNavigationBridge.Find(view);
        using var preparation = new RootPreparation(MauiNavigationHostFactory.ExistingModels(Window));
        model.PendingRootPreparation = preparation;
        var activating = false;
        if (bridge != null) { bridge.Composing = true; bridge.PreparedTabIndex = null; }
        try
        {
            await build();
            DiscoverRetained(owner);
            var custom = view as ICustomTabbedViewBase;
            var selected = custom != null && bridge?.PreparedTabIndex is int index
                ? custom.Children[index].View : SelectedVisual(view);
            if (!ReferenceEquals(oldSelection, selected)) await DeactivateEntriesAsync(oldBranch);

            var currentModels = ViewModelTree.Collect(view);
            var removedModels = oldModels.Except(currentModels).ToArray();
            var removedItems = oldItems.Where(item => item.Entry?.ViewModel is ViewModelBase legacy && removedModels.Contains(legacy)).ToArray();
            activating = true;
            try { await NavigationLifetimeGroup.DismissAsync(removedItems.Where(item => item.Entry != null).Select(item => item.Entry!.Lifetime)
                .Concat(removedModels.Select(item => item.Lifetime)), DismissalReason.Removed); }
            catch (Exception error) { NavigationDiagnostics.Report(error, "Retained composition cleanup"); }
            composition.UnwireScopedSubscriptions(removedModels);
            ForgetRetained(removedItems);
            activating = true;
            preparation.FinishPreparation();
            if (bridge != null) bridge.Applying = true;
            if (custom != null && bridge?.PreparedTabIndex is int targetIndex)
                await custom.SwitchToAsync(targetIndex);
            await preparation.ActivateAsync();
            await ActivateVisibleEntriesAsync();
        }
        catch
        {
            if (!activating)
            {
                if (view is TabbedPage tabs && oldTabs != null)
                {
                    foreach (var added in tabs.Children.Except(oldTabs).ToArray()) tabs.Children.Remove(added);
                    tabs.CurrentPage = oldSelection as Page;
                }
                if (view is IRetainedTabMaintenance maintenance) maintenance.RemoveChildrenAfter(oldCount);
                if (view is FlyoutPage flyout) { flyout.Flyout = oldMenu; flyout.Detail = oldSelection as Page; }
                await preparation.AbandonAsync();
                composition.UnwireScopedSubscriptions(preparation.OwnedModels);
                ForgetRetained(retainedItems.Except(oldItems).ToArray());
                await ActivateVisibleEntriesAsync(notifyRetained: true);
            }
            throw;
        }
        finally
        {
            model.PendingRootPreparation = null;
            if (bridge != null) { bridge.Applying = false; bridge.Composing = false; bridge.PreparedTabIndex = null; }
            foreach (var state in retainedContainers)
            {
                state.Value.Selected = SelectedIdentity(state.Key);
                if (state.Key is FlyoutPage flyout && state.Value.Menu != flyout.Flyout)
                {
                    if (state.Value.Menu != null) RetainedNavigationBridge.Release(state.Value.Menu);
                    state.Value.Menu = flyout.Flyout;
                    if (flyout.Flyout != null) RetainedNavigationBridge.Bind(flyout.Flyout, state.Value.Bridge);
                }
            }
            BindRetainedContainers();
        }
    }
}
