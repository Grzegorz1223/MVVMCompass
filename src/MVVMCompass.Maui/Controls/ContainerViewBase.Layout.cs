namespace MVVMCompass;

public abstract partial class ContainerViewBase
{
    /// <summary>Identifies BodyTemplate.</summary>
    public static readonly BindableProperty BodyTemplateProperty = BindableProperty.Create(nameof(BodyTemplate), typeof(ControlTemplate), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Optional frame around the tabs, shared content and detail. Include a ContentPresenter; the header and footer remain outside the frame.</summary>
    public ControlTemplate? BodyTemplate { get => (ControlTemplate?)GetValue(BodyTemplateProperty); set => SetValue(BodyTemplateProperty, value); }

    /// <summary>Identifies DetailMargin.</summary>
    public static readonly BindableProperty DetailMarginProperty = BindableProperty.Create(nameof(DetailMargin), typeof(Thickness), typeof(ContainerViewBase), Thickness.Zero, propertyChanged: Changed,
        validateValue: (_, value) => value is Thickness margin && double.IsFinite(margin.Left) && margin.Left >= 0 && double.IsFinite(margin.Top) && margin.Top >= 0 && double.IsFinite(margin.Right) && margin.Right >= 0 && double.IsFinite(margin.Bottom) && margin.Bottom >= 0);
    /// <summary>Insets the retained detail presenter without moving the rail, shared content or overlays.</summary>
    public Thickness DetailMargin { get => (Thickness)GetValue(DetailMarginProperty); set => SetValue(DetailMarginProperty, value); }

    /// <summary>Identifies TabBarContent.</summary>
    public static readonly BindableProperty TabBarContentProperty = BindableProperty.Create(nameof(TabBarContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Optional replacement for the entire tab row, including application-owned trailing chrome.</summary>
    public View? TabBarContent { get => (View?)GetValue(TabBarContentProperty); set => SetValue(TabBarContentProperty, value); }

    /// <summary>Identifies IsTabBarVisible.</summary>
    public static readonly BindableProperty IsTabBarVisibleProperty = BindableProperty.Create(nameof(IsTabBarVisible), typeof(bool), typeof(ContainerViewBase), true, propertyChanged: Changed);
    /// <summary>Whether the tab selector row occupies layout space.</summary>
    public bool IsTabBarVisible { get => (bool)GetValue(IsTabBarVisibleProperty); set => SetValue(IsTabBarVisibleProperty, value); }

    /// <summary>Identifies IsTabBarEnabled.</summary>
    public static readonly BindableProperty IsTabBarEnabledProperty = BindableProperty.Create(nameof(IsTabBarEnabled), typeof(bool), typeof(ContainerViewBase), true, propertyChanged: Changed);
    /// <summary>Whether built-in tab selection input is enabled. Trailing controls remain interactive.</summary>
    public bool IsTabBarEnabled { get => (bool)GetValue(IsTabBarEnabledProperty); set => SetValue(IsTabBarEnabledProperty, value); }

    /// <summary>Identifies TabBarHeight.</summary>
    public static readonly BindableProperty TabBarHeightProperty = BindableProperty.Create(nameof(TabBarHeight), typeof(double), typeof(ContainerViewBase), -1d, propertyChanged: Changed, validateValue: (_, value) => double.IsFinite((double)value) && (double)value >= -1);
    /// <summary>Height of the horizontal tab row; -1 uses its natural height.</summary>
    public double TabBarHeight { get => (double)GetValue(TabBarHeightProperty); set => SetValue(TabBarHeightProperty, value); }

    /// <summary>Identifies LeadingContent.</summary>
    public static readonly BindableProperty LeadingContentProperty = BindableProperty.Create(nameof(LeadingContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Content beside the detail body, below shared content.</summary>
    public View? LeadingContent { get => (View?)GetValue(LeadingContentProperty); set => SetValue(LeadingContentProperty, value); }

    /// <summary>Identifies LeadingContentWidth.</summary>
    public static readonly BindableProperty LeadingContentWidthProperty = BindableProperty.Create(nameof(LeadingContentWidth), typeof(GridLength), typeof(ContainerViewBase), GridLength.Auto, propertyChanged: Changed, validateValue: (_, value) => value is GridLength length && double.IsFinite(length.Value) && length.Value >= 0);
    /// <summary>Width reserved for visible leading body content.</summary>
    public GridLength LeadingContentWidth { get => (GridLength)GetValue(LeadingContentWidthProperty); set => SetValue(LeadingContentWidthProperty, value); }

    /// <summary>Identifies IsLeadingContentVisible.</summary>
    public static readonly BindableProperty IsLeadingContentVisibleProperty = BindableProperty.Create(nameof(IsLeadingContentVisible), typeof(bool), typeof(ContainerViewBase), true, propertyChanged: Changed);
    /// <summary>Whether leading body content occupies layout space.</summary>
    public bool IsLeadingContentVisible { get => (bool)GetValue(IsLeadingContentVisibleProperty); set => SetValue(IsLeadingContentVisibleProperty, value); }

    /// <summary>Identifies BodyRowOverlayContent.</summary>
    public static readonly BindableProperty BodyRowOverlayContentProperty = BindableProperty.Create(nameof(BodyRowOverlayContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Overlay across the detail and leading body content, below shared content.</summary>
    public View? BodyRowOverlayContent { get => (View?)GetValue(BodyRowOverlayContentProperty); set => SetValue(BodyRowOverlayContentProperty, value); }

    /// <summary>Gets declared tab metadata, including hidden destinations and any eagerly prepared models.</summary>
    public IReadOnlyList<NavigationItemContext> TabItems => context?.TabItems ?? (IReadOnlyList<NavigationItemContext>)Array.Empty<NavigationItemContext>();
    /// <summary>Gets the selected tab root metadata.</summary>
    public NavigationItemContext? CurrentTab => context?.TabItems.FirstOrDefault(item => item.Id == context.SelectedDestinationId);
    /// <summary>Raised after the selected root is activated and its container's OnActiveTabChanged callback completes, including replacement of a selected root.</summary>
    public event EventHandler? CurrentTabChanged;
    private NavigationItemContext? lastSelectedTab;
    private ViewModelBase? lastSelectedModel;
    private void ContainerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(NavigationContext.SelectedDestinationId)) return;
        OnPropertyChanged(nameof(TabItems));
        var selected = CurrentTab;
        if (ReferenceEquals(selected, lastSelectedTab) && ReferenceEquals(selected?.ViewModel, lastSelectedModel)) return;
        lastSelectedTab = selected; lastSelectedModel = selected?.ViewModel;
        OnPropertyChanged(nameof(CurrentTab));
    }
    private void ContainerSelectionChanged(object? sender, EventArgs args) => CurrentTabChanged?.Invoke(this, EventArgs.Empty);
    private void ApplyLayout(NavigationView view)
    {
        view.BodyTemplate = BodyTemplate;
        view.DetailMargin = DetailMargin;
        view.TabBarContent = TabBarContent;
        view.IsTabBarVisible = IsTabBarVisible;
        view.IsTabBarEnabled = IsTabBarEnabled;
        view.TabBarHeight = TabBarHeight;
        view.LeadingContent = LeadingContent;
        view.LeadingContentWidth = LeadingContentWidth;
        view.IsLeadingContentVisible = IsLeadingContentVisible;
        view.BodyRowOverlayContent = BodyRowOverlayContent;
    }
}
