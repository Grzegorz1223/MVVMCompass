namespace MVVMCompass;

/// <summary>Persistent custom navigation layout shared by plain, tabbed, rail, and flyout configurations.</summary>
internal sealed partial class NavigationView : ContentView
{
    private readonly Grid root = new();
    private readonly Grid chrome = new() { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
    private readonly Grid flyoutOverlay = new() { IsVisible = false };
    private NavigationView? presentedFlyout;
    private WeakReference<VisualElement>? previousFocus;
    private readonly Grid body = new() { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
        RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
    private readonly ContentView sharedTop = new();
    private readonly ContentView sharedBottom = new();
    private readonly Grid drawer = new() { WidthRequest = 300, HorizontalOptions = LayoutOptions.Start,
        RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
    private readonly Grid drawerLayer = new();
    private readonly Button scrim = NavigationButton.Create();
    private readonly NavigationSelector menu = new() { Orientation = StackOrientation.Vertical };
    private readonly ContentView menuHeader = new();
    private readonly ContentView menuFooter = new();
    private readonly ContentView header = new();
    private readonly ContentView footer = new();
    private readonly ContentView overlay = new() { InputTransparent = true };
    private readonly ContentView bodyOverlay = new() { InputTransparent = true };
    private readonly Brush defaultLoadingBackdrop = new SolidColorBrush(Color.FromArgb("#33FFFFFF"));
    private readonly Grid busy = new() { InputTransparent = true };
    private NavigationContext? context;
    private bool disconnected;

    /// <summary>Creates a reusable layout and its default toolbar and selectors.</summary>
    public NavigationView()
    {
        Toolbar = new NavigationToolbar();
        Presenter = new NavigationContentPresenter();
        TabSelector = new NavigationSelector();
        RailSelector = new NavigationSelector { Orientation = StackOrientation.Vertical };
        chrome.Add(Toolbar, 0, 0); chrome.Add(header, 0, 1);
        var bodyLayers = new Grid(); bodyLayers.Add(Presenter); bodyLayers.Add(bodyOverlay);
        var detail = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)] };
        detail.Add(sharedTop, 0, 0); detail.Add(bodyLayers, 0, 1); detail.Add(sharedBottom, 0, 2);
        body.Add(detail, 1, 1); body.Add(TabSelector, 1, 0); body.Add(RailSelector, 0, 1);
        chrome.Add(body, 0, 2); chrome.Add(footer, 0, 3);
        scrim.BackgroundColor = Color.FromArgb("#77000000");
        scrim.Command = new Command(() => context?.SetFlyout(false));
        SemanticProperties.SetDescription(scrim, "Close menu");
        drawer.Add(menuHeader, 0, 0); drawer.Add(menu, 0, 1); drawer.Add(menuFooter, 0, 2);
        drawerLayer.Add(scrim); drawerLayer.Add(drawer);
        root.Add(chrome); root.Add(overlay); root.Add(busy); root.Add(flyoutOverlay);
        Content = root;
        ToolbarDefaults = new NavigationToolbarDefinition();
        Refresh();
    }

