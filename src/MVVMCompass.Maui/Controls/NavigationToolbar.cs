using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MVVMCompass;

/// <summary>A persistent, fully custom toolbar with automatic navigation and configurable center/actions.</summary>
public sealed class NavigationToolbar : ContentView
{
    private readonly Grid layout = new() { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 4 };
    private readonly ContentView leadingHost = new() { HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center };
    private readonly ContentView center = new() { HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Center };
    private readonly HorizontalStackLayout actions = new() { Spacing = 4, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center };
    private readonly ScrollView actionScroller;
    private readonly Button leading = NavigationButton.Create();
    private readonly Label title = new() { FontSize = 18, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly List<Action> subscriptions = [];
    private readonly List<Action> actionLayoutSubscriptions = [];
    private readonly List<OwnedCommand> commands = [];
    private readonly ConditionalWeakTable<NavigationToolbarDefinition, TemplateContent> centerTemplates = new();
    private NavigationContext? context;
    private NavigationToolbarDefinition? defaults => Definition;
    private ViewBase? screen;
    private bool refreshing;
    private bool disconnected;
    private object? actionSource;
    private object? actionTemplate;
    private object? actionOwner;
    private DataTemplate? installedLeadingTemplate;
    private NavigationToolbarDefinition[] appearanceDefinitions = [];
    private ViewBase[] observedViews = [];
    private INotifyCollectionChanged? buttonCollection;
    private NavigationLeadingAction? notifiedLeadingAction;
    private bool balanceQueued;
    private bool scrollResetRequired = true;
    private bool scrollResetQueued;
    private bool? actionsOverflow;
    private double actionViewportWidth = double.NaN;
    private double measuredActionsWidth;
    private double? leadingSlotWidth;
    private double actionAreaSpacing;
    private ToolbarCenterPlacement centerPlacement;

    /// <summary>Creates a toolbar with a centered content area and horizontally scrollable right actions.</summary>
    public NavigationToolbar()
    {
        Padding = new Thickness(8, 4);
        leading.Padding = 8;
        actionScroller = new ScrollView { Content = actions, Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never, HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center };
        LeadingCommand = new Command(async () => { if (context != null) await context.ExecuteLeadingAsync(); },
            () => context is { IsActive: true, IsNavigating: false, HasOpenFlyout: false } && context.LeadingAction != NavigationLeadingAction.None);
        leading.Command = LeadingCommand;
        leadingHost.Content = leading;
        layout.Add(leadingHost, 0); layout.Add(center, 1); layout.Add(actionScroller, 2);
        Content = layout;
        SizeChanged += (_, _) => Balance();
        leadingHost.SizeChanged += (_, _) => RequestBalance();
        Definition = new NavigationToolbarDefinition();
    }

    /// <summary>Identifies bindable toolbar content and actions, also usable outside a navigation host.</summary>
    public static readonly BindableProperty DefinitionProperty = BindableProperty.Create(nameof(Definition), typeof(NavigationToolbarDefinition), typeof(NavigationToolbar), propertyChanged: RefreshProperty);
    /// <summary>Identifies foreground color for the default title and buttons.</summary>
    public static readonly BindableProperty ForegroundColorProperty = BindableProperty.Create(nameof(ForegroundColor), typeof(Color), typeof(NavigationToolbar), Colors.Black, propertyChanged: AppearanceChanged);
    private static readonly BindablePropertyKey EffectiveBackgroundPropertyKey = BindableProperty.CreateReadOnly(nameof(EffectiveBackground), typeof(Brush), typeof(NavigationToolbar), null);
    /// <summary>Identifies the resolved background exposed to custom templates.</summary>
    public static readonly BindableProperty EffectiveBackgroundProperty = EffectiveBackgroundPropertyKey.BindableProperty;
    private static readonly BindablePropertyKey EffectiveForegroundColorPropertyKey = BindableProperty.CreateReadOnly(nameof(EffectiveForegroundColor), typeof(Color), typeof(NavigationToolbar), Colors.Black);
    /// <summary>Identifies the resolved foreground exposed to custom templates.</summary>
    public static readonly BindableProperty EffectiveForegroundColorProperty = EffectiveForegroundColorPropertyKey.BindableProperty;
    /// <summary>Identifies the default button style.</summary>
    public static readonly BindableProperty ButtonStyleProperty = BindableProperty.Create(nameof(ButtonStyle), typeof(Style), typeof(NavigationToolbar), propertyChanged: RebuildActionsProperty);
    /// <summary>Identifies a navigation-button template bound to this toolbar and LeadingCommand.</summary>
    public static readonly BindableProperty LeadingButtonTemplateProperty = BindableProperty.Create(nameof(LeadingButtonTemplate), typeof(DataTemplate), typeof(NavigationToolbar), propertyChanged: RefreshProperty);
    /// <summary>Gets or sets the default foreground color.</summary>
    public Color ForegroundColor { get => (Color)GetValue(ForegroundColorProperty); set => SetValue(ForegroundColorProperty, value); }
    /// <summary>Gets the active screen's inherited background, falling back to this toolbar's configured Background.</summary>
    public Brush? EffectiveBackground => (Brush?)GetValue(EffectiveBackgroundProperty);
    /// <summary>Gets the active screen's inherited foreground, falling back to ForegroundColor.</summary>
    public Color EffectiveForegroundColor => (Color)GetValue(EffectiveForegroundColorProperty);
    /// <summary>Gets or sets a style applied to default toolbar buttons.</summary>
    public Style? ButtonStyle { get => (Style?)GetValue(ButtonStyleProperty); set => SetValue(ButtonStyleProperty, value); }
    /// <summary>Gets or sets a template for the automatic navigation button.</summary>
    public DataTemplate? LeadingButtonTemplate { get => (DataTemplate?)GetValue(LeadingButtonTemplateProperty); set => SetValue(LeadingButtonTemplateProperty, value); }
    /// <summary>Gets or sets toolbar content and actions. A navigation host supplies this from its defaults.</summary>
    public NavigationToolbarDefinition? Definition { get => (NavigationToolbarDefinition?)GetValue(DefinitionProperty); set => SetValue(DefinitionProperty, value); }
    /// <summary>Gets the library-owned navigation command for custom leading-button templates.</summary>
    public ICommand LeadingCommand { get; }
    /// <summary>Gets the navigation action's accessible name.</summary>
    public string LeadingText => context?.LeadingAction.ToString() ?? string.Empty;
    internal Grid LayoutForTests => layout;
    internal ContentView LeadingHostForTests => leadingHost;
    internal ContentView CenterHostForTests => center;
    internal HorizontalStackLayout ActionsForTests => actions;
    internal ScrollView ActionScrollerForTests => actionScroller;

    private static void RefreshProperty(BindableObject bindable, object oldValue, object newValue)
        => ((NavigationToolbar)bindable).Refresh();
    private static void RebuildActionsProperty(BindableObject bindable, object oldValue, object newValue)
    { var toolbar = (NavigationToolbar)bindable; toolbar.actionSource = null; toolbar.Refresh(); }
    private static void AppearanceChanged(BindableObject bindable, object oldValue, object newValue) => ((NavigationToolbar)bindable).RefreshAppearance();

    /// <summary>Keeps the rendered background independent of configured fallback values and covers the toolbar padding.</summary>
    protected override void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (layout == null) return;
        if (propertyName is nameof(Padding) or nameof(HeightRequest) or nameof(MinimumHeightRequest)) RefreshGeometry();
        else if (propertyName == nameof(Background)) RefreshAppearance();
        else if (propertyName == nameof(FlowDirection)) RequestBalance(true);
    }

