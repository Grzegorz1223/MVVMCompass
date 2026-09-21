using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Fact]
    public async Task Loading_backdrop_inherits_independently_of_template_and_changes_without_recreating_content()
    {
        var root = await Open<Flyout>();
        var outer = root.Current!.View;
        var leaf = root.Deepest.Current!.View;
        var creations = 0;
        outer.LoadingBackdrop = new SolidColorBrush(Colors.Red);
        leaf.LoadingPresentationTemplate = new DataTemplate(() => { creations++; return new Label { Text = "Customer loading" }; });
        leaf.IsBusy = true;
        var content = root.View.BusyContent;
        Assert.Same(outer.LoadingBackdrop, root.View.BusyBackdrop);
        var transparent = leaf.LoadingBackdrop = new SolidColorBrush(Colors.Transparent);
        Assert.Same(transparent, root.View.BusyBackdrop);
        Assert.Same(content, root.View.BusyContent);
        outer.LoadingBackdrop = new SolidColorBrush(Colors.Blue);
        Assert.Same(transparent, root.View.BusyBackdrop);
        leaf.ClearValue(ViewBase.LoadingBackdropProperty);
        Assert.Same(outer.LoadingBackdrop, root.View.BusyBackdrop);
        Assert.Same(content, root.View.BusyContent);
        outer.LoadingBackdrop = null;
        Assert.Equal(Color.FromArgb("#33FFFFFF"), Assert.IsType<SolidColorBrush>(root.View.BusyBackdrop).Color);
        Assert.Equal(1, creations);
    }

    [Fact]
    public async Task Loading_backdrop_restores_after_replacement_and_does_not_inherit_between_windows()
    {
        var first = await Open<Leaf>();
        var second = await Open<Leaf>();
        var firstView = first.Current!.View;
        firstView.LoadingBackdrop = new SolidColorBrush(Colors.Transparent);
        firstView.IsBusy = true;
        second.Current!.View.IsBusy = true;
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(first.View.BusyBackdrop).Color);
        Assert.Equal(Color.FromArgb("#33FFFFFF"), Assert.IsType<SolidColorBrush>(second.View.BusyBackdrop).Color);
        Success(await ((Leaf)firstView.ViewModel).Navigation.SetRoot<Leaf>(cancellationToken: Token));
        Assert.Equal(Color.FromArgb("#33FFFFFF"), Assert.IsType<SolidColorBrush>(first.View.BusyBackdrop).Color);
        firstView.LoadingBackdrop = new SolidColorBrush(Colors.Green);
        Assert.Equal(Color.FromArgb("#33FFFFFF"), Assert.IsType<SolidColorBrush>(first.View.BusyBackdrop).Color);
    }

    [Fact]
    public async Task Loading_backdrop_dynamic_resource_updates_keep_the_presenter_and_declaring_model()
    {
        var root = await Open<Leaf>();
        var view = root.Current!.View;
        var color = new SolidColorBrush();
        color.SetBinding(SolidColorBrush.ColorProperty, new Binding(nameof(BackdropModel.Color)));
        var owner = new BackdropModel(Colors.Green);
        view.BindingContext = owner;
        view.Resources["LoadingBrush"] = color;
        view.SetDynamicResource(ViewBase.LoadingBackdropProperty, "LoadingBrush");
        view.IsBusy = true;
        var content = root.View.BusyContent;
        Assert.Same(owner, color.BindingContext);
        Assert.Equal(Colors.Green, Assert.IsType<SolidColorBrush>(root.View.BusyBackdrop).Color);
        view.BindingContext = new BackdropModel(Colors.Yellow);
        Assert.Equal(Colors.Yellow, color.Color);
        var replacement = new SolidColorBrush(Colors.Transparent);
        view.Resources["LoadingBrush"] = replacement;
        Assert.Same(replacement, root.View.BusyBackdrop);
        Assert.Same(content, root.View.BusyContent);
    }

    private sealed record BackdropModel(Color Color);
}
