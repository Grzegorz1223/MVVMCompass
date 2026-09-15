namespace MVVMCompass.Sample;

internal sealed class ResultPopup : PopupViewBase<PopupViewModel, string?>
{
    public ResultPopup(PopupViewModel vm, RegressionSmokeRunner smoke) : base(vm)
    {
        CanBeDismissedByTappingOutsideOfPopup = true;
        Content = Ui.Stack(Ui.Heading("Owned popup result"), Ui.Note("Return a value, return null, close through the service, or tap outside. Inspect the trace for cleanup and underlying-page flags."),
            Ui.Button("Return value", vm, () => CloseAsync("accepted")),
            Ui.Button("Return null", vm, () => CloseAsync((string?)null)),
            Ui.Button("Close through navigation service", vm, vm.Navigation.ClosePopup));
        Opened += async (_, _) =>
        {
            vm.Log.Write($"POPUP opened: {vm.Name}");
            if (!smoke.AutoClosePopups) return;
            try { await Task.Delay(200); await CloseAsync("accepted"); }
            catch (Exception error) { vm.Log.Write($"POPUP automatic close failed: {error}"); }
        };
        Closed += (_, _) => vm.Log.Write($"POPUP closed: {vm.Name}; terminal={vm.IsDismissed}");
    }
    protected override Task DisplayToast(ToastEventArgs args) => GetToast(args.Message ?? args.Id, args.ToastType).Show();
    protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Ui.CustomAction(args, ViewModel);
    protected override bool NotifyLanguageChange() { ViewModel.Log.Write("LANGUAGE popup"); return true; }
    protected override IDisposable ShowLoading(LoadingType loadingType) => Ui.Loading(ViewModel, value => ViewModel.IsBusy = value);
    protected override void HideLoading() => ViewModel.IsBusy = false;
}
