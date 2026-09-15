using Microsoft.Maui.Dispatching;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;
using Microsoft.Maui.Controls.PlatformConfiguration.WindowsSpecific;
using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using MVVMCompass.Interfaces;
using Page = Microsoft.Maui.Controls.Page;
using VisualElement = Microsoft.Maui.Controls.VisualElement;

namespace MVVMCompass
{
    /// <summary>Content page with a typed legacy model, lifecycle forwarding and delegated presentation helpers.</summary>
    internal abstract class LegacyViewBase<T> : ContentPage, IHasVM, IModal, ITabLifecycleReceiver where T : ViewModelBase
    {
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

        private bool _isModal;
        /// <summary>Gets or sets whether this destination uses modal presentation.</summary>
        public bool IsModal
        {
            get => _isModal;

            set
            {
                _isModal = value;
                ViewModel.IsModal = value;
            }
        }

        /// <summary>Content page with a typed legacy model, lifecycle forwarding and delegated presentation helpers.</summary>
        public LegacyViewBase(T viewModel)
        {
            ViewModel = viewModel;
            BindingContext = viewModel;

            ViewModel.DisplayToastEvent += DisplayToast;
            ViewModel.SendCustomActionEvent += SendCustomAction;
            ViewModel.CustomActionDispatcher = action => Dispatcher.DispatchAsync(action);
            ViewModel.NotifyLanguageChangeEvent += NotifyLanguageChange;
            ViewModel.ShowLoadingEvent += ShowLoading;
            ViewModel.HideLoadingEvent += HideLoading;
        }

        /// <summary>Forwards navigation-to notification when extracted content becomes the selected child.</summary>
        public virtual void OnTabNavigatedTo()
        {
            OnNavigatedTo(null);
        }

        /// <summary>
        /// Gets the nearest ancestor Page that has a platform Handler.
        /// When this view is a tab child inside <see cref="CustomTabbedViewBase{T}"/>,
        /// it has no Handler (its content was extracted). We walk up the Parent chain
        /// (set in LegacyNavigationService) to find the host page that IS in the visual tree.
        /// For normal (non-tab) pages, returns <c>this</c>.
        /// </summary>
        protected Page GetHandledPage()
        {
            if (Handler != null)
                return this;

            Element? current = Parent;
            while (current != null)
            {
                if (current is Page page && page.Handler != null)
                    return page;
                current = current.Parent;
            }

            return Window?.Page ?? Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page
                ?? throw new InvalidOperationException(
                    "No handled Page found. Ensure the view is parented to a visual tree.");
        }

        /// <summary>Displays an alert on this view's handled page and awaits its completion within the owning window.</summary>
        protected new Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel)
        {
            var page = GetHandledPage();
            return Services.HandledPageDialogs.RunAsync(page, () => page.DisplayAlertAsync(title, message, accept, cancel));
        }

        /// <summary>Displays an alert on this view's handled page and awaits its completion within the owning window.</summary>
        protected new async Task DisplayAlertAsync(string title, string message, string cancel)
        {
            var page = GetHandledPage();
            await Services.HandledPageDialogs.RunAsync(page, async () => { await page.DisplayAlertAsync(title, message, cancel); return true; });
        }

        // Preserve the existing void helper while observing asynchronous Toolkit failures.
        /// <summary>Presents a Toolkit popup on the handled page; asynchronous failures are reported through navigation diagnostics.</summary>
        public void ShowPopup(Popup popup)
        {
            ArgumentNullException.ThrowIfNull(popup);
            Services.HandledPageDialogs.Observe(ShowOwnedPopupAsync<object?>(popup));
        }

        /// <summary>Presents a typed Toolkit popup on the handled page and awaits its result and owned completion.</summary>
        public Task<IPopupResult<TResult>> ShowPopupAsync<TResult>(Popup<TResult> popup) => ShowOwnedPopupAsync<TResult>(popup);

        private Task<IPopupResult<TResult>> ShowOwnedPopupAsync<TResult>(Popup popup)
        {
            ArgumentNullException.ThrowIfNull(popup);
            if (ViewModel.IsDismissed) throw new InvalidOperationException("A dismissed view cannot present a popup.");
            var page = GetHandledPage();
            return Services.HandledPageDialogs.RunAsync<IPopupResult<TResult>>(page, async () =>
                await Services.PopupOwnership.ShowAsync<TResult>(page.Window
                    ?? throw new InvalidOperationException("The handled page requires a window."), popup, ViewModel));
        }

