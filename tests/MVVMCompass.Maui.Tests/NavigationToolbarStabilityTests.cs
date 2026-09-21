using System.Collections.ObjectModel;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task Toolbar_edge_controls_remain_pinned_across_action_visibility_and_content_changes(FlowDirection direction)
    {
        var root = await Open<Leaf>();
        var model = Assert.IsType<Leaf>(root.Current!.ViewModel);
        Success(await model.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var screen = root.Deepest.Current!.View;
        screen.ToolbarLeadingTemplate = new DataTemplate(() =>
        {
            var button = new Button
            {
                AutomationId = "stable-leading", WidthRequest = 44, HeightRequest = 44,
                MinimumWidthRequest = 44, MinimumHeightRequest = 44, Padding = 0
            };
            button.SetBinding(Button.CommandProperty, nameof(NavigationToolbar.LeadingCommand));
            return button;
        });
        var first = new ToolbarButton { Text = "A", Command = new Command(() => { }) };
        var second = new ToolbarButton { Text = "B", Command = new Command(() => { }), IsVisible = false };
        screen.Toolbar = new NavigationToolbarDefinition
        {
            Title = "Centered",
            RightItems = new ObservableCollection<ToolbarButton> { first, second }
        };
        var toolbar = root.View.Toolbar;
        toolbar.FlowDirection = direction;
        ArrangeToolbar(toolbar, 360);
        var original = Edges(toolbar, direction);
        Assert.Equal(LayoutOptions.Start, toolbar.LeadingHostForTests.HorizontalOptions);
        Assert.Equal(LayoutOptions.End, toolbar.ActionScrollerForTests.HorizontalOptions);
        Assert.Equal(LayoutOptions.End, toolbar.ActionsForTests.HorizontalOptions);

        second.IsVisible = true;
        ArrangeToolbar(toolbar, 360);
        var expanded = Edges(toolbar, direction);
        Assert.Same(toolbar, root.View.Toolbar);
        Assert.InRange(Math.Abs(expanded.Leading - original.Leading), 0, 1);
        Assert.InRange(Math.Abs(expanded.Trailing - original.Trailing), 0, 1);
        Assert.InRange(Math.Abs(expanded.Center - 180), 0, 1);

        second.IsVisible = false;
        first.Text = "Expanded action";
        first.Icon = new FontImageSource { Glyph = "+", Size = 24 };
        ArrangeToolbar(toolbar, 360);
        var contentChanged = Edges(toolbar, direction);
        Assert.InRange(Math.Abs(contentChanged.Leading - original.Leading), 0, 1);
        Assert.InRange(Math.Abs(contentChanged.Trailing - original.Trailing), 0, 1);

        first.Text = "A";
        first.Icon = null;
        ArrangeToolbar(toolbar, 360);
        var restored = Edges(toolbar, direction);
        Assert.InRange(Math.Abs(restored.Leading - original.Leading), 0, 1);
        Assert.InRange(Math.Abs(restored.Trailing - original.Trailing), 0, 1);
    }

    [Fact]
    public async Task Toolbar_center_host_centers_natural_height_and_does_not_retain_replaced_content_measurement()
    {
        var root = await Open<Leaf>();
        var screen = root.Current!.View;
        var custom = new Grid { HeightRequest = 24, AutomationId = "custom-center" };
        custom.Add(new Label { Text = "Line one\nLine two", FontSize = 10 });
        var definition = screen.Toolbar = new NavigationToolbarDefinition { CenterContent = custom };
        var toolbar = root.View.Toolbar;

        ArrangeToolbar(toolbar, 360);
        var customHostBounds = BoundsIn(toolbar.CenterHostForTests, toolbar);
        Assert.Equal(LayoutOptions.Center, toolbar.CenterHostForTests.VerticalOptions);
        Assert.InRange(Math.Abs(customHostBounds.Center.Y - 28), 0, 1);

        definition.CenterContent = null;
        definition.Title = "Plain";
        ArrangeToolbar(toolbar, 360);
        var plain = Descendants(toolbar).OfType<Label>().Single(label => label.Text == "Plain");
        Assert.NotNull(plain);
        Assert.InRange(Math.Abs(BoundsIn(toolbar.CenterHostForTests, toolbar).Center.Y - 28), 0, 1);

        definition.CenterContent = custom;
        ArrangeToolbar(toolbar, 360);
        Assert.InRange(Math.Abs(BoundsIn(toolbar.CenterHostForTests, toolbar).Center.Y - 28), 0, 1);
    }

    [Fact]
    public async Task Replacing_overflowing_actions_resets_the_persistent_scroller_to_the_trailing_edge()
    {
        var root = await Open<Leaf>();
        var screen = root.Current!.View;
        var items = new ObservableCollection<ToolbarButton>(Enumerable.Range(0, 10)
            .Select(index => new ToolbarButton { Text = "Action " + index, Command = new Command(() => { }) }));
        var definition = screen.Toolbar = new NavigationToolbarDefinition { Title = "Overflow", RightItems = items };
        var toolbar = root.View.Toolbar;
        ArrangeToolbar(toolbar, 320);
        var scroll = Descendants(toolbar).OfType<ScrollView>().Single();

        definition.RightItems = [new ToolbarButton { Text = "Only", Command = new Command(() => { }) }];
        ArrangeToolbar(toolbar, 320);
        Assert.Same(scroll, Descendants(toolbar).OfType<ScrollView>().Single());
        Assert.False(scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Always);
        Assert.Contains(Descendants(toolbar).OfType<Button>(), button => button.Text == "Only");
        Assert.InRange(Math.Abs(BoundsIn(scroll, toolbar).Right - 312), 0, 1);
    }

    private static void ArrangeToolbar(NavigationToolbar toolbar, double width)
    {
        ((ICrossPlatformLayout)toolbar).CrossPlatformMeasure(width, 56);
        var manager = new GridLayoutManager(toolbar.LayoutForTests);
        manager.Measure(width, 56);
        manager.ArrangeChildren(new Rect(0, 0, width, 56));
    }

    private static (double Leading, double Trailing, double Center) Edges(NavigationToolbar toolbar, FlowDirection direction)
    {
        Assert.Contains(Descendants(toolbar).OfType<Button>(), button => button.AutomationId == "stable-leading");
        var leadingBounds = BoundsIn(toolbar.LeadingHostForTests, toolbar);
        var actionBounds = BoundsIn(toolbar.ActionScrollerForTests, toolbar);
        Assert.Contains(Descendants(toolbar).OfType<Label>(), label => label.Text == "Centered");
        return direction == FlowDirection.RightToLeft
            ? (leadingBounds.Right, actionBounds.Left, BoundsIn(toolbar.CenterHostForTests, toolbar).Center.X)
            : (leadingBounds.Left, actionBounds.Right, BoundsIn(toolbar.CenterHostForTests, toolbar).Center.X);
    }

    private static Rect BoundsIn(VisualElement element, Element ancestor)
    {
        var x = element.X;
        var y = element.Y;
        for (var parent = element.Parent; parent != null && !ReferenceEquals(parent, ancestor); parent = parent.Parent)
        {
            if (parent is VisualElement visual) { x += visual.X; y += visual.Y; }
        }
        return new(x, y, element.Width, element.Height);
    }
}
