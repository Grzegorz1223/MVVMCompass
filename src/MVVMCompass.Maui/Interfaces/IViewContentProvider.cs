namespace MVVMCompass.Interfaces
{
    /// <summary>Exposes content that a custom tab host can extract from its child view.</summary>
    internal interface IViewContentProvider
    {
        /// <summary>Gets the content a custom tab host can display while retaining the original view for lifecycle forwarding.</summary>
        IView? ViewContent { get; set; }
    }
}
