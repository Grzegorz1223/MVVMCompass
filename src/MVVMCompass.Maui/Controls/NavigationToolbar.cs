using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MVVMCompass;

/// <summary>A persistent, fully custom toolbar with automatic navigation and configurable center/actions.</summary>
public sealed class NavigationToolbar : ContentView
{
    private readonly Grid layout = new() { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 4 };
    private readonly ContentView leadingHost = new();
    private readonly ContentView center = new();
    private readonly HorizontalStackLayout actions = new() { Spacing = 4 };
    private readonly ScrollView actionScroller;
    private readonly Button leading = new() { MinimumWidthRequest = 44, HorizontalOptions = LayoutOptions.Start, Padding = 8, BackgroundColor = Colors.Transparent };
    private readonly Label title = new() { FontSize = 18, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly List<Action> subscriptions = [];
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

    /// <summary>Creates a toolbar with a centered content area and horizontally scrollable right actions.</summary>
    public NavigationToolbar()
    {
        Padding = new Thickness(8, 4);
        MinimumHeightRequest = 56;
        actionScroller = new ScrollView { Content = actions, Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never, VerticalOptions = LayoutOptions.Center };
        LeadingCommand = new Command(async () => { if (context != null) await context.ExecuteLeadingAsync(); },
            () => context is { IsActive: true, IsNavigating: false } && context.LeadingAction != NavigationLeadingAction.None);
        leading.Command = LeadingCommand;
        leadingHost.Content = leading;
        layout.Add(leadingHost, 0); layout.Add(center, 1); layout.Add(actionScroller, 2);
        Content = layout;
        SizeChanged += (_, _) => Balance();
        Definition = new NavigationToolbarDefinition();
    }

    /// <summary>Identifies bindable toolbar content and actions, also usable outside a navigation host.</summary>
    public static readonly BindableProperty DefinitionProperty = BindableProperty.Create(nameof(Definition), typeof(NavigationToolbarDefinition), typeof(NavigationToolbar), propertyChanged: RefreshProperty);
    /// <summary>Identifies foreground color for the default title and buttons.</summary>
    public static readonly BindableProperty ForegroundColorProperty = BindableProperty.Create(nameof(ForegroundColor), typeof(Color), typeof(NavigationToolbar), Colors.Black, propertyChanged: RefreshProperty);
    /// <summary>Identifies the default button style.</summary>
    public static readonly BindableProperty ButtonStyleProperty = BindableProperty.Create(nameof(ButtonStyle), typeof(Style), typeof(NavigationToolbar), propertyChanged: RefreshProperty);
    /// <summary>Identifies a navigation-button template bound to this toolbar and LeadingCommand.</summary>
    public static readonly BindableProperty LeadingButtonTemplateProperty = BindableProperty.Create(nameof(LeadingButtonTemplate), typeof(DataTemplate), typeof(NavigationToolbar), propertyChanged: RefreshProperty);
    /// <summary>Gets or sets the default foreground color.</summary>
    public Color ForegroundColor { get => (Color)GetValue(ForegroundColorProperty); set => SetValue(ForegroundColorProperty, value); }
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

    private static void RefreshProperty(BindableObject bindable, object oldValue, object newValue)
    { var toolbar = (NavigationToolbar)bindable; toolbar.actionSource = null; toolbar.Refresh(); }

    internal void Bind(NavigationContext? owner, NavigationToolbarDefinition? definition)
    {
        // Only the root toolbar presents the active branch's content. A hidden
        // nested toolbar must not attach the same View to a second native parent.
        if (owner?.ParentContext != null)
        {
            Disconnect();
            IsVisible = false;
            return;
        }
        disconnected = false;
        if (context != owner)
        {
            if (context != null) context.PropertyChanged -= ContextChanged;
            context = owner;
            if (context != null) context.PropertyChanged += ContextChanged;
        }
        Definition = definition;
        Refresh();
    }

    /// <summary>Supplies the binding owner for toolbar definitions used outside a navigation host.</summary>
    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (context == null && Definition != null) SetInheritedBindingContext(Definition, BindingContext);
    }

