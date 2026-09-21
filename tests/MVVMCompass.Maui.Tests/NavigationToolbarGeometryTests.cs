using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Fact]
    public async Task Toolbar_geometry_inherits_independently_and_zero_overrides_do_not_replace_fallback_bindings()
    {
        var root = await Open<Flyout>();
        var toolbar = root.View.Toolbar;
        toolbar.Resources["Insets"] = new Thickness(9, 3);
        toolbar.ClearValue(ContentView.PaddingProperty);
        toolbar.SetDynamicResource(ContentView.PaddingProperty, "Insets");
        var outer = root.Current!.View.Toolbar = new()
        {
            Padding = new Thickness(20, 0, 10, 0), HeightRequest = 55, LeadingSlotWidth = 30,
            ColumnSpacing = 0, ActionAreaSpacing = 8, CenterPlacement = ToolbarCenterPlacement.Middle
        };
        var inner = root.Deepest.Current!.View.Toolbar = new() { Title = "Child", ActionSpacing = 7 };
        var layout = toolbar.LayoutForTests;
        Assert.Equal(outer.Padding, layout.Padding);
        Assert.Equal(55, layout.HeightRequest);
        Assert.Equal(0, layout.MinimumHeightRequest);
        Assert.Equal(0, layout.ColumnSpacing);
        Assert.Equal(7, toolbar.ActionsForTests.Spacing);
        Assert.Equal(new Thickness(9, 3), toolbar.Padding);

        inner.Padding = 0;
        inner.HeightRequest = 0;
        inner.MinimumHeightRequest = 0;
        inner.ActionSpacing = 0;
        Assert.Equal(new Thickness(0), layout.Padding);
        Assert.Equal(0, layout.HeightRequest);
        Assert.Equal(0, layout.MinimumHeightRequest);
        Assert.Equal(0, toolbar.ActionsForTests.Spacing);
        inner.ClearValue(NavigationToolbarDefinition.PaddingProperty);
        inner.HeightRequest = null;
        Assert.Equal(outer.Padding, layout.Padding);
        Assert.Equal(55, layout.HeightRequest);

        outer.Padding = null;
        outer.HeightRequest = null;
        inner.MinimumHeightRequest = null;
        toolbar.Resources["Insets"] = new Thickness(12, 6);
        Assert.Equal(new Thickness(12, 6), toolbar.Padding);
        Assert.Equal(toolbar.Padding, layout.Padding);
        Assert.Equal(-1, layout.HeightRequest);
        Assert.Equal(56, layout.MinimumHeightRequest);
        outer.ColumnSpacing = null;
        inner.ActionSpacing = null;
        Assert.Equal(4, layout.ColumnSpacing);
        Assert.Equal(4, toolbar.ActionsForTests.Spacing);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(360)]
    public async Task Leading_slot_retains_configured_width_when_controls_change_or_disappear(double width)
    {
        var root = await Open<Leaf>();
        Success(await ((Leaf)root.Current!.ViewModel).Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var screen = root.Deepest.Current!.View;
        screen.ToolbarLeadingTemplate = new DataTemplate(() => new BoxView { WidthRequest = 25, HeightRequest = 25 });
        var second = new ToolbarButton { Text = "Status" };
        var definition = screen.Toolbar = LegacyToolbarDefinition();
        definition.RightItems = [new() { Text = "Refresh" }, second];
        definition.RightItemTemplate = new DataTemplate(() => new BoxView { WidthRequest = 30, HeightRequest = 30 });
        definition.CenterContent = new BoxView { HeightRequest = 30, HorizontalOptions = LayoutOptions.Fill };
        var toolbar = root.View.Toolbar;
        foreach (var count in new[] { 2, 1, 2, 0, 1 })
        {
            definition.RightItems[0].IsVisible = count > 0;
            second.IsVisible = count > 1;
            ArrangeConfiguredToolbar(toolbar, width, 55);
            var middle = BoundsIn(toolbar.CenterHostForTests, toolbar);
            Assert.Equal(20, BoundsIn(toolbar.LeadingHostForTests, toolbar).Left, 4);
            Assert.Equal(30, toolbar.LayoutForTests.ColumnDefinitions[0].Width.Value);
            Assert.Equal(50, middle.Left, 4);
            Assert.Equal(27.5, middle.Center.Y, 4);
        }
        Success(await ((Leaf)screen.ViewModel).Navigation.NavigateBack(Token));
        var rootDefinition = root.Current!.View.Toolbar = LegacyToolbarDefinition();
        ArrangeConfiguredToolbar(toolbar, width, 55);
        Assert.False(toolbar.LeadingHostForTests.IsVisible);
        Assert.Equal(30, toolbar.LayoutForTests.ColumnDefinitions[0].Width.Value);
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task Full_width_center_includes_asymmetric_insets_and_releases_overlay_state_when_mode_changes(FlowDirection direction)
    {
        var root = await Open<Leaf>();
        var definition = root.Current!.View.Toolbar = LegacyToolbarDefinition();
        definition.CenterContent = new BoxView { HeightRequest = 30 };
        definition.CenterPlacement = ToolbarCenterPlacement.FullWidth;
        var toolbar = root.View.Toolbar;
        toolbar.FlowDirection = direction;
        ArrangeConfiguredToolbar(toolbar, 360, 55);
        var center = toolbar.CenterHostForTests;
        var bounds = BoundsIn(center, toolbar);
        Assert.Equal(0, bounds.Left, 4);
        Assert.Equal(360, bounds.Right, 4);
        Assert.Equal(180, bounds.Center.X, 4);
        Assert.True(center.InputTransparent);
        Assert.Equal(3, Grid.GetColumnSpan(center));

        definition.CenterPlacement = ToolbarCenterPlacement.Middle;
        ArrangeConfiguredToolbar(toolbar, 360, 55);
        Assert.Equal(1, Grid.GetColumn(center));
        Assert.Equal(1, Grid.GetColumnSpan(center));
        Assert.Equal(new Thickness(0), center.Margin);
        Assert.False(center.InputTransparent);
        Assert.Equal(0, toolbar.LeadingHostForTests.ZIndex);
    }

    [Fact]
    public async Task Geometry_follows_committed_navigation_and_restores_defaults_without_recreating_templates()
    {
        var root = await Open<Leaf>();
        var leaf = (Leaf)root.Current!.ViewModel;
        var source = root.Current.View.Toolbar = LegacyToolbarDefinition();
        var toolbar = root.View.Toolbar;
        leaf.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token)).Status);
        Assert.Equal(55, toolbar.LayoutForTests.HeightRequest);
        leaf.Allowed = true;
        Success(await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        Assert.Equal(-1, toolbar.LayoutForTests.HeightRequest);
        Assert.Equal(56, toolbar.LayoutForTests.MinimumHeightRequest);
        Assert.Equal(new Thickness(8, 4), toolbar.LayoutForTests.Padding);
        Assert.Equal(4, toolbar.LayoutForTests.ColumnSpacing);
        var created = 0;
        var definition = root.Deepest.Current!.View.Toolbar = new()
        {
            RightItems = [new() { Text = "One" }],
            RightItemTemplate = new DataTemplate(() => { created++; return new BoxView { WidthRequest = 30, HeightRequest = 30 }; })
        };
        var action = Assert.Single(toolbar.ActionsForTests);
        for (var i = 0; i < 10; i++)
        {
            definition.Padding = i % 2 == 0 ? 0 : new Thickness(20, 0, 10, 0);
            definition.CenterPlacement = i % 2 == 0 ? ToolbarCenterPlacement.Middle : ToolbarCenterPlacement.FullWidth;
            definition.ActionSpacing = i;
        }
        Assert.Equal(1, created);
        Assert.Same(action, Assert.Single(toolbar.ActionsForTests));
        Success(await ((Leaf)root.Deepest.Current.ViewModel).Navigation.NavigateBack(Token));
        Assert.Same(toolbar, root.View.Toolbar);
        Assert.Equal(source.Padding, toolbar.LayoutForTests.Padding);
        Assert.Equal(55, toolbar.LayoutForTests.HeightRequest);
    }

    [Fact]
    public void Geometry_rejects_nonfinite_negative_dimensions_and_invalid_modes()
    {
        var definition = new NavigationToolbarDefinition();
        foreach (var value in new[] { -1d, double.NaN, double.PositiveInfinity })
        {
            definition.LeadingSlotWidth = value;
            definition.HeightRequest = value;
            definition.Padding = new Thickness(value);
            Assert.Null(definition.LeadingSlotWidth);
            Assert.Null(definition.HeightRequest);
            Assert.Null(definition.Padding);
        }
        definition.CenterPlacement = (ToolbarCenterPlacement)99;
        Assert.Null(definition.CenterPlacement);
    }

    private static NavigationToolbarDefinition LegacyToolbarDefinition() => new()
    {
        Padding = new Thickness(20, 0, 10, 0), HeightRequest = 55, MinimumHeightRequest = 0,
        LeadingSlotWidth = 30, ColumnSpacing = 0, ActionAreaSpacing = 8, ActionSpacing = 4,
        CenterPlacement = ToolbarCenterPlacement.Middle
    };

    private static void ArrangeConfiguredToolbar(NavigationToolbar toolbar, double width, double height)
    {
        ((IView)toolbar).Measure(width, height);
        var manager = new GridLayoutManager(toolbar.LayoutForTests);
        manager.Measure(width, height);
        manager.ArrangeChildren(new Rect(0, 0, width, height));
    }
}