    private void RefreshAppearance()
    {
        if (layout == null) return;
        var background = appearanceDefinitions.FirstOrDefault(item => item.Background != null)?.Background ?? Background;
        var foreground = appearanceDefinitions.FirstOrDefault(item => item.ForegroundColor != null)?.ForegroundColor ?? ForegroundColor;
        SetValue(EffectiveBackgroundPropertyKey, background);
        SetValue(EffectiveForegroundColorPropertyKey, foreground);
        layout.Background = background;
        title.TextColor = leading.TextColor = foreground;
        foreach (var action in actions.OfType<ToolbarActionView>()) action.SetForeground(foreground);
    }

    private T? InheritedGeometry<T>(BindableProperty property) where T : struct
    {
        foreach (var definition in appearanceDefinitions)
            if (definition.GetValue(property) is T value) return value;
        return null;
    }

    private void RefreshGeometry()
    {
        // Apply definition values to the painting/layout surface rather than replacing
        // the toolbar's own bindings or dynamic-resource fallbacks.
        var padding = InheritedGeometry<Thickness>(NavigationToolbarDefinition.PaddingProperty) ?? Padding;
        var height = InheritedGeometry<double>(NavigationToolbarDefinition.HeightRequestProperty);
        var minimum = InheritedGeometry<double>(NavigationToolbarDefinition.MinimumHeightRequestProperty);
        layout.Margin = new(-Padding.Left, -Padding.Top, -Padding.Right, -Padding.Bottom);
        layout.Padding = padding;
        layout.HeightRequest = height ?? HeightRequest;
        layout.MinimumHeightRequest = minimum ?? (MinimumHeightRequest >= 0 ? MinimumHeightRequest
            : height.HasValue || HeightRequest >= 0 ? 0 : 56);
        layout.ColumnSpacing = InheritedGeometry<double>(NavigationToolbarDefinition.ColumnSpacingProperty) ?? 4;
        actions.Spacing = InheritedGeometry<double>(NavigationToolbarDefinition.ActionSpacingProperty) ?? 4;
        leadingSlotWidth = InheritedGeometry<double>(NavigationToolbarDefinition.LeadingSlotWidthProperty);
        actionAreaSpacing = InheritedGeometry<double>(NavigationToolbarDefinition.ActionAreaSpacingProperty) ?? 0;
        centerPlacement = InheritedGeometry<ToolbarCenterPlacement>(NavigationToolbarDefinition.CenterPlacementProperty) ?? ToolbarCenterPlacement.Balanced;
        var fullWidth = centerPlacement == ToolbarCenterPlacement.FullWidth;
        Grid.SetColumn(center, fullWidth ? 0 : 1);
        Grid.SetColumnSpan(center, fullWidth ? 3 : 1);
        center.Margin = fullWidth ? new Thickness(-padding.Left, 0, -padding.Right, 0) : 0;
        center.InputTransparent = fullWidth;
        leadingHost.ZIndex = fullWidth ? 1 : 0;
        if (actionScroller != null)
        {
            actionScroller.ZIndex = fullWidth ? 1 : 0;
            RequestBalance(true);
        }
    }

