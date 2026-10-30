using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Services;

internal sealed partial class NavigationService(RegisteredScreenFactory registry, MauiNavigationHostFactory hosts) : INavigationService, IFlyoutNavigationService, IDeferredPopupNavigationService
{
    private NavigationContext? context;
    private NavigationScreenEntry? screen;
    internal void Bind(NavigationContext owner, NavigationScreenEntry origin) { context = owner; screen = origin; }
    private bool Valid => context != null && screen != null && context.RootContext.ContainsActiveOrigin(screen.Entry);
    private NavigationRequestOptions Options => new() { Origin = screen?.Entry };
    private NavigationContext DestinationContext => screen?.Children ?? context!;
    private NavigationContext PushContext => DestinationContext.Deepest.DetailContext();
    private static NavigationResult Invalid => new(NavigationStatus.InvalidOrigin);

    private Task<NavigationResult> OnUI(Func<Task<NavigationResult>> operation) => context == null ? Task.FromResult(Invalid) :
        context.Host.IsContentCallback ? Task.FromResult(new NavigationResult(NavigationStatus.Reentrant)) :
        context.Window.Dispatcher.DispatchAsync(async () =>
        {
            try { return await operation(); }
            catch (Exception error) { return new NavigationResult(NavigationStatus.Failed, error: error); }
        });

    public Task<NavigationResult> NavigateTo<TViewModel>(Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase => OnUI(async () => !Valid ? Invalid :
        NavigationResult.From(await PushContext.PushAsync(registry.For(typeof(TViewModel)), parameters, Options, cancellationToken)));

    public Task<NavigationResult> Select(string id, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default) => OnUI(async () =>
    {
        if (!Valid) return Invalid;
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = id.Split('/');
        var owner = DestinationContext.FindContainer(path[0]);
        if (owner == null) return new(NavigationStatus.DestinationNotFound);
        return NavigationResult.From(await owner.SelectPathAsync(path, parameters, Options, cancellationToken));
    });

    public Task<NavigationResult> Select<TViewModel>(Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default) where TViewModel : ViewModelBase => OnUI(() =>
    {
        if (!Valid) return Task.FromResult(Invalid);
        for (var owner = DestinationContext; owner != null; owner = owner.ParentContext)
        {
            var matches = owner.Matching(typeof(TViewModel));
            if (matches.Count == 1) return Select(matches[0].Id, parameters, cancellationToken);
            if (matches.Count > 1) return Task.FromResult(new NavigationResult(NavigationStatus.AmbiguousDestination));
        }
        return Task.FromResult(new NavigationResult(NavigationStatus.DestinationNotFound));
    });

    public Task<NavigationResult> NavigateBack(CancellationToken cancellationToken = default) => OnUI(async () => !Valid ? Invalid :
        NavigationResult.From(await context!.BackContext().BackAsync(Options, cancellationToken)));

    public Task<NavigationResult> NavigateBackToRoot(CancellationToken cancellationToken = default) => OnUI(async () => !Valid ? Invalid :
        NavigationResult.From(await PushContext.PopToRootAsync(Options, cancellationToken)));

    public Task<NavigationResult> CloseFlyout(CancellationToken cancellationToken = default) => OnUI(async () =>
    {
        if (!Valid) return Invalid;
        for (var owner = DestinationContext; owner != null; owner = owner.ParentContext)
            if (owner.HasFlyout) return NavigationResult.From(await owner.CloseFlyoutAsync(Options, cancellationToken));
        return new(NavigationStatus.DestinationNotFound);
    });

    public Task<NavigationResult> SetRoot<TViewModel>(Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default) where TViewModel : ViewModelBase =>
        OnUI(async () => !Valid ? Invalid : NavigationResult.From(await context!.Host.ReplaceContentRootAsync(new(registry.Definition(typeof(TViewModel), parameters))
        { Origin = screen!.Entry }, cancellationToken: cancellationToken)));

    public Task<NavigationResult> Reset(string id, CancellationToken cancellationToken = default) => OnUI(async () =>
    {
        if (!Valid) return Invalid;
        var owner = DestinationContext.FindContainer(id);
        return owner == null ? new(NavigationStatus.DestinationNotFound) : NavigationResult.From(await owner.ResetDestinationAsync(id, Options, cancellationToken));
    });

    public Task<NavigationResult> Remove(string id, string? replacementId = null, CancellationToken cancellationToken = default) => OnUI(async () =>
    {
        if (!Valid) return Invalid;
        var owner = DestinationContext.FindContainer(id);
        return owner == null ? new(NavigationStatus.DestinationNotFound) : NavigationResult.From(await owner.RemoveDestinationAsync(id, replacementId, Options, cancellationToken));
    });

    internal Task<NavigationResult> SetItems(IReadOnlyList<NavigationItem> items, NavigationPresentation kind, CancellationToken token) => OnUI(() =>
        !Valid || screen?.Children == null ? Task.FromResult(Invalid) : screen.Children.ReconcileAsync(registry.Definition(items, kind, screen.ViewModel.CompositionOptions), Options, token));

    internal Task<WindowNavigationResult> OpenWindow<TViewModel>(Action<Window> configure, Dictionary<string, object>? parameters,
        WindowBootstrapOptions? options, CancellationToken token) where TViewModel : ViewModelBase => context == null
        ? Task.FromResult(new WindowNavigationResult(Invalid, null, Task.CompletedTask))
        : context.Window.Dispatcher.DispatchAsync(async () =>
        {
            if (!Valid) return new WindowNavigationResult(Invalid, null, Task.CompletedTask);
            if (context.Host.IsContentCallback) return new WindowNavigationResult(new(NavigationStatus.Reentrant), null, Task.CompletedTask);
            try { return await hosts.OpenWindow<TViewModel>(configure, parameters, options, token); }
            catch (Exception error) { return new WindowNavigationResult(new(NavigationStatus.Failed, error: error), null, Task.CompletedTask); }
        });

    public async Task<NavigationResult> OpenNewWindow<TViewModel>(Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default) where TViewModel : ViewModelBase
    {
        return (await OpenWindow<TViewModel>(_ => { }, parameters, null, cancellationToken)).Navigation;
    }
}
