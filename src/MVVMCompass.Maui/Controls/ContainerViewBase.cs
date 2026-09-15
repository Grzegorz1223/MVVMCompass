namespace MVVMCompass;

internal interface IContainerView
{
    NavigationPresentation Kind { get; }
    void Attach(NavigationContext context);
}

/// <summary>Shared visual configuration for ViewModel-owned tabs and flyouts. Destinations are declared by the model.</summary>
public abstract class ContainerViewBase : ViewBase, IContainerView
{
    private NavigationContext? context;
    /// <summary>Creates a container bound to its owner model.</summary>
    protected ContainerViewBase(ViewModelBase model) : base(model) { }
    internal abstract NavigationPresentation Presentation { get; }
    NavigationPresentation IContainerView.Kind => Presentation;
    void IContainerView.Attach(NavigationContext owner)
    {
        context = owner;
        owner.View.BindingContext = ViewModel;
        Content = owner.View;
        ApplyVisuals();
    }
    /// <summary>Identifies SharedContent.</summary>
    public static readonly BindableProperty SharedContentProperty = BindableProperty.Create(nameof(SharedContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Persistent content bound to this container model.</summary>
    public View? SharedContent { get => (View?)GetValue(SharedContentProperty); set => SetValue(SharedContentProperty, value); }
    /// <summary>Identifies SharedContentPosition.</summary>
    public static readonly BindableProperty SharedContentPositionProperty = BindableProperty.Create(nameof(SharedContentPosition), typeof(SharedContentPosition), typeof(ContainerViewBase), SharedContentPosition.Top, propertyChanged: Changed, validateValue: (_, value) => Enum.IsDefined((SharedContentPosition)value));
    /// <summary>Placement of shared content above or below the detail body.</summary>
    public SharedContentPosition SharedContentPosition { get => (SharedContentPosition)GetValue(SharedContentPositionProperty); set => SetValue(SharedContentPositionProperty, value); }
    /// <summary>Identifies HeaderContent.</summary>
    public static readonly BindableProperty HeaderContentProperty = BindableProperty.Create(nameof(HeaderContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Persistent content above the body and selector.</summary>
    public View? HeaderContent { get => (View?)GetValue(HeaderContentProperty); set => SetValue(HeaderContentProperty, value); }
    /// <summary>Identifies FooterContent.</summary>
    public static readonly BindableProperty FooterContentProperty = BindableProperty.Create(nameof(FooterContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Persistent content below the body and selector.</summary>
    public View? FooterContent { get => (View?)GetValue(FooterContentProperty); set => SetValue(FooterContentProperty, value); }
    /// <summary>Identifies OverlayContent.</summary>
    public static readonly BindableProperty OverlayContentProperty = BindableProperty.Create(nameof(OverlayContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Content covering the entire container.</summary>
    public View? OverlayContent { get => (View?)GetValue(OverlayContentProperty); set => SetValue(OverlayContentProperty, value); }
    /// <summary>Identifies BodyOverlayContent.</summary>
    public static readonly BindableProperty BodyOverlayContentProperty = BindableProperty.Create(nameof(BodyOverlayContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Content covering the detail body.</summary>
    public View? BodyOverlayContent { get => (View?)GetValue(BodyOverlayContentProperty); set => SetValue(BodyOverlayContentProperty, value); }
    /// <summary>Identifies TabBarPosition.</summary>
    public static readonly BindableProperty TabBarPositionProperty = BindableProperty.Create(nameof(TabBarPosition), typeof(TabBarPosition), typeof(ContainerViewBase), TabBarPosition.Top, propertyChanged: Changed, validateValue: (_, value) => Enum.IsDefined((TabBarPosition)value));
    /// <summary>The edge occupied by tabs.</summary>
    public TabBarPosition TabBarPosition { get => (TabBarPosition)GetValue(TabBarPositionProperty); set => SetValue(TabBarPositionProperty, value); }
    /// <summary>Identifies TabItemSizing.</summary>
    public static readonly BindableProperty TabItemSizingProperty = BindableProperty.Create(nameof(TabItemSizing), typeof(TabItemSizing), typeof(ContainerViewBase), TabItemSizing.Content, propertyChanged: Changed, validateValue: (_, value) => Enum.IsDefined((TabItemSizing)value));
    /// <summary>Natural or equal sizing of visible tabs.</summary>
    public TabItemSizing TabItemSizing { get => (TabItemSizing)GetValue(TabItemSizingProperty); set => SetValue(TabItemSizingProperty, value); }
    /// <summary>Identifies TabBarBackground.</summary>
    public static readonly BindableProperty TabBarBackgroundProperty = BindableProperty.Create(nameof(TabBarBackground), typeof(Brush), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>The background of the entire tab strip, including unused space.</summary>
    public Brush? TabBarBackground { get => (Brush?)GetValue(TabBarBackgroundProperty); set => SetValue(TabBarBackgroundProperty, value); }
    /// <summary>Identifies TabBarCenterContent.</summary>
    public static readonly BindableProperty TabBarCenterContentProperty = BindableProperty.Create(nameof(TabBarCenterContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Content reserved in the tab strip between two groups of items.</summary>
    public View? TabBarCenterContent { get => (View?)GetValue(TabBarCenterContentProperty); set => SetValue(TabBarCenterContentProperty, value); }
    /// <summary>Identifies TabBarTrailingContent.</summary>
    public static readonly BindableProperty TabBarTrailingContentProperty = BindableProperty.Create(nameof(TabBarTrailingContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Content reserved at the end of the tab strip.</summary>
    public View? TabBarTrailingContent { get => (View?)GetValue(TabBarTrailingContentProperty); set => SetValue(TabBarTrailingContentProperty, value); }
    /// <summary>Identifies SelectedTabItemTemplate.</summary>
    public static readonly BindableProperty SelectedTabItemTemplateProperty = BindableProperty.Create(nameof(SelectedTabItemTemplate), typeof(DataTemplate), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>The appearance of a selected tab; selection input is handled by the library.</summary>
    public DataTemplate? SelectedTabItemTemplate { get => (DataTemplate?)GetValue(SelectedTabItemTemplateProperty); set => SetValue(SelectedTabItemTemplateProperty, value); }
    /// <summary>Identifies UnselectedTabItemTemplate.</summary>
    public static readonly BindableProperty UnselectedTabItemTemplateProperty = BindableProperty.Create(nameof(UnselectedTabItemTemplate), typeof(DataTemplate), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>The appearance of an unselected tab.</summary>
    public DataTemplate? UnselectedTabItemTemplate { get => (DataTemplate?)GetValue(UnselectedTabItemTemplateProperty); set => SetValue(UnselectedTabItemTemplateProperty, value); }
    /// <summary>Identifies FlyoutPanelBackground.</summary>
    public static readonly BindableProperty FlyoutPanelBackgroundProperty = BindableProperty.Create(nameof(FlyoutPanelBackground), typeof(Brush), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>The background of the entire flyout panel.</summary>
    public Brush? FlyoutPanelBackground { get => (Brush?)GetValue(FlyoutPanelBackgroundProperty); set => SetValue(FlyoutPanelBackgroundProperty, value); }
    /// <summary>Identifies FlyoutHeaderContent.</summary>
    public static readonly BindableProperty FlyoutHeaderContentProperty = BindableProperty.Create(nameof(FlyoutHeaderContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Content at the top of the flyout panel.</summary>
    public View? FlyoutHeaderContent { get => (View?)GetValue(FlyoutHeaderContentProperty); set => SetValue(FlyoutHeaderContentProperty, value); }
    /// <summary>Identifies FlyoutFooterContent.</summary>
    public static readonly BindableProperty FlyoutFooterContentProperty = BindableProperty.Create(nameof(FlyoutFooterContent), typeof(View), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>Content at the bottom of the flyout panel.</summary>
    public View? FlyoutFooterContent { get => (View?)GetValue(FlyoutFooterContentProperty); set => SetValue(FlyoutFooterContentProperty, value); }
    /// <summary>Identifies SelectedFlyoutItemTemplate.</summary>
    public static readonly BindableProperty SelectedFlyoutItemTemplateProperty = BindableProperty.Create(nameof(SelectedFlyoutItemTemplate), typeof(DataTemplate), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>The appearance of a selected flyout item.</summary>
    public DataTemplate? SelectedFlyoutItemTemplate { get => (DataTemplate?)GetValue(SelectedFlyoutItemTemplateProperty); set => SetValue(SelectedFlyoutItemTemplateProperty, value); }
    /// <summary>Identifies UnselectedFlyoutItemTemplate.</summary>
    public static readonly BindableProperty UnselectedFlyoutItemTemplateProperty = BindableProperty.Create(nameof(UnselectedFlyoutItemTemplate), typeof(DataTemplate), typeof(ContainerViewBase), null, propertyChanged: Changed);
    /// <summary>The appearance of an unselected flyout item.</summary>
    public DataTemplate? UnselectedFlyoutItemTemplate { get => (DataTemplate?)GetValue(UnselectedFlyoutItemTemplateProperty); set => SetValue(UnselectedFlyoutItemTemplateProperty, value); }
    private static void Changed(BindableObject bindable, object oldValue, object newValue) => ((ContainerViewBase)bindable).ApplyVisuals();
    private void ApplyVisuals()
    {
        if (context == null) return;
        var view = context.View;
        view.SharedContent = SharedContent;
        view.SharedContentPosition = SharedContentPosition;
        view.HeaderContent = HeaderContent;
        view.FooterContent = FooterContent;
        view.OverlayContent = OverlayContent;
        view.BodyOverlayContent = BodyOverlayContent;
        view.TabBarPosition = TabBarPosition;
        view.TabItemSizing = TabItemSizing;
        view.TabBarBackground = TabBarBackground;
        view.TabBarCenterContent = TabBarCenterContent;
        view.TabBarTrailingContent = TabBarTrailingContent;
        view.SelectedTabItemTemplate = SelectedTabItemTemplate;
        view.UnselectedTabItemTemplate = UnselectedTabItemTemplate;
        view.FlyoutPanelBackground = FlyoutPanelBackground;
        view.FlyoutHeaderContent = FlyoutHeaderContent;
        view.FlyoutFooterContent = FlyoutFooterContent;
        view.SelectedFlyoutItemTemplate = SelectedFlyoutItemTemplate;
        view.UnselectedFlyoutItemTemplate = UnselectedFlyoutItemTemplate;
        view.Refresh();
    }
}

/// <summary>Custom tabs with XAML visuals and membership supplied by SetTabs in the model.</summary>
public class TabbedViewBase<TViewModel> : ContainerViewBase where TViewModel : ViewModelBase
{
    /// <summary>Creates tabs bound to the supplied model.</summary>
    public TabbedViewBase(TViewModel model) : base(model) { }
    /// <summary>Gets the container model.</summary>
    public new TViewModel ViewModel => (TViewModel)base.ViewModel;
    internal override NavigationPresentation Presentation => NavigationPresentation.Tabs;
}

/// <summary>A custom flyout with XAML visuals and membership supplied by SetFlyoutItems in the model.</summary>
public class FlyoutViewBase<TViewModel> : ContainerViewBase where TViewModel : ViewModelBase
{
    /// <summary>Creates a flyout bound to the supplied model.</summary>
    public FlyoutViewBase(TViewModel model) : base(model) { }
    /// <summary>Gets the container model.</summary>
    public new TViewModel ViewModel => (TViewModel)base.ViewModel;
    internal override NavigationPresentation Presentation => NavigationPresentation.Flyout;
}
