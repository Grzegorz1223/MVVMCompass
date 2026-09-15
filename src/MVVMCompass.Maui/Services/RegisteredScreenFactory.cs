using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Services;

internal sealed record RegisteredDestination(Func<IServiceProvider, ViewModelBase> Model, Func<IServiceProvider, VisualElement> View);

internal sealed class RegisteredScreenFactory(NavigationRegistrationBuilder registration, IServiceScopeFactory scopes)
{
    private readonly IServiceScopeFactory scopeFactory = scopes;
    internal ScreenFactory For(Type model, IReadOnlyDictionary<string, object>? parameters = null) =>
        new RegisteredFactory(this, registration.Destination(model), model, parameters);

    internal NavigationDefinition Definition(Type model, Dictionary<string, object>? parameters = null) =>
        new([new(model.FullName!, model.Name, For(model, parameters))]);

    internal NavigationDefinition Definition(IEnumerable<NavigationItem> items, NavigationPresentation kind)
    {
        var entries = items.ToArray();
        return new(entries.Select(item => new NavigationDestination(item.Id, item.Title, For(item.ViewModelType, item.Parameters), item.UnselectedIcon)
        { Item = item }), kind, entries.FirstOrDefault(item => item.IsInitiallySelected)?.Id ?? entries.First(item => item.IsEnabled).Id);
    }

    internal async Task<View> PreparePopupAsync<TViewModel, TResult>(NavigationContext owner, Dictionary<string, object>? parameters,
        CancellationToken token) where TViewModel : ViewModelBase
    {
        var destination = registration.Destination(typeof(TViewModel));
        var scope = new NavigationEntryScope(scopeFactory);
        ViewModelBase? model = null;
        var bound = false;
        try
        {
            using (scope.Construct(typeof(TViewModel))) model = destination.Model(scope.Services);
            if (model.IsDismissed || NavigationEntryScope.IsOwned(model) || MauiNavigationHost.IsClaimedModel(model))
                throw new InvalidOperationException("Popup models must be fresh and unowned.");
            scope.Own(model.Lifetime); bound = true;
            var view = destination.View(scope.Services) as CommunityToolkit.Maui.Views.Popup<TResult>
                ?? throw new InvalidOperationException("Register a PopupViewBase with the requested result type.");
            if (view.Parent != null || !ReferenceEquals((view as IHasVM)?.ViewModel, model))
                throw new InvalidOperationException("A popup must be detached and bound to its scoped model.");
            scope.Bind(view, model.Lifetime);
            var service = (NavigationService)scope.Services.GetRequiredService<INavigationService>();
            model.NavigationBinding = service; service.BindPopup(owner, view, model);
            if (parameters != null) await model.GetParameters(parameters);
            token.ThrowIfCancellationRequested();
            await model.BeforeFirstShown(); token.ThrowIfCancellationRequested();
            return view;
        }
        catch
        {
            if (bound) await model!.Lifetime.DismissAsync(DismissalReason.PreparationFailed);
            else await scope.AbandonAsync();
            throw;
        }
    }

    private sealed class RegisteredFactory(RegisteredScreenFactory registry, RegisteredDestination destination, Type modelType,
        IReadOnlyDictionary<string, object>? initialParameters) : ScreenFactory
    {
        internal override async Task<NavigationScreenEntry> PrepareAsync(NavigationContext owner, Action<NavigationEntry<ViewModelBase>> own,
            Dictionary<string, object>? parameters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scope = new NavigationEntryScope(registry.scopeFactory);
            var bound = false;
            try
            {
                ViewModelBase model;
                using (scope.Construct(modelType))
                using (owner.ContainerScreen == null ? null : ViewModelBase.SetPendingParent(owner.ContainerScreen.ViewModel))
                    model = destination.Model(scope.Services);
                var entry = owner.ClaimScreen(model);
                own(entry);
                scope.Own(model.Lifetime); bound = true;
                var service = scope.Services.GetRequiredService<INavigationService>() as NavigationService
                    ?? throw new InvalidOperationException("The registered navigation service must support entry binding.");
                model.NavigationBinding = service;
                var view = destination.View(scope.Services) as ViewBase
                    ?? throw new InvalidOperationException("Register a ViewBase, TabbedViewBase, or FlyoutViewBase for a screen.");
                if (!ReferenceEquals(view.ViewModel, model) || view.Parent != null)
                    throw new InvalidOperationException("A registered view must be detached and bound to its scoped model.");
                scope.Bind(view, model.Lifetime);
                var screen = new NavigationScreenEntry(view, entry);
                service.Bind(owner, screen);
                view.Navigator = new(owner, screen); model.Navigator = view.Navigator;
                var data = new Dictionary<string, object>(initialParameters ?? new Dictionary<string, object>());
                if (parameters != null) foreach (var item in parameters) data[item.Key] = item.Value;
                model.IsPreparingComposition = true;
                try
                {
                    if (data.Count != 0) await model.GetParameters(data);
                    cancellationToken.ThrowIfCancellationRequested();
                    await model.BeforeFirstShown();
                }
                finally { model.IsPreparingComposition = false; }
                cancellationToken.ThrowIfCancellationRequested();
                if (view is IContainerView container)
                {
                    if (model.CompositionKind != container.Kind)
                        throw new InvalidOperationException("The container model must call SetTabs or SetFlyoutItems during BeforeFirstShown.");
                    var definition = registry.Definition(model.CompositionItems, container.Kind);
                    var child = owner.CreateChild(screen, definition);
                    screen.Children = child;
                    container.Attach(child);
                    await child.InitializeContentsAsync(cancellationToken);
                }
                else if (model.CompositionKind != null)
                    throw new InvalidOperationException("Register a matching container view for this model's destinations.");
                return screen;
            }
            catch
            {
                if (!bound) await scope.AbandonAsync();
                throw;
            }
        }
    }
}
