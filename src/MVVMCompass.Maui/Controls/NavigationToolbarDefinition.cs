using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;

namespace MVVMCompass;

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
