using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;

namespace MVVMCompass;

/// <summary>Controls how toolbar center content uses the space between or behind the edge controls.</summary>
public enum ToolbarCenterPlacement
{
    /// <summary>Reserves symmetric sides when possible and gives the title more room on compact widths.</summary>
    Balanced,
    /// <summary>Centers content in the remaining slot between independently measured edge areas.</summary>
    Middle,
    /// <summary>Spans the complete toolbar width, including padding, without intercepting edge-control input.</summary>
    FullWidth
}

/// <summary>Bindable toolbar content. Unset areas inherit the navigation view's defaults.</summary>
public sealed class NavigationToolbarDefinition : BindableObject
{
    /// <summary>Identifies the title property.</summary>
    public static readonly BindableProperty TitleProperty = BindableProperty.Create(nameof(Title), typeof(string), typeof(NavigationToolbarDefinition));
    /// <summary>Identifies arbitrary center content.</summary>
    public static readonly BindableProperty CenterContentProperty = BindableProperty.Create(nameof(CenterContent), typeof(View), typeof(NavigationToolbarDefinition), propertyChanged: Changed);
    /// <summary>Identifies the center template.</summary>
    public static readonly BindableProperty CenterContentTemplateProperty = BindableProperty.Create(nameof(CenterContentTemplate), typeof(DataTemplate), typeof(NavigationToolbarDefinition));
    /// <summary>Identifies the right-side button collection.</summary>
    public static readonly BindableProperty RightItemsProperty = BindableProperty.Create(nameof(RightItems), typeof(ObservableCollection<ToolbarButton>), typeof(NavigationToolbarDefinition), propertyChanged: Changed);
    /// <summary>Identifies the right-side button template.</summary>
    public static readonly BindableProperty RightItemTemplateProperty = BindableProperty.Create(nameof(RightItemTemplate), typeof(DataTemplate), typeof(NavigationToolbarDefinition));
    /// <summary>Identifies toolbar visibility.</summary>
    public static readonly BindableProperty IsVisibleProperty = BindableProperty.Create(nameof(IsVisible), typeof(bool?), typeof(NavigationToolbarDefinition));
    /// <summary>Identifies the toolbar background. Null inherits from the enclosing active screen or host.</summary>
    public static readonly BindableProperty BackgroundProperty = BindableProperty.Create(nameof(Background), typeof(Brush), typeof(NavigationToolbarDefinition), propertyChanged: Changed);
    /// <summary>Identifies the default title and button text color. Null inherits from the enclosing active screen or host.</summary>
    public static readonly BindableProperty ForegroundColorProperty = BindableProperty.Create(nameof(ForegroundColor), typeof(Color), typeof(NavigationToolbarDefinition));
    /// <summary>Identifies the toolbar's content insets. Null inherits; zero removes the insets.</summary>
    public static readonly BindableProperty PaddingProperty = BindableProperty.Create(nameof(Padding), typeof(Thickness?), typeof(NavigationToolbarDefinition), validateValue: ValidInsets);
    /// <summary>Identifies the preferred toolbar height. Null inherits; zero is an explicit request.</summary>
    public static readonly BindableProperty HeightRequestProperty = BindableProperty.Create(nameof(HeightRequest), typeof(double?), typeof(NavigationToolbarDefinition), validateValue: ValidLength);
    /// <summary>Identifies the minimum toolbar height. Null inherits; zero removes the minimum.</summary>
    public static readonly BindableProperty MinimumHeightRequestProperty = BindableProperty.Create(nameof(MinimumHeightRequest), typeof(double?), typeof(NavigationToolbarDefinition), validateValue: ValidLength);
    /// <summary>Identifies a fixed logical leading column, retained even when its control is hidden.</summary>
    public static readonly BindableProperty LeadingSlotWidthProperty = BindableProperty.Create(nameof(LeadingSlotWidth), typeof(double?), typeof(NavigationToolbarDefinition), validateValue: ValidLength);
    /// <summary>Identifies the gap between toolbar columns. Null inherits; zero removes it.</summary>
    public static readonly BindableProperty ColumnSpacingProperty = BindableProperty.Create(nameof(ColumnSpacing), typeof(double?), typeof(NavigationToolbarDefinition), validateValue: ValidLength);
    /// <summary>Identifies spacing between visible action templates.</summary>
    public static readonly BindableProperty ActionSpacingProperty = BindableProperty.Create(nameof(ActionSpacing), typeof(double?), typeof(NavigationToolbarDefinition), validateValue: ValidLength);
    /// <summary>Identifies the additional gap before the logical trailing action area.</summary>
    public static readonly BindableProperty ActionAreaSpacingProperty = BindableProperty.Create(nameof(ActionAreaSpacing), typeof(double?), typeof(NavigationToolbarDefinition), validateValue: ValidLength);
    /// <summary>Identifies center-content placement. Null inherits; Balanced is the library default.</summary>
    public static readonly BindableProperty CenterPlacementProperty = BindableProperty.Create(nameof(CenterPlacement), typeof(ToolbarCenterPlacement?), typeof(NavigationToolbarDefinition),
        validateValue: (_, value) => value == null || value is ToolbarCenterPlacement placement && Enum.IsDefined(placement));

