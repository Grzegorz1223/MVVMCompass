using System.ComponentModel;

namespace MVVMCompass;

internal sealed partial class NavigationView
{
    private ViewBase? busyOwner;
    private DataTemplate? busyTemplate;
    private bool busyUsesTypedContext;
    private bool defaultBusyEnabled = true;
    private View? busyContent;
    private ActivityIndicator? defaultBusyIndicator;
    private LoadingPresentationContext? loadingContext;

    internal View? BusyContent => busyContent;
    internal bool IsBusyPresented => busy.IsVisible;
    internal Brush? BusyBackdrop => busy.Background;

    internal void RefreshBusy()
    {
        if (disconnected || context?.ParentContext != null)
        {
            ClearBusy();
            return;
        }
        ViewBase? owner = null;
        var usesTypedContext = false;
        var useDefault = true;
        Brush? backdrop = null;
        ViewBase? backdropOwner = null;
        for (var current = context; current?.Current is { } screen; current = screen.Children)
        {
            if (screen.View.LoadingPresentationTemplate != null || screen.View.BusyOverlayTemplate != null)
            {
                owner = screen.View;
                usesTypedContext = screen.View.LoadingPresentationTemplate != null;
            }
            useDefault &= screen.View.UseDefaultBusyIndicator;
            if (screen.View.LoadingBackdrop is { } configuredBackdrop)
            {
                backdrop = configuredBackdrop;
                backdropOwner = screen.View;
            }
        }
        var template = usesTypedContext ? owner?.LoadingPresentationTemplate : owner?.BusyOverlayTemplate;
        if (busyOwner != owner || busyTemplate != template || busyUsesTypedContext != usesTypedContext || defaultBusyEnabled != useDefault)
        {
            ClearBusy();
            busyOwner = owner;
            busyTemplate = template;
            busyUsesTypedContext = usesTypedContext;
            defaultBusyEnabled = useDefault;
            if (busyOwner != null) busyOwner.PropertyChanged += BusyOwnerChanged;
        }
        busy.Background = backdrop ?? defaultLoadingBackdrop;
        if (busyOwner != null)
        {
            // The declaring screen may sit inside our presenter. Preserve its
            // bindings/resources when the busy visual is hosted beside that body.
            busy.Resources = busyOwner.Resources;
        }
        var state = context?.ActiveChain().Select(item => item.Current?.View).Where(item => item != null)
            .Select(item => item!.LoadingState).LastOrDefault(item => item.Requested) ?? default;
        var requested = IsBusy || state.Requested;
        var type = state.Requested ? state.Type : LoadingType.Loading;
        var visible = (context == null || context.IsActive && !context.IsNavigating) && requested && (template != null || useDefault);
        if (visible && busyContent == null)
        {
            if (template != null)
            {
                if (busyUsesTypedContext)
                {
                    loadingContext = new();
                    loadingContext.Update(type, busyOwner?.BindingContext);
                    busy.BindingContext = loadingContext;
                }
                else busy.BindingContext = busyOwner?.BindingContext;
                var content = NavigationToolbar.TemplateView(template);
                if (content.Parent != null || content is ViewBase)
                    throw new InvalidOperationException("A busy template must create a fresh, detached ordinary View.");
                busyContent = content;
            }
            else
            {
                defaultBusyIndicator = new ActivityIndicator { WidthRequest = 36, HeightRequest = 36,
                    HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
                SemanticProperties.SetDescription(defaultBusyIndicator, "Loading");
                busyContent = defaultBusyIndicator;
            }
            busy.Add(busyContent);
        }
        else if (busyUsesTypedContext && loadingContext != null)
            loadingContext.Update(type, busyOwner?.BindingContext);
        else if (!busyUsesTypedContext && busyOwner != null)
            busy.BindingContext = busyOwner.BindingContext;
        busy.IsVisible = visible;
        if (defaultBusyIndicator != null) defaultBusyIndicator.IsRunning = visible;
        if (backdrop != null) SetInheritedBindingContext(backdrop, backdropOwner?.BindingContext);
    }

    private void BusyOwnerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(BindingContext) or nameof(Resources) or null or "") RefreshBusy();
    }

    private void ClearBusy()
    {
        busy.IsVisible = false;
        busy.Background = defaultLoadingBackdrop;
        if (defaultBusyIndicator != null) defaultBusyIndicator.IsRunning = false;
        if (busyOwner != null) busyOwner.PropertyChanged -= BusyOwnerChanged;
        if (busyOwner != null) busy.Resources = new ResourceDictionary();
        loadingContext?.Clear();
        busy.BindingContext = null;
        busy.Clear();
        busyContent = null; defaultBusyIndicator = null; loadingContext = null; busyOwner = null; busyTemplate = null;
        busyUsesTypedContext = false;
    }
}
