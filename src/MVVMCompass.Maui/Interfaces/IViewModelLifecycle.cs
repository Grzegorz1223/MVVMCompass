namespace MVVMCompass.Interfaces
{
    /// <summary>Initialization, visibility, and retained or terminal cleanup callbacks.</summary>
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
        /// Performs permanent cleanup when the lifetime ends. The TabSwitchCompatibility lifecycle also
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
        /// Called when the view model is covered or leaves the active branch. The default
        /// Deactivated implementation forwards to this callback; a Deactivated override
        /// controls whether that forwarding occurs.
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
