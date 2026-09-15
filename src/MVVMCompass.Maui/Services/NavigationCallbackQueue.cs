namespace MVVMCompass.Services;

/// <summary>Defers native callbacks raised during a coordinated operation until owned cleanup finishes.</summary>
internal sealed class NavigationCallbackQueue : IDisposable
{
    private readonly List<ViewModelBase> models = [];
    private readonly Queue<(ViewModelBase Model, Func<Task> Callback, string Operation)> callbacks = new();

    internal void Capture(IEnumerable<ViewModelBase> values)
    {
        foreach (var model in values.Distinct())
        {
            if (model.PendingNavigationCallbacks == this) continue;
            if (model.PendingNavigationCallbacks != null)
                throw new InvalidOperationException("The view model already belongs to another navigation callback batch.");
            model.PendingNavigationCallbacks = this;
            models.Add(model);
        }
    }

    private readonly HashSet<(ViewModelBase Model, string Operation)> suppressed = [];
    internal void Suppress(ViewModelBase model, string operation) => suppressed.Add((model, operation));
    internal void Defer(ViewModelBase model, Func<Task> callback, string operation) => callbacks.Enqueue((model, callback, operation));

    internal async Task DrainAsync()
    {
        List<Exception> errors = [];
        while (callbacks.TryDequeue(out var item))
            if (!item.Model.IsDismissed && !suppressed.Contains((item.Model, item.Operation)))
                try { await item.Callback(); }
                catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Navigation lifecycle callbacks failed.", errors);
    }

    public void Dispose()
    {
        foreach (var model in models)
            if (model.PendingNavigationCallbacks == this) model.PendingNavigationCallbacks = null;
        callbacks.Clear();
    }
}