    private void ContextChanged(object? sender, PropertyChangedEventArgs args) => Refresh();
    private void DefinitionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is ViewBase && args.PropertyName is not (nameof(ViewBase.Toolbar) or nameof(ViewBase.ToolbarLeadingTemplate) or nameof(BindingContext))) return;
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
            foreach (var release in subscriptions) release();
            subscriptions.Clear();
            var chain = context?.ActiveChain().Select(item => item.Current).Where(item => item != null).ToArray() ?? [];
            screen = chain.LastOrDefault()?.View;
            var definition = screen?.Toolbar;
            var definitions = chain.Reverse().Select(item => item!.View.Toolbar).Where(item => item != null).Cast<NavigationToolbarDefinition>()
                .Concat(defaults == null ? [] : new[] { defaults }).Distinct().ToArray();
            foreach (var item in definitions) Observe(item);
            foreach (var entry in chain)
            {
                var observed = entry!.View;
                observed.PropertyChanged += DefinitionChanged;
                subscriptions.Add(() => observed.PropertyChanged -= DefinitionChanged);
            }
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
            title.TextColor = ForegroundColor;
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
                commands.Clear(); actions.Clear();
                actionSource = buttons; actionTemplate = templateForButton; actionOwner = ownerEntry;
                foreach (var item in buttons ?? [])
                {
                    var command = new OwnedCommand(item, () => !disconnected && (context == null ||
                        context is { IsActive: true, IsNavigating: false } && ownerEntry != null && context.RootContext.ContainsActiveOrigin(ownerEntry)));
                    commands.Add(command);
                    var presentation = new ToolbarButton { Command = command };
                    presentation.SetBinding(ToolbarButton.TextProperty, Binding.Create(static (ToolbarButton item) => item.Text, source: item));
                    presentation.SetBinding(ToolbarButton.IconProperty, Binding.Create(static (ToolbarButton item) => item.Icon, source: item));
                    presentation.SetBinding(ToolbarButton.CommandParameterProperty, Binding.Create(static (ToolbarButton item) => item.CommandParameter, source: item));
                    presentation.SetBinding(ToolbarButton.IsVisibleProperty, Binding.Create(static (ToolbarButton item) => item.IsVisible, source: item));
                    presentation.SetBinding(ToolbarButton.IsEnabledProperty, Binding.Create(static (ToolbarButton item) => item.IsEnabled, source: item));
                    presentation.SetBinding(ToolbarButton.AccessibilityLabelProperty, Binding.Create(static (ToolbarButton item) => item.AccessibilityLabel, source: item));
                    var button = templateForButton == null ? DefaultButton() : TemplateView(templateForButton);
                    button.BindingContext = presentation;
                    button.SetBinding(IsVisibleProperty, Binding.Create(static (ToolbarButton item) => item.IsVisible));
                    actions.Add(button);
                }
            }
            if (buttons != null)
            {
                buttons.CollectionChanged += ItemsChanged;
                subscriptions.Add(() => buttons.CollectionChanged -= ItemsChanged);
            }
            var leadingTemplate = chain.Reverse().Select(item => item!.View.ToolbarLeadingTemplate).FirstOrDefault(item => item != null) ?? LeadingButtonTemplate;
            if (installedLeadingTemplate != leadingTemplate)
            {
                installedLeadingTemplate = leadingTemplate;
                leadingHost.Content = installedLeadingTemplate == null ? leading : TemplateView(installedLeadingTemplate);
                if (installedLeadingTemplate != null) leadingHost.Content.BindingContext = this;
            }
            leading.Text = context?.LeadingAction switch
            { NavigationLeadingAction.Back => "‹", NavigationLeadingAction.Close => "✕", NavigationLeadingAction.Menu => "☰", _ => string.Empty };
            leading.TextColor = ForegroundColor; leading.Style = ButtonStyle;
            SemanticProperties.SetDescription(leading, LeadingText);
            leadingHost.IsVisible = context?.LeadingAction is not (null or NavigationLeadingAction.None);
            leadingHost.IsEnabled = context is { IsActive: true, IsNavigating: false };
            ((Command)LeadingCommand).ChangeCanExecute();
            foreach (var command in commands) command.Raise();
            OnPropertyChanged(nameof(LeadingText));
            Balance();
        }
        finally { refreshing = false; }
    }

    private Button DefaultButton()
    {
        var button = new Button { MinimumWidthRequest = 44, MinimumHeightRequest = 44, Padding = new Thickness(10, 4),
            BackgroundColor = Colors.Transparent, TextColor = ForegroundColor, Style = ButtonStyle };
        button.SetBinding(Button.TextProperty, Binding.Create(static (ToolbarButton item) => item.Text));
        button.SetBinding(Button.ImageSourceProperty, Binding.Create(static (ToolbarButton item) => item.Icon));
        button.SetBinding(Button.CommandProperty, Binding.Create(static (ToolbarButton item) => item.Command));
        button.SetBinding(Button.CommandParameterProperty, Binding.Create(static (ToolbarButton item) => item.CommandParameter));
        button.SetBinding(IsEnabledProperty, Binding.Create(static (ToolbarButton item) => item.IsEnabled));
        button.SetBinding(SemanticProperties.DescriptionProperty, Binding.Create(static (ToolbarButton item) => item.AccessibilityLabel));
        return button;
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
        var measured = ((IView)actions).Measure(double.PositiveInfinity, Math.Max(44, height)).Width;
        var reserve = Math.Max(leadingHost.IsVisible ? 44 : 0, Math.Min(measured, Math.Max(44, width * .4)));
        leadingHost.WidthRequest = reserve;
        // Keep equal side columns even when the leading button is hidden.
        layout.ColumnDefinitions[0].Width = new GridLength(reserve);
        layout.ColumnDefinitions[2].Width = new GridLength(reserve);
        actionScroller.WidthRequest = reserve;
    }
    internal void Disconnect()
    {
        disconnected = true;
        if (context != null) context.PropertyChanged -= ContextChanged;
        context = null; Definition = null; screen = null;
        foreach (var release in subscriptions) release(); subscriptions.Clear();
        foreach (var command in commands) command.Dispose(); commands.Clear();
        center.Content = null; center.BindingContext = null;
        actions.Clear(); actionSource = null; actionOwner = null;
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
