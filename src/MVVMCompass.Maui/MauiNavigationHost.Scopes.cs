using MVVMCompass.Core;
using MVVMCompass.Services;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    /// <summary>Gets the current ownership forest, including nonvisual children. Read on the window dispatcher.</summary>
    public IReadOnlyList<NavigationOwnershipNode> OwnershipRoots
    {
        get
        {
            var nodes = AllEntries().Select(entry => entry.Ownership)
                .Concat(Window.Page == null ? [] : PopupOwnership.ModelsFor(Window.Page).Select(model => model.Ownership))
                .Where(node => !node.Lifetime.IsDismissed).Distinct().ToHashSet();
            return nodes.Where(node => !HasSelectedAncestor(node)).ToArray();
            bool HasSelectedAncestor(NavigationOwnershipNode node)
            {
                for (var parent = node.Parent; parent != null; parent = parent.Parent)
                    if (nodes.Contains(parent)) return true;
                return false;
            }
        }
    }

    /// <summary>Replaces the root using a scope shared by both factories and disposed after terminal model cleanup.</summary>
    public Task<NavigationOutcome<MauiNavigationRoot<TModel>>> ReplaceScopedRootAsync<TModel, TParameter>(
        NavigationRequest<TParameter> request, Func<IServiceProvider, TModel> createViewModel,
        Func<IServiceProvider, TModel, Page> createPage, bool navigable = false, CancellationToken cancellationToken = default)
        where TModel : class, INavigationInitializable<TParameter> =>
        RunReplacementAsync(request, ScopedFactory(createViewModel, createPage),
            (entry, parameter, token) => entry.InitializeAsync(parameter, token), navigable, cancellationToken);

    /// <summary>Pushes an ordinary typed model using a scope owned by the new entry.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TModel>>> PushScopedAsync<TModel, TParameter>(
        NavigationRequest<TParameter> request, Func<IServiceProvider, TModel> createViewModel,
        Func<IServiceProvider, TModel, Page> createPage, bool animated = true, CancellationToken cancellationToken = default)
        where TModel : class, INavigationInitializable<TParameter> =>
        RunPresentationAsync(request, ScopedFactory(createViewModel, createPage),
            (entry, parameter, token) => entry.InitializeAsync(parameter, token), false, false, animated, cancellationToken);

    /// <summary>Opens an ordinary typed modal using a scope owned by the new entry.</summary>
    public Task<NavigationOutcome<MauiNavigationPage<TModel>>> OpenScopedModalAsync<TModel, TParameter>(
        NavigationRequest<TParameter> request, Func<IServiceProvider, TModel> createViewModel,
        Func<IServiceProvider, TModel, Page> createPage, bool navigable = false, bool animated = true,
        CancellationToken cancellationToken = default) where TModel : class, INavigationInitializable<TParameter> =>
        RunPresentationAsync(request, ScopedFactory(createViewModel, createPage),
            (entry, parameter, token) => entry.InitializeAsync(parameter, token), true, navigable, animated, cancellationToken);

    private Func<RootPreparation, Action<NavigationEntry<TModel>>, Task<Page>> ScopedFactory<TModel>(
        Func<IServiceProvider, TModel> createViewModel, Func<IServiceProvider, TModel, Page> createPage) where TModel : class
    {
        ArgumentNullException.ThrowIfNull(createViewModel);
        ArgumentNullException.ThrowIfNull(createPage);
        var factory = scopeFactory ?? throw new InvalidOperationException("Resolve the host factory from DI or supply an IServiceScopeFactory.");
        return async (preparation, own) =>
        {
            var scope = new NavigationEntryScope(factory);
            NavigationEntry<TModel>? entry = null;
            try
            {
                TModel model;
                using (scope.Construct()) model = createViewModel(scope.Services)
                    ?? throw new InvalidOperationException("The view model factory returned null.");
                if (model is ViewModelBase)
                    throw new InvalidOperationException("Use registered overloads for ViewModelBase and its existing lifecycle.");
                entry = Claim(model, () => coordinator.CreateEntry(model));
                own(entry);
                scope.Own(entry.Lifetime);
                Page page;
                using (scope.Construct()) page = createPage(scope.Services, model)
                    ?? throw new InvalidOperationException("The page factory returned null.");
                if (page.Parent != null || ReferenceEquals(page, Window.Page))
                    throw new InvalidOperationException("Navigation factories must return detached pages.");
                if (page.BindingContext != null && !ReferenceEquals(page.BindingContext, model))
                    throw new InvalidOperationException("The page must bind to the supplied view model.");
                preparation.Track(page);
                page.BindingContext = model;
                ViewModelTree.AdoptChildren(page, entry.Lifetime);
                scope.Bind(page, entry.Lifetime);
                return page;
            }
            catch
            {
                // After Claim, the operation owns terminal model cleanup and the scope.
                if (entry == null) await scope.DisposeAsync();
                throw;
            }
        };
    }
}
