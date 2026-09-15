using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Core;

namespace MVVMCompass.Sample;

internal sealed class WorkbenchSession : ObservableObject
{
    private readonly NavigationOptions options;
    private readonly HashSet<CancellationTokenSource> pending = [];
    private readonly Dictionary<TaskCompletionSource, string> holds = [];
    private readonly NavigationEntry foreignOrigin = new NavigationCoordinator().CreateEntry(new object());
    private NavigationEntry? savedOrigin;
    private int sequence;
    private string status = "Ready. Each action writes its outcome and commitment state to the trace.";
    private string mode = "Ordinary scoped";
    private string policy = "FIFO";
    private string origin = "Application";
    private string failure = "None";
    private string caption = "Typed parameter";
    private bool holdInitialization;
    private bool reenter;
    public WorkbenchSession(MauiNavigationHost host, ScenarioLog log, NavigationOptions options)
    { Host = host; Log = log; this.options = options; }
    public MauiNavigationHost Host { get; }
    public ScenarioLog Log { get; }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string Mode { get => mode; set => SetProperty(ref mode, value); }
    public string Policy { get => policy; set => SetProperty(ref policy, value); }
    public string Origin { get => origin; set => SetProperty(ref origin, value); }
    public string Failure { get => failure; set => SetProperty(ref failure, value); }
    public string Caption { get => caption; set => SetProperty(ref caption, value); }
    public bool HoldInitialization { get => holdInitialization; set => SetProperty(ref holdInitialization, value); }
    public bool Reenter { get => reenter; set => SetProperty(ref reenter, value); }

    private NavigationRequest<T> Request<T>(T parameter) => new(parameter)
    {
        Origin = Origin switch { "Current entry" => Host.CurrentEntry, "Saved entry" => savedOrigin, "Foreign entry" => foreignOrigin, _ => null },
        Priority = Policy == "Required" ? NavigationPriority.Required : NavigationPriority.Normal,
        CoalescingKey = Policy == "Coalesce" ? "workbench" : null,
        RejectIfBusy = Policy == "Reject if busy"
    };

    public async Task NavigateAsync(string action)
    {
        if (Origin == "Saved entry" && savedOrigin == null) throw new InvalidOperationException("Save an origin first.");
        using var cancellation = new CancellationTokenSource();
        pending.Add(cancellation);
        var parameter = new WorkbenchParameter(this, $"{Caption} #{++sequence}", Failure, HoldInitialization, Reenter);
        var request = Request(parameter);
        var mode = Mode;
        Log.Write($"SUBMIT {action} {parameter.Caption}; {mode}; {Policy}; origin={request.Origin?.Id.ToString()[..6] ?? "application"}");
        try
        {
            if (mode == "Registered legacy")
            {
                if (parameter.Failure == "Page factory") throw new InvalidOperationException("Choose an ordinary model to fail its page factory.");
                if (action == "root") await ReportAsync(action, Host.ReplaceRootAsync<WorkbenchLegacyModel, WorkbenchParameter>(request, navigable: true, cancellationToken: cancellation.Token));
                else if (action == "push") await ReportAsync(action, Host.PushAsync<WorkbenchLegacyModel, WorkbenchParameter>(request, cancellationToken: cancellation.Token));
                else await ReportAsync(action, Host.OpenModalAsync<WorkbenchLegacyModel, WorkbenchParameter>(request, navigable: true, cancellationToken: cancellation.Token));
            }
            else if (mode == "Ordinary scoped")
            {
                WorkbenchModel Model(IServiceProvider services) => services.GetRequiredService<WorkbenchModel>();
                Page Page(IServiceProvider services, WorkbenchModel model)
                {
                    Log.Write($"PROVIDER {model.State.Resource.Id}; factories identical={ReferenceEquals(services.GetRequiredService<WorkbenchResource>(), model.State.Resource)}");
                    return CreatePage(model, parameter.Failure);
                }
                if (action == "root") await ReportAsync(action, Host.ReplaceScopedRootAsync(request, Model, Page, navigable: true, cancellationToken: cancellation.Token));
                else if (action == "push") await ReportAsync(action, Host.PushScopedAsync(request, Model, Page, cancellationToken: cancellation.Token));
                else await ReportAsync(action, Host.OpenScopedModalAsync(request, Model, Page, navigable: true, cancellationToken: cancellation.Token));
            }
            else
            {
                WorkbenchModel Model() => new(new WorkbenchResource(Log));
                Page Page(WorkbenchModel model) => CreatePage(model, parameter.Failure);
                Task Cleanup(WorkbenchModel model) => model.State.Resource.DisposeAsync().AsTask();
                if (action == "root") await ReportAsync(action, Host.ReplaceRootAsync(request, Model, Page, navigable: true, cleanup: Cleanup, cancellationToken: cancellation.Token));
                else if (action == "push") await ReportAsync(action, Host.PushAsync(request, Model, Page, cleanup: Cleanup, cancellationToken: cancellation.Token));
                else await ReportAsync(action, Host.OpenModalAsync(request, Model, Page, navigable: true, cleanup: Cleanup, cancellationToken: cancellation.Token));
            }
        }
        finally { pending.Remove(cancellation); Refresh(); }
    }

