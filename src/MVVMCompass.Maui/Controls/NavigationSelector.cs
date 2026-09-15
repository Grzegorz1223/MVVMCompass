using System.Collections.Specialized;
using System.ComponentModel;

namespace MVVMCompass;

/// <summary>A templated destination selector with library-owned pointer, keyboard, and accessibility actions.</summary>
public sealed class NavigationSelector : ContentView
{
    private readonly Grid items = new() { ColumnSpacing = 4, RowSpacing = 4 };
    private readonly ScrollView scroll;
    private readonly List<Action> cleanup = [];
    private INotifyCollectionChanged? collection;

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
    /// <summary>Gets or sets shared strip content. It retains the container model as its binding context.</summary>
    public View? CenterContent { get => (View?)GetValue(CenterContentProperty); set => SetValue(CenterContentProperty, value); }
    /// <summary>Gets or sets trailing strip content.</summary>
    public View? TrailingContent { get => (View?)GetValue(TrailingContentProperty); set => SetValue(TrailingContentProperty, value); }

    private static void Changed(BindableObject bindable, object oldValue, object newValue) => ((NavigationSelector)bindable).Rebuild();
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => Rebuild();
    private void Rebuild()
    {
        if (scroll == null) return;
        foreach (var release in cleanup) release(); cleanup.Clear(); items.Clear(); items.ColumnDefinitions.Clear(); items.RowDefinitions.Clear();
        if (collection != null) collection.CollectionChanged -= CollectionChanged;
        collection = ItemsSource as INotifyCollectionChanged;
        if (collection != null) collection.CollectionChanged += CollectionChanged;
        var horizontal = Orientation == StackOrientation.Horizontal;
        scroll.Orientation = horizontal ? ScrollOrientation.Horizontal : ScrollOrientation.Vertical;
        // Detach before transferring between the finite equal grid and the natural-size scroller.
        Content = null; scroll.Content = null;
        if (ItemSizing == TabItemSizing.Content) { scroll.Content = items; Content = scroll; }
        else Content = items;
        var visible = (ItemsSource ?? []).Where(item => item.IsVisible).ToArray();
        var index = 0;
        void Add(View view, bool destination)
        {
            var length = destination && ItemSizing == TabItemSizing.Equal ? GridLength.Star : GridLength.Auto;
            if (horizontal) { items.ColumnDefinitions.Add(new(length)); items.Add(view, index++, 0); }
            else { items.RowDefinitions.Add(new(length)); items.Add(view, 0, index++); }
        }
        for (var i = 0; i <= visible.Length; i++)
        {
            if (i == visible.Length / 2 && CenterContent != null) Add(CenterContent, false);
            if (i == visible.Length) break;
            var item = visible[i];
            var cell = new Grid { BindingContext = item };
            var visual = new ContentView { InputTransparent = true, CascadeInputTransparent = true };
            var input = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0,
                MinimumHeightRequest = 44, MinimumWidthRequest = 44, Command = item.SelectCommand, AutomationId = "destination-" + item.Id };
            input.SetBinding(IsEnabledProperty, Binding.Create(static (NavigationItemContext value) => value.IsEnabled));
            input.SetBinding(SemanticProperties.DescriptionProperty, Binding.Create(static (NavigationItemContext value) => value.Title));
            cell.Add(visual); cell.Add(input);
            void Update()
            {
                var template = (item.IsSelected ? SelectedItemTemplate : UnselectedItemTemplate) ?? ItemTemplate;
                visual.Content = template != null ? NavigationToolbar.TemplateView(template) : new Label
                { Text = item.Title, Padding = new Thickness(12, 8), TextColor = Color.FromArgb("#172554"),
                    VerticalTextAlignment = TextAlignment.Center, HorizontalTextAlignment = TextAlignment.Center,
                    BackgroundColor = item.IsSelected ? Color.FromArgb("#E2E8F0") : Colors.Transparent };
                visual.Content.BindingContext = item;
                visual.Content.SetBinding(IsVisibleProperty, Binding.Create(static (NavigationItemContext value) => value.IsVisible));
                visual.Content.SetBinding(IsEnabledProperty, Binding.Create(static (NavigationItemContext value) => value.IsEnabled));
                SemanticProperties.SetHint(input, item.IsSelected ? "Selected" : "Select destination");
            }
            void ItemChanged(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(NavigationItemContext.IsVisible)) Rebuild();
                else if (args.PropertyName is nameof(NavigationItemContext.IsSelected) or nameof(NavigationItemContext.Title)) Update();
            }
            item.PropertyChanged += ItemChanged; cleanup.Add(() => item.PropertyChanged -= ItemChanged);
            Update(); Add(cell, true);
        }
        foreach (var item in (ItemsSource ?? []).Where(item => !item.IsVisible))
        {
            void VisibilityChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(NavigationItemContext.IsVisible)) Rebuild(); }
            item.PropertyChanged += VisibilityChanged; cleanup.Add(() => item.PropertyChanged -= VisibilityChanged);
        }
        if (TrailingContent != null) Add(TrailingContent, false);
    }
    internal void Disconnect()
    {
        if (collection != null) collection.CollectionChanged -= CollectionChanged;
        foreach (var release in cleanup) release(); cleanup.Clear(); items.Clear(); collection = null;
        ItemsSource = null; CenterContent = null; TrailingContent = null;
    }
}