        /// <summary>Delivers a notification through the application-provided notification service.</summary>
        public void SendNotification(string text, ToastType toastType)
        {
            GetService<INotificationService>().SendNotification(text, toastType);
        }

        /// <summary>Creates a short or long Toolkit toast; notification categories require the notification service.</summary>
        public IToast GetToast(string text, ToastType toastType)
        {
            switch (toastType)
            {
                case ToastType.Long:
                    return Toast.Make(text, ToastDuration.Long);
                case ToastType.Short:
                    return Toast.Make(text, ToastDuration.Short);
                default:
                    throw new InvalidOperationException("Wrong type of Toast");
            }
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

    /// <summary>Standard MAUI tabbed page with a typed legacy model and presentation helpers.</summary>
    internal abstract class LegacyTabbedViewBase<T> : Microsoft.Maui.Controls.TabbedPage, IHasVM where T : ViewModelBase
    {
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

        /// <summary>Standard MAUI tabbed page with a typed legacy model and presentation helpers.</summary>
        public LegacyTabbedViewBase(T viewModel)
        {
            ViewModel = viewModel;
            BindingContext = viewModel;

            ViewModel.DisplayToastEvent += DisplayToast;
            ViewModel.SendCustomActionEvent += SendCustomAction;
            ViewModel.CustomActionDispatcher = action => Dispatcher.DispatchAsync(action);
            ViewModel.NotifyLanguageChangeEvent += NotifyLanguageChange;
            ViewModel.ShowLoadingEvent += ShowLoading;
            ViewModel.HideLoadingEvent += HideLoading;

            On<Microsoft.Maui.Controls.PlatformConfiguration.Android>().SetToolbarPlacement(Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.ToolbarPlacement.Bottom);
            On<Microsoft.Maui.Controls.PlatformConfiguration.Windows>().SetToolbarPlacement(Microsoft.Maui.Controls.PlatformConfiguration.WindowsSpecific.ToolbarPlacement.Bottom);
        }

        /// <summary>Delivers a notification through the application-provided notification service.</summary>
        public void SendNotification(string text, ToastType toastType)
        {
            GetService<INotificationService>().SendNotification(text, toastType);
        }

        /// <summary>Creates a short or long Toolkit toast; notification categories require the notification service.</summary>
        public IToast GetToast(string text, ToastType toastType)
        {
            switch (toastType)
            {
                case ToastType.Long:
                    return Toast.Make(text, ToastDuration.Long);
                case ToastType.Short:
                    return Toast.Make(text, ToastDuration.Short);
                default:
                    throw new InvalidOperationException("Wrong type of Toast");
            }
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

    /// <summary>MAUI flyout host with a typed legacy model and presentation helpers.</summary>
    internal abstract class LegacyFlyoutViewBase<T> : Microsoft.Maui.Controls.FlyoutPage, IHasVM where T : ViewModelBase
    {
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

        /// <summary>MAUI flyout host with a typed legacy model and presentation helpers.</summary>
        public LegacyFlyoutViewBase(T viewModel)
        {
            ViewModel = viewModel;
            BindingContext = viewModel;

            ViewModel.DisplayToastEvent += DisplayToast;
            ViewModel.SendCustomActionEvent += SendCustomAction;
            ViewModel.CustomActionDispatcher = action => Dispatcher.DispatchAsync(action);
            ViewModel.NotifyLanguageChangeEvent += NotifyLanguageChange;
            ViewModel.ShowLoadingEvent += ShowLoading;
            ViewModel.HideLoadingEvent += HideLoading;
        }

        /// <summary>Delivers a notification through the application-provided notification service.</summary>
        public void SendNotification(string text, ToastType toastType)
        {
            GetService<INotificationService>().SendNotification(text, toastType);
        }

        /// <summary>Creates a short or long Toolkit toast; notification categories require the notification service.</summary>
        public IToast GetToast(string text, ToastType toastType)
        {
            switch (toastType)
            {
                case ToastType.Long:
                    return Toast.Make(text, ToastDuration.Long);
                case ToastType.Short:
                    return Toast.Make(text, ToastDuration.Short);
                default:
                    throw new InvalidOperationException("Wrong type of Toast");
            }
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

    /// <summary>Flyout menu with observable item metadata, guarded selection and popup actions.</summary>
    internal abstract class FlyoutViewFlyoutBase<T> : ContentPage, IHasVM, IFlyoutMenuSelectItem, IFlyoutMenuItems, IFlyoutMenuClosedEvent where T : ViewModelBase
    {
        /// <summary>Identifies the bindable MenuItems property.</summary>
        public static readonly BindableProperty MenuItemsProperty =
            BindableProperty.Create(nameof(MenuItems), typeof(ObservableCollection<FlyoutMenuItem>), typeof(FlyoutViewFlyoutBase<T>), default(ObservableCollection<FlyoutMenuItem>), BindingMode.TwoWay, null);

        /// <summary>Gets or sets the flyout menu's retained item collection.</summary>
        public ObservableCollection<FlyoutMenuItem> MenuItems
        {
            get => (ObservableCollection<FlyoutMenuItem>)GetValue(MenuItemsProperty);
            set => SetValue(MenuItemsProperty, value);
        }

        /// <summary>Gets the legacy model associated with this view or child.</summary>
        public T ViewModel { get; }

        ViewModelBase IHasVM.ViewModel => ViewModel;

        /// <summary>Raised when the flyout menu requests closure.</summary>
        public event EventHandler<EventArgs?>? ClosedFlyout;

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

        /// <summary>Flyout menu with observable item metadata, guarded selection and popup actions.</summary>
        public FlyoutViewFlyoutBase(T viewModel)
        {
            ViewModel = viewModel;
            BindingContext = viewModel;
        }

        /// <summary>Raises the menu closure notification.</summary>
        public void CloseFlyout(object sender, EventArgs? e)
        {
            ClosedFlyout?.Invoke(sender, e);
        }

        /// <summary>Routes collection-view selection to the retained flyout item.</summary>
        protected async void MenuItems_ItemSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e?.CurrentSelection == null || e.CurrentSelection.Count == 0)
            {
                return;
            }

            var selectedItem = e?.CurrentSelection?.FirstOrDefault() as FlyoutMenuItem;

            var collection = sender as CollectionView;
            if (collection != null)
            {
                collection.SelectedItem = null;
                collection.SelectedItems = new List<object>();
            }

            if (selectedItem != null)
            {
                try { await SelectMenuItem(selectedItem, null); }
                catch (Exception exception) { NavigationDiagnostics.Report(exception, "Flyout selection"); }
            }
            else
            {
                throw new Exception("MenuItems_ItemSelected selected item null");
            }
        }

        /// <summary>Selects the first matching flyout item by metadata ID with optional parameters.</summary>
        public async Task SelectMenuItemById(string id, Dictionary<string, object>? parameters)
        {
            FlyoutMenuItem? selectedItem = MenuItems.FirstOrDefault(item => item.Id == id);

            if (selectedItem != null)
            {
                await SelectMenuItem(selectedItem, parameters);
            }
            else
            {
                throw new Exception("SelectMenuItemById selected item null");
            }
        }

        /// <summary>Selects the supplied retained page or opens its popup action; guards may retain the current destination.</summary>
        public async Task SelectMenuItem(FlyoutMenuItem selectedItem, Dictionary<string, object>? parameters)
        {
            if (ViewModel.IsDismissed) return;
            if (selectedItem.Content is Page && Services.RetainedNavigationBridge.Find(this) is
                { Applying: false, Composing: false, SelectFlyout: { } select })
            {
                await select(selectedItem, parameters);
                return;
            }
            if (selectedItem.Content is Page page)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    CloseFlyout(this, null);
                });

                var previousSelectedItem = MenuItems.FirstOrDefault(item => item.IsSelected);

                if (ReferenceEquals(previousSelectedItem, selectedItem) && parameters == null)
                    return;

                var previousHasVM = previousSelectedItem?.Content as IHasVM;

                if (previousHasVM != null)
                {
                    var canNavigate = await previousHasVM.ViewModel.CanNavigate();

                    if (!canNavigate)
                    {
                        return;
                    }

                    await previousHasVM.ViewModel.DeactivateRetainedAsync();
                }

                if (previousSelectedItem != null)
                {
                    previousSelectedItem.IsSelected = false;
                }

                selectedItem.IsSelected = true;

                await RunOnAppearing(selectedItem, parameters);

                if (selectedItem.UpdateDetail != null)
                {
                    selectedItem.UpdateDetail(page);
                }
                else
                {
                    throw new Exception("SelectMenuItem UpdateDetail null");
                }
            }
            else if (selectedItem.Content is View view)
            {
                var window = Window ?? throw new InvalidOperationException("A flyout popup requires an owning window.");
                var root = window.Page;
                if (Services.ViewModelTree.Collect(view).Any(model => model.IsDismissed))
                {
                    view = await (selectedItem.RecreatePopupAsync?.Invoke()
                        ?? throw new InvalidOperationException("Replace a completed popup with a fresh view before showing it again."));
                    if (ViewModel.IsDismissed || window.Page != root)
                    {
                        await Services.LegacyNavigationService.DismissViewModelsAsync(Services.ViewModelTree.Collect(view), Core.DismissalReason.PreparationFailed);
                        return;
                    }
                    selectedItem.Content = view;
                    if (MauiNavigationHostFactory.Find(window) is { } host) await host.ReconcileNativeAsync();
                }
                await RunOnAppearing(selectedItem, parameters);
                if (ViewModel.IsDismissed || window.Page != root) return;
                Services.HandledPageDialogs.Observe(window.Dispatcher.DispatchAsync(async () =>
                {
                    var showing = Services.PopupOwnership.ShowAsync<object?>(window, view, ViewModel);
                    CloseFlyout(this, null);
                    await showing;
                }));
            }
            else
            {
                throw new Exception("SelectMenuItem selected item invalid type");
            }
        }

        private async Task RunOnAppearing(FlyoutMenuItem selectedItem, Dictionary<string, object>? parameters)
        {
            var newHasVM = selectedItem.Content as IHasVM;
            {
                if (newHasVM != null)
                {
                    if (parameters != null)
                    {
                        await newHasVM.ViewModel.GetParameters(parameters);
                    }

                    await newHasVM.ViewModel.Appearing();
                }
            }
        }
    }

