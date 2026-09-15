namespace MVVMCompass.Interfaces
{
    /// <summary>Legacy initialization, visibility, navigation guard and retained or terminal cleanup callbacks.</summary>
    public interface IViewModelLifecycle
    {
        /// <summary>
        /// Called when new set of parameters is passed
        /// </summary>
        Task GetParameters(Dictionary<string, object> parameters);

        /// <summary>
        /// Called exactly once, before the viewModel enters the navigation stack
        /// </summary>
        Task BeforeFirstShown();

        /// <summary>
        /// Performs permanent cleanup when the lifetime ends. The legacy retained profile also
        /// invokes this callback for temporary tab or flyout deactivation; inspect IsDismissed
        /// before releasing resources needed for reactivation.
        /// </summary>
        Task AfterDismissed();

        /// <summary>Ends the lifetime and awaits permanent cleanup once.</summary>
        Task DismissAsync();

        /// <summary>Notifies a view that is inactive but retained.</summary>
        Task Deactivated();

        /// <summary>
        /// Called when the viewModel is appearing
        /// </summary>
        Task Appearing();

        /// <summary>
        /// The automatic counterpart to <see cref="Appearing"/> — called when the viewModel's page is
        /// about to be covered, by a page pushed on top of it or by itself being popped/replaced.
        /// Forwarded by LegacyNavigationService.AddEvents from the real MAUI Page.Disappearing event, so it
        /// fires for every navigation away regardless of cause — no caller has to remember to signal it.
        /// </summary>
        Task Disappearing();

        /// <summary>
        /// Called when the viewModel is fully navigated to.
        /// </summary>
        Task NavigatedTo();

        /// <summary>
        /// Called when the viewModel is fully loaded.
        /// </summary>
        Task Loaded();

        // You may also wish to implement any of the following...
        //Task BeforeAppearing(); // Called before a viewmodel appears, when navigating either forwards or backwards
        //Task AfterAppearing(); // Called after a viewmodel appears, when navigating either forwards or backwards
    }
}
