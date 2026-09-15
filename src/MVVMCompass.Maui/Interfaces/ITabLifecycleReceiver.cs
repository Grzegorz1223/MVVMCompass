namespace MVVMCompass.Interfaces
{
    /// <summary>
    /// Implemented by child tab views to receive tab-switch lifecycle notifications
    /// that would normally be delivered by the MAUI navigation stack.
    /// </summary>
    internal interface ITabLifecycleReceiver
    {
        /// <summary>
        /// Called when this tab becomes the active tab.
        /// Implementations should call <c>base.OnNavigatedTo</c> to trigger Page-level logic.
        /// </summary>
        void OnTabNavigatedTo();
    }
}
