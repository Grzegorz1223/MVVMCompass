using MVVMCompass.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace MVVMCompass.Services
{
    /// <summary>Resolves registered views synchronously from the root provider or asynchronously using the configured entry scope policy.</summary>
    internal class ViewLocator : IAsyncViewLocator
    {
        private IDictionary<Type, Type> _viewsContainer;
        private readonly IServiceProvider _services;
        private readonly NavigationOptions options;

        /// <summary>Creates a locator using the supplied provider and optional entry-scope policy.</summary>
        public ViewLocator(IServiceProvider services) : this(services, new NavigationOptions()) { }

        /// <summary>Creates a locator using the supplied provider and optional entry-scope policy.</summary>
        public ViewLocator(IServiceProvider services, NavigationOptions options)
        {
            _viewsContainer = new Dictionary<Type, Type>();
            _services = services;
            this.options = options;
        }

        /// <summary>Copies the supplied view/model registrations for subsequent resolution.</summary>
        public void Initialize(Dictionary<Type, Type> registerPairs)
        {
            _viewsContainer = new Dictionary<Type, Type>(registerPairs);
        }

        /// <summary>Creates the registered visual element synchronously through the root service provider; navigation does not own that provider.</summary>
        public VisualElement CreateAndBindVEFor<TViewModel>() where TViewModel : ViewModelBase
        {
            var viewType = FindVEForViewModel(typeof(TViewModel));

            var viewBase = (VisualElement)_services.GetRequiredService(viewType);

            return viewBase;
        }

        /// <summary>Creates the registered visual element synchronously through the root service provider; navigation does not own that provider.</summary>
        public VisualElement CreateAndBindVEFor(Type type)
        {
            var viewType = FindVEForViewModel(type);

            var viewBase = (VisualElement)_services.GetRequiredService(viewType);

            return viewBase;
        }

        /// <summary>Resolves a destination with the configured per-entry scope policy.</summary>
        public async Task<VisualElement> CreateAndBindVEForAsync(Type viewModelType, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!options.UseEntryScopes) return CreateAndBindVEFor(viewModelType);
            var scope = new NavigationEntryScope(_services.GetRequiredService<IServiceScopeFactory>());
            try
            {
                VisualElement view;
                using (scope.Construct(viewModelType))
                    view = (VisualElement)scope.Services.GetRequiredService(FindVEForViewModel(viewModelType));
                var model = (view as IHasVM)?.ViewModel ?? view.BindingContext as ViewModelBase
                    ?? throw new InvalidOperationException("A scoped registered view requires a legacy view model.");
                var found = ViewModelTree.Collect(view);
                if (view.Parent != null || model.Ownership.Parent != null || found.Any(item => item.IsDismissed
                    || item.PendingRootPreparation != null
                    || item.Ownership.Parent is { } parent && !found.Any(owner => owner.Ownership == parent)
                    || NavigationEntryScope.IsOwned(item)
                    || MauiNavigationHost.IsClaimedModel(item)) || Application.Current?.Windows.Any(window =>
                        ViewModelTree.Collect(window.Page, true).Intersect(found).Any()) == true)
                    throw new InvalidOperationException("Scoped destinations require fresh, unowned views and models.");
                cancellationToken.ThrowIfCancellationRequested();
                scope.Bind(view, model.Lifetime);
                return view;
            }
            catch
            {
                try { await scope.AbandonAsync(); }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Scope preparation cleanup"); }
                throw;
            }
        }

        /// <summary>Returns the registered visual element type for the supplied model type, or throws when unregistered.</summary>
        public Type FindVEForViewModel(Type viewModelType)
        {
            var view = _viewsContainer[viewModelType];

            return view;
        }

        /// <summary>Returns the registered model type for the supplied visual element type, or throws when unregistered.</summary>
        public Type FindViewModelForVE(Type page)
        {
            var vm = _viewsContainer.FirstOrDefault(x => x.Value == page).Key;

            return vm;
        }
    }
}
