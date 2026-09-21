using System.Collections.Specialized;
using System.ComponentModel;

namespace MVVMCompass;

/// <summary>A templated destination selector with library-owned pointer, keyboard, and accessibility actions.</summary>
public sealed class NavigationSelector : ContentView
{
    private readonly Grid items = new() { ColumnSpacing = 4, RowSpacing = 4 };
    private readonly ScrollView scroll;
    private readonly Dictionary<NavigationItemContext, DestinationCell> cells = [];
    private INotifyCollectionChanged? collection;
    private bool disconnected;

    /// <summary>Creates an empty horizontal selector.</summary>
    public NavigationSelector()
    {
        Padding = 4;
        scroll = new ScrollView { HorizontalScrollBarVisibility = ScrollBarVisibility.Never, VerticalScrollBarVisibility = ScrollBarVisibility.Never };
        Rebuild();
    }
    /// <summary>Identifies the destination metadata supplied by navigation.</summary>
    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(nameof(ItemsSource), typeof(IEnumerable<NavigationItemContext>), typeof(NavigationSelector), propertyChanged: Changed);
    /// <summary>Identifies the common item template.</summary>
    public static readonly BindableProperty ItemTemplateProperty = BindableProperty.Create(nameof(ItemTemplate), typeof(DataTemplate), typeof(NavigationSelector), propertyChanged: Changed);
    /// <summary>Identifies the selected item template.</summary>
    public static readonly BindableProperty SelectedItemTemplateProperty = BindableProperty.Create(nameof(SelectedItemTemplate), typeof(DataTemplate), typeof(NavigationSelector), propertyChanged: Changed);
    /// <summary>Identifies the unselected item template.</summary>
    public static readonly BindableProperty UnselectedItemTemplateProperty = BindableProperty.Create(nameof(UnselectedItemTemplate), typeof(DataTemplate), typeof(NavigationSelector), propertyChanged: Changed);
    /// <summary>Identifies selector orientation.</summary>
    public static readonly BindableProperty OrientationProperty = BindableProperty.Create(nameof(Orientation), typeof(StackOrientation), typeof(NavigationSelector), StackOrientation.Horizontal, propertyChanged: Changed);
    /// <summary>Identifies natural or equal item sizing.</summary>
    public static readonly BindableProperty ItemSizingProperty = BindableProperty.Create(nameof(ItemSizing), typeof(TabItemSizing), typeof(NavigationSelector), TabItemSizing.Content, propertyChanged: Changed);
    /// <summary>Identifies spacing between destination items.</summary>
    public static readonly BindableProperty ItemSpacingProperty = BindableProperty.Create(nameof(ItemSpacing), typeof(double), typeof(NavigationSelector), 4d,
        validateValue: (_, value) => double.IsFinite((double)value) && (double)value >= 0, propertyChanged: SpacingChanged);
    /// <summary>Identifies scrollbar visibility on the selector's scrolling axis.</summary>
    public static readonly BindableProperty ScrollBarVisibilityProperty = BindableProperty.Create(nameof(ScrollBarVisibility), typeof(ScrollBarVisibility), typeof(NavigationSelector), ScrollBarVisibility.Never,
        validateValue: (_, value) => Enum.IsDefined((ScrollBarVisibility)value), propertyChanged: SpacingChanged);
    /// <summary>Identifies content reserved between the two halves of the destination items.</summary>
    public static readonly BindableProperty CenterContentProperty = BindableProperty.Create(nameof(CenterContent), typeof(View), typeof(NavigationSelector), propertyChanged: Changed);
    /// <summary>Identifies content reserved after destination items.</summary>
    public static readonly BindableProperty TrailingContentProperty = BindableProperty.Create(nameof(TrailingContent), typeof(View), typeof(NavigationSelector), propertyChanged: Changed);
    /// <summary>Gets or sets committed destination metadata.</summary>
    public IEnumerable<NavigationItemContext>? ItemsSource { get => (IEnumerable<NavigationItemContext>?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    /// <summary>Gets or sets the common template, bound to NavigationItemContext.</summary>
    public DataTemplate? ItemTemplate { get => (DataTemplate?)GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    /// <summary>Gets or sets the selected appearance.</summary>
    public DataTemplate? SelectedItemTemplate { get => (DataTemplate?)GetValue(SelectedItemTemplateProperty); set => SetValue(SelectedItemTemplateProperty, value); }
    /// <summary>Gets or sets the unselected appearance.</summary>
    public DataTemplate? UnselectedItemTemplate { get => (DataTemplate?)GetValue(UnselectedItemTemplateProperty); set => SetValue(UnselectedItemTemplateProperty, value); }
    /// <summary>Gets or sets horizontal tabs or a vertical rail/menu.</summary>
    public StackOrientation Orientation { get => (StackOrientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    /// <summary>Gets or sets equal shares or natural item sizes.</summary>
    public TabItemSizing ItemSizing { get => (TabItemSizing)GetValue(ItemSizingProperty); set => SetValue(ItemSizingProperty, value); }
    /// <summary>Gets or sets the gap between items in device-independent units. The default is 4.</summary>
    public double ItemSpacing { get => (double)GetValue(ItemSpacingProperty); set => SetValue(ItemSpacingProperty, value); }
    /// <summary>Gets or sets the native scrollbar policy for natural-size items. Equal-size items do not scroll.</summary>
    public ScrollBarVisibility ScrollBarVisibility { get => (ScrollBarVisibility)GetValue(ScrollBarVisibilityProperty); set => SetValue(ScrollBarVisibilityProperty, value); }
    /// <summary>Gets or sets shared strip content. It retains the container model as its binding context.</summary>
    public View? CenterContent { get => (View?)GetValue(CenterContentProperty); set => SetValue(CenterContentProperty, value); }
    /// <summary>Gets or sets ordinary interactive content after visible items. It inherits the selector's context and scrolls with natural-size items.</summary>
    public View? TrailingContent { get => (View?)GetValue(TrailingContentProperty); set => SetValue(TrailingContentProperty, value); }

    private static void Changed(BindableObject bindable, object oldValue, object newValue) => ((NavigationSelector)bindable).Rebuild();
    private static void SpacingChanged(BindableObject bindable, object oldValue, object newValue) => ((NavigationSelector)bindable).ApplyScrollSettings();
    private void ApplyScrollSettings()
    {
        items.ColumnSpacing = items.RowSpacing = ItemSpacing;
        if (scroll == null) return;
        scroll.HorizontalScrollBarVisibility = Orientation == StackOrientation.Horizontal ? ScrollBarVisibility : ScrollBarVisibility.Never;
        scroll.VerticalScrollBarVisibility = Orientation == StackOrientation.Vertical ? ScrollBarVisibility : ScrollBarVisibility.Never;
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => Rebuild();
    private void Rebuild()
    {
        if (scroll == null || disconnected) return;
        var nextCollection = ItemsSource as INotifyCollectionChanged;
        if (!ReferenceEquals(collection, nextCollection))
        {
            if (collection != null) collection.CollectionChanged -= CollectionChanged;
            collection = nextCollection;
            if (collection != null) collection.CollectionChanged += CollectionChanged;
        }
        var source = (ItemsSource ?? []).ToArray();
        var retained = source.ToHashSet();
        foreach (var old in cells.Keys.Where(item => !retained.Contains(item)).ToArray())
        {
            old.PropertyChanged -= ItemChanged;
            cells.Remove(old);
        }
        foreach (var item in source)
            if (!cells.ContainsKey(item))
            {
                cells.Add(item, new DestinationCell(item));
                item.PropertyChanged += ItemChanged;
            }
        var horizontal = Orientation == StackOrientation.Horizontal;
        scroll.Orientation = horizontal ? ScrollOrientation.Horizontal : ScrollOrientation.Vertical;
        ApplyScrollSettings();
        // Only transfer when the sizing mode actually changes. Reparenting an
        // attached ScrollView resets native scrolling/focus on some platforms.
        if (ItemSizing == TabItemSizing.Content && !ReferenceEquals(Content, scroll))
        { Content = null; scroll.Content = items; Content = scroll; }
        else if (ItemSizing != TabItemSizing.Content && !ReferenceEquals(Content, items))
        { Content = null; scroll.Content = null; Content = items; }
        var visible = source.Where(item => item.IsVisible).ToArray();
        var ordered = new List<(View View, bool Destination)>();
        for (var i = 0; i <= visible.Length; i++)
        {
            if (i == visible.Length / 2 && CenterContent != null) ordered.Add((CenterContent, false));
            if (i == visible.Length) break;
            var cell = cells[visible[i]];
            cell.Update((visible[i].IsSelected ? SelectedItemTemplate : UnselectedItemTemplate) ?? ItemTemplate);
            ordered.Add((cell, true));
        }
        if (TrailingContent != null) ordered.Add((TrailingContent, false));
        var wanted = ordered.Select(item => item.View).ToHashSet();
        foreach (var old in items.Children.OfType<View>().Where(view => !wanted.Contains(view)).ToArray())
            items.Remove(old);
        items.ColumnDefinitions.Clear(); items.RowDefinitions.Clear();
        for (var index = 0; index < ordered.Count; index++)
        {
            var (view, destination) = ordered[index];
            var length = destination && ItemSizing == TabItemSizing.Equal ? GridLength.Star : GridLength.Auto;
            if (horizontal) items.ColumnDefinitions.Add(new(length));
            else items.RowDefinitions.Add(new(length));
            Grid.SetColumn(view, horizontal ? index : 0);
            Grid.SetRow(view, horizontal ? 0 : index);
            if (items.Children.IndexOf(view) != index)
            {
                items.Children.Remove(view);
                items.Children.Insert(index, view);
            }
        }
    }
    private void ItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NavigationItemContext.IsVisible)) Rebuild();
        else if (args.PropertyName is nameof(NavigationItemContext.IsSelected) or nameof(NavigationItemContext.Title)
            && sender is NavigationItemContext item && cells.TryGetValue(item, out var cell))
            cell.Update((item.IsSelected ? SelectedItemTemplate : UnselectedItemTemplate) ?? ItemTemplate);
    }
    internal bool FocusSelected()
    {
        var inputs = items.Children.OfType<DestinationCell>().Select(cell => cell.Input).Where(input => input.IsEnabled).ToArray();
        return (inputs.FirstOrDefault(input => input.BindingContext is NavigationItemContext { IsSelected: true }) ?? inputs.FirstOrDefault())?.Focus() == true;
    }
    internal void Disconnect()
    {
        disconnected = true;
        if (collection != null) collection.CollectionChanged -= CollectionChanged;
        foreach (var item in cells.Keys) item.PropertyChanged -= ItemChanged;
        cells.Clear(); items.Clear(); collection = null;
        ItemsSource = null; CenterContent = null; TrailingContent = null;
    }

    private sealed class DestinationCell : Grid
    {
        private readonly NavigationItemContext item;
        private readonly ContentView visual = new() { InputTransparent = true, CascadeInputTransparent = true };
        private DataTemplate? installedTemplate;
        internal Button Input { get; } = NavigationButton.Create();

        internal DestinationCell(NavigationItemContext item)
        {
            this.item = item;
            BindingContext = item;
            Input.Command = item.SelectCommand;
            Input.AutomationId = "destination-" + item.Id;
            Input.SetBinding(IsEnabledProperty, Binding.Create(static (NavigationItemContext value) => value.IsEnabled));
            Input.SetBinding(SemanticProperties.DescriptionProperty, Binding.Create(static (NavigationItemContext value) => value.Title));
            Add(visual); Add(Input);
        }

        internal void Update(DataTemplate? template)
        {
            if (visual.Content == null || installedTemplate != template)
            {
                installedTemplate = template;
                visual.Content = template != null ? NavigationToolbar.TemplateView(template) : new Label
                {
                    Padding = new Thickness(12, 8), TextColor = Color.FromArgb("#172554"),
                    VerticalTextAlignment = TextAlignment.Center, HorizontalTextAlignment = TextAlignment.Center
                };
                visual.Content.BindingContext = item;
                visual.Content.SetBinding(IsVisibleProperty, Binding.Create(static (NavigationItemContext value) => value.IsVisible));
                visual.Content.SetBinding(IsEnabledProperty, Binding.Create(static (NavigationItemContext value) => value.IsEnabled));
            }
            if (template == null && visual.Content is Label label)
            {
                label.Text = item.Title;
                label.BackgroundColor = item.IsSelected ? Color.FromArgb("#E2E8F0") : Colors.Transparent;
            }
            SemanticProperties.SetHint(Input, item.IsSelected ? "Selected" : "Select destination");
        }
    }
}
