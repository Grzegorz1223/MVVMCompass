using Microsoft.Maui.Controls;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Theory]
    [InlineData(TabItemSizing.Content)]
    [InlineData(TabItemSizing.Equal)]
    public async Task Tab_strip_settings_follow_every_edge_and_preserve_retained_history(TabItemSizing sizing)
    {
        var root = await Open<Tabs>();
        var view = (TabsView)root.Current!.View;
        var child = root.Current.Children!;
        Assert.Equal(new Thickness(4), view.TabBarPadding);
        Assert.Equal(4, view.TabItemSpacing);
        Assert.Equal(ScrollBarVisibility.Never, view.TabScrollBarVisibility);
        Success(await view.ViewModel.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        var detail = child.Current;
        view.TabItemSizing = sizing;
        view.SetDynamicResource(ContainerViewBase.TabBarPaddingProperty, "TabInsets");
        view.SetDynamicResource(ContainerViewBase.TabItemSpacingProperty, "TabGap");
        view.SetDynamicResource(ContainerViewBase.TabScrollBarVisibilityProperty, "TabScrollbars");
        var creations = 0;
        var template = new DataTemplate(() => { creations++; return new Label(); });
        view.SelectedTabItemTemplate = view.UnselectedTabItemTemplate = template;
        foreach (var position in Enum.GetValues<TabBarPosition>())
        {
            view.TabBarPosition = position;
            var horizontal = position is TabBarPosition.Top or TabBarPosition.Bottom;
            var selector = horizontal ? child.View.TabSelector : child.View.RailSelector;
            var content = selector.Content;
            var before = creations;
            var input = Descendants(selector).OfType<Button>().First();
            foreach (var gap in new[] { 7d, 0d, 4d })
            {
                var insets = gap == 0 ? new Thickness(0) : new Thickness(gap, 2, 3, 5);
                view.Resources["TabInsets"] = insets;
                view.Resources["TabGap"] = gap;
                view.Resources["TabScrollbars"] = ScrollBarVisibility.Always;
                Assert.Equal(insets, selector.Padding);
                Assert.Equal(gap, selector.ItemSpacing);
                Assert.Equal(ScrollBarVisibility.Always, selector.ScrollBarVisibility);
                var grid = sizing == TabItemSizing.Content ? Assert.IsType<Grid>(Assert.IsType<ScrollView>(selector.Content).Content)
                    : Assert.IsType<Grid>(selector.Content);
                Assert.Equal(gap, grid.ColumnSpacing); Assert.Equal(gap, grid.RowSpacing);
                if (selector.Content is ScrollView scroll)
                {
                    Assert.Equal(horizontal ? ScrollBarVisibility.Always : ScrollBarVisibility.Never, scroll.HorizontalScrollBarVisibility);
                    Assert.Equal(horizontal ? ScrollBarVisibility.Never : ScrollBarVisibility.Always, scroll.VerticalScrollBarVisibility);
                }
                Assert.Same(content, selector.Content);
                Assert.Same(input, Descendants(selector).OfType<Button>().First());
                Assert.Equal(before, creations);
                Assert.Same(detail, child.Current);
            }
            Success(await view.ViewModel.Navigation.Select("archive", cancellationToken: Token));
            Success(await view.ViewModel.Navigation.Select("open", cancellationToken: Token));
            Assert.Same(detail, child.Current);
        }
    }

    [Fact]
    public async Task Clearing_local_tab_settings_restores_the_existing_defaults()
    {
        var root = await Open<Tabs>(); var view = (TabsView)root.Current!.View;
        view.TabBarPadding = 7; view.TabItemSpacing = 8; view.TabScrollBarVisibility = ScrollBarVisibility.Always;
        view.ClearValue(ContainerViewBase.TabBarPaddingProperty);
        view.ClearValue(ContainerViewBase.TabItemSpacingProperty);
        view.ClearValue(ContainerViewBase.TabScrollBarVisibilityProperty);
        var selector = root.Current.Children!.View.TabSelector;
        Assert.Equal(new Thickness(4), selector.Padding);
        Assert.Equal(4, selector.ItemSpacing);
        Assert.Equal(ScrollBarVisibility.Never, selector.ScrollBarVisibility);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Tab_strip_rejects_invalid_values_without_replacing_valid_settings(double invalid)
    {
        var root = await Open<Tabs>(); var view = (TabsView)root.Current!.View;
        var valid = new Thickness(1, 2, 3, 4);
        view.TabBarPadding = valid; view.TabItemSpacing = 9;
        view.TabScrollBarVisibility = ScrollBarVisibility.Always;
        foreach (var padding in new[] { new Thickness(invalid, 2, 3, 4), new Thickness(1, invalid, 3, 4),
            new Thickness(1, 2, invalid, 4), new Thickness(1, 2, 3, invalid) })
        {
            view.TabBarPadding = padding;
            Assert.Equal(valid, view.TabBarPadding);
        }
        view.TabItemSpacing = invalid;
        view.TabScrollBarVisibility = (ScrollBarVisibility)99;
        Assert.Equal(9, view.TabItemSpacing);
        Assert.Equal(ScrollBarVisibility.Always, view.TabScrollBarVisibility);
        Assert.Equal(valid, root.Current.Children!.View.TabSelector.Padding);
    }
}
