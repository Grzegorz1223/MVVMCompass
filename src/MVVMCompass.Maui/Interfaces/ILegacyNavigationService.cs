namespace MVVMCompass.Interfaces
{
    /// <summary>Preserved view-model-first navigation and presentation helpers. Use a window host for explicit coordinated requests.</summary>
    internal interface ILegacyNavigationService
    {
        /// <summary>
        /// Initialize the service
        /// </summary>
        void Initialize(Dictionary<Type, Type> registerPairs);

        /// <summary>
        /// Gets the main view
        /// </summary>
        Page? GetPresentationRoot();

        /// <summary>
        /// Gets a View from a ViewModel
        /// </summary>
        VisualElement GetViewFromVM<T>() where T : ViewModelBase;

        /// <summary>
        /// Creates a page from the viewmodel to be the main page of the application
        /// </summary>
        Task<Page> CreateMainPage<T>() where T : ViewModelBase;

        /// <summary>
        /// Creates a page from the viewmodel to be the main page of the application, with parameters
        /// </summary>
        Task<Page> CreateMainPage<T>(Dictionary<string, object>? parameters) where T : ViewModelBase;

        /// <summary>
        /// Creates a page the viewmodel as the main page of the application, and wraps its page within a Navigation page
        /// </summary>
        Task<NavigationPage> CreateNavigableMainPage<T>() where T : ViewModelBase;

        /// <summary>
        /// Creates a page the viewmodel as the main page of the application, and wraps its page within a Navigation page, with parameters
        /// </summary>
        Task<NavigationPage> CreateNavigableMainPage<T>(Dictionary<string, object>? parameters) where T : ViewModelBase;

        /// <summary>
        /// Sets the viewmodel to be the main page of the application
        /// </summary>
        Task PresentAsMainPage<T>() where T : ViewModelBase;

        /// <summary>
        /// Sets the viewmodel to be the main page of the application, with parameters
        /// </summary>
        Task PresentAsMainPage<T>(Dictionary<string, object>? parameters) where T : ViewModelBase;

        /// <summary>
        /// Sets the viewmodel as the main page of the application, and wraps its page within a Navigation page
        /// </summary>
        Task PresentAsNavigableMainPage<T>() where T : ViewModelBase;

        /// <summary>
        /// Sets the viewmodel as the main page of the application, and wraps its page within a Navigation page, with parameters
        /// </summary>
        Task PresentAsNavigableMainPage<T>(Dictionary<string, object>? parameters) where T : ViewModelBase;

        /// <summary>
        /// Navigate to the given page on top of the current navigation stack
        /// </summary>
        Task NavigateTo<T>() where T : ViewModelBase;

        /// <summary>
        /// Navigate to the given page on top of the current navigation stack, with parameters
        /// </summary>
        Task NavigateTo<T>(Dictionary<string, object>? parameters) where T : ViewModelBase;

        /// <summary>Presents a registered popup over the supplied legacy origin and awaits its owned completion.</summary>
        Task DisplayPopup<T>(ViewModelBase currentVM) where T : ViewModelBase;

        /// <summary>Presents a registered popup over the supplied legacy origin and awaits its owned completion.</summary>
        Task DisplayPopup<T>(ViewModelBase currentVM, Dictionary<string, object>? parameters) where T : ViewModelBase;

        /// <summary>Presents a registered popup and returns its result after cleanup; dismissal without a result returns the default value.</summary>
        Task<TResult?> DisplayPopupWithResult<T, TResult>(ViewModelBase currentVM) where T : ViewModelBase;

        /// <summary>Presents a registered popup and returns its result after cleanup; dismissal without a result returns the default value.</summary>
        Task<TResult?> DisplayPopupWithResult<T, TResult>(ViewModelBase currentVM, Dictionary<string, object>? parameters) where T : ViewModelBase;

        /// <summary>
        /// Closes the top popup on the service's presentation window and awaits its owned cleanup.
        /// </summary>
        Task ClosePopup();

        /// <summary>Selects the first flyout item with the supplied metadata ID and applies any new parameters.</summary>
        Task NavigateToFlyoutItem(string id);

        /// <summary>
        /// Navigate to the given page of the flyout menu with parameters
        /// </summary>
        Task NavigateToFlyoutItem(string id, Dictionary<string, object>? parameters);

        /// <summary>
        /// Open new window with given page
        /// </summary>
        Task OpenNewWindow<T>(Action? windowClosed = null, bool isResizable = false) where T : ViewModelBase;

        /// <summary>
        /// Open new window with given page, with parameters
        /// </summary>
        Task OpenNewWindow<T>(Dictionary<string, object>? parameters, Action? windowClosed = null, bool isResizable = false) where T : ViewModelBase;

        /// <summary>
        /// Optional hook invoked right after OpenNewWindow&lt;T&gt; opens and activates a new secondary
        /// window. Generic on purpose — this project has no concept of dock mode; the host app wires this
        /// once at startup to add whatever platform-specific behavior it wants (see MauiProgram.cs).
        /// </summary>
        Action<Window>? NewWindowOpened { get; set; }

        /// <summary>
        /// Navigate to the previous item in the navigation stack
        /// </summary>
        Task NavigateBack(ViewModelBase currentVM);

        /// <summary>
        /// Navigate back to the element at the root of the navigation stack
        /// </summary>
        Task NavigateBackToRoot();

        /// <summary>
        /// Returns the Type of the currently visible ViewModel.
        /// For tabbed pages, returns the ViewModel type of the active tab.
        /// For normal pages, returns the topmost page's ViewModel type.
        /// Returns null if no page is currently displayed.
        /// </summary>
        Type? GetActiveViewModelType();
    }
}
