using MVVMCompass.Core;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    /// <summary>Replaces the root using an ordinary model without requesting initialization. Lifecycle interfaces are optional.</summary>
    public Task<NavigationOutcome<MauiNavigationRoot<TModel>>> ReplaceRootAsync<TModel>(
        NavigationRequestOptions request, Func<TModel> createViewModel, Func<TModel, Page> createPage,
        bool navigable = false, Func<TModel, Task>? cleanup = null, CancellationToken cancellationToken = default)
        where TModel : class =>
        RunReplacementAsync(WithoutParameters(request), PlainFactory(createViewModel, createPage, cleanup),
            (_, _, _) => Task.CompletedTask, navigable, cancellationToken);

    /// <summary>Pushes an ordinary model without requesting initialization. Explicit cleanup owns only declared resources.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TModel>>> PushAsync<TModel>(
        NavigationRequestOptions request, Func<TModel> createViewModel, Func<TModel, Page> createPage,
        bool animated = true, Func<TModel, Task>? cleanup = null, CancellationToken cancellationToken = default)
        where TModel : class =>
        RunPresentationAsync(WithoutParameters(request), PlainFactory(createViewModel, createPage, cleanup),
            (_, _, _) => Task.CompletedTask, false, false, animated, cancellationToken);

    /// <summary>Opens an ordinary modal without requesting initialization, optionally with an independent stack.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TModel>>> OpenModalAsync<TModel>(
        NavigationRequestOptions request, Func<TModel> createViewModel, Func<TModel, Page> createPage,
        bool navigable = false, bool animated = true, Func<TModel, Task>? cleanup = null,
        CancellationToken cancellationToken = default) where TModel : class =>
        RunPresentationAsync(WithoutParameters(request), PlainFactory(createViewModel, createPage, cleanup),
            (_, _, _) => Task.CompletedTask, true, navigable, animated, cancellationToken);

    /// <summary>Replaces the root without initialization using factories sharing an entry-owned DI scope.</summary>
    public Task<NavigationOutcome<MauiNavigationRoot<TModel>>> ReplaceScopedRootAsync<TModel>(
        NavigationRequestOptions request, Func<IServiceProvider, TModel> createViewModel,
        Func<IServiceProvider, TModel, Page> createPage, bool navigable = false,
        CancellationToken cancellationToken = default) where TModel : class =>
        RunReplacementAsync(WithoutParameters(request), ScopedFactory(createViewModel, createPage),
            (_, _, _) => Task.CompletedTask, navigable, cancellationToken);

    /// <summary>Pushes an ordinary model without initialization using factories sharing an entry-owned DI scope.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TModel>>> PushScopedAsync<TModel>(
        NavigationRequestOptions request, Func<IServiceProvider, TModel> createViewModel,
        Func<IServiceProvider, TModel, Page> createPage, bool animated = true,
        CancellationToken cancellationToken = default) where TModel : class =>
        RunPresentationAsync(WithoutParameters(request), ScopedFactory(createViewModel, createPage),
            (_, _, _) => Task.CompletedTask, false, false, animated, cancellationToken);

    /// <summary>Opens an ordinary modal without initialization using factories sharing an entry-owned DI scope.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TModel>>> OpenScopedModalAsync<TModel>(
        NavigationRequestOptions request, Func<IServiceProvider, TModel> createViewModel,
        Func<IServiceProvider, TModel, Page> createPage, bool navigable = false, bool animated = true,
        CancellationToken cancellationToken = default) where TModel : class =>
        RunPresentationAsync(WithoutParameters(request), ScopedFactory(createViewModel, createPage),
            (_, _, _) => Task.CompletedTask, true, navigable, animated, cancellationToken);

    private static NavigationRequest<object?> WithoutParameters(NavigationRequestOptions request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(null)
        {
            Origin = request.Origin,
            Priority = request.Priority,
            CoalescingKey = request.CoalescingKey,
            RejectIfBusy = request.RejectIfBusy
        };
    }
}