    /// <summary>Identifies host-default toolbar configuration.</summary>
    public static readonly BindableProperty ToolbarDefaultsProperty = BindableProperty.Create(nameof(ToolbarDefaults), typeof(NavigationToolbarDefinition), typeof(NavigationView), propertyChanged: Changed);
    /// <summary>Identifies content below the toolbar and above the selector/body.</summary>
    public static readonly BindableProperty HeaderContentProperty = BindableProperty.Create(nameof(HeaderContent), typeof(View), typeof(NavigationView), propertyChanged: Changed);
    /// <summary>Identifies content below the body.</summary>
    public static readonly BindableProperty FooterContentProperty = BindableProperty.Create(nameof(FooterContent), typeof(View), typeof(NavigationView), propertyChanged: Changed);
    /// <summary>Identifies an overlay covering the whole custom layout.</summary>
    public static readonly BindableProperty OverlayContentProperty = BindableProperty.Create(nameof(OverlayContent), typeof(View), typeof(NavigationView), propertyChanged: Changed);
    /// <summary>Identifies an overlay confined to the body.</summary>
    public static readonly BindableProperty BodyOverlayContentProperty = BindableProperty.Create(nameof(BodyOverlayContent), typeof(View), typeof(NavigationView), propertyChanged: Changed);
    /// <summary>Identifies visual loading state; this does not veto Back.</summary>
    public static readonly BindableProperty IsBusyProperty = BindableProperty.Create(nameof(IsBusy), typeof(bool), typeof(NavigationView), false, propertyChanged: Changed);
    /// <summary>Identifies whether a right swipe requests guarded Back.</summary>
    public static readonly BindableProperty IsBackSwipeEnabledProperty = BindableProperty.Create(nameof(IsBackSwipeEnabled), typeof(bool), typeof(NavigationView), false, propertyChanged: Changed);
    /// <summary>Gets the persistent toolbar instance.</summary>
    public NavigationToolbar Toolbar { get; }
    /// <summary>Gets the body transition presenter.</summary>
    public NavigationContentPresenter Presenter { get; }
    /// <summary>Gets the horizontal tab selector for customization.</summary>
    public NavigationSelector TabSelector { get; }
    /// <summary>Gets the vertical rail selector for customization.</summary>
    public NavigationSelector RailSelector { get; }
    /// <summary>Gets the owning navigation context, or null for an unattached layout.</summary>
    public NavigationContext? Context => context;
    /// <summary>Gets or sets toolbar defaults inherited by screens.</summary>
    public NavigationToolbarDefinition ToolbarDefaults { get => (NavigationToolbarDefinition)GetValue(ToolbarDefaultsProperty); set => SetValue(ToolbarDefaultsProperty, value); }
    /// <summary>Gets or sets the shared header.</summary>
    public View? HeaderContent { get => (View?)GetValue(HeaderContentProperty); set => SetValue(HeaderContentProperty, value); }
    /// <summary>Gets or sets the shared footer.</summary>
    public View? FooterContent { get => (View?)GetValue(FooterContentProperty); set => SetValue(FooterContentProperty, value); }
    /// <summary>Gets or sets an overlay over the complete layout.</summary>
    public View? OverlayContent { get => (View?)GetValue(OverlayContentProperty); set => SetValue(OverlayContentProperty, value); }
    /// <summary>Gets or sets an overlay over the body only.</summary>
    public View? BodyOverlayContent { get => (View?)GetValue(BodyOverlayContentProperty); set => SetValue(BodyOverlayContentProperty, value); }
    /// <summary>Gets or sets the loading indicator independently of navigation permission.</summary>
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    /// <summary>Gets or sets an opt-in swipe that uses the same guard as toolbar and platform Back.</summary>
    public bool IsBackSwipeEnabled { get => (bool)GetValue(IsBackSwipeEnabledProperty); set => SetValue(IsBackSwipeEnabledProperty, value); }

