using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Fact]
    public async Task Flyout_overlay_covers_the_host_and_blocks_toolbar_commands_until_closed()
    {
        var root = await Open<Flyout>();
        var child = root.Current!.Children!;
        var current = root.Deepest.Current;
        var calls = 0;
        current!.View.Toolbar = new() { RightItems = [new() { Text = "Refresh", Command = new Command(() => calls++) }] };
        var button = Descendants(root.View.Toolbar).OfType<Button>().Single(item => item.Text == "Refresh");
        button.Command!.Execute(null);
        Assert.Equal(1, calls);

        child.SetFlyout(true);
        var menu = FlyoutMenu(root, child);
        Assert.DoesNotContain(menu, Descendants(child.View));
        Assert.Single(Descendants(root.View), item => ReferenceEquals(item, menu));
        Assert.False(button.IsEnabled);
        Assert.False(button.Command.CanExecute(null));
        Assert.False(root.View.Toolbar.LeadingCommand.CanExecute(null));
        button.Command.Execute(null);
        Assert.Equal(1, calls);
        Assert.True(MenuInput(menu, "other").IsEnabled);

        Success(await ((Leaf)current.ViewModel).Navigation.CloseFlyout(Token));
        Assert.False(IsPresented(menu));
        var parent = menu.Parent;
        child.SetFlyout(true);
        Assert.Same(parent, menu.Parent);
        Assert.True(IsPresented(menu));
        child.SetFlyout(false);
        Assert.Same(current, root.Deepest.Current);
        Assert.True(button.IsEnabled);
        button.Command.Execute(null);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Flyout_overlay_retains_container_bindings_and_local_resources()
    {
        var root = await Open<Flyout>();
        var owner = (FlyoutView)root.Current!.View;
        var child = root.Current.Children!;
        owner.Resources["MenuColor"] = Colors.Orange;
        var header = new Label();
        header.SetBinding(Label.TextProperty, nameof(Leaf.Filter));
        header.SetDynamicResource(Label.TextColorProperty, "MenuColor");
        owner.FlyoutHeaderContent = header;
        child.SetFlyout(true);
        Assert.Same(owner.ViewModel, header.BindingContext);
        Assert.Equal(Colors.Orange, header.TextColor);
        owner.Resources["MenuColor"] = Colors.Blue;
        Assert.Equal(Colors.Blue, header.TextColor);
        Assert.Single(Descendants(root.View), item => ReferenceEquals(item, header));
        child.SetFlyout(false); child.SetFlyout(true);
        Assert.Same(owner.ViewModel, header.BindingContext);
        Assert.Single(Descendants(root.View), item => ReferenceEquals(item, header));
    }

    [Fact]
    public async Task Nested_flyouts_present_only_the_innermost_open_menu_and_Back_restores_the_outer_menu()
    {
        var root = await Open<Flyout>();
        var parent = (Flyout)root.Current!.ViewModel;
        Success(await parent.Replace([new(typeof(Flyout), "Nested", "nested"), Item("other")]));
        var outer = root.Current.Children!;
        var inner = outer.Current!.Children!;
        var current = root.Deepest.Current;
        outer.SetFlyout(true);
        var outerMenu = FlyoutMenu(root, outer);
        inner.SetFlyout(true);
        var innerMenu = FlyoutMenu(root, inner);
        Assert.False(IsPresented(outerMenu));
        Assert.True(root.RequestPlatformBack());
        await root.NativeBackCompletion;
        Assert.False(inner.IsFlyoutOpen);
        Assert.True(outer.IsFlyoutOpen);
        Assert.Contains(outerMenu, Descendants(root.View));
        Assert.False(IsPresented(innerMenu));
        Assert.Same(current, root.Deepest.Current);
        Success(await parent.Navigation.CloseFlyout(Token));
        Assert.False(outer.IsFlyoutOpen);
    }

    [Fact]
    public async Task Flyout_overlay_does_not_cross_windows_and_releases_its_visuals_on_root_replacement()
    {
        var first = await Open<Flyout>();
        var second = await Open<Flyout>();
        first.Current!.Children!.SetFlyout(true);
        second.Current!.Children!.SetFlyout(true);
        var firstMenu = FlyoutMenu(first, first.Current.Children);
        var secondMenu = FlyoutMenu(second, second.Current.Children);
        Assert.DoesNotContain(firstMenu, Descendants(second.View));
        Success(await ((Flyout)first.Current.ViewModel).Navigation.SetRoot<Leaf>(cancellationToken: Token));
        Assert.DoesNotContain(firstMenu, Descendants(first.View));
        Assert.True(second.Current.Children.IsFlyoutOpen);
        Assert.Contains(secondMenu, Descendants(second.View));
    }

    [Fact]
    public async Task Modal_flyout_uses_its_own_host_and_restores_the_underlying_open_menu_on_close()
    {
        var root = await Open<Flyout>();
        var rootChild = root.Current!.Children!;
        rootChild.SetFlyout(true);
        var rootMenu = FlyoutMenu(root, rootChild);
        var registry = services.GetRequiredService<MVVMCompass.Services.RegisteredScreenFactory>();
        var opened = await root.OpenModalAsync(registry.Definition(typeof(Flyout)), cancellationToken: Token);
        Assert.True(opened.IsSuccess, opened.Error?.ToString());
        var modal = opened.Value!;
        var child = modal.Current!.Children!;
        child.SetFlyout(true);
        var modalMenu = FlyoutMenu(modal, child);
        Assert.DoesNotContain(modalMenu, Descendants(root.View));
        Assert.DoesNotContain(rootMenu, Descendants(modal.View));
        Assert.False(IsPresented(rootMenu));
        Assert.True((await modal.CloseModalAsync(cancellationToken: Token)).IsSuccess);
        Assert.DoesNotContain(modalMenu, Descendants(modal.View));
        Assert.Contains(rootMenu, Descendants(root.View));
        Assert.True(rootChild.IsFlyoutOpen);
    }

    [Fact]
    public async Task Flyout_list_settings_apply_live_without_recreating_destination_models()
    {
        var root = await Open<Flyout>();
        var owner = (FlyoutView)root.Current!.View;
        var child = root.Current.Children!;
        var current = root.Deepest.Current;
        child.SetFlyout(true);
        var menu = FlyoutMenu(root, child);
        owner.FlyoutListPadding = new(0, 8, 0, 12);
        owner.FlyoutItemSpacing = 0;
        owner.FlyoutScrollBarVisibility = ScrollBarVisibility.Always;
        Assert.Equal(new Thickness(0, 8, 0, 12), menu.Padding);
        Assert.Equal(0, menu.ItemSpacing);
        var scroll = Assert.IsType<ScrollView>(menu.Content);
        Assert.Equal(ScrollBarVisibility.Always, scroll.VerticalScrollBarVisibility);
        Assert.Equal(0, Assert.IsType<Grid>(scroll.Content).RowSpacing);
        Assert.Same(current, root.Deepest.Current);
        owner.FlyoutItemSpacing = 12;
        owner.FlyoutScrollBarVisibility = ScrollBarVisibility.Never;
        Assert.Equal(12, Assert.IsType<Grid>(scroll.Content).RowSpacing);
        Assert.Equal(ScrollBarVisibility.Never, scroll.VerticalScrollBarVisibility);
        Assert.Same(menu, FlyoutMenu(root, child));
    }

    [Fact]
    public async Task Navigation_input_is_isolated_from_implicit_button_geometry_and_visual_states()
    {
        var root = await Open<Flyout>();
        var states = new VisualStateGroupList { new VisualStateGroup { Name = "CommonStates", States =
        {
            new VisualState { Name = "Normal", Setters = { new Setter { Property = VisualElement.BackgroundColorProperty, Value = Colors.Magenta } } },
            new VisualState { Name = "Disabled", Setters = { new Setter { Property = VisualElement.BackgroundColorProperty, Value = Colors.Red } } }
        } } };
        root.View.Resources.Add(new Style(typeof(Button)) { ApplyToDerivedTypes = true, Setters =
        {
            new() { Property = Button.CornerRadiusProperty, Value = 20 },
            new() { Property = View.MarginProperty, Value = new Thickness(15) },
            new() { Property = VisualElement.WidthRequestProperty, Value = 200d },
            new() { Property = VisualElement.HeightRequestProperty, Value = 90d },
            new() { Property = VisualStateManager.VisualStateGroupsProperty, Value = states }
        } });
        // The application style really applies to ordinary controls in this tree.
        var applicationButton = new Button();
        ((FlyoutView)root.Current!.View).FlyoutFooterContent = applicationButton;
        var child = root.Current.Children!;
        child.SetFlyout(true);
        Assert.Equal(20, applicationButton.CornerRadius);
        Assert.Equal(200, applicationButton.WidthRequest);
        var input = MenuInput(FlyoutMenu(root, child), "other");
        var scrim = Descendants(root.View).OfType<Button>().Single(button => SemanticProperties.GetDescription(button) == "Close menu");
        foreach (var button in new[] { input, scrim })
        {
            Assert.Equal(0, button.CornerRadius);
            Assert.Equal(new Thickness(0), button.Margin);
            Assert.Equal(-1, button.WidthRequest);
            Assert.Equal(-1, button.HeightRequest);
            var background = button.BackgroundColor;
            button.IsEnabled = false;
            VisualStateManager.GoToState(button, "Disabled");
            Assert.Equal(background, button.BackgroundColor);
            button.IsEnabled = true;
            VisualStateManager.GoToState(button, "Normal");
            Assert.Equal(background, button.BackgroundColor);
        }
    }

    [Fact]
    public async Task Default_toolbar_icons_change_without_losing_commands_text_or_explicit_style()
    {
        var root = await Open<Leaf>();
        var explicitStyle = new Style(typeof(Button)) { Setters = { new() { Property = Button.FontSizeProperty, Value = 22d } } };
        root.View.Toolbar.ButtonStyle = explicitStyle;
        var item = new ToolbarButton { Text = "Save", Command = new Command(() => { }), Icon = new FontImageSource { Glyph = "+", Size = 112 }, AccessibilityLabel = "Save record" };
        root.Current!.View.Toolbar = new() { RightItems = [item] };
        var button = Descendants(root.View.Toolbar).OfType<Button>().Single(input => input.Text == "Save");
        var image = Assert.Single(Descendants(root.View.Toolbar).OfType<Image>());
        Assert.Equal(24, image.WidthRequest);
        Assert.Equal(24, image.HeightRequest);
        Assert.Equal(Aspect.AspectFit, image.Aspect);
        Assert.Equal(22, button.FontSize);
        Assert.Equal("Save record", SemanticProperties.GetDescription(button));
        Assert.True(button.Padding.Left >= 38);
        root.View.FlowDirection = FlowDirection.RightToLeft;
        Assert.True(button.Padding.Right >= 38);
        item.Text = "";
        Assert.Equal(LayoutOptions.Center, image.HorizontalOptions);
        item.Icon = null;
        Assert.False(image.IsVisible);
        item.Text = "Save";
        Assert.Equal(new Thickness(10, 4), button.Padding);
        Assert.True(button.Command!.CanExecute(null));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Flyout_list_ignores_invalid_spacing_and_padding(double invalid)
    {
        var root = await Open<Flyout>();
        var view = (FlyoutView)root.Current!.View;
        view.FlyoutItemSpacing = invalid;
        view.FlyoutListPadding = new Thickness(invalid);
        view.FlyoutScrollBarVisibility = (ScrollBarVisibility)99;
        Assert.Equal(4, view.FlyoutItemSpacing);
        Assert.Equal(new Thickness(4), view.FlyoutListPadding);
        Assert.Equal(ScrollBarVisibility.Default, view.FlyoutScrollBarVisibility);
    }

    private static NavigationSelector FlyoutMenu(NavigationContext root, NavigationContext owner) =>
        Descendants(root.View).OfType<NavigationSelector>().Single(selector => ReferenceEquals(selector.ItemsSource, owner.MenuItems));
    private static Button MenuInput(NavigationSelector menu, string id) =>
        Descendants(menu).OfType<Button>().Single(button => button.AutomationId == "destination-" + id);
    private static bool IsPresented(Element element)
    {
        for (Element? current = element; current != null; current = current.Parent)
            if (current is VisualElement { IsVisible: false }) return false;
        return true;
    }
}
