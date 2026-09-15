using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Services;

/// <summary>Owns detached candidates and delays library activation until outgoing cleanup finishes.</summary>
internal sealed class RootPreparation(IEnumerable<ViewModelBase> existingModels, CancellationToken cancellationToken = default) : IDisposable
{
    private readonly HashSet<ViewModelBase> existing = new(existingModels, ReferenceEqualityComparer.Instance);
    private readonly List<VisualElement> views = [];
    private readonly List<ViewModelBase> models = [];
    private readonly Queue<Func<Task>> activation = new();
    internal bool IsPreparing { get; private set; } = true;
    internal CancellationToken CancellationToken { get; } = cancellationToken;
    internal Page? Root => views.FirstOrDefault() as Page;

    internal void Track(VisualElement view)
    {
        var found = ViewModelTree.Collect(view);
        // Validate before taking ownership: a singleton from a live tree must never be
        // initialized or cleaned up as an abandoned candidate.
        if (view.Parent != null || views.Contains(view) || found.Any(model =>
                existing.Contains(model) || model.IsDismissed || model.PendingRootPreparation != null
                || model.Ownership.Parent is { } parent && !found.Any(item => item.Ownership == parent)))
            throw new InvalidOperationException("Root preparation requires new, unowned views and view models. Register navigation entries as transient services.");

        views.Add(view);
        foreach (var model in found)
        {
            models.Add(model);
            model.PendingRootPreparation = this;
        }
        ViewModelTree.AdoptChildren(view);
    }

    internal VisualElement? Find(ViewModelBase model) =>
        views.FirstOrDefault(view => view is IHasVM hasVm && ReferenceEquals(hasVm.ViewModel, model));

    internal void Defer(Func<Task> callback) => activation.Enqueue(callback);

    internal void FinishPreparation() => IsPreparing = false;

    internal void Validate(IEnumerable<ViewModelBase> outgoing)
    {
        existing.UnionWith(outgoing);
        if (views.SelectMany(view => ViewModelTree.Collect(view)).Any(model => existing.Contains(model) || model.IsDismissed))
            throw new InvalidOperationException("The prepared root contains an existing or dismissed view model.");
    }

    internal async Task ActivateAsync()
    {
        // Native events raised by installation or tab selection join this same queue.
        // Their failures therefore fault the root operation instead of an async-void event.
        while (activation.TryDequeue(out var callback)) await callback();
    }

    internal IEnumerable<ViewModelBase> OwnedModels =>
        views.AsEnumerable().Reverse().SelectMany(view => ViewModelTree.Collect(view))
            .Concat(models.AsEnumerable().Reverse()).Where(model => !existing.Contains(model));

    internal Task AbandonAsync() => LegacyNavigationService.DismissViewModelsAsync(OwnedModels, DismissalReason.PreparationFailed);

    public void Dispose()
    {
        foreach (var model in models)
            if (ReferenceEquals(model.PendingRootPreparation, this)) model.PendingRootPreparation = null;
        activation.Clear();
        views.Clear();
        models.Clear();
        existing.Clear();
    }
}
