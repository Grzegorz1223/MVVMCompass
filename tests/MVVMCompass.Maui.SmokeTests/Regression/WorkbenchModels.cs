using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

internal sealed record WorkbenchParameter(WorkbenchSession Session, string Caption, string Failure, bool HoldInitialization, bool Reenter);

internal sealed class WorkbenchResource(ScenarioLog log) : IAsyncDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString()[..6];
    public int Disposals { get; private set; }
    public Func<Task>? BeforeDispose { get; set; }
    public async ValueTask DisposeAsync()
    {
        log.Write($"RESOURCE {Id} disposal starting; UI thread={MainThread.IsMainThread}");
        if (BeforeDispose != null) await BeforeDispose();
        Disposals++;
        log.Write($"RESOURCE {Id} disposal completed ({Disposals})");
    }
}

// Shared presentation data, not a replacement for either lifecycle contract being demonstrated.
internal sealed class WorkbenchState(WorkbenchResource resource)
{
    public WorkbenchResource Resource { get; } = resource;
    public WorkbenchParameter? Parameter { get; private set; }
    public Action? Prepared { get; set; }
    public bool AllowNavigation { get; set; } = true;
    public bool HoldCleanup { get; set; }
    public bool FailCleanup { get; set; }
    public async Task InitializeAsync(WorkbenchParameter parameter, CancellationToken token)
    {
        Parameter = parameter;
        parameter.Session.Log.Write($"INITIALIZE {parameter.Caption}; resource={Resource.Id}");
        Prepared?.Invoke();
        if (parameter.HoldInitialization) await parameter.Session.HoldAsync("initialization", token);
        token.ThrowIfCancellationRequested();
        if (parameter.Failure == "Initialization") throw new InvalidOperationException("Requested initialization failure");
        if (parameter.Reenter)
            await parameter.Session.ReportAsync("Reentrant request from initialization", parameter.Session.Host.BackAsync());
    }
    public Task ActivateAsync()
    {
        Parameter?.Session.Log.Write($"ACTIVATE {Parameter.Caption}; resource={Resource.Id}");
        if (Parameter?.Failure == "Activation") throw new InvalidOperationException("Requested activation failure");
        return Task.CompletedTask;
    }
    public Task DeactivateAsync()
    { Parameter?.Session.Log.Write($"DEACTIVATE {Parameter.Caption}; dependency still live={Resource.Disposals == 0}"); return Task.CompletedTask; }
    public async Task CleanupAsync(string reason)
    {
        if (Parameter is not { } parameter) return;
        parameter.Session.Log.Write($"MODEL cleanup {parameter.Caption}; reason={reason}; resource disposals={Resource.Disposals}");
        if (HoldCleanup) await parameter.Session.HoldAsync("cleanup", CancellationToken.None);
        if (FailCleanup) throw new InvalidOperationException("Requested cleanup failure");
    }
}

internal sealed class WorkbenchModel(WorkbenchResource resource) : INavigationInitializable<WorkbenchParameter>, INavigationAware, INavigationGuard
{
    public WorkbenchState State { get; } = new(resource);
    public Task InitializeAsync(WorkbenchParameter parameter, CancellationToken cancellationToken) => State.InitializeAsync(parameter, cancellationToken);
    public Task ActivateAsync(CancellationToken lifetimeToken) => State.ActivateAsync();
    public Task DeactivateAsync() => State.DeactivateAsync();
    public Task DismissAsync(DismissalReason reason) => State.CleanupAsync(reason.ToString());
    public Task<bool> CanNavigateAsync(CancellationToken cancellationToken) => Task.FromResult(State.AllowNavigation);
}

internal sealed class WorkbenchLegacyModel(ILegacyNavigationService navigation, ScenarioLog log, WorkbenchResource resource)
    : ProbeViewModel(navigation, log), INavigationInitializable<WorkbenchParameter>
{
    public WorkbenchState State { get; } = new(resource);
    public Task InitializeAsync(WorkbenchParameter parameter, CancellationToken cancellationToken) => State.InitializeAsync(parameter, cancellationToken);
    public override async Task GetParameters(Dictionary<string, object> parameters)
    {
        await base.GetParameters(parameters);
        if (parameters.TryGetValue("workbench", out var value) && value is WorkbenchParameter parameter)
            await InitializeAsync(parameter, CancellationToken.None);
    }
    public override Task Appearing() => State.ActivateAsync();
    public override Task Deactivated() => State.DeactivateAsync();
    public override Task AfterDismissed() => IsDismissed ? State.CleanupAsync("terminal legacy callback") : State.DeactivateAsync();
    public override Task<bool> CanNavigate() => Task.FromResult(State.AllowNavigation);
}

internal sealed class WorkbenchLegacyPage : PlaygroundPage<WorkbenchLegacyModel>
{
    public WorkbenchLegacyPage(WorkbenchLegacyModel model, WorkbenchResource resource) : base(model)
    {
        if (!ReferenceEquals(resource, model.State.Resource)) throw new InvalidOperationException("Constructor providers differ");
        model.State.Prepared = () =>
        {
            Title = model.State.Parameter!.Caption;
            Content = model.State.Parameter.Session.CreateControls(model.State);
            model.Log.Write($"PROVIDER {resource.Id}; view/model/helper identical={ReferenceEquals(resource, GetService<WorkbenchResource>())}; constructor parent={model.ParentDuringConstruction?.GetType().Name ?? "none"}");
        };
    }
}

internal sealed class WorkbenchTabsModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log), INavigationInitializable<WorkbenchParameter>
{
    private WorkbenchParameter? parameter;
    private int count;
    public Task InitializeAsync(WorkbenchParameter value, CancellationToken cancellationToken)
    { parameter = value; return Task.CompletedTask; }
    public override async Task BeforeFirstShown() { await AppendAsync(); await AppendAsync(); }
    public Task AppendAsync()
    {
        var child = parameter! with { Caption = $"Retained child {++count}", HoldInitialization = false, Failure = "None", Reenter = false };
        return AddTabbedViewModels(new(new[] { new TabModel(typeof(WorkbenchLegacyModel), parameters:
            new() { ["caption"] = child.Caption, ["workbench"] = child }) }, this));
    }
}

internal sealed class WorkbenchTabsPage : TabbedPage, IHasVM
{
    public WorkbenchTabsPage(WorkbenchTabsModel model) { BindingContext = ViewModel = model; Title = "Retained workbench"; }
    public ViewModelBase ViewModel { get; }
}
