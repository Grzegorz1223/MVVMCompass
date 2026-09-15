namespace MVVMCompass.Interfaces
{
    /// <summary>
    /// Non-generic interface for LegacyNavigationService to interact with CustomTabbedViewBase
    /// without knowing T.
    /// </summary>
    internal interface ICustomTabbedViewBase
    {
        /// <summary>Adds an already prepared child to the custom tab host in declaration order.</summary>
        void AddChildInternal(ChildTabInfo child);
        /// <summary>Gets the number of retained children, including hidden tab-bar items.</summary>
        int ChildCount { get; }
        /// <summary>Selects a retained child by index or the first matching model type, awaiting guards and lifecycle callbacks.</summary>
        Task<bool> SwitchToAsync(int index);
        /// <summary>Selects a retained child by index or the first matching model type, awaiting guards and lifecycle callbacks.</summary>
        Task<bool> SwitchToAsync(Type viewModelType);

        /// <summary>
        /// Controls whether tab switching is allowed.
        /// When false, a visual overlay covers the tab bar and navigation is blocked.
        /// </summary>
        bool IsTabBarEnabled { get; set; }

        /// <summary>
        /// The currently active tab, or null if no tab has been selected yet.
        /// </summary>
        ChildTabInfo? CurrentTab { get; }

        /// <summary>
        /// Every child tab, in the order it was added — including tabs that have never been shown.
        /// Needed so tearing down the host can also dismiss the children it owns; their views are
        /// extracted into the host's content area, so they are not pages on the navigation stack and
        /// nothing else can find them.
        /// </summary>
        IReadOnlyList<ChildTabInfo> Children { get; }
    }
}
