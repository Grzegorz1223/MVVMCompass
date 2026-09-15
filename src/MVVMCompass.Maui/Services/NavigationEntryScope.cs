using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Services;

/// <summary>Owns one DI scope; the destination lifetime releases it after all model cleanup.</summary>
internal sealed class NavigationEntryScope : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<VisualElement, NavigationEntryScope> views = new();
    private static readonly ConditionalWeakTable<ViewModelBase, NavigationEntryScope> models = new();
    [ThreadStatic] private static Construction? construction;
    private readonly AsyncServiceScope scope;
    private readonly List<ViewModelBase> candidates = [];
    private Task? disposal;
    private bool bound;

    internal NavigationEntryScope(IServiceScopeFactory factory) => scope = factory.CreateAsyncScope();
    internal IServiceProvider Services => scope.ServiceProvider;

    internal IDisposable Construct(Type? destination = null)
    {
        var previous = construction;
        construction = new(this, destination);
        return new ConstructionScope(() => construction = previous);
    }

    internal static void Constructed(ViewModelBase model)
    {
        if (construction is { Destination: { } destination } current && destination.IsInstanceOfType(model))
            current.Scope.candidates.Add(model);
    }

    internal static IServiceProvider? For(VisualElement view)
    {
        for (Element? current = view; current != null; current = current.Parent)
        {
            if (current is VisualElement visual && views.TryGetValue(visual, out var owned)) return owned.Services;
            if (current is IHasVM vm && models.TryGetValue(vm.ViewModel, out var modelScope)) return modelScope.Services;
        }
        return construction?.Scope.Services;
    }

    internal static bool IsOwned(ViewModelBase model) => models.TryGetValue(model, out var scope) && scope.disposal == null;

    internal void Own(NavigationLifetime lifetime)
    {
        if (bound) throw new InvalidOperationException("An entry scope cannot be shared by separate destinations.");
        if (lifetime.Ownership.Owner is ViewModelBase model)
        {
            if (models.TryGetValue(model, out _)) throw new InvalidOperationException("The model already has an entry scope.");
            models.Add(model, this);
        }
        lifetime.Ownership.RegisterCleanup(() => DisposeAsync().AsTask(), runLast: true);
        bound = true;
        candidates.Clear();
    }

    internal void Bind(VisualElement view, NavigationLifetime lifetime)
    {
        if (views.TryGetValue(view, out _)) throw new InvalidOperationException("The view already has an entry scope.");
        if (!bound) Own(lifetime);
        views.Add(view, this);
    }

    internal async Task AbandonAsync()
    {
        try
        {
            // Capture only the requested legacy destination, not singleton VM services
            // which happen to be constructed as dependencies in the same DI call.
            await NavigationLifetimeGroup.DismissAsync(candidates.Where(model => !IsOwned(model)
                && !MauiNavigationHost.IsClaimedModel(model)).Select(model => model.Lifetime), DismissalReason.PreparationFailed);
        }
        finally { candidates.Clear(); await DisposeAsync(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (candidates)
        {
            if (disposal != null) return new(disposal);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
            _ = ReleaseAsync();
            return new(disposal);
            async Task ReleaseAsync()
            {
                try { await scope.DisposeAsync(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
            }
        }
    }

    private sealed record Construction(NavigationEntryScope Scope, Type? Destination);
    private sealed class ConstructionScope(Action restore) : IDisposable
    { public void Dispose() => restore(); }
}
