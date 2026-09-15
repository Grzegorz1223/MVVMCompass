using MVVMCompass.Interfaces;

namespace MVVMCompass.Services;

internal static class NavigationViewFactory
{
    internal static Task<VisualElement> CreateAsync<T>(IViewLocator locator, CancellationToken cancellationToken = default) where T : ViewModelBase
    {
        cancellationToken.ThrowIfCancellationRequested();
        return locator is IAsyncViewLocator asynchronous ? asynchronous.CreateAndBindVEForAsync(typeof(T), cancellationToken)
            : Task.FromResult(locator.CreateAndBindVEFor<T>());
    }

    internal static Task<VisualElement> CreateAsync(IViewLocator locator, Type type, ViewModelBase? parent = null) =>
        CreateWithCancellationAsync(locator, type, parent, CancellationToken.None);

    internal static async Task<VisualElement> CreateWithCancellationAsync(IViewLocator locator, Type type, ViewModelBase? parent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Async-local context follows a custom locator across awaits without leaking to other requests.
        using var pending = parent == null ? null : ViewModelBase.SetPendingParent(parent);
        return locator is IAsyncViewLocator asynchronous ? await asynchronous.CreateAndBindVEForAsync(type, cancellationToken)
            : locator.CreateAndBindVEFor(type);
    }
}
