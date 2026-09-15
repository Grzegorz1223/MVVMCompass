using Microsoft.Maui.Controls;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("tabs")]
    [InlineData("flyout")]
    public async Task Failed_modal_deactivation_disposes_candidate_and_restores_the_active_branch(string layout)
    {
        var context = layout switch { "tabs" => await Open<Tabs>(), "flyout" => await Open<Flyout>(), _ => await Open<Leaf>() };
        var parent = (Leaf)context.Current!.ViewModel;
        var leaf = (Leaf)context.Deepest.Current!.ViewModel;
        var error = new InvalidOperationException("Deactivation failed");
        leaf.Deactivate = () => throw error;
        var result = await parent.Navigation.NavigateTo<Modal>(cancellationToken: Token);

        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.True(result.HasCommitted);
        Assert.Same(error, result.Error);
        Assert.Empty(context.Window.Navigation.ModalStack);
        var candidate = Assert.IsType<Modal>(Modal.Last);
        Assert.True(candidate.IsDismissed);
        Assert.True(candidate.Resource.Disposed);
        Assert.Equal(1, candidate.Dismissals);
        Assert.All(context.ActiveChain(), item =>
        {
            Assert.True(item.IsActive);
            Assert.Equal(NavigationEntryState.Active, item.Current!.Entry.State);
        });

        leaf.Deactivate = null;
        Success(await parent.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        Success(await ((Leaf)context.Deepest.Current!.ViewModel).Navigation.NavigateBack(Token));
        Assert.Same(leaf, context.Deepest.Current!.ViewModel);
        await context.Host.DisposeAsync();
        Assert.Equal(1, candidate.Dismissals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Removal_skips_disabled_destinations_and_uses_current_definition_order(bool flyout, bool retained)
    {
        var (context, parent, replace) = await OpenRemovalContainer(flyout);
        Success(await replace([Item("open"), Item("disabled"), Item("earlier"), Item("later")]));
        if (retained)
        {
            Success(await parent.Navigation.Select("disabled", cancellationToken: Token));
            Success(await parent.Navigation.Select("open", cancellationToken: Token));
        }
        Success(await replace([Item("open"), Disabled("disabled"), Item("later"), Item("earlier")]));
        Assert.Equal(NavigationStatus.DestinationUnavailable, (await parent.Navigation.Select("disabled", cancellationToken: Token)).Status);
        var removed = context.Deepest.Current!.ViewModel;

        Success(await parent.Navigation.Remove("open", cancellationToken: Token));

        Assert.Equal("later", context.Current!.Children!.SelectedDestinationId);
        Assert.True(removed.IsDismissed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_with_no_enabled_replacement_preserves_selection_and_ownership(bool flyout)
    {
        var (context, parent, replace) = await OpenRemovalContainer(flyout);
        Success(await replace([Item("open"), Disabled("disabled")]));
        var current = context.Deepest.Current;

        var result = await parent.Navigation.Remove("open", cancellationToken: Token);

        Assert.Equal(NavigationStatus.DestinationUnavailable, result.Status);
        Assert.False(result.HasCommitted);
        Assert.Same(current, context.Deepest.Current);
        Assert.False(current!.ViewModel.IsDismissed);
        Assert.Equal(2, context.Current!.Children!.Destinations.Count);
        Success(await replace([Item("open"), Item("disabled")]));
        Success(await parent.Navigation.Remove("open", cancellationToken: Token));
    }

    [Theory]
    [InlineData(false, "disabled", NavigationStatus.DestinationUnavailable)]
    [InlineData(true, "disabled", NavigationStatus.DestinationUnavailable)]
    [InlineData(false, "missing", NavigationStatus.DestinationNotFound)]
    [InlineData(true, "missing", NavigationStatus.DestinationNotFound)]
    public async Task Invalid_explicit_removal_replacement_leaves_the_active_destination_intact(bool flyout, string replacement, NavigationStatus status)
    {
        var (context, parent, replace) = await OpenRemovalContainer(flyout);
        Success(await replace([Item("open"), Disabled("disabled"), Item("available")]));
        var current = context.Deepest.Current;
        var result = await parent.Navigation.Remove("open", replacement, Token);
        Assert.Equal(status, result.Status);
        Assert.False(result.HasCommitted);
        Assert.Same(current, context.Deepest.Current);
        Assert.False(current!.ViewModel.IsDismissed);
        Success(await parent.Navigation.Remove("open", "available", Token));
        Assert.Equal("available", context.Current!.Children!.SelectedDestinationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_destination_renames_update_the_existing_accessible_input(bool flyout)
    {
        var (context, _, replace) = await OpenRemovalContainer(flyout);
        Success(await replace([Item("open"), Item("archive")]));
        var child = context.Current!.Children!;
        var selector = flyout ? Descendants(child.View).OfType<NavigationSelector>()
            .Single(item => ReferenceEquals(item.ItemsSource, child.MenuItems)) : child.View.TabSelector;
        var input = Descendants(selector).OfType<Button>().Single(item => item.AutomationId == "destination-open");
        var current = context.Deepest.Current;

        foreach (var title in new[] { "Renamed", "Translated title" })
        {
            Success(await replace([new(typeof(Leaf), title, "open"), Item("archive")]));
            var updated = Descendants(selector).OfType<Button>().Single(item => item.AutomationId == "destination-open");
            Assert.Equal(title, SemanticProperties.GetDescription(updated));
            Assert.Same(current, context.Deepest.Current);
            if (!flyout) Assert.Same(input, updated);
        }
    }

    private async Task<(NavigationContext Context, Leaf Parent, Func<IEnumerable<NavigationItem>, Task<NavigationResult>> Replace)> OpenRemovalContainer(bool flyout)
    {
        if (flyout)
        {
            var context = await Open<Flyout>();
            var parent = (Flyout)context.Current!.ViewModel;
            return (context, parent, parent.Replace);
        }
        else
        {
            var context = await Open<Tabs>();
            var parent = (Tabs)context.Current!.ViewModel;
            return (context, parent, parent.Replace);
        }
    }
}