    /// <summary>Gets or sets the default title when no center content is configured.</summary>
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>Gets or sets an arbitrary center view.</summary>
    public View? CenterContent { get => (View?)GetValue(CenterContentProperty); set => SetValue(CenterContentProperty, value); }
    /// <summary>Gets or sets a center template bound to this definition's model.</summary>
    public DataTemplate? CenterContentTemplate { get => (DataTemplate?)GetValue(CenterContentTemplateProperty); set => SetValue(CenterContentTemplateProperty, value); }
    /// <summary>Gets or sets buttons; null inherits defaults and an empty collection removes default buttons.</summary>
    public ObservableCollection<ToolbarButton>? RightItems
    { get => (ObservableCollection<ToolbarButton>?)GetValue(RightItemsProperty); set => SetValue(RightItemsProperty, value); }
    /// <summary>Gets or sets a button template whose context is ToolbarButton.</summary>
    public DataTemplate? RightItemTemplate { get => (DataTemplate?)GetValue(RightItemTemplateProperty); set => SetValue(RightItemTemplateProperty, value); }
    /// <summary>Gets or sets visibility; null inherits the host default.</summary>
    public bool? IsVisible { get => (bool?)GetValue(IsVisibleProperty); set => SetValue(IsVisibleProperty, value); }
    /// <summary>Gets or sets the full toolbar background, including padding. Null inherits; a transparent brush explicitly overrides.</summary>
    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    /// <summary>Gets or sets the default title and button text color. Null inherits. Icons and custom content keep their own colors.</summary>
    public Color? ForegroundColor { get => (Color?)GetValue(ForegroundColorProperty); set => SetValue(ForegroundColorProperty, value); }
    /// <summary>Gets or sets content insets. Null inherits; the default is 8 horizontal and 4 vertical DIP.</summary>
    public Thickness? Padding { get => (Thickness?)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    /// <summary>Gets or sets the preferred height in DIP. Null inherits. An explicit height replaces the automatic 56-DIP minimum unless a minimum is explicitly configured.</summary>
    public double? HeightRequest { get => (double?)GetValue(HeightRequestProperty); set => SetValue(HeightRequestProperty, value); }
    /// <summary>Gets or sets the minimum height in DIP. Null inherits; zero removes the minimum.</summary>
    public double? MinimumHeightRequest { get => (double?)GetValue(MinimumHeightRequestProperty); set => SetValue(MinimumHeightRequestProperty, value); }
    /// <summary>Gets or sets a fixed leading-column width in DIP, overriding automatic measurement and its 44-DIP floor. Null inherits automatic sizing.</summary>
    /// <remarks>The column remains reserved when the navigation control is hidden. Custom templates must fit the chosen width.</remarks>
    public double? LeadingSlotWidth { get => (double?)GetValue(LeadingSlotWidthProperty); set => SetValue(LeadingSlotWidthProperty, value); }
    /// <summary>Gets or sets column spacing in DIP. Null inherits; the library default is 4.</summary>
    public double? ColumnSpacing { get => (double?)GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    /// <summary>Gets or sets spacing between visible actions in DIP. Null inherits; the library default is 4.</summary>
    public double? ActionSpacing { get => (double?)GetValue(ActionSpacingProperty); set => SetValue(ActionSpacingProperty, value); }
    /// <summary>Gets or sets an extra logical gap between the title and action area, including an empty area. Null inherits; the library default is zero.</summary>
    public double? ActionAreaSpacing { get => (double?)GetValue(ActionAreaSpacingProperty); set => SetValue(ActionAreaSpacingProperty, value); }
    /// <summary>Gets or sets how center content is placed. Null inherits the closest configured mode.</summary>
    public ToolbarCenterPlacement? CenterPlacement { get => (ToolbarCenterPlacement?)GetValue(CenterPlacementProperty); set => SetValue(CenterPlacementProperty, value); }

    private static bool ValidLength(BindableObject _, object value) => value == null || value is double length && double.IsFinite(length) && length >= 0;
    private static bool ValidInsets(BindableObject _, object value) => value == null || value is Thickness insets &&
        double.IsFinite(insets.Left) && insets.Left >= 0 && double.IsFinite(insets.Top) && insets.Top >= 0 &&
        double.IsFinite(insets.Right) && insets.Right >= 0 && double.IsFinite(insets.Bottom) && insets.Bottom >= 0;

    private static void Changed(BindableObject bindable, object oldValue, object newValue)
    {
        var definition = (NavigationToolbarDefinition)bindable;
        if (oldValue is INotifyCollectionChanged oldItems) oldItems.CollectionChanged -= definition.ItemsChanged;
        if (newValue is INotifyCollectionChanged newItems) newItems.CollectionChanged += definition.ItemsChanged;
        definition.InheritContexts();
    }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) => InheritContexts();
    /// <summary>Passes the configuration owner's binding context to its content and actions.</summary>
    protected override void OnBindingContextChanged() { base.OnBindingContextChanged(); InheritContexts(); }
    private void InheritContexts()
    {
        if (Background != null) SetInheritedBindingContext(Background, BindingContext);
        if (CenterContent != null) SetInheritedBindingContext(CenterContent, BindingContext);
        foreach (var item in RightItems ?? []) SetInheritedBindingContext(item, BindingContext);
    }
}

/// <summary>A bindable toolbar action. Templates bind to this definition and its guarded Command.</summary>
public sealed class ToolbarButton : BindableObject
{
    /// <summary>Identifies button text.</summary>
    public static readonly BindableProperty TextProperty = BindableProperty.Create(nameof(Text), typeof(string), typeof(ToolbarButton), string.Empty);
    /// <summary>Identifies the button icon.</summary>
    public static readonly BindableProperty IconProperty = BindableProperty.Create(nameof(Icon), typeof(ImageSource), typeof(ToolbarButton));
    /// <summary>Identifies the button command.</summary>
    public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(ToolbarButton));
    /// <summary>Identifies the command parameter.</summary>
    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(ToolbarButton));
    /// <summary>Identifies button visibility.</summary>
    public static readonly BindableProperty IsVisibleProperty = BindableProperty.Create(nameof(IsVisible), typeof(bool), typeof(ToolbarButton), true);
    /// <summary>Identifies button availability.</summary>
    public static readonly BindableProperty IsEnabledProperty = BindableProperty.Create(nameof(IsEnabled), typeof(bool), typeof(ToolbarButton), true);
    /// <summary>Identifies the spoken accessibility label.</summary>
    public static readonly BindableProperty AccessibilityLabelProperty = BindableProperty.Create(nameof(AccessibilityLabel), typeof(string), typeof(ToolbarButton), string.Empty);

    /// <summary>Gets or sets text.</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Gets or sets an image source.</summary>
    public ImageSource? Icon { get => (ImageSource?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>Gets or sets the action.</summary>
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    /// <summary>Gets or sets the action parameter.</summary>
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
    /// <summary>Gets or sets visibility.</summary>
    public bool IsVisible { get => (bool)GetValue(IsVisibleProperty); set => SetValue(IsVisibleProperty, value); }
    /// <summary>Gets or sets availability in addition to Command.CanExecute.</summary>
    public bool IsEnabled { get => (bool)GetValue(IsEnabledProperty); set => SetValue(IsEnabledProperty, value); }
    /// <summary>Gets or sets a label for assistive technology.</summary>
    public string AccessibilityLabel { get => (string)GetValue(AccessibilityLabelProperty); set => SetValue(AccessibilityLabelProperty, value); }
}