    /// <summary>Indicates whether a registered destination should use modal presentation.</summary>
    internal interface IModal
    {
        /// <summary>Gets or sets whether this destination uses modal presentation.</summary>
        bool IsModal { get; set; }
    }

    /// <summary>
    /// Implemented by a host page that wants to know when a tab child it hosts (see
    /// <see cref="LegacyViewBase{T}.GetHandledPage"/>) is about to show a delegated alert/popup on it, and
    /// when that resolves. MVVMCompass has no opinion on what a host does with this — it exists
    /// purely so one can react, without LegacyViewBase needing any knowledge of what that reaction is.
    /// </summary>
    internal interface IHandledPageModalHost
    {
        /// <summary>Notifies this handled page immediately before a delegated modal presentation.</summary>
        void OnHandledPageModalOpening();

        /// <summary>Balances a delegated modal notification after completion and owned cleanup, including failures.</summary>
        void OnHandledPageModalClosed();
    }

    /// <summary>Optional title and initial geometry for a newly opened window.</summary>
    internal interface IWindowView
    {
        /// <summary>Gets the requested title for a newly created window.</summary>
        string WindowTitle { get; }

        /// <summary>Gets the requested initial window height in device-independent units.</summary>
        int WindowHeight { get; }

        /// <summary>Gets the requested initial window width in device-independent units.</summary>
        int WindowWidth { get; }
    }