    internal void Bind(NavigationContext? owner, NavigationToolbarDefinition? definition)
    {
        // Only the root toolbar presents the active branch's content. A hidden
        // nested toolbar must not attach the same View to a second native parent.
        if (owner?.ParentContext != null)
        {
            if (!disconnected) Disconnect();
            IsVisible = false;
            return;
        }
        var needsRefresh = disconnected || context != owner;
        disconnected = false;
        if (context != owner)
        {
            if (context != null) context.PropertyChanged -= ContextChanged;
            context = owner;
            if (context != null) context.PropertyChanged += ContextChanged;
        }
        var sameDefinition = ReferenceEquals(Definition, definition);
        Definition = definition; // A changed definition refreshes through its callback.
        if (sameDefinition && needsRefresh) Refresh();
    }

    /// <summary>Supplies the binding owner for toolbar definitions used outside a navigation host.</summary>
    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (context == null && Definition != null) SetInheritedBindingContext(Definition, BindingContext);
    }

    private void ContextChanged(object? sender, PropertyChangedEventArgs args)
    {
        // Publish raises Current for every committed branch change, including a
        // nested change whose enclosing entry is retained. Availability changes
        // only affect command state; they do not replace content or subscriptions.
        if (args.PropertyName is nameof(NavigationContext.Current) or null or "") Refresh();
        else if (args.PropertyName is nameof(NavigationContext.LeadingAction) or nameof(NavigationContext.IsNavigating)
            or nameof(NavigationContext.IsActive) or nameof(NavigationContext.IsClosed)) RefreshAvailability();
    }
    private void DefinitionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is ViewBase && args.PropertyName is not (nameof(ViewBase.Toolbar) or nameof(ViewBase.ToolbarLeadingTemplate) or nameof(BindingContext))) return;
        if (sender is NavigationToolbarDefinition && args.PropertyName is nameof(NavigationToolbarDefinition.Background) or nameof(NavigationToolbarDefinition.ForegroundColor))
        { RefreshAppearance(); return; }
        Refresh();
    }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) { actionSource = null; Refresh(); }

    internal void Refresh()
    {
        if (refreshing || disconnected) return;
        refreshing = true;
        try
        {
            if (context == null && Definition != null) SetInheritedBindingContext(Definition, BindingContext);
            var chain = context?.ActiveChain().Select(item => item.Current).Where(item => item != null).ToArray() ?? [];
            screen = chain.LastOrDefault()?.View;
            var definition = screen?.Toolbar;
            var definitions = chain.Reverse().Select(item => item!.View.Toolbar).Where(item => item != null).Cast<NavigationToolbarDefinition>()
                .Concat(defaults == null ? [] : new[] { defaults }).Distinct().ToArray();
            var views = chain.Select(entry => entry!.View).ToArray();
            if (!appearanceDefinitions.SequenceEqual(definitions) || !observedViews.SequenceEqual(views))
            {
                foreach (var release in subscriptions) release();
                subscriptions.Clear();
                foreach (var item in definitions) Observe(item);
                foreach (var observed in views)
                {
                    observed.PropertyChanged += DefinitionChanged;
                    subscriptions.Add(() => observed.PropertyChanged -= DefinitionChanged);
                }
                observedViews = views;
            }
            appearanceDefinitions = definitions;
            RefreshAppearance();
            RefreshGeometry();
            IsVisible = context?.ParentContext == null && (definitions.FirstOrDefault(item => item.IsVisible != null)?.IsVisible ?? true);
            var centerOwner = definitions.FirstOrDefault(item => item.IsSet(NavigationToolbarDefinition.CenterContentProperty) ||
                item.IsSet(NavigationToolbarDefinition.CenterContentTemplateProperty) || item.Title != null);
            var centerView = centerOwner?.CenterContent;
            if (centerView == null && centerOwner?.CenterContentTemplate is { } template)
            {
                if (!centerTemplates.TryGetValue(centerOwner, out var cached) || cached.Template != template)
                {
                    centerTemplates.Remove(centerOwner);
                    cached = new(template, TemplateView(template));
                    centerTemplates.Add(centerOwner, cached);
                }
                centerView = cached.View;
                SetInheritedBindingContext(centerView, centerOwner.BindingContext);
            }
            title.Text = centerOwner?.Title ?? definition?.Title ?? defaults?.Title ??
                context?.Definition.Destinations.SelectMany(item => item.Children.Count != 0 ? item.Children : [item])
                    .FirstOrDefault(item => item.Id == context.SelectedDestinationId)?.Title ?? string.Empty;
            title.TextColor = EffectiveForegroundColor;
            // The persistent toolbar inherits the host model, while its center may belong to
            // a screen. Set the container's owner before parenting content or a new template.
            center.BindingContext = centerOwner?.BindingContext;
            if (!ReferenceEquals(center.Content, centerView ?? title)) center.Content = centerView ?? title;

            var buttonsOwner = definitions.FirstOrDefault(item => item.RightItems != null);
            var buttons = buttonsOwner?.RightItems;
            var templateForButton = definitions.FirstOrDefault(item => item.RightItemTemplate != null)?.RightItemTemplate;
            var ownerEntry = chain.FirstOrDefault(item => item!.View.Toolbar == buttonsOwner)?.Entry ?? context?.Current?.Entry;
            if (actionSource != buttons || actionTemplate != templateForButton || actionOwner != ownerEntry)
            {
                foreach (var command in commands) command.Dispose();
                commands.Clear();
                foreach (var release in actionLayoutSubscriptions) release();
                actionLayoutSubscriptions.Clear();
                actions.Clear();
                actionSource = buttons; actionTemplate = templateForButton; actionOwner = ownerEntry;
                scrollResetRequired = true;
                foreach (var item in buttons ?? [])
                {
                    var command = new OwnedCommand(item, () => !disconnected && (context == null ||
                        context is { IsActive: true, IsNavigating: false, HasOpenFlyout: false } && ownerEntry != null && context.RootContext.ContainsActiveOrigin(ownerEntry)));
                    commands.Add(command);
                    var presentation = new ToolbarButton { Command = command };
                    presentation.SetBinding(ToolbarButton.TextProperty, Binding.Create(static (ToolbarButton item) => item.Text, source: item));
                    presentation.SetBinding(ToolbarButton.IconProperty, Binding.Create(static (ToolbarButton item) => item.Icon, source: item));
                    presentation.SetBinding(ToolbarButton.CommandParameterProperty, Binding.Create(static (ToolbarButton item) => item.CommandParameter, source: item));
                    presentation.SetBinding(ToolbarButton.IsVisibleProperty, Binding.Create(static (ToolbarButton item) => item.IsVisible, source: item));
                    presentation.SetBinding(ToolbarButton.IsEnabledProperty, Binding.Create(static (ToolbarButton item) => item.IsEnabled, source: item));
                    presentation.SetBinding(ToolbarButton.AccessibilityLabelProperty, Binding.Create(static (ToolbarButton item) => item.AccessibilityLabel, source: item));
                    var button = templateForButton == null ? new ToolbarActionView(EffectiveForegroundColor, ButtonStyle) : TemplateView(templateForButton);
                    button.BindingContext = presentation;
                    button.SetBinding(IsVisibleProperty, Binding.Create(static (ToolbarButton item) => item.IsVisible));
                    PropertyChangedEventHandler presentationChanged = (_, args) =>
                    {
                        if (args.PropertyName is nameof(ToolbarButton.Text) or nameof(ToolbarButton.Icon) or nameof(ToolbarButton.IsVisible) or null or "")
                            RequestBalance(true);
                    };
                    EventHandler sizeChanged = (_, _) => RequestBalance(true);
                    presentation.PropertyChanged += presentationChanged;
                    button.SizeChanged += sizeChanged;
                    actionLayoutSubscriptions.Add(() => presentation.PropertyChanged -= presentationChanged);
                    actionLayoutSubscriptions.Add(() => button.SizeChanged -= sizeChanged);
                    actions.Add(button);
                }
            }
            if (!ReferenceEquals(buttonCollection, buttons))
            {
                if (buttonCollection != null) buttonCollection.CollectionChanged -= ItemsChanged;
                buttonCollection = buttons;
                if (buttonCollection != null) buttonCollection.CollectionChanged += ItemsChanged;
            }
            var leadingTemplate = chain.Reverse().Select(item => item!.View.ToolbarLeadingTemplate).FirstOrDefault(item => item != null) ?? LeadingButtonTemplate;
            if (installedLeadingTemplate != leadingTemplate)
            {
                installedLeadingTemplate = leadingTemplate;
                leadingHost.Content = installedLeadingTemplate == null ? leading : TemplateView(installedLeadingTemplate);
                if (installedLeadingTemplate != null) leadingHost.Content.BindingContext = this;
                RequestBalance();
            }
            leading.TextColor = EffectiveForegroundColor; leading.Style = ButtonStyle;
            RefreshAvailability();
            Balance();
        }
        finally { refreshing = false; }
    }

    private void RefreshAvailability()
    {
        if (disconnected) return;
        var action = context?.LeadingAction;
        leading.Text = action switch
        { NavigationLeadingAction.Back => "‹", NavigationLeadingAction.Close => "✕", NavigationLeadingAction.Menu => "☰", _ => string.Empty };
        SemanticProperties.SetDescription(leading, LeadingText);
        leadingHost.IsVisible = action is not (null or NavigationLeadingAction.None);
        leadingHost.IsEnabled = context is { IsActive: true, IsNavigating: false, HasOpenFlyout: false };
        ((Command)LeadingCommand).ChangeCanExecute();
        foreach (var command in commands) command.Raise();
        if (notifiedLeadingAction != action)
        {
            notifiedLeadingAction = action;
            OnPropertyChanged(nameof(LeadingText));
            RequestBalance();
        }
    }

    private void Observe(NavigationToolbarDefinition? definition)
    {
        if (definition == null) return;
        definition.PropertyChanged += DefinitionChanged;
        subscriptions.Add(() => definition.PropertyChanged -= DefinitionChanged);
    }
    /// <summary>Fits the action area to the current measure constraint before sizing the center.</summary>
    protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
    {
        Balance(widthConstraint, heightConstraint);
        return base.MeasureOverride(widthConstraint, heightConstraint);
    }

    private void Balance(double width = double.NaN, double height = double.NaN)
    {
        if (double.IsNaN(width)) width = Width;
        if (double.IsNaN(height)) height = Height;
        if (!double.IsFinite(width) || width <= 0) return;
        var available = Math.Max(0, width - layout.Padding.HorizontalThickness - 2 * layout.ColumnSpacing);
        var constraint = double.IsFinite(height) ? Math.Max(0, height - layout.Padding.VerticalThickness) : double.PositiveInfinity;
        // A compact definition can leave less row space than its action artwork.
        // Preserve the natural vertical extent of the horizontal scroll viewport;
        // otherwise native scroll views clip correctly positioned custom controls.
        var actionSize = ((IView)actions).Measure(double.PositiveInfinity, double.PositiveInfinity);
        var measured = actionSize.Width;
        measuredActionsWidth = measured;
        var leadingWidth = Math.Min(available, leadingSlotWidth ?? (leadingHost.IsVisible && leadingHost.Content != null
            ? Math.Max(44, ((IView)leadingHost.Content).Measure(double.PositiveInfinity, constraint).Width) : 0));
        var centerWidth = center.Content == null || centerPlacement == ToolbarCenterPlacement.FullWidth
            ? 0 : ((IView)center.Content).Measure(double.PositiveInfinity, constraint).Width;
        var actionWidth = measured + actionAreaSpacing;
        var symmetric = Math.Max(leadingWidth, actionWidth);
        double left, right;
        if (centerPlacement == ToolbarCenterPlacement.Balanced && 2 * symmetric + centerWidth <= available)
            left = right = symmetric;
        else
        {
            left = leadingWidth;
            // On compact widths, spend unused leading space on the title. Long
            // action collections keep a finite, horizontally scrollable viewport.
            var remaining = available - left;
            var titleReserve = centerPlacement == ToolbarCenterPlacement.Balanced
                ? Math.Min(centerWidth, Math.Min(160, remaining * .6)) : 0;
            right = Math.Min(actionWidth, Math.Max(0, remaining - titleReserve));
        }
        layout.ColumnDefinitions[0].Width = new GridLength(left);
        layout.ColumnDefinitions[2].Width = new GridLength(right);
        var viewport = Math.Min(measured, Math.Max(0, right - actionAreaSpacing));
        actionScroller.WidthRequest = viewport;
        actionScroller.HeightRequest = actionSize.Height;
        var overflow = measured > viewport + 1;
        if (actionsOverflow != overflow || !double.IsFinite(actionViewportWidth) || Math.Abs(actionViewportWidth - viewport) > .5)
            scrollResetRequired = true;
        actionsOverflow = overflow;
        actionViewportWidth = viewport;
        actionScroller.HorizontalScrollBarVisibility = overflow ? ScrollBarVisibility.Always : ScrollBarVisibility.Never;
        SemanticProperties.SetHint(actionScroller, overflow ? "Scroll horizontally for more actions" : null);
        if (scrollResetRequired) ScheduleScrollReset();
    }

    private void RequestBalance(bool resetScroll = false)
    {
        if (disconnected) return;
        scrollResetRequired |= resetScroll;
        actions.InvalidateMeasure();
        center.InvalidateMeasure();
        layout.InvalidateMeasure();
        InvalidateMeasure();
        if (balanceQueued) return;
        balanceQueued = true;
        Dispatcher.Dispatch(() =>
        {
            balanceQueued = false;
            if (!disconnected) Balance();
        });
    }

    private void ScheduleScrollReset()
    {
        if (scrollResetQueued || disconnected) return;
        scrollResetQueued = true;
        Dispatcher.Dispatch(() =>
        {
            scrollResetQueued = false;
            if (disconnected || !scrollResetRequired) return;
            scrollResetRequired = false;
            var rightToLeft = (((IVisualElementController)this).EffectiveFlowDirection & EffectiveFlowDirection.RightToLeft) != 0;
            _ = actionScroller.ScrollToAsync(rightToLeft ? 0 : measuredActionsWidth, 0, false);
        });
    }
    internal void Disconnect()
    {
        disconnected = true;
        if (context != null) context.PropertyChanged -= ContextChanged;
        context = null; Definition = null; screen = null;
        appearanceDefinitions = [];
        observedViews = [];
        RefreshAppearance();
        RefreshGeometry();
        foreach (var release in subscriptions) release(); subscriptions.Clear();
        foreach (var release in actionLayoutSubscriptions) release(); actionLayoutSubscriptions.Clear();
        if (buttonCollection != null) buttonCollection.CollectionChanged -= ItemsChanged;
        buttonCollection = null;
        foreach (var command in commands) command.Dispose(); commands.Clear();
        center.Content = null; center.BindingContext = null;
        actions.Clear(); actionSource = null; actionTemplate = null; actionOwner = null;
        balanceQueued = false; scrollResetRequired = false; scrollResetQueued = false;
        actionsOverflow = null; actionViewportWidth = double.NaN; measuredActionsWidth = 0;
    }
    internal static View TemplateView(DataTemplate template) => template.CreateContent() is View view ? view
        : throw new InvalidOperationException("A navigation template must create a View.");
    private sealed record TemplateContent(DataTemplate Template, View View);

    private sealed class OwnedCommand : ICommand, IDisposable
    {
        private readonly ToolbarButton source;
        private readonly Func<bool> active;
        private ICommand? command;
        private bool disposed;
        internal OwnedCommand(ToolbarButton source, Func<bool> active)
        { this.source = source; this.active = active; source.PropertyChanged += Changed; Rebind(); }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => !disposed && active() && source.IsEnabled && source.IsVisible && command?.CanExecute(parameter) == true;
        public void Execute(object? parameter) { if (CanExecute(parameter)) command!.Execute(parameter); }
        internal void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        private void Changed(object? sender, PropertyChangedEventArgs args) { Rebind(); Raise(); }
        private void CommandChanged(object? sender, EventArgs args) => Raise();
        private void Rebind()
        {
            if (command == source.Command) return;
            if (command != null) command.CanExecuteChanged -= CommandChanged;
            command = source.Command;
            if (command != null) command.CanExecuteChanged += CommandChanged;
        }
        public void Dispose()
        {
            disposed = true; source.PropertyChanged -= Changed;
            if (command != null) command.CanExecuteChanged -= CommandChanged;
            command = null; Raise();
        }
    }
}
