using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

internal sealed class WorkbenchPopupModel(ILegacyNavigationService navigation, ScenarioLog log, WorkbenchResource resource)
    : ProbeViewModel(navigation, log)
{
    public WorkbenchSession? Session { get; private set; }
    public Action? CancelWait { get; private set; }
    public bool HoldCleanup { get; set; }
    public void CancelResultWait()
    {
        var cancel = CancelWait; CancelWait = null; cancel?.Invoke();
    }
    public override async Task GetParameters(Dictionary<string, object> parameters)
    {
        await base.GetParameters(parameters);
        Session = (WorkbenchSession)parameters["workbench"];
        CancelWait = (Action)parameters["cancelWait"];
    }
    public override async Task AfterDismissed()
    {
        await base.AfterDismissed();
        Log.Write($"POPUP model cleanup; resource={resource.Id}; disposals={resource.Disposals}");
        if (HoldCleanup) await Session!.HoldAsync("popup cleanup", CancellationToken.None);
        CancelWait = null;
    }
}

internal sealed class WorkbenchPopup : PopupViewBase<WorkbenchPopupModel, string>
{
    private bool veto;
    public WorkbenchPopup(WorkbenchPopupModel model) : base(model)
    {
        CanBeDismissedByTappingOutsideOfPopup = true;
        Opened += (_, _) =>
        {
            var session = model.Session!;
            var nativePage = session.Host.Window.Navigation.ModalStack.LastOrDefault();
            void Veto(object? sender, ModalPoppingEventArgs args)
            {
                if (veto && args.Modal == nativePage)
                { args.Cancel = true; model.Log.Write("POPUP native close vetoed; disable veto and retry"); }
            }
            session.Host.Window.ModalPopping += Veto;
            model.Ownership.RegisterCleanup(() => { session.Host.Window.ModalPopping -= Veto; return Task.CompletedTask; });
            Closed += (_, _) => session.Host.Window.ModalPopping -= Veto;
            Content = new ScrollView { MaximumHeightRequest = 620, Content = Ui.Stack(
                Ui.Heading("Owned popup"),
                Ui.Note("Close with a value, return null, tap outside or use native back. A cancelled result wait leaves this popup open."),
                Ui.Button("Return value", model, () => CloseAsync("workbench result")),
                Ui.Button("Return null", model, () => CloseAsync()),
                Ui.Button("Close through this window's host", model, () => session.Host.ClosePopupAsync()),
                Ui.Button("Toggle native close veto", model, () => { veto = !veto; model.Log.Write($"POPUP veto={veto}"); return Task.CompletedTask; }),
                Ui.Button("Cancel result wait", model, () => { model.CancelResultWait(); return Task.CompletedTask; }),
                Ui.Button("Hold terminal cleanup", model, () => { model.HoldCleanup = true; model.Log.Write("POPUP cleanup will wait; use Release all current holds on the underlying page after closing"); return Task.CompletedTask; }),
                Ui.Button("Open nested popup", model, () => session.ShowPopupAsync(model)),
                Ui.Trace(model.Log)) };
        };
    }
    protected override Task DisplayToast(ToastEventArgs args) => Ui.Toast(args);
    protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Ui.CustomAction(args, ViewModel);
    protected override bool NotifyLanguageChange() => true;
    protected override IDisposable ShowLoading(LoadingType loadingType) => Ui.Loading(ViewModel, value => ViewModel.IsBusy = value);
    protected override void HideLoading() => ViewModel.IsBusy = false;
}