    /// <summary>Provides a stable metadata identifier for a navigation item.</summary>
    internal interface IIdProvider
    {
        /// <summary>Returns the stable metadata identifier used to locate this item.</summary>
        public abstract string GetId();
    }

    /// <summary>Provides display text for a tab or flyout item.</summary>
    internal interface ITitleProvider
    {
        /// <summary>Returns the current display title for this item.</summary>
        public abstract string GetTitle();
    }

    /// <summary>Provides the icon displayed when an item is selected.</summary>
    internal interface ISelectedIconProvider
    {
        /// <summary>Returns the icon displayed for this item when selected.</summary>
        public abstract string GetSelectedIcon();
    }

    /// <summary>Provides the icon displayed when an item is inactive.</summary>
    internal interface IUnselectedIconProvider
    {
        /// <summary>Returns the icon displayed for this item when inactive.</summary>
        public abstract string GetUnselectedIcon();
    }

    /// <summary>
    /// Optional capability for a tab's ViewModel: exposes a busy flag so the host can show an in-progress
    /// indicator on that tab's chip even while a different tab is active (e.g. Chart's findings
    /// extraction). Named IsTabBusy, not IsBusy, so it can never collide with ViewModelBase's own
    /// pre-existing, unrelated virtual IsBusy. Back it with an observable property — re-read on PropertyChanged.
    /// </summary>
    internal interface IActivityIndicatorProvider
    {
        /// <summary>Gets whether the custom tab bar should show this item's activity indicator.</summary>
        bool IsTabBusy { get; }
    }

