using System.ComponentModel;

namespace MVVMCompass;

/// <summary>The MAUI page boundary around a custom navigation scope.</summary>
internal sealed class NavigationHostPage : ContentPage
{
    internal NavigationHostPage(NavigationContext context)
    {
        Context = context;
        Content = context.View;
        SafeAreaEdges = SafeAreaEdges.All;
        NavigationPage.SetHasNavigationBar(this, false);
        NavigationPage.SetHasBackButton(this, false);
    }
    /// <summary>Gets the content navigation scope.</summary>
    public NavigationContext Context { get; }
    /// <summary>Routes platform Back through the content guard before leaving a screen.</summary>
    protected override bool OnBackButtonPressed() => Context.RequestPlatformBack() || base.OnBackButtonPressed();
}

/// <summary>A platform flyout whose detail retains one fully custom navigation layout.</summary>
internal sealed class NavigationFlyoutPage : FlyoutPage
{
    private readonly NavigationSelector menu;
    internal NavigationFlyoutPage(NavigationContext context, NavigationHostPage detail)
    {
        Context = context;
        menu = new NavigationSelector { ItemsSource = context.MenuItems, Orientation = StackOrientation.Vertical };
        Flyout = new ContentPage { Title = "Destinations", Content = menu, Padding = new Thickness(8, 16) };
        Detail = detail;
        PropertyChanged += Changed;
        SizeChanged += Resized;
    }
    /// <summary>Gets the content navigation scope.</summary>
    public NavigationContext Context { get; }
    /// <summary>Gets the menu surface for template customization.</summary>
    public NavigationSelector Menu => menu;
    private void Changed(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(IsPresented) or nameof(FlyoutLayoutBehavior)) Context.FlyoutChanged(); }
    private void Resized(object? sender, EventArgs args) => Context.FlyoutChanged();
    /// <summary>Closes an open drawer or requests guarded content Back.</summary>
    protected override bool OnBackButtonPressed() => Context.RequestPlatformBack() || base.OnBackButtonPressed();
    internal void Disconnect() { PropertyChanged -= Changed; SizeChanged -= Resized; menu.Disconnect(); }
}