    private Page CreatePage(WorkbenchModel model, string failure)
    {
        if (failure == "Page factory") throw new InvalidOperationException("Requested page factory failure");
        var page = new ContentPage { BackgroundColor = Colors.White };
        model.State.Prepared = () => { page.Title = model.State.Parameter!.Caption; page.Content = CreateControls(model.State); };
        return page;
    }

    public async Task ReportAsync<T>(string action, Task<NavigationOutcome<T>> operation)
    {
        var result = await operation;
        Log.Write($"OUTCOME {action}: {result.Status}; committed={result.HasCommitted}; error={result.Error?.Message ?? "none"}; cleanup errors={result.CleanupErrors.Count}");
        Refresh();
    }

    public async Task ShowPopupAsync(ViewModelBase? origin = null)
    {
        using var cancellation = new CancellationTokenSource();
        var waiting = true;
        void CancelWait() { if (waiting) cancellation.Cancel(); }
        pending.Add(cancellation);
        try
        {
            var result = await Host.DisplayPopupAsync<WorkbenchPopupModel, string>(origin,
                new() { ["workbench"] = this, ["cancelWait"] = (System.Action)CancelWait }, cancellation.Token);
            Log.Write($"POPUP result={result.Result ?? "<null>"}; outside={result.WasDismissedByTappingOutsideOfPopup}; reason={result.Reason}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { Log.Write("POPUP wait cancelled; the presented popup remains owned until closed"); }
        finally { waiting = false; pending.Remove(cancellation); Refresh(); }
    }

    public async Task HoldAsync(string phase, CancellationToken token)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        holds.Add(signal, phase); Log.Write($"HOLD {phase}; use Release holds or cancel uncommitted initialization"); Refresh();
        // Window destruction must not wait forever for a manual release button that disappeared.
        void Destroyed(object? sender, EventArgs args) => signal.TrySetResult();
        Host.Window.Destroying += Destroyed;
        try { if (!Host.IsClosed) await signal.Task.WaitAsync(token); }
        finally { Host.Window.Destroying -= Destroyed; holds.Remove(signal); Refresh(); }
    }

    private void Refresh() => Status = $"Current: {Host.CurrentEntry?.ViewModel.GetType().Name ?? "none"} / {Host.CurrentEntry?.State}\n"
        + $"Requests awaiting completion: {pending.Count}; holds: {string.Join(", ", holds.Values)}\n"
        + $"Saved origin: {savedOrigin?.Id.ToString()[..6] ?? "none"} / {savedOrigin?.State}";

    public View CreateControls(WorkbenchState state)
    {
        Button Button(string text, Func<Task> action) => Ui.Button(text, Log, action);
        Button Action(string text, System.Action action) => Button(text, () => { action(); Refresh(); return Task.CompletedTask; });
        var label = Ui.Note(Status); label.BindingContext = this; label.SetBinding(Label.TextProperty, static (WorkbenchSession source) => source.Status);
        var caption = new Entry { Placeholder = "Typed parameter", BindingContext = this };
        caption.SetBinding(Entry.TextProperty, static (WorkbenchSession source) => source.Caption, BindingMode.TwoWay);
        var views = new List<View>
        {
            Ui.Heading(state.Parameter!.Caption),
            Ui.Note($"Model: resource {state.Resource.Id}. Lifecycle: {options.RetainedViewLifecycleBehavior}. Registered scopes: {options.UseEntryScopes}. Ordinary scoped factories always own a scope."),
            label, caption,
            Choice("Destination model", Binding.Create(static (WorkbenchSession source) => source.Mode, BindingMode.TwoWay), ["Ordinary scoped", "Ordinary with explicit cleanup", "Registered legacy"]),
            Choice("Request policy", Binding.Create(static (WorkbenchSession source) => source.Policy, BindingMode.TwoWay), ["FIFO", "Required", "Coalesce", "Reject if busy"]),
            Choice("Request origin", Binding.Create(static (WorkbenchSession source) => source.Origin, BindingMode.TwoWay), ["Application", "Current entry", "Saved entry", "Foreign entry"]),
            Choice("Failure in next destination", Binding.Create(static (WorkbenchSession source) => source.Failure, BindingMode.TwoWay), ["None", "Page factory", "Initialization", "Activation"]),
            Toggle("Hold next initialization", this, Binding.Create(static (WorkbenchSession source) => source.HoldInitialization, BindingMode.TwoWay)),
            Toggle("Attempt reentrant navigation from initialization", this, Binding.Create(static (WorkbenchSession source) => source.Reenter, BindingMode.TwoWay)),
            Button("Replace root", () => NavigateAsync("root")),
            Button("Push page", () => NavigateAsync("push")),
            Button("Open modal / nested modal", () => NavigateAsync("modal")),
            Action("Cancel requests awaiting completion", () => { foreach (var item in pending.ToArray()) item.Cancel(); Log.Write("CALLER cancellation requested; committed cleanup still runs"); }),
            Action("Release all current holds", () => { foreach (var item in holds.Keys.ToArray()) item.TrySetResult(); }),
            Action("Save current origin", () => { savedOrigin = Host.CurrentEntry; Log.Write("ORIGIN saved; cover, remove or reactivate this entry before trying it again"); }),
            Toggle("Allow leaving this page", state, Binding.Create(static (WorkbenchState source) => source.AllowNavigation, BindingMode.TwoWay)),
            Toggle("Hold this model's terminal cleanup", state, Binding.Create(static (WorkbenchState source) => source.HoldCleanup, BindingMode.TwoWay)),
            Toggle("Fail this model's terminal cleanup", state, Binding.Create(static (WorkbenchState source) => source.FailCleanup, BindingMode.TwoWay)),
            Action("Hold this resource's disposal", () => { state.Resource.BeforeDispose = () => HoldAsync("resource disposal", CancellationToken.None); Log.Write("RESOURCE disposal will wait for release; provider-owned resources may outlive navigation"); }),
            Button("Back", () => ReportAsync("back", Host.BackAsync(Request<object?>(null)))),
            Button("Pop to root", () => ReportAsync("pop to root", Host.PopToRootAsync(Request<object?>(null)))),
            Button("Close modal", () => ReportAsync("close modal", Host.CloseModalAsync(Request<object?>(null)))),
            Action("Attach explicit child and final resource cleanup", AttachChild),
            Action("Inspect ownership tree", InspectOwnership),
            Button("Open retained tabs workbench", () => ReportAsync("retained root", Host.ReplaceRootAsync<WorkbenchTabsModel, WorkbenchParameter>(Request(state.Parameter with { HoldInitialization = false, Failure = "None", Reenter = false })))),
            Button("Native back / await reconciliation", async () =>
            {
                var navigation = Host.Window.Navigation;
                if (navigation.ModalStack.Count > 0) await navigation.PopModalAsync();
                else if (Host.CurrentRoot?.Page is NavigationPage stack && stack.Navigation.NavigationStack.Count > 1) await stack.PopAsync();
                await Host.ReconcileNativeAsync(); Log.Write("NATIVE completion awaited"); Refresh();
            }),
            Button("Popup: result, cancellation, nesting and native veto", () => ShowPopupAsync()),
            Button("Return to catalog / recover root", async () =>
            {
                foreach (var signal in holds.Keys.ToArray()) signal.TrySetResult();
                await ReportAsync("catalog", Host.ReplaceRootAsync<CatalogViewModel>(new(null) { Priority = NavigationPriority.Required }, navigable: true));
            })
        };
        {
            WorkbenchTabsPage Tabs() => Host.CurrentRoot?.Page as WorkbenchTabsPage ?? throw new InvalidOperationException("Open the retained tabs workbench first.");
            views.Add(Button("Select next retained tab through coordinator", () => ReportAsync("select tab", Host.SelectTabAsync(Tabs(), Request((Tabs().Children.IndexOf(Tabs().CurrentPage) + 1) % Tabs().Children.Count)))));
            views.Add(Button("Append retained child", () => ((WorkbenchTabsModel)Tabs().ViewModel).AppendAsync()));
            views.Add(Button("Remove inactive tab natively / await cleanup", async () =>
            {
                var tabs = Tabs();
                var other = tabs.Children.FirstOrDefault(page => page != tabs.CurrentPage);
                if (other != null) tabs.Children.Remove(other);
                await Host.ReconcileNativeAsync(); InspectOwnership();
            }));
        }
        views.Add(Ui.Note("Hold initialization, submit from another button, then cancel or release. Required and coalesced requests can supersede uncommitted work. The trace reports outcomes; it does not mark manual checks as passed."));
        views.Add(Ui.Trace(Log));
        return new ScrollView { Content = Ui.Stack(views.ToArray()) };
    }

    private Picker Choice(string title, BindingBase binding, string[] values)
    {
        var picker = new Picker { Title = title, ItemsSource = values, BindingContext = this };
        picker.SetBinding(Picker.SelectedItemProperty, binding); return picker;
    }
    private static View Toggle(string title, object source, BindingBase binding)
    {
        var toggle = new Switch { BindingContext = source };
        toggle.SetBinding(Switch.IsToggledProperty, binding);
        SemanticProperties.SetDescription(toggle, title);
        var note = Ui.Note(title); note.VerticalOptions = LayoutOptions.Center;
        var row = new Grid { ColumnSpacing = 10, ColumnDefinitions =
            { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Star } } };
        row.Children.Add(toggle); row.Children.Add(note); Grid.SetColumn(note, 1);
        return row;
    }
    private void AttachChild()
    {
        var owner = Host.CurrentEntry ?? throw new InvalidOperationException("No active entry");
        var id = Guid.NewGuid().ToString()[..6];
        var child = new NavigationLifetime(() => { Log.Write($"CHILD {id} cleanup; parent dismissed={owner.Lifetime.IsDismissed}"); return Task.CompletedTask; });
        child.Ownership.RegisterCleanup(() => { Log.Write($"CHILD {id} final resource released"); return Task.CompletedTask; }, runLast: true);
        owner.Ownership.Adopt(child.Ownership); InspectOwnership();
    }
    private void InspectOwnership()
    {
        foreach (var root in Host.OwnershipRoots)
            foreach (var node in root.Snapshot())
                Log.Write($"OWNER {node.Id.ToString()[..6]} type={node.Owner?.GetType().Name ?? "explicit child"}; parent={node.Parent?.Id.ToString()[..6] ?? "root"}; dismissed={node.Lifetime.IsDismissed}");
        Refresh();
    }
}
