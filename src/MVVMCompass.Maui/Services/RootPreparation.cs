namespace MVVMCompass.Services;

/// <summary>Validates detached root visuals and tracks their models until the root is installed.</summary>
internal sealed class RootPreparation(IEnumerable<ViewModelBase> existingModels) : IDisposable
{
    private readonly HashSet<ViewModelBase> existing = new(existingModels, ReferenceEqualityComparer.Instance);
    private readonly List<VisualElement> views = [];

    internal void Track(VisualElement view)
    {
        var found = ViewModelTree.Collect(view);
        if (view.Parent != null || views.Contains(view) || found.Any(model =>
                existing.Contains(model) || model.IsDismissed
                || model.Ownership.Parent is { } parent && !found.Any(item => item.Ownership == parent)))
            throw new InvalidOperationException("Root preparation requires new, unowned views and view models. Register navigation entries as transient services.");
        views.Add(view);
        ViewModelTree.AdoptChildren(view);
    }

    internal void Validate(IEnumerable<ViewModelBase> outgoing)
    {
        existing.UnionWith(outgoing);
        if (views.SelectMany(view => ViewModelTree.Collect(view)).Any(model => existing.Contains(model) || model.IsDismissed))
            throw new InvalidOperationException("The prepared root contains an existing or dismissed view model.");
    }

    internal IEnumerable<ViewModelBase> OwnedModels => views.AsEnumerable().Reverse()
        .SelectMany(view => ViewModelTree.Collect(view)).Where(model => !existing.Contains(model));

    public void Dispose() { views.Clear(); existing.Clear(); }
}