    /// <summary>Supplies the retained items displayed by a flyout menu.</summary>
    internal interface IFlyoutMenuItems
    {
        /// <summary>Gets or sets the flyout menu's retained item collection.</summary>
        ObservableCollection<FlyoutMenuItem> MenuItems { get; set; }
    }

    /// <summary>Notifies the host when its flyout menu should close.</summary>
    internal interface IFlyoutMenuClosedEvent
    {
        /// <summary>Raised when the flyout menu requests closure.</summary>
        event EventHandler<EventArgs?>? ClosedFlyout;
    }

    /// <summary>Selects retained flyout pages or opens registered popup menu items.</summary>
    internal interface IFlyoutMenuSelectItem
    {
        /// <summary>Selects the first matching flyout item by metadata ID with optional parameters.</summary>
        Task SelectMenuItemById(string id, Dictionary<string, object>? parameters = null);

        /// <summary>Selects the supplied retained page or opens its popup action; guards may retain the current destination.</summary>
        Task SelectMenuItem(FlyoutMenuItem selectedItem, Dictionary<string, object>? parameters = null);
    }

    /// <summary>Observable flyout metadata and retained content with deferred metadata refresh functions.</summary>
    internal class FlyoutMenuItem : ObservableObject
    {
        internal Func<Task<View>>? RecreatePopupAsync { get; set; }
        private Func<string> _titleUpdater;
        private Func<string> _selectedIconUpdater;
        private Func<string> _unselectedIconUpdater;

        private string _id = string.Empty;
        /// <summary>Gets or sets the metadata identifier for this item or action.</summary>
        public string Id
        {
            get { return _id; }
            set { SetProperty(ref _id, value); }
        }

        private string _title = string.Empty;
        /// <summary>Gets or sets the text displayed for this item.</summary>
        public string Title
        {
            get { return _title; }
            set { SetProperty(ref _title, value); }
        }

        private string _selectedIcon = string.Empty;
        /// <summary>Gets or sets the icon displayed for the selected item.</summary>
        public string SelectedIcon
        {
            get { return _selectedIcon; }
            set { SetProperty(ref _selectedIcon, value); }
        }

        private string _unselectedIcon = string.Empty;
        /// <summary>Gets or sets the icon displayed for an inactive item.</summary>
        public string UnselectedIcon
        {
            get { return _unselectedIcon; }
            set { SetProperty(ref _unselectedIcon, value); }
        }

        private bool _isSelected = false;
        /// <summary>Gets or sets whether this item is selected.</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set { SetProperty(ref _isSelected, value); }
        }

        private VisualElement? _content;
        /// <summary>Gets or sets the retained visual content for this item.</summary>
        public VisualElement? Content
        {
            get { return _content; }
            set { SetProperty(ref _content, value); }
        }

        private Func<Page, bool>? _updateDetail;
        /// <summary>Gets or sets the callback that installs a page as the flyout detail and reports whether it succeeded.</summary>
        public Func<Page, bool>? UpdateDetail
        {
            get { return _updateDetail; }
            set { SetProperty(ref _updateDetail, value); }
        }

        /// <summary>Creates retained flyout content with functions that refresh its title, icons and detail page.</summary>
        public FlyoutMenuItem(string id, Func<string> titleUpdater, Func<string> selectedIconUpdater,
            Func<string> unselectedIconUpdater, VisualElement? content, Func<Page, bool> updateDetail)
        {
            _titleUpdater = titleUpdater;
            _selectedIconUpdater = selectedIconUpdater;
            _unselectedIconUpdater = unselectedIconUpdater;

            Id = id;
            Content = content;
            UpdateDetail = updateDetail;

            Title = _titleUpdater();
            SelectedIcon = _selectedIconUpdater();
            UnselectedIcon = _unselectedIconUpdater();
        }
    }
}
