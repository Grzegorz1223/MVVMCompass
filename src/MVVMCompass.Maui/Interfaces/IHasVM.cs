namespace MVVMCompass.Interfaces
{
    /// <summary>Exposes the legacy view model bound to a visual element.</summary>
    public interface IHasVM
    {
        /// <summary>Gets the legacy model associated with this view or child.</summary>
        ViewModelBase ViewModel { get; }
    }
}
