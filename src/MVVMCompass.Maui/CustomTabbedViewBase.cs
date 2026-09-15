using Microsoft.Maui.Dispatching;
using CommunityToolkit.Maui;
using CommunityToolkit.Mvvm.ComponentModel;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass
{
    /// <summary>
    /// Info about a child tab managed by CustomTabbedViewBase.
    /// </summary>
    internal class ChildTabInfo
    {
        /// <summary>Gets the child visual element retained by the custom tab host.</summary>
        public required VisualElement View { get; init; }
        /// <summary>Gets the model that owns this retained child.</summary>
        public required ViewModelBase ViewModel { get; init; }
        /// <summary>Gets the extracted content displayed by the custom tab host.</summary>
        public required View Content { get; init; }

        /// <summary>
        /// When true the tab is navigable but gets no item in the horizontal tab bar (an alternative surface,
        /// e.g. a compact rail, presents it instead).
        /// </summary>
        public bool HideInTabBar { get; init; }
    }

    /// <summary>
    /// Represents the visual and data context for a single tab item in CustomTabbedViewBase.
    /// Used as the BindingContext for SelectedTabItemTemplate / UnselectedTabItemTemplate.
    /// </summary>
    internal class TabItemContext : ObservableObject
    {
        private string _title = string.Empty;
        /// <summary>Gets or sets the text displayed for this item.</summary>
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        private string _selectedIcon = string.Empty;
        /// <summary>Gets or sets the icon displayed for the selected item.</summary>
        public string SelectedIcon
        {
            get => _selectedIcon;
            set => SetProperty(ref _selectedIcon, value);
        }

        private string _unselectedIcon = string.Empty;
        /// <summary>Gets or sets the icon displayed for an inactive item.</summary>
        public string UnselectedIcon
        {
            get => _unselectedIcon;
            set => SetProperty(ref _unselectedIcon, value);
        }

        private bool _isSelected;
        /// <summary>Gets or sets whether this item is selected.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private bool _isBusy;

        /// <summary>Mirrors the tab's ViewModel when it implements IActivityIndicatorProvider — see there.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        internal int Index { get; set; }
    }

    /// <summary>Hosts extracted child content, retained tab selection, templates and shared presentation controls.</summary>
    internal abstract class CustomTabbedViewBase<T> : ContentPage, IHasVM, ICustomTabbedViewBase, IRetainedTabMaintenance where T : ViewModelBase
    {
        #region BindableProperties

        /// <summary>Identifies the bindable SelectedTabItemTemplate property.</summary>
        public static readonly BindableProperty SelectedTabItemTemplateProperty =
            BindableProperty.Create(nameof(SelectedTabItemTemplate), typeof(DataTemplate), typeof(CustomTabbedViewBase<T>));

        /// <summary>Identifies the bindable UnselectedTabItemTemplate property.</summary>
        public static readonly BindableProperty UnselectedTabItemTemplateProperty =
            BindableProperty.Create(nameof(UnselectedTabItemTemplate), typeof(DataTemplate), typeof(CustomTabbedViewBase<T>));

        /// <summary>Identifies the bindable SelectedTabTextColor property.</summary>
        public static readonly BindableProperty SelectedTabTextColorProperty =
            BindableProperty.Create(nameof(SelectedTabTextColor), typeof(Color), typeof(CustomTabbedViewBase<T>), Colors.Black);

        /// <summary>Identifies the bindable UnselectedTabTextColor property.</summary>
        public static readonly BindableProperty UnselectedTabTextColorProperty =
            BindableProperty.Create(nameof(UnselectedTabTextColor), typeof(Color), typeof(CustomTabbedViewBase<T>), Colors.Gray);

        /// <summary>Identifies the bindable SelectedTabBackgroundColor property.</summary>
        public static readonly BindableProperty SelectedTabBackgroundColorProperty =
            BindableProperty.Create(nameof(SelectedTabBackgroundColor), typeof(Color), typeof(CustomTabbedViewBase<T>), Colors.Transparent);

        /// <summary>Identifies the bindable UnselectedTabBackgroundColor property.</summary>
        public static readonly BindableProperty UnselectedTabBackgroundColorProperty =
            BindableProperty.Create(nameof(UnselectedTabBackgroundColor), typeof(Color), typeof(CustomTabbedViewBase<T>), Colors.Transparent);

        /// <summary>Identifies the bindable TabBarBackground property.</summary>
        public static readonly BindableProperty TabBarBackgroundProperty =
            BindableProperty.Create(
                nameof(TabBarBackground),
                typeof(Brush),
                typeof(CustomTabbedViewBase<T>),
                null,
                propertyChanged: (bindable, oldValue, newValue) =>
                {
                    var self = (CustomTabbedViewBase<T>)bindable;
                    self._tabBarGrid.Background = newValue as Brush;
                });

        /// <summary>Identifies the bindable IsTabBarEnabled property.</summary>
        public static readonly BindableProperty IsTabBarEnabledProperty =
            BindableProperty.Create(
                nameof(IsTabBarEnabled),
                typeof(bool),
                typeof(CustomTabbedViewBase<T>),
                true,
                propertyChanged: (bindable, oldValue, newValue) =>
                {
                    var self = (CustomTabbedViewBase<T>)bindable;
                    self.UpdateTabBarOverlay((bool)newValue);
                });

        #endregion

        /// <summary>
        /// DataTemplate used to render a tab in its selected state.
        /// BindingContext is a <see cref="TabItemContext"/>.
        /// When null, a default Icon + Label layout is used with the color properties.
        /// </summary>
        public DataTemplate? SelectedTabItemTemplate
        {
            get => (DataTemplate?)GetValue(SelectedTabItemTemplateProperty);
            set => SetValue(SelectedTabItemTemplateProperty, value);
        }

        /// <summary>
        /// DataTemplate used to render a tab in its unselected state.
        /// BindingContext is a <see cref="TabItemContext"/>.
        /// When null, a default Icon + Label layout is used with the color properties.
        /// </summary>
        public DataTemplate? UnselectedTabItemTemplate
        {
            get => (DataTemplate?)GetValue(UnselectedTabItemTemplateProperty);
            set => SetValue(UnselectedTabItemTemplateProperty, value);
        }

        /// <summary>Text color for the default selected tab item (when no SelectedTabItemTemplate is set).</summary>
        public Color SelectedTabTextColor
        {
            get => (Color)GetValue(SelectedTabTextColorProperty);
            set => SetValue(SelectedTabTextColorProperty, value);
        }

        /// <summary>Text color for the default unselected tab item (when no UnselectedTabItemTemplate is set).</summary>
        public Color UnselectedTabTextColor
        {
            get => (Color)GetValue(UnselectedTabTextColorProperty);
            set => SetValue(UnselectedTabTextColorProperty, value);
        }

        /// <summary>Background color for the default selected tab item (when no SelectedTabItemTemplate is set).</summary>
        public Color SelectedTabBackgroundColor
        {
            get => (Color)GetValue(SelectedTabBackgroundColorProperty);
            set => SetValue(SelectedTabBackgroundColorProperty, value);
        }

        /// <summary>Background color for the default unselected tab item (when no UnselectedTabItemTemplate is set).</summary>
        public Color UnselectedTabBackgroundColor
        {
            get => (Color)GetValue(UnselectedTabBackgroundColorProperty);
            set => SetValue(UnselectedTabBackgroundColorProperty, value);
        }

        /// <summary>Background brush applied to the entire tab bar row. Accepts both Color and Brush resources.</summary>
        public Brush? TabBarBackground
        {
            get => (Brush?)GetValue(TabBarBackgroundProperty);
            set => SetValue(TabBarBackgroundProperty, value);
        }

        /// <summary>Identifies the bindable TabBarRowBackground property.</summary>
        public static readonly BindableProperty TabBarRowBackgroundProperty =
            BindableProperty.Create(
                nameof(TabBarRowBackground),
                typeof(Brush),
                typeof(CustomTabbedViewBase<T>),
                null,
                propertyChanged: (bindable, oldValue, newValue) =>
                {
                    var self = (CustomTabbedViewBase<T>)bindable;
                    self._tabBarRowGrid.Background = newValue as Brush;
                });

        /// <summary>
        /// Background brush applied to the entire tab bar row (tabs + trailing content).
        /// Use this to style the full-width title bar area when tabs live in the title bar.
        /// </summary>
        public Brush? TabBarRowBackground
        {
            get => (Brush?)GetValue(TabBarRowBackgroundProperty);
            set => SetValue(TabBarRowBackgroundProperty, value);
        }

        /// <summary>
        /// Controls whether tab switching is allowed. When false, a semi-transparent overlay
        /// covers the tab bar and all tap gestures are blocked. Bindable from XAML/ViewModel.
        /// </summary>
        public bool IsTabBarEnabled
        {
            get => (bool)GetValue(IsTabBarEnabledProperty);
            set => SetValue(IsTabBarEnabledProperty, value);
        }

        /// <summary>Gets the legacy model associated with this view or child.</summary>
        public T ViewModel { get; }

        ViewModelBase IHasVM.ViewModel => ViewModel;

        /// <summary>Resolves a required service from the entry provider, or the application provider when no entry scope is owned. Throws if unavailable.</summary>
        public TService GetService<TService>() => Current.GetService<TService>() ?? throw new InvalidOperationException("Cannot resolve TService");

        /// <summary>Gets the service provider for this entry, falling back to the initialized application provider.</summary>
        public IServiceProvider Current
        {
            get
            {
                if (Services.NavigationEntryScope.For(this) is { } entryServices) return entryServices;
                IPlatformApplication? app = IPlatformApplication.Current;
                if (app == null)
                    throw new InvalidOperationException("Cannot resolve current application. Services should be accessed after MauiProgram initialization.");
                return app.Services;
            }
        }

        private readonly Grid _tabBarGrid = new()
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto) },
            ColumnSpacing = 8
        };
        private readonly Grid _tabBarRowGrid;

        private readonly ContentView _sharedContentHost = new();
        private readonly ContentView _tabContentHost = new();

        // The root layout: 3 rows (tab bar / shared content / tab content) x 2 columns (leading / standard).
        // Held as a field so subclasses can overlay or add leading content after construction.
        private readonly Grid _rootGrid;

        // Optional content pinned down the leading (left) edge of the page-content row. Collapses while empty.
        private readonly ContentView _leadingHost = new()
        {
            IsVisible = false
        };

        // Optional full-width header above everything else. Collapses while empty.
        private readonly ContentView _headerHost = new()
        {
            IsVisible = false
        };

        // The subclass-supplied shared content, remembered so an override can be swapped in and undone.
        private View? _sharedContentOverride;

        private readonly ContentView _tabBarTrailingHost = new()
        {
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center
        };

        private readonly ActivityIndicator _activityIndicator = new()
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };

        private readonly Grid _activityIndicatorOverlay;

        /// <summary>
        /// Semi-transparent overlay placed on top of the tab bar to indicate it is disabled.
        /// Uses InputTransparent = false to absorb taps when visible.
        /// Lives inside _tabBarGrid and spans all tab columns so it matches their width exactly.
        /// </summary>
        private readonly BoxView _tabBarOverlay = new()
        {
            Color = Colors.LightSlateGray,
            Opacity = 0.15,
            Margin = new Thickness(10, 1, 0, 1),
            IsVisible = false,
            CornerRadius = 8,
            InputTransparent = false,
            ZIndex = 1
        };

        private readonly List<ChildTabInfo> _children = new();
        private readonly List<TabItemContext> _tabItems = new();
        private readonly Dictionary<ChildTabInfo, Action> _activitySubscriptions = new();
        private ChildTabInfo? _currentTab;
        private bool _isHandlingNavigation;

        /// <summary>Identifies the bindable SharedContent property.</summary>
        public static readonly BindableProperty SharedContentProperty =
                    BindableProperty.Create(
                        nameof(SharedContent),
                        typeof(View),
                        typeof(CustomTabbedViewBase<T>),
                        null,
                        propertyChanged: (bindable, oldValue, newValue) =>
                        {
                            var self = (CustomTabbedViewBase<T>)bindable;

                            // An active override wins; it will fall back to this value when cleared.
                            if (self._sharedContentOverride is null)
                            {
                                self._sharedContentHost.Content = newValue as View;
                            }
                        });

        /// <summary>Identifies the bindable TabBarTrailingContent property.</summary>
        public static readonly BindableProperty TabBarTrailingContentProperty =
            BindableProperty.Create(
                nameof(TabBarTrailingContent),
                typeof(View),
                typeof(CustomTabbedViewBase<T>),
                null,
                propertyChanged: (bindable, oldValue, newValue) =>
                {
                    var self = (CustomTabbedViewBase<T>)bindable;
                    self._tabBarTrailingHost.Content = newValue as View;
                });

        /// <summary>Identifies the bindable ActivityIndicatorIsRunning property.</summary>
        public static readonly BindableProperty ActivityIndicatorIsRunningProperty =
            BindableProperty.Create(
                nameof(ActivityIndicatorIsRunning),
                typeof(bool),
                typeof(CustomTabbedViewBase<T>),
                false,
                propertyChanged: (bindable, oldValue, newValue) =>
                {
                    var self = (CustomTabbedViewBase<T>)bindable;
                    var isRunning = (bool)newValue;
                    self._activityIndicatorOverlay.IsVisible = isRunning;
                    self._activityIndicator.IsRunning = isRunning;
                });

        /// <summary>
        /// Drives the shared loading overlay rendered over the tab content area.
        /// Bind this on the concrete subclass to the hosting view model's busy flag.
        /// </summary>
        public bool ActivityIndicatorIsRunning
        {
            get => (bool)GetValue(ActivityIndicatorIsRunningProperty);
            set => SetValue(ActivityIndicatorIsRunningProperty, value);
        }

        /// <summary>Identifies the bindable BusyOverlayColor property.</summary>
        public static readonly BindableProperty BusyOverlayColorProperty =
            BindableProperty.Create(
                nameof(BusyOverlayColor),
                typeof(Color),
                typeof(CustomTabbedViewBase<T>),
                propertyChanged: (bindable, oldValue, newValue) =>
                {
                    var self = (CustomTabbedViewBase<T>)bindable;
                    if (newValue is Color color)
                    {
                        self._activityIndicator.Color = color;
                    }
                });

        /// <summary>Identifies the bindable BusyOverlayDescription property.</summary>
        public static readonly BindableProperty BusyOverlayDescriptionProperty =
            BindableProperty.Create(
                nameof(BusyOverlayDescription),
                typeof(string),
                typeof(CustomTabbedViewBase<T>),
                string.Empty,
                propertyChanged: (bindable, oldValue, newValue) =>
                {
                    var self = (CustomTabbedViewBase<T>)bindable;
                    SemanticProperties.SetDescription(self._activityIndicator, (string)newValue);
                    SemanticProperties.SetDescription(self._activityIndicatorOverlay, (string)newValue);
                });

        /// <summary>Identifies the bindable BusyOverlayBackgroundColor property.</summary>
        public static readonly BindableProperty BusyOverlayBackgroundColorProperty =
            BindableProperty.Create(
                nameof(BusyOverlayBackgroundColor),
                typeof(Color),
                typeof(CustomTabbedViewBase<T>),
                propertyChanged: (bindable, oldValue, newValue) =>
                {
                    var self = (CustomTabbedViewBase<T>)bindable;
                    if (newValue is Color color)
                    {
                        self._activityIndicatorOverlay.BackgroundColor = color;
                    }
                });

        /// <summary>
        /// Color of the spinner drawn over the busy overlay. No default: this project has no theme/resource
        /// lookup of its own, so an unset value leaves <c>ActivityIndicator.Color</c> at whatever MAUI's own
        /// control default is. A subclass that wants a specific color sets it explicitly, e.g.
        /// <c>BusyOverlayColor="{toolkit:AppThemeResource YourAccentColorKey}"</c>.
        /// </summary>
        public Color BusyOverlayColor
        {
            get => (Color)GetValue(BusyOverlayColorProperty);
            set => SetValue(BusyOverlayColorProperty, value);
        }

        /// <summary>
        /// Backdrop color of the full-screen busy overlay. Same "no default, generic infrastructure"
        /// pattern as <see cref="BusyOverlayColor"/> — an unset value leaves the overlay's Grid at MAUI's
        /// own default (transparent), so a subclass that wants a themed tint sets it explicitly, e.g.
        /// <c>BusyOverlayBackgroundColor="{toolkit:AppThemeResource YourColorKey}"</c>.
        /// </summary>
        public Color BusyOverlayBackgroundColor
        {
            get => (Color)GetValue(BusyOverlayBackgroundColorProperty);
            set => SetValue(BusyOverlayBackgroundColorProperty, value);
        }

        /// <summary>
        /// Narrator text for the busy overlay. No default (empty string): this project (MVVMCompass) has
        /// no reference to the concrete app's localization service, and isn't meant to acquire one just for
        /// one string — a subclass that has access to real localization sets this to a localized value.
        /// </summary>
        public string BusyOverlayDescription
        {
            get => (string)GetValue(BusyOverlayDescriptionProperty);
            set => SetValue(BusyOverlayDescriptionProperty, value);
        }

        /// <summary>
        /// The shared/common area displayed between the tab bar and tab content.
        /// Can be set in XAML via property-element syntax on the concrete subclass.
        /// </summary>
        public View? SharedContent
        {
            get => (View?)GetValue(SharedContentProperty);
            set => SetValue(SharedContentProperty, value);
        }

        /// <summary>
        /// Optional content displayed at the trailing (right) end of the tab bar row.
        /// Use for window chrome buttons, menus, or any controls that should sit alongside the tabs.
        /// Can be set in XAML via property-element syntax on the concrete subclass.
        /// </summary>
        public View? TabBarTrailingContent
        {
            get => (View?)GetValue(TabBarTrailingContentProperty);
            set => SetValue(TabBarTrailingContentProperty, value);
        }

        /// <summary>
        /// Read-only access to child tabs.
        /// </summary>
        public IReadOnlyList<ChildTabInfo> Children => _children.AsReadOnly();

        /// <summary>
        /// Read-only access to tab item contexts for external tab bar rendering.
        /// </summary>
        public IReadOnlyList<TabItemContext> TabItems => _tabItems.AsReadOnly();

        /// <summary>
        /// The currently active tab, or null if none.
        /// </summary>
        public ChildTabInfo? CurrentTab => _currentTab;

        /// <summary>
        /// Fired after a tab switch completes.
        /// </summary>
        public event EventHandler? CurrentTabChanged;

        /// <summary>Hosts extracted child content, retained tab selection, templates and shared presentation controls.</summary>
        public CustomTabbedViewBase(T viewModel)
        {
            ViewModel = viewModel;
            BindingContext = viewModel;
            ViewModel.RegisterSubscriptionCleanup(((IRetainedTabMaintenance)this).ReleaseSubscriptions);

            ViewModel.DisplayToastEvent += DisplayToast;
            ViewModel.SendCustomActionEvent += SendCustomAction;
            ViewModel.CustomActionDispatcher = action => Dispatcher.DispatchAsync(action);
            ViewModel.NotifyLanguageChangeEvent += NotifyLanguageChange;
            ViewModel.ShowLoadingEvent += ShowLoading;
            ViewModel.HideLoadingEvent += HideLoading;

            // Remove the NavigationPage toolbar space when hosted inside one
            NavigationPage.SetHasNavigationBar(this, false);
            Padding = 0;

            // Place the overlay inside the tab bar grid itself so it only covers the tab columns.
            // Column span is updated each time a tab is added via AddTabBarItem.
            _tabBarGrid.Children.Add(_tabBarOverlay);

            // Wrap tab items + trailing content in a single row
            _tabBarRowGrid = new Grid
            {
                ColumnSpacing = 0,
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),  // Tab items
                    new ColumnDefinition(GridLength.Star),   // Spacer / drag region
                    new ColumnDefinition(GridLength.Auto),   // Trailing content
                },
                HeightRequest = 32,
                Padding = new Thickness(5, 0)
            };

            Grid.SetColumn(_tabBarGrid, 0);
            Grid.SetColumn(_tabBarTrailingHost, 2);

            _tabBarRowGrid.Children.Add(_tabBarGrid);
            _tabBarRowGrid.Children.Add(_tabBarTrailingHost);

            _rootGrid = new Grid
            {
                RowSpacing = 0,
                ColumnSpacing = 0,
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),  // Header (full width, hidden unless supplied)
                    new RowDefinition(GridLength.Auto),  // Tab bar row (tabs + trailing)
                    new RowDefinition(GridLength.Auto),  // Shared content
                    new RowDefinition(GridLength.Star),  // Leading content + tab content
                },
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),  // Leading content (collapses when empty/hidden)
                    new ColumnDefinition(GridLength.Star),  // Standard content
                }
            };

            // The header, tab bar and shared panel run the full width; only the bottom row is split so the
            // leading content sits BESIDE the page content rather than beneath the chrome above it.
            Grid.SetRow(_headerHost, 0);
            Grid.SetRow(_tabBarRowGrid, 1);
            Grid.SetRow(_sharedContentHost, 2);
            Grid.SetRow(_tabContentHost, 3);

            _tabContentHost.SetValue(AutomationIdProperty, "TabContentHost");
            _tabBarTrailingHost.SetValue(AutomationIdProperty, "TabBarTrailingHost");

            Grid.SetColumn(_tabContentHost, 1);

            foreach (var fullWidth in new View[] { _headerHost, _tabBarRowGrid, _sharedContentHost })
            {
                Grid.SetColumn(fullWidth, 0);
                Grid.SetColumnSpan(fullWidth, _rootGrid.ColumnDefinitions.Count);
            }

            _rootGrid.Children.Add(_headerHost);
            _rootGrid.Children.Add(_tabBarRowGrid);
            _rootGrid.Children.Add(_sharedContentHost);
            _rootGrid.Children.Add(_tabContentHost);

            // Leading content: the page-content row only, so it starts level with the content.
            Grid.SetRow(_leadingHost, 3);
            Grid.SetColumn(_leadingHost, 0);
            _rootGrid.Children.Add(_leadingHost);

            // Loading overlay, sized to the tab content row and drawn above it. BusyOverlayColor has no
            // default (see its own doc comment) — only touch ActivityIndicator.Color if a subclass actually
            // set one, so an app that never sets it keeps MAUI's own control default instead of an explicit
            // null (which some platforms render as no color at all, not "use the default").
            if (BusyOverlayColor is Color busyOverlayColor)
            {
                _activityIndicator.Color = busyOverlayColor;
            }

            _activityIndicator.SetValue(AutomationIdProperty, "TabContentBusyIndicator");
            SemanticProperties.SetDescription(_activityIndicator, BusyOverlayDescription);
            _activityIndicatorOverlay = new Grid
            {
                IsVisible = false,
                ZIndex = 2
            };
            // BusyOverlayBackgroundColor has no default (see its own doc comment) — only touch
            // BackgroundColor if a subclass actually set one, same reasoning as BusyOverlayColor above.
            if (BusyOverlayBackgroundColor is Color busyOverlayBackgroundColor)
            {
                _activityIndicatorOverlay.BackgroundColor = busyOverlayBackgroundColor;
            }
            // A Grid normally stays untagged (an id promotes an otherwise-anonymous layout into
            // Narrator's Content view) — this one is the exception: it's an IsVisible-toggled
            // full-screen busy overlay, not permanent structural chrome.
            _activityIndicatorOverlay.SetValue(AutomationIdProperty, "TabContentBusyOverlay");
            SemanticProperties.SetDescription(_activityIndicatorOverlay, BusyOverlayDescription);
            _activityIndicatorOverlay.Children.Add(_activityIndicator);
            Grid.SetRow(_activityIndicatorOverlay, 3);
            Grid.SetColumn(_activityIndicatorOverlay, 1);
            _rootGrid.Children.Add(_activityIndicatorOverlay);

            Content = _rootGrid;
        }

        /// <summary>
        /// Adds a view that overlays the entire page (spans all rows AND columns), drawn above the standard
        /// content. Used by subclasses to host things like a compact/overlay presentation without exposing the
        /// internal layout.
        /// </summary>
        protected void AddOverlay(View overlay)
        {
            Grid.SetRow(overlay, 0);
            Grid.SetRowSpan(overlay, _rootGrid.RowDefinitions.Count);
            Grid.SetColumn(overlay, 0);
            Grid.SetColumnSpan(overlay, _rootGrid.ColumnDefinitions.Count);
            _rootGrid.Children.Add(overlay);
        }

        /// <summary>
        /// Adds a view that overlays just the page-content row (leading content + tab content), spanning both
        /// columns. Unlike <see cref="AddOverlay"/> this leaves the header, tab bar and shared-content rows
        /// above it visible AND clickable — required for a presentation whose only dismiss control lives in
        /// that chrome (e.g. a picker toggled from the shared-content row), which a full-page overlay would
        /// cover and trap the user behind.
        /// </summary>
        protected void AddContentOverlay(View overlay)
        {
            Grid.SetRow(overlay, 3);
            Grid.SetColumn(overlay, 0);
            Grid.SetColumnSpan(overlay, _rootGrid.ColumnDefinitions.Count);
            _rootGrid.Children.Add(overlay);
        }

        /// <summary>
        /// Shows or hides the standard content (tab bar row, shared content, tab content) as a group.
        /// Hidden rows collapse, letting an overlay fill the page.
        /// </summary>
        protected void SetPrimaryContentVisible(bool visible)
        {
            _tabBarRowGrid.IsVisible = visible;
            _sharedContentHost.IsVisible = visible;
            _tabContentHost.IsVisible = visible;
        }

        /// <summary>Shows or hides just the tab bar row, leaving shared/tab content untouched.</summary>
        protected void SetTabBarRowVisible(bool visible)
        {
            _tabBarRowGrid.IsVisible = visible;
        }

        /// <summary>Sets the content pinned down the leading (left) edge, spanning every row.</summary>
        protected void SetLeadingContent(View? content)
        {
            _leadingHost.Content = content;
        }

        /// <summary>Shows or hides the leading content. Hidden, its column collapses to zero width.</summary>
        protected void SetLeadingContentVisible(bool visible)
        {
            _leadingHost.IsVisible = visible;
        }

        /// <summary>
        /// Pins the leading (rail) column to an exact width instead of leaving it `Auto`-negotiated.
        /// `Auto` sizes to content, which normally resolves to whatever the rail itself requests — but
        /// `_headerHost`/`_tabBarRowGrid`/`_sharedContentHost` all span this column plus the standard
        /// content column (see ColumnSpan set below), and when one of those full-width rows changes
        /// several things in the same layout pass, the Auto resolution can come out wider than the rail's
        /// own content actually needs, with nothing clipping the excess. A caller that knows its rail's
        /// content is a fixed size can call this with that exact value to remove the ambiguity; passing
        /// `GridLength.Auto` restores the original content-driven sizing (e.g. when the rail is hidden and
        /// should collapse to 0).
        /// </summary>
        protected void SetLeadingColumnWidth(GridLength width)
        {
            _rootGrid.ColumnDefinitions[0].Width = width;
        }

        /// <summary>Sets the full-width header shown above the tab bar.</summary>
        protected void SetHeaderContent(View? content)
        {
            _headerHost.Content = content;
        }

        /// <summary>Shows or hides the header. Hidden, its row collapses to zero height.</summary>
        protected void SetHeaderContentVisible(bool visible)
        {
            _headerHost.IsVisible = visible;
        }

        /// <summary>
        /// Temporarily replaces the shared content panel (e.g. a compact variant for a narrow presentation).
        /// Pass null to restore whatever <see cref="SharedContent"/> holds.
        /// </summary>
        protected void SetSharedContentOverride(View? content)
        {
            _sharedContentOverride = content;
            _sharedContentHost.Content = content ?? SharedContent;
        }

        /// <summary>
        /// Updates the overlay visibility based on the IsTabBarEnabled state.
        /// </summary>
        private void UpdateTabBarOverlay(bool isEnabled)
        {
            _tabBarOverlay.IsVisible = !isEnabled;
        }

        #region ICustomTabbedViewBase explicit implementation

        void ICustomTabbedViewBase.AddChildInternal(ChildTabInfo child) => AddChild(child);
        int ICustomTabbedViewBase.ChildCount => _children.Count;
        Task<bool> ICustomTabbedViewBase.SwitchToAsync(int index) => SwitchToAsync(index);
        Task<bool> ICustomTabbedViewBase.SwitchToAsync(Type viewModelType) => SwitchToAsync(viewModelType);

        bool ICustomTabbedViewBase.IsTabBarEnabled
        {
            get => IsTabBarEnabled;
            set => IsTabBarEnabled = value;
        }

        #endregion

        /// <summary>
        /// Called by LegacyNavigationService when child tabs are created.
        /// Adds the child and auto-generates a corresponding tab bar item.
        /// </summary>
        internal void AddChild(ChildTabInfo child)
        {
            _children.Add(child);

            var tabContext = CreateTabItemContext(child);
            tabContext.Index = _tabItems.Count;
            _tabItems.Add(tabContext);

            // Hidden tabs stay navigable (they're in _children/_tabItems) but contribute no tab bar item.
            if (!child.HideInTabBar)
            {
                AddTabBarItem(tabContext);
            }
        }

        void IRetainedTabMaintenance.ReleaseSubscriptions()
        {
            foreach (var unsubscribe in _activitySubscriptions.Values) unsubscribe();
            _activitySubscriptions.Clear();
        }

        void IRetainedTabMaintenance.RemoveChildrenAfter(int count)
        {
            foreach (var child in _children.Skip(count))
            {
                if (_activitySubscriptions.Remove(child, out var unsubscribe)) unsubscribe();
                child.View.Parent = null;
            }
            var visibleCount = _children.Take(count).Count(child => !child.HideInTabBar);
            foreach (var chip in _tabBarGrid.Children.OfType<View>()
                .Where(view => !ReferenceEquals(view, _tabBarOverlay) && Grid.GetColumn(view) >= visibleCount).ToArray())
                _tabBarGrid.Children.Remove(chip);
            while (_tabBarGrid.ColumnDefinitions.Count > visibleCount)
                _tabBarGrid.ColumnDefinitions.RemoveAt(_tabBarGrid.ColumnDefinitions.Count - 1);
            Grid.SetColumnSpan(_tabBarOverlay, Math.Max(1, visibleCount));
            _children.RemoveRange(count, _children.Count - count);
            _tabItems.RemoveRange(count, _tabItems.Count - count);
        }

        private TabItemContext CreateTabItemContext(ChildTabInfo child)
        {
            var context = new TabItemContext();

            // Extract tab metadata from the View first, then fall back to ViewModel
            if (child.View is ITitleProvider viewTitle)
                context.Title = viewTitle.GetTitle();
            else if (child.ViewModel is ITitleProvider vmTitle)
                context.Title = vmTitle.GetTitle();

            if (child.View is ISelectedIconProvider viewSelIcon)
                context.SelectedIcon = viewSelIcon.GetSelectedIcon();
            else if (child.ViewModel is ISelectedIconProvider vmSelIcon)
                context.SelectedIcon = vmSelIcon.GetSelectedIcon();

            if (child.View is IUnselectedIconProvider viewUnselIcon)
                context.UnselectedIcon = viewUnselIcon.GetUnselectedIcon();
            else if (child.ViewModel is IUnselectedIconProvider vmUnselIcon)
                context.UnselectedIcon = vmUnselIcon.GetUnselectedIcon();

            // Unlike the metadata above (read once), IsBusy can change for the lifetime of the tab, so also
            // re-read it on every PropertyChanged from the ViewModel — the flag is expected to live behind an
            // observable property, and IActivityIndicatorProvider itself has no change notification of its own.
            if (child.ViewModel is IActivityIndicatorProvider vmActivity)
            {
                context.IsBusy = vmActivity.IsTabBusy;
                System.ComponentModel.PropertyChangedEventHandler changed = (s, e) => context.IsBusy = vmActivity.IsTabBusy;
                child.ViewModel.PropertyChanged += changed;
                _activitySubscriptions.Add(child, () => child.ViewModel.PropertyChanged -= changed);
            }

            return context;
        }

        private void AddTabBarItem(TabItemContext tabContext)
        {
            _tabBarGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            // Column position is the count of VISIBLE tab items, not the tab's global index — hidden tabs
            // (HideInTabBar) add no column, so the two can diverge.
            int column = _tabBarGrid.ColumnDefinitions.Count - 1;

            // Keep the overlay spanning all tab columns
            Grid.SetColumnSpan(_tabBarOverlay, _tabBarGrid.ColumnDefinitions.Count);

            // Create both views upfront — swap via IsSelected change
            var selectedView = CreateTabView(SelectedTabItemTemplate, tabContext, isSelected: true);
            var unselectedView = CreateTabView(UnselectedTabItemTemplate, tabContext, isSelected: false);

            var container = new ContentView
            {
                Content = tabContext.IsSelected ? selectedView : unselectedView
            };
            // This ContentView, not the SelectedTabItemTemplate/UnselectedTabItemTemplate content
            // swapped inside it, is the real hit target below — it's the one that owns the tap
            // gesture and drives SwitchToAsync. Tab order is stable, so Index (already used to
            // drive SwitchToAsync below) is already an invariant, scope-local identity — no need
            // for a separate id-only property.
            //
            // "TabBarChip", not "TabRailChip": the app-specific dock rail (BaseCustomTabbedView's
            // TabSurfaceItems) already uses TabRailChip_{featureId} for its own chips, and both surfaces
            // can be present in the same tree at once (the horizontal bar row collapses via IsVisible
            // when docked, which doesn't remove it from the automation tree) — same id prefix, different
            // elements, would make a FlaUI query for TabRailChip_X ambiguous between the two surfaces.
            container.SetValue(AutomationIdProperty, $"TabBarChip_{tabContext.Index}");
            container.SetBinding(SemanticProperties.DescriptionProperty,
                static (TabItemContext source) => source.Title, source: tabContext);

            tabContext.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(TabItemContext.IsSelected))
                    container.Content = tabContext.IsSelected ? selectedView : unselectedView;
            };

            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += async (s, e) =>
            {
                if (!IsTabBarEnabled) return;
                try { await SwitchToAsync(tabContext.Index); }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Custom tab selection"); }
            };
            container.GestureRecognizers.Add(tapGesture);

            Grid.SetColumn(container, column);
            _tabBarGrid.Children.Add(container);
        }

        private View CreateTabView(DataTemplate? template, TabItemContext tabContext, bool isSelected)
        {
            if (template != null)
            {
                var view = (View)template.CreateContent();
                view.BindingContext = tabContext;
                return view;
            }

            return CreateDefaultTabItem(tabContext, isSelected);
        }

        /// <summary>
        /// Creates a default tab item view when no template is provided for the given state.
        /// Override to customise the default appearance without needing a full DataTemplate.
        /// </summary>
        protected virtual View CreateDefaultTabItem(TabItemContext tabContext, bool isSelected)
        {
            var icon = new Image
            {
                WidthRequest = 24,
                HeightRequest = 24,
                HorizontalOptions = LayoutOptions.Center
            };
            if (isSelected)
                icon.SetBinding(Image.SourceProperty, static (TabItemContext source) => source.SelectedIcon);
            else
                icon.SetBinding(Image.SourceProperty, static (TabItemContext source) => source.UnselectedIcon);

            var label = new Label
            {
                HorizontalTextAlignment = TextAlignment.Center,
                FontSize = 12,
                TextColor = isSelected ? SelectedTabTextColor : UnselectedTabTextColor
            };
            label.SetBinding(Label.TextProperty, static (TabItemContext source) => source.Title);

            var stack = new VerticalStackLayout
            {
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Spacing = 4,
                Children = { icon, label }
            };

            var container = new Border
            {
                Padding = new Thickness(8, 0),
                StrokeThickness = 0,
                HorizontalOptions = LayoutOptions.Fill,
                BackgroundColor = isSelected ? SelectedTabBackgroundColor : UnselectedTabBackgroundColor,
                Content = stack
            };

            container.BindingContext = tabContext;

            return container;
        }

        /// <summary>
        /// Switch to a tab by ViewModel type. Includes CanNavigate guard
        /// and full lifecycle (AfterDismissed → visual state update → Appearing → NavigatedTo).
        /// </summary>
        protected Task<bool> SwitchToAsync(Type viewModelType)
        {
            var targetIndex = _children.FindIndex(c =>
                c.ViewModel.GetType() == viewModelType);

            if (targetIndex < 0)
                throw new InvalidOperationException(
                    $"No child tab registered for {viewModelType.Name}");

            return SwitchToIndexAsync(targetIndex);
        }

        private async Task<bool> SwitchToIndexAsync(int targetIndex)
        {
            var bridge = RetainedNavigationBridge.Find(this);
            if (ViewModel.IsDismissed) return false;
            if (bridge is { Applying: false, Composing: false, SelectTab: { } select }) return await select(targetIndex);
            if (_isHandlingNavigation || !IsTabBarEnabled) return false;
            var target = _children[targetIndex];

            if (target == _currentTab) return true;

            _isHandlingNavigation = true;

            try
            {
                // Guard: ask current tab if navigation is allowed
                if (_currentTab != null && bridge?.Applying != true)
                {
                    var canNavigate = await _currentTab.ViewModel.CanNavigate();
                    if (!canNavigate) return false;

                    await _currentTab.ViewModel.DeactivateRetainedAsync();
                }

                // Update tab selection visual state — triggers ContentView swap
                var currentIndex = _currentTab != null ? _children.IndexOf(_currentTab) : -1;
                if (currentIndex >= 0 && currentIndex < _tabItems.Count)
                    _tabItems[currentIndex].IsSelected = false;
                _tabItems[targetIndex].IsSelected = true;

                // Swap content
                _tabContentHost.Content = target.Content;
                _currentTab = target;

                // Notify new tab — View lifecycle (fires Page.OnNavigatedTo)
                if (target.View is ITabLifecycleReceiver receiver)
                    receiver.OnTabNavigatedTo();

                // Notify new tab — ViewModel lifecycle
                await target.ViewModel.NavigatedTo();
                await target.ViewModel.Appearing();

                // Notify parent ViewModel that the active tab changed
                if (this is IHasVM hasVm)
                    await hasVm.ViewModel.OnActiveTabChanged(target.ViewModel);

                CurrentTabChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            finally
            {
                _isHandlingNavigation = false;
            }
        }

        /// <summary>
        /// Switch to a tab by index.
        /// </summary>
        protected Task<bool> SwitchToAsync(int index)
        {
            if (index < 0 || index >= _children.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            return SwitchToIndexAsync(index);
        }

        /// <summary>Delivers a notification through the application-provided notification service.</summary>
        public void SendNotification(string text, ToastType toastType)
        {
            GetService<INotificationService>().SendNotification(text, toastType);
        }

        /// <summary>Handles or dispatches a toast request and awaits its presentation callback.</summary>
        protected abstract Task DisplayToast(ToastEventArgs args);

        /// <summary>Handles or dispatches an application-defined action and returns its result, which may be null.</summary>
        protected abstract Task<object?> SendCustomAction(CustomActionEventArgs args);

        /// <summary>Requests a localization refresh and returns whether the view handled it.</summary>
        protected abstract bool NotifyLanguageChange();

        /// <summary>Shows the requested loading presentation and returns its cleanup handle; an unhandled model request returns null.</summary>
        protected abstract IDisposable ShowLoading(LoadingType loadingType);

        /// <summary>Hides the active loading presentation.</summary>
        protected abstract void HideLoading();
    }
}
