namespace MVVMCompass.Interfaces;

/// <summary>Optional asynchronous resolution, including cleanup when construction fails.</summary>
internal interface IAsyncViewLocator : IViewLocator
{
    /// <summary>Creates a destination. Its lifetime owns any resources acquired by the resolver.</summary>
    Task<VisualElement> CreateAndBindVEForAsync(Type viewModelType, CancellationToken cancellationToken = default);
}
