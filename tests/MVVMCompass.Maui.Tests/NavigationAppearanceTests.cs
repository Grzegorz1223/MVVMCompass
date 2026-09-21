using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Fact]
    public async Task Toolbar_appearance_inherits_each_value_independently_and_preserves_configured_fallbacks()
    {
        var root = await Open<Flyout>();
        var toolbar = root.View.Toolbar;
        toolbar.Resources["FallbackBrush"] = new SolidColorBrush(Colors.Blue);
        toolbar.SetDynamicResource(VisualElement.BackgroundProperty, "FallbackBrush");
        toolbar.ForegroundColor = Colors.Green;
        var configured = toolbar.Background;
        var outer = root.Current!.View.Toolbar = new() { Background = new SolidColorBrush(Colors.Orange) };
        var inner = root.Current.Children!.Current!.View.Toolbar = new() { ForegroundColor = Colors.Purple };
        var leaf = root.Deepest.Current!.View.Toolbar = new() { Title = "Leaf" };
        Assert.Same(outer.Background, toolbar.EffectiveBackground);
        Assert.Equal(Colors.Purple, toolbar.EffectiveForegroundColor);
        Assert.Same(configured, toolbar.Background);

        leaf.Background = new SolidColorBrush(Colors.Transparent);
        leaf.ForegroundColor = Colors.White;
        Assert.Same(leaf.Background, toolbar.EffectiveBackground);
        Assert.Equal(Colors.White, toolbar.EffectiveForegroundColor);
        leaf.ClearValue(NavigationToolbarDefinition.BackgroundProperty);
        leaf.ForegroundColor = null;
        Assert.Same(outer.Background, toolbar.EffectiveBackground);
        Assert.Equal(Colors.Purple, toolbar.EffectiveForegroundColor);
        root.View.ToolbarDefaults.Background = new SolidColorBrush(Colors.Red);
        outer.Background = null;
        Assert.Same(root.View.ToolbarDefaults.Background, toolbar.EffectiveBackground);
        root.View.ToolbarDefaults.Background = null;
        inner.ForegroundColor = null;
        Assert.Same(configured, toolbar.EffectiveBackground);
        Assert.Equal(Colors.Green, toolbar.EffectiveForegroundColor);
        toolbar.Resources["FallbackBrush"] = new SolidColorBrush(Colors.Yellow);
        Assert.Same(toolbar.Background, toolbar.EffectiveBackground);
        Assert.NotSame(configured, toolbar.Background);
    }

    [Fact]
    public async Task Appearance_changes_update_existing_default_actions_and_template_bindings()
    {
        var root = await Open<Leaf>();
        var calls = 0;
        var custom = new Label { Text = "Custom", TextColor = Colors.Pink };
        var definition = root.Current!.View.Toolbar = new()
        {
            CenterContent = custom,
            RightItems = [new() { Text = "Save", Icon = new FontImageSource { Glyph = "+", Color = Colors.Blue }, Command = new Command(() => calls++) }]
        };
        root.View.Toolbar.LeadingButtonTemplate = new DataTemplate(() =>
        {
            var label = new Label();
            label.SetBinding(Label.TextColorProperty, nameof(NavigationToolbar.EffectiveForegroundColor));
            return label;
        });
        var toolbar = root.View.Toolbar;
        var input = Descendants(toolbar).OfType<Button>().Single(button => button.Text == "Save");
        var command = input.Command;
        var icon = Descendants(toolbar).OfType<Image>().Single();
        var leadingLabel = Descendants(toolbar).OfType<Label>().Single(label => !ReferenceEquals(label, custom));
        for (var i = 0; i < 5; i++)
        {
            definition.Background = new SolidColorBrush(i % 2 == 0 ? Colors.Red : Colors.Blue);
            definition.ForegroundColor = i % 2 == 0 ? Colors.White : Colors.Yellow;
            Assert.Same(input, Descendants(toolbar).OfType<Button>().Single(button => button.Text == "Save"));
            Assert.Same(command, input.Command);
            Assert.Same(icon, Descendants(toolbar).OfType<Image>().Single());
            Assert.Equal(definition.ForegroundColor, input.TextColor);
            Assert.Equal(definition.ForegroundColor, leadingLabel.TextColor);
        }
        Assert.Equal(Colors.Pink, custom.TextColor);
        Assert.Equal(Colors.Blue, Assert.IsType<FontImageSource>(icon.Source).Color);
        input.Command!.Execute(null);
        Assert.Equal(1, calls);
        definition.ForegroundColor = null;
        toolbar.ForegroundColor = Colors.Brown;
        Assert.Equal(Colors.Brown, input.TextColor);
        var background = toolbar.EffectiveBackground;
        toolbar.SetValue(NavigationToolbar.EffectiveForegroundColorProperty, Colors.Red);
        toolbar.SetValue(NavigationToolbar.EffectiveBackgroundProperty, new SolidColorBrush(Colors.Red));
        Assert.Equal(Colors.Brown, toolbar.EffectiveForegroundColor);
        Assert.Same(background, toolbar.EffectiveBackground);
    }

    [Fact]
    public async Task Toolbar_appearance_follows_committed_navigation_and_isolated_hosts()
    {
        var first = await Open<Flyout>();
        var second = await Open<Leaf>();
        var original = first.Deepest.Current!;
        var definition = original.View.Toolbar = new() { Background = new SolidColorBrush(Colors.Orange), ForegroundColor = Colors.White };
        second.Current!.View.Toolbar = new() { ForegroundColor = Colors.Green };
        var leaf = (Leaf)original.ViewModel;
        leaf.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token)).Status);
        Assert.Same(definition.Background, first.View.Toolbar.EffectiveBackground);
        leaf.Allowed = true;
        Success(await leaf.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        first.Deepest.Current!.View.Toolbar = new() { ForegroundColor = Colors.Purple };
        Assert.Equal(Colors.Purple, first.View.Toolbar.EffectiveForegroundColor);
        Assert.Equal(Colors.Green, second.View.Toolbar.EffectiveForegroundColor);
        Success(await ((Leaf)first.Deepest.Current.ViewModel).Navigation.NavigateBack(Token));
        Assert.Same(original, first.Deepest.Current);
        Assert.Equal(Colors.White, first.View.Toolbar.EffectiveForegroundColor);
        Success(await leaf.Navigation.SetRoot<Leaf>(cancellationToken: Token));
        definition.ForegroundColor = Colors.Red;
        Assert.Equal(first.View.Toolbar.ForegroundColor, first.View.Toolbar.EffectiveForegroundColor);
        Assert.Equal(Colors.Green, second.View.Toolbar.EffectiveForegroundColor);
    }

    [Fact]
    public async Task Flyout_trailing_content_scrolls_after_visible_destinations_and_retains_its_owner()
    {
        var root = await Open<Flyout>();
        var owner = (FlyoutView)root.Current!.View;
        var model = owner.ViewModel;
        var child = root.Current.Children!;
        owner.Resources["TrailingColor"] = Colors.Orange;
        var trailing = new Button { Text = "About" };
        trailing.SetDynamicResource(Button.TextColorProperty, "TrailingColor");
        owner.FlyoutTrailingContent = trailing;
        owner.FlyoutFooterContent = new Label { Text = "Version" };
        owner.FlyoutItemSpacing = 7;
        child.SetFlyout(true);
        var menu = FlyoutMenu(root, child);
        var scroll = Assert.IsType<ScrollView>(menu.Content);
        var parent = Assert.IsType<Grid>(trailing.Parent);
        var changes = 0;
        trailing.ParentChanged += (_, _) => changes++;
        Assert.Same(scroll.Content, parent);
        Assert.Same(model, trailing.BindingContext);
        Assert.Equal(Colors.Orange, trailing.TextColor);
        Assert.False(trailing.InputTransparent);
        Assert.DoesNotContain(owner.FlyoutFooterContent, Descendants(scroll));
        foreach (var visible in new[] { 6, 4, 0, 6 })
        {
            Success(await model.Replace(Enumerable.Range(0, 6).Select(index => new NavigationItem(typeof(Leaf), "Item " + index, "item-" + index) { IsVisible = index < visible })));
            Assert.Same(parent, trailing.Parent);
            Assert.Equal(visible, Grid.GetRow(trailing));
            Assert.Equal(visible + 1, parent.RowDefinitions.Count);
            Assert.All(parent.RowDefinitions, row => Assert.Equal(GridLength.Auto, row.Height));
            Assert.Equal(7, parent.RowSpacing);
            child.SetFlyout(false); child.SetFlyout(true);
            Assert.Same(scroll, menu.Content);
            Assert.Same(model, trailing.BindingContext);
        }
        Assert.Equal(0, changes);
        owner.Resources["TrailingColor"] = Colors.Blue;
        Assert.Equal(Colors.Blue, trailing.TextColor);
        var explicitContext = new object();
        var replacement = new Entry { BindingContext = explicitContext };
        owner.FlyoutTrailingContent = replacement;
        Assert.Null(trailing.Parent);
        Assert.Same(explicitContext, replacement.BindingContext);
        Assert.DoesNotContain(trailing, Descendants(root.View));
        owner.FlyoutTrailingContent = null;
        Assert.Null(replacement.Parent);
    }

    [Fact]
    public async Task Trailing_content_is_detached_when_its_navigation_host_is_released()
    {
        var root = await Open<Flyout>();
        var owner = (FlyoutView)root.Current!.View;
        var child = root.Current.Children!;
        var content = new Label { Text = "About" };
        owner.FlyoutTrailingContent = content;
        child.SetFlyout(true);
        var menu = FlyoutMenu(root, child);
        Success(await owner.ViewModel.Navigation.SetRoot<Leaf>(cancellationToken: Token));
        Assert.Null(content.Parent);
        Assert.Null(menu.TrailingContent);
        Assert.Null(child.View.FlyoutTrailingContent);
        Assert.DoesNotContain(content, Descendants(root.View));
    }

    [Fact]
    public void Empty_selector_has_one_auto_trailing_row_and_preserves_the_configured_context()
    {
        var model = new object();
        var trailing = new Entry();
        var selector = new NavigationSelector { BindingContext = model, Orientation = StackOrientation.Vertical, TrailingContent = trailing };
        var grid = Assert.IsType<Grid>(Assert.IsType<ScrollView>(selector.Content).Content);
        Assert.Single(grid.RowDefinitions);
        Assert.Equal(GridLength.Auto, grid.RowDefinitions[0].Height);
        Assert.Equal(0, Grid.GetRow(trailing));
        Assert.Same(model, trailing.BindingContext);
    }

    [Fact]
    public async Task Membership_updates_keep_existing_destination_templates_and_publish_one_complete_list()
    {
        var root = await Open<Flyout>();
        var owner = (FlyoutView)root.Current!.View;
        var child = root.Current.Children!;
        var created = 0;
        var template = new DataTemplate(() => { created++; return new Label(); });
        owner.SelectedFlyoutItemTemplate = owner.UnselectedFlyoutItemTemplate = template;
        child.SetFlyout(true);
        var menu = FlyoutMenu(root, child);
        var input = MenuInput(menu, "other");
        var initial = created;
        var observedCounts = new List<int>();
        ((System.Collections.Specialized.INotifyCollectionChanged)child.MenuItems).CollectionChanged += (_, _) => observedCounts.Add(child.MenuItems.Count);
        Success(await owner.ViewModel.Replace([new(typeof(Tabs), "Workspace", "workspace"), Item("other"), Item("third")]));
        Assert.Equal(new[] { 3 }, observedCounts);
        Assert.Equal(initial + 1, created);
        Assert.Same(input, MenuInput(menu, "other"));
        var removedCommand = MenuInput(menu, "third").Command!;
        Success(await owner.ViewModel.Navigation.Select("other", cancellationToken: Token));
        Assert.Equal(initial + 1, created);
        Assert.Same(input, MenuInput(menu, "other"));
        Success(await owner.ViewModel.Replace([new(typeof(Tabs), "Workspace", "workspace"), Item("other")]));
        Assert.False(removedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Modal_appearance_does_not_replace_underlying_window_values()
    {
        var root = await Open<Leaf>();
        root.Current!.View.Toolbar = new() { ForegroundColor = Colors.Orange };
        Success(await ((Leaf)root.Current.ViewModel).Navigation.NavigateTo<Modal>(cancellationToken: Token));
        var modal = Modal.Last!;
        var modalPage = root.Window.Navigation.ModalStack.Last();
        var view = Descendants(modalPage).OfType<ModalView>().Single();
        view.Toolbar = new() { Background = new SolidColorBrush(Colors.Purple), ForegroundColor = Colors.White };
        var toolbar = Descendants(modalPage).OfType<NavigationToolbar>().Single(item => item.IsVisible);
        Assert.Equal(Colors.White, toolbar.EffectiveForegroundColor);
        Assert.Equal(Colors.Orange, root.View.Toolbar.EffectiveForegroundColor);
        Success(await modal.Navigation.NavigateBack(Token));
        Assert.Equal(Colors.Orange, root.View.Toolbar.EffectiveForegroundColor);
    }

    [Fact]
    public async Task Availability_updates_keep_actions_and_bindings_live_while_a_guard_is_pending()
    {
        var root = await Open<Tabs>();
        var current = root.Deepest.Current!;
        var model = (Leaf)current.ViewModel;
        var calls = 0;
        var definition = current.View.Toolbar = new()
        {
            Title = "Editing", RightItems = [new() { Text = "Save", Command = new Command(() => calls++) }]
        };
        var input = Descendants(root.View.Toolbar).OfType<Button>().Single(button => button.Text == "Save");
        var command = input.Command;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Guard = async () => { entered.TrySetResult(); return await release.Task; };
        var navigation = model.Navigation.NavigateTo<Detail>(cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(input.IsEnabled);
        input.Command!.Execute(null);
        Assert.Equal(0, calls);
        definition.Title = "Guard pending";
        definition.ForegroundColor = Colors.Purple;
        Assert.Contains(Descendants(root.View.Toolbar).OfType<Label>(), label => label.Text == "Guard pending");
        Assert.Equal(Colors.Purple, input.TextColor);
        release.SetResult(false);
        Assert.Equal(NavigationStatus.GuardRejected, (await navigation).Status);
        Assert.Same(command, input.Command);
        Assert.Same(input, Descendants(root.View.Toolbar).OfType<Button>().Single(button => button.Text == "Save"));
        Assert.True(input.IsEnabled);
        input.Command.Execute(null);
        Assert.Equal(1, calls);

        var previousItems = definition.RightItems;
        definition.RightItems = [new() { Text = "Replacement", Command = new Command(() => calls++) }];
        var replacement = Descendants(root.View.Toolbar).OfType<Button>().Single(button => button.Text == "Replacement");
        previousItems.Add(new() { Text = "Stale" });
        Assert.Same(replacement, Descendants(root.View.Toolbar).OfType<Button>().Single(button => button.Text == "Replacement"));
        Assert.DoesNotContain(Descendants(root.View.Toolbar).OfType<Button>(), button => button.Text == "Stale");
        Assert.False(command!.CanExecute(null));
    }
}
