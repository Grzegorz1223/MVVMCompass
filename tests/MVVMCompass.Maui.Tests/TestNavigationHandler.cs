using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace MVVMCompass.Maui.Tests;

// Completes MAUI's real managed navigation pipeline without rendering a native window.
// Device gesture and animation behavior still require platform integration tests.
internal sealed class TestNavigationHandler : IViewHandler
{
    public object PlatformView { get; } = new();
    public IView VirtualView { get; private set; } = null!;
    IElement IElementHandler.VirtualView => VirtualView;
    public object ContainerView => PlatformView;
    public bool HasContainer { get; set; }
    public Size GetDesiredSize(double widthConstraint, double heightConstraint) => new(widthConstraint, heightConstraint);
    public void PlatformArrange(Rect frame) { }
    public IMauiContext MauiContext { get; private set; } = new MauiContext(new ServiceCollection().BuildServiceProvider());
    public void SetMauiContext(IMauiContext context) => MauiContext = context;
    public void SetVirtualView(IElement view) => VirtualView = (IView)view;
    public void UpdateValue(string property) { }
    public void DisconnectHandler() { }
    public void Invoke(string command, object? args)
    {
        if (command == nameof(IStackNavigation.RequestNavigation) && args is NavigationRequest request)
            ((IStackNavigation)VirtualView).NavigationFinished(request.NavigationStack);
    }
    internal static NavigationPage Create(Page root)
    {
        var page = new NavigationPage(root);
        page.Handler = new TestNavigationHandler();
        return page;
    }
}