    /// <summary>Identifies SharedContent.</summary>
    public static readonly BindableProperty SharedContentProperty = BindableProperty.Create(nameof(SharedContent), typeof(View), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>Persistent content bound to this container model.</summary>
    public View? SharedContent { get => (View?)GetValue(SharedContentProperty); set => SetValue(SharedContentProperty, value); }
    /// <summary>Identifies SharedContentPosition.</summary>
    public static readonly BindableProperty SharedContentPositionProperty = BindableProperty.Create(nameof(SharedContentPosition), typeof(SharedContentPosition), typeof(NavigationView), SharedContentPosition.Top, propertyChanged: Changed);
    /// <summary>Placement of shared content above or below the detail body.</summary>
    public SharedContentPosition SharedContentPosition { get => (SharedContentPosition)GetValue(SharedContentPositionProperty); set => SetValue(SharedContentPositionProperty, value); }
    /// <summary>Identifies TabBarPosition.</summary>
    public static readonly BindableProperty TabBarPositionProperty = BindableProperty.Create(nameof(TabBarPosition), typeof(TabBarPosition), typeof(NavigationView), TabBarPosition.Top, propertyChanged: Changed);
    /// <summary>The edge occupied by tabs.</summary>
    public TabBarPosition TabBarPosition { get => (TabBarPosition)GetValue(TabBarPositionProperty); set => SetValue(TabBarPositionProperty, value); }
    /// <summary>Identifies TabItemSizing.</summary>
    public static readonly BindableProperty TabItemSizingProperty = BindableProperty.Create(nameof(TabItemSizing), typeof(TabItemSizing), typeof(NavigationView), TabItemSizing.Content, propertyChanged: Changed);
    /// <summary>Natural or equal sizing of visible tabs.</summary>
    public TabItemSizing TabItemSizing { get => (TabItemSizing)GetValue(TabItemSizingProperty); set => SetValue(TabItemSizingProperty, value); }
    internal static readonly BindableProperty TabBarPaddingProperty = BindableProperty.Create(nameof(TabBarPadding), typeof(Thickness), typeof(NavigationView), new Thickness(4), propertyChanged: Changed);
    internal Thickness TabBarPadding { get => (Thickness)GetValue(TabBarPaddingProperty); set => SetValue(TabBarPaddingProperty, value); }
    internal static readonly BindableProperty TabItemSpacingProperty = BindableProperty.Create(nameof(TabItemSpacing), typeof(double), typeof(NavigationView), 4d, propertyChanged: Changed);
    internal double TabItemSpacing { get => (double)GetValue(TabItemSpacingProperty); set => SetValue(TabItemSpacingProperty, value); }
    internal static readonly BindableProperty TabScrollBarVisibilityProperty = BindableProperty.Create(nameof(TabScrollBarVisibility), typeof(ScrollBarVisibility), typeof(NavigationView), ScrollBarVisibility.Never, propertyChanged: Changed);
    internal ScrollBarVisibility TabScrollBarVisibility { get => (ScrollBarVisibility)GetValue(TabScrollBarVisibilityProperty); set => SetValue(TabScrollBarVisibilityProperty, value); }
    /// <summary>Identifies TabBarBackground.</summary>
    public static readonly BindableProperty TabBarBackgroundProperty = BindableProperty.Create(nameof(TabBarBackground), typeof(Brush), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>The background of the entire tab strip, including unused space.</summary>
    public Brush? TabBarBackground { get => (Brush?)GetValue(TabBarBackgroundProperty); set => SetValue(TabBarBackgroundProperty, value); }
    /// <summary>Identifies TabBarCenterContent.</summary>
    public static readonly BindableProperty TabBarCenterContentProperty = BindableProperty.Create(nameof(TabBarCenterContent), typeof(View), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>Content reserved in the tab strip between two groups of items.</summary>
    public View? TabBarCenterContent { get => (View?)GetValue(TabBarCenterContentProperty); set => SetValue(TabBarCenterContentProperty, value); }
    /// <summary>Identifies TabBarTrailingContent.</summary>
    public static readonly BindableProperty TabBarTrailingContentProperty = BindableProperty.Create(nameof(TabBarTrailingContent), typeof(View), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>Content reserved at the end of the tab strip.</summary>
    public View? TabBarTrailingContent { get => (View?)GetValue(TabBarTrailingContentProperty); set => SetValue(TabBarTrailingContentProperty, value); }
    /// <summary>Identifies SelectedTabItemTemplate.</summary>
    public static readonly BindableProperty SelectedTabItemTemplateProperty = BindableProperty.Create(nameof(SelectedTabItemTemplate), typeof(DataTemplate), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>The appearance of a selected tab; selection input is handled by the library.</summary>
    public DataTemplate? SelectedTabItemTemplate { get => (DataTemplate?)GetValue(SelectedTabItemTemplateProperty); set => SetValue(SelectedTabItemTemplateProperty, value); }
    /// <summary>Identifies UnselectedTabItemTemplate.</summary>
    public static readonly BindableProperty UnselectedTabItemTemplateProperty = BindableProperty.Create(nameof(UnselectedTabItemTemplate), typeof(DataTemplate), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>The appearance of an unselected tab.</summary>
    public DataTemplate? UnselectedTabItemTemplate { get => (DataTemplate?)GetValue(UnselectedTabItemTemplateProperty); set => SetValue(UnselectedTabItemTemplateProperty, value); }
    /// <summary>Identifies FlyoutPanelBackground.</summary>
    public static readonly BindableProperty FlyoutPanelBackgroundProperty = BindableProperty.Create(nameof(FlyoutPanelBackground), typeof(Brush), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>The background of the entire flyout panel.</summary>
    public Brush? FlyoutPanelBackground { get => (Brush?)GetValue(FlyoutPanelBackgroundProperty); set => SetValue(FlyoutPanelBackgroundProperty, value); }
    /// <summary>Identifies FlyoutHeaderContent.</summary>
    public static readonly BindableProperty FlyoutHeaderContentProperty = BindableProperty.Create(nameof(FlyoutHeaderContent), typeof(View), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>Content at the top of the flyout panel.</summary>
    public View? FlyoutHeaderContent { get => (View?)GetValue(FlyoutHeaderContentProperty); set => SetValue(FlyoutHeaderContentProperty, value); }
    /// <summary>Identifies FlyoutFooterContent.</summary>
    public static readonly BindableProperty FlyoutFooterContentProperty = BindableProperty.Create(nameof(FlyoutFooterContent), typeof(View), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>Content at the bottom of the flyout panel.</summary>
    public View? FlyoutFooterContent { get => (View?)GetValue(FlyoutFooterContentProperty); set => SetValue(FlyoutFooterContentProperty, value); }
    /// <summary>Identifies content following destinations inside the flyout's scrollable list.</summary>
    public static readonly BindableProperty FlyoutTrailingContentProperty = BindableProperty.Create(nameof(FlyoutTrailingContent), typeof(View), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>Gets or sets interactive content after the visible destinations, with the container's binding context.</summary>
    public View? FlyoutTrailingContent { get => (View?)GetValue(FlyoutTrailingContentProperty); set => SetValue(FlyoutTrailingContentProperty, value); }
    /// <summary>Identifies padding inside the scrollable flyout destination list.</summary>
    public static readonly BindableProperty FlyoutListPaddingProperty = BindableProperty.Create(nameof(FlyoutListPadding), typeof(Thickness), typeof(NavigationView), new Thickness(4), propertyChanged: Changed, validateValue: (_, value) => value is Thickness padding && double.IsFinite(padding.Left) && padding.Left >= 0 && double.IsFinite(padding.Top) && padding.Top >= 0 && double.IsFinite(padding.Right) && padding.Right >= 0 && double.IsFinite(padding.Bottom) && padding.Bottom >= 0);
    /// <summary>Gets or sets destination-list padding. Header and footer padding are independent. The default is 4.</summary>
    public Thickness FlyoutListPadding { get => (Thickness)GetValue(FlyoutListPaddingProperty); set => SetValue(FlyoutListPaddingProperty, value); }
    /// <summary>Identifies spacing between flyout destinations.</summary>
    public static readonly BindableProperty FlyoutItemSpacingProperty = BindableProperty.Create(nameof(FlyoutItemSpacing), typeof(double), typeof(NavigationView), 4d, propertyChanged: Changed,
        validateValue: (_, value) => double.IsFinite((double)value) && (double)value >= 0);
    /// <summary>Gets or sets the gap between flyout destinations in device-independent units. The default is 4.</summary>
    public double FlyoutItemSpacing { get => (double)GetValue(FlyoutItemSpacingProperty); set => SetValue(FlyoutItemSpacingProperty, value); }
    /// <summary>Identifies the flyout list's vertical scrollbar policy.</summary>
    public static readonly BindableProperty FlyoutScrollBarVisibilityProperty = BindableProperty.Create(nameof(FlyoutScrollBarVisibility), typeof(ScrollBarVisibility), typeof(NavigationView), ScrollBarVisibility.Default, propertyChanged: Changed,
        validateValue: (_, value) => Enum.IsDefined((ScrollBarVisibility)value));
    /// <summary>Gets or sets the native vertical scrollbar policy. The default follows the platform.</summary>
    public ScrollBarVisibility FlyoutScrollBarVisibility { get => (ScrollBarVisibility)GetValue(FlyoutScrollBarVisibilityProperty); set => SetValue(FlyoutScrollBarVisibilityProperty, value); }
    /// <summary>Identifies SelectedFlyoutItemTemplate.</summary>
    public static readonly BindableProperty SelectedFlyoutItemTemplateProperty = BindableProperty.Create(nameof(SelectedFlyoutItemTemplate), typeof(DataTemplate), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>The appearance of a selected flyout item.</summary>
    public DataTemplate? SelectedFlyoutItemTemplate { get => (DataTemplate?)GetValue(SelectedFlyoutItemTemplateProperty); set => SetValue(SelectedFlyoutItemTemplateProperty, value); }
    /// <summary>Identifies UnselectedFlyoutItemTemplate.</summary>
    public static readonly BindableProperty UnselectedFlyoutItemTemplateProperty = BindableProperty.Create(nameof(UnselectedFlyoutItemTemplate), typeof(DataTemplate), typeof(NavigationView), null, propertyChanged: Changed);
    /// <summary>The appearance of an unselected flyout item.</summary>
    public DataTemplate? UnselectedFlyoutItemTemplate { get => (DataTemplate?)GetValue(UnselectedFlyoutItemTemplateProperty); set => SetValue(UnselectedFlyoutItemTemplateProperty, value); }

    private SwipeGestureRecognizer? swipe;
    private static void Changed(BindableObject bindable, object oldValue, object newValue) => ((NavigationView)bindable).Refresh();
    /// <summary>Preserves the host's binding context for shared toolbar defaults.</summary>
    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (ToolbarDefaults != null) SetInheritedBindingContext(ToolbarDefaults, BindingContext);
    }
    internal void Bind(NavigationContext owner) { disconnected = false; context = owner; Refresh(); }
    internal void Refresh()
    {
        if (Toolbar == null || disconnected) return;
        if (ToolbarDefaults != null) SetInheritedBindingContext(ToolbarDefaults, BindingContext);
        Toolbar.Bind(context, ToolbarDefaults);
        header.Content = HeaderContent; header.IsVisible = HeaderContent != null;
        footer.Content = FooterContent; footer.IsVisible = FooterContent != null;
        overlay.Content = OverlayContent; overlay.IsVisible = OverlayContent != null;
        overlay.InputTransparent = OverlayContent == null;
        bodyOverlay.Content = BodyOverlayContent; bodyOverlay.IsVisible = BodyOverlayContent != null;
        bodyOverlay.InputTransparent = BodyOverlayContent == null;
        RefreshBusy();
        var position = context?.Definition.Presentation == NavigationPresentation.Rail ? TabBarPosition.Left : TabBarPosition;
        var rail = position is TabBarPosition.Left or TabBarPosition.Right;
        var hasTabs = context?.TabItems.Count > 0;
        // Empty Auto tracks on both axes defer selector measurement until after the
        // star-sized body. Give unused edges zero size so chrome reserves its space first.
        body.RowDefinitions[0].Height = hasTabs && position == TabBarPosition.Top ? GridLength.Auto : new GridLength(0);
        body.RowDefinitions[2].Height = hasTabs && position == TabBarPosition.Bottom ? GridLength.Auto : new GridLength(0);
        body.ColumnDefinitions[0].Width = hasTabs && position == TabBarPosition.Left ? GridLength.Auto : new GridLength(0);
        body.ColumnDefinitions[2].Width = hasTabs && position == TabBarPosition.Right ? GridLength.Auto : new GridLength(0);
        Toolbar.IsVisible = context?.ParentContext == null && Toolbar.IsVisible;
        Grid.SetRow(TabSelector, position == TabBarPosition.Bottom ? 2 : 0);
        Grid.SetColumn(RailSelector, position == TabBarPosition.Right ? 2 : 0);
        var selected = rail ? RailSelector : TabSelector;
        var unused = rail ? TabSelector : RailSelector;
        unused.CenterContent = null; unused.TrailingContent = null;
        unused.ItemsSource = null; unused.IsVisible = false;
        selected.ItemsSource = context?.TabItems;
        selected.IsVisible = hasTabs;
        selected.ItemSizing = TabItemSizing;
        selected.Padding = TabBarPadding;
        selected.ItemSpacing = TabItemSpacing;
        selected.ScrollBarVisibility = TabScrollBarVisibility;
        selected.SelectedItemTemplate = SelectedTabItemTemplate;
        selected.UnselectedItemTemplate = UnselectedTabItemTemplate;
        selected.CenterContent = TabBarCenterContent;
        selected.TrailingContent = TabBarTrailingContent;
        selected.Background = TabBarBackground;
        var sharedHost = SharedContentPosition == SharedContentPosition.Top ? sharedTop : sharedBottom;
        var unusedShared = ReferenceEquals(sharedHost, sharedTop) ? sharedBottom : sharedTop;
        unusedShared.Content = null; unusedShared.IsVisible = false;
        sharedHost.Content = SharedContent; sharedHost.IsVisible = SharedContent != null;
        menu.ItemsSource = context?.MenuItems;
        menu.TrailingContent = FlyoutTrailingContent;
        menu.Padding = FlyoutListPadding;
        menu.ItemSpacing = FlyoutItemSpacing;
        menu.ScrollBarVisibility = FlyoutScrollBarVisibility;
        menu.SelectedItemTemplate = SelectedFlyoutItemTemplate; menu.UnselectedItemTemplate = UnselectedFlyoutItemTemplate;
        menuHeader.Content = FlyoutHeaderContent; menuHeader.IsVisible = FlyoutHeaderContent != null;
        menuFooter.Content = FlyoutFooterContent; menuFooter.IsVisible = FlyoutFooterContent != null;
        drawer.Background = FlyoutPanelBackground ?? new SolidColorBrush(Colors.White);
        context?.RootContext.View.RefreshFlyoutOverlay();
        var backSwipeEnabled = IsBackSwipeEnabled || context?.Current is { Children: null }
            && context.RootContext.ActiveChain().Any(owner => owner.Current?.View.IsBackSwipeEnabled == true);
        if (backSwipeEnabled && swipe == null)
        {
            swipe = new SwipeGestureRecognizer { Direction = SwipeDirection.Right };
            swipe.Swiped += BackSwiped;
            Presenter.GestureRecognizers.Add(swipe);
        }
        else if (!backSwipeEnabled && swipe != null)
        { swipe.Swiped -= BackSwiped; Presenter.GestureRecognizers.Remove(swipe); swipe = null; }
    }
    private void BackSwiped(object? sender, SwipedEventArgs args) => context?.RequestPlatformBack();
    internal void RefreshFlyoutOverlay()
    {
        if (context?.ParentContext != null || disconnected) return;
        var next = context is { IsActive: true, IsClosed: false }
            ? context.ActiveChain().LastOrDefault(owner => owner.ParentContext != null && !owner.IsClosed && owner.IsFlyoutOpen)?.View : null;
        if (ReferenceEquals(next, presentedFlyout)) return;
        var wasOpen = presentedFlyout != null;
        if (presentedFlyout != null)
        {
            FindFocused(presentedFlyout.drawerLayer)?.Unfocus();
            presentedFlyout.drawerLayer.IsVisible = false;
        }
        presentedFlyout = next;
        if (next != null)
        {
            if (!wasOpen)
            {
                var focused = FindFocused(root);
                previousFocus = focused == null ? null : new(focused);
                focused?.Unfocus();
            }
            // The menu retains its declaring container's data and local resources,
            // while its single visual parent is the window/modal root overlay.
            next.drawerLayer.BindingContext = next.BindingContext;
            next.drawerLayer.Resources = next.context?.ContainerScreen?.View.Resources ?? next.Resources;
            // Retain the native parent across close/reopen. Reattaching an Android
            // ScrollView with unchanged bounds can leave a pending native layout
            // request that prevents its programmatic scrolling from completing.
            if (!flyoutOverlay.Children.Contains(next.drawerLayer)) flyoutOverlay.Add(next.drawerLayer);
            next.drawerLayer.IsVisible = true;
        }
        flyoutOverlay.IsVisible = next != null;
        chrome.IsEnabled = overlay.IsEnabled = busy.IsEnabled = next == null;
        AutomationProperties.SetExcludedWithChildren(chrome, next != null);
        AutomationProperties.SetExcludedWithChildren(overlay, next != null);
        AutomationProperties.SetExcludedWithChildren(busy, next != null);
        if (next != null)
        {
            Dispatcher.Dispatch(() =>
            {
                if (ReferenceEquals(next, presentedFlyout) && context?.IsActive == true && !next.menu.FocusSelected()) next.scrim.Focus();
            });
        }
        else
        {
            if (context?.IsActive == true && previousFocus?.TryGetTarget(out var focused) == true && focused.IsEnabled && focused.IsVisible && IsInChrome(focused)) focused.Focus();
            previousFocus = null;
        }
    }

    private bool IsInChrome(Element element)
    {
        for (Element? current = element; current != null; current = current.Parent)
            if (ReferenceEquals(current, chrome)) return true;
        return false;
    }

    private static VisualElement? FindFocused(IVisualTreeElement element)
    {
        if (element is VisualElement { IsVisible: false }) return null;
        if (element is VisualElement { IsFocused: true } focused) return focused;
        foreach (var child in element.GetVisualChildren())
            if (FindFocused(child) is { } found) return found;
        return null;
    }
    internal void Unbind()
    {
        context?.RootContext.View.RefreshFlyoutOverlay();
        context?.RootContext.View.flyoutOverlay.Remove(drawerLayer);
        flyoutOverlay.Clear(); presentedFlyout = null; previousFocus = null;
        disconnected = true;
        ClearBusy();
        if (swipe != null) { swipe.Swiped -= BackSwiped; Presenter.GestureRecognizers.Remove(swipe); swipe = null; }
        Toolbar.Disconnect(); TabSelector.Disconnect(); RailSelector.Disconnect(); menu.Disconnect();
        sharedTop.Content = null; sharedBottom.Content = null; menuHeader.Content = null; menuFooter.Content = null;
        Presenter.Install(null); context = null;
        HeaderContent = null; FooterContent = null; OverlayContent = null; BodyOverlayContent = null;
        FlyoutHeaderContent = null; FlyoutFooterContent = null; FlyoutTrailingContent = null;
        header.Content = null; footer.Content = null; overlay.Content = null; bodyOverlay.Content = null;
    }
}
