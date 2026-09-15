using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Mvvm.Input;

namespace MVVMCompass.Sample;

internal static class Ui
{
    internal static Label Heading(string text) => new() { Text = text, FontSize = 25, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("182D46") };
    internal static Label Note(string text) => new() { Text = text, FontSize = 14, TextColor = Color.FromArgb("475569") };
    internal static VerticalStackLayout Stack(params View[] views)
    {
        var stack = new VerticalStackLayout { Spacing = 12, Padding = 20 };
        foreach (var view in views) stack.Children.Add(view);
        return stack;
    }
    internal static Button Button(string text, ProbeViewModel vm, Func<Task> action) => Button(text, vm.Log, action);
    internal static Button Button(string text, ScenarioLog log, Func<Task> action)
    {
        var button = new Button { Text = text, BackgroundColor = Color.FromArgb("274E77"), TextColor = Colors.White, CornerRadius = 10, MinimumHeightRequest = 46 };
        button.Clicked += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception error) { log.Write($"ACTION FAILED [{text}]: {error}"); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    internal static View Trace(ScenarioLog log)
    {
        var label = new Label { FontSize = 11, TextColor = Color.FromArgb("1E3A5F"), BindingContext = log };
        label.SetBinding(Label.TextProperty, static (ScenarioLog source) => source.Text);
        return new ScrollView { Content = label, HeightRequest = 220, BackgroundColor = Color.FromArgb("EDF3F9"), Padding = 12 };
    }
    internal static ActivityIndicator LoadingIndicator(ProbeViewModel vm)
    {
        var indicator = new ActivityIndicator { BindingContext = vm, Color = Color.FromArgb("274E77") };
        indicator.SetBinding(ActivityIndicator.IsRunningProperty, static (ProbeViewModel source) => source.IsBusy);
        indicator.SetBinding(VisualElement.IsVisibleProperty, static (ProbeViewModel source) => source.IsBusy);
        SemanticProperties.SetDescription(indicator, "Loading the current scenario");
        return indicator;
    }
    internal static Button Home(ProbeViewModel vm) => Button("Return to scenarios / recover root", vm,
        () => vm.Navigation.PresentAsNavigableMainPage<CatalogViewModel>());
    internal static View[] CommonActions(ProbeViewModel vm) =>
    [
        Button("Push another instance with parameters", vm, () => vm.Navigation.NavigateTo<DetailViewModel>(new() { ["caption"] = "Repeated destination", ["instance"] = Guid.NewGuid().ToString()[..6] })),
        Button("Open modal", vm, () => vm.Navigation.NavigateTo<ModalViewModel>()),
        Button("Back", vm, () => vm.Navigation.NavigateBack(vm)),
        Button("Pop to root", vm, vm.Navigation.NavigateBackToRoot),
        Button("Toggle tab / flyout guard", vm, () => { vm.AllowNavigation = !vm.AllowNavigation; vm.Log.Write($"{vm.Name} guard now {vm.AllowNavigation}"); return Task.CompletedTask; }),
        Home(vm)
    ];
    internal static Task<object?> CustomAction(CustomActionEventArgs args, ProbeViewModel vm)
    {
        vm.Log.Write($"CUSTOM ACTION {args.Id}; UI thread={MainThread.IsMainThread}");
        return Task.FromResult<object?>(args.Message ?? args.Parameter?.Parameter);
    }
    internal static Task Toast(ToastEventArgs args) => CommunityToolkit.Maui.Alerts.Toast.Make(args.Message ?? args.Id,
        args.ToastType == ToastType.Long ? ToastDuration.Long : ToastDuration.Short).Show();
    internal static IDisposable Loading(ProbeViewModel vm, Action<bool> setBusy)
    {
        setBusy(true);
        vm.Log.Write("LOADING shown");
        return new Scope(() => { setBusy(false); vm.Log.Write("LOADING disposed"); });
    }
    internal static async Task CommandFailure(ProbeViewModel vm)
    {
        try { await ((IAsyncRelayCommand)vm.FailingCommand).ExecuteAsync(null); }
        catch (InvalidOperationException error) { vm.Log.Write($"COMMAND expected error: {error.Message}; busy restored={!vm.IsBusy}"); }
    }
    private sealed class Scope(Action release) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (disposed) return; disposed = true; release(); }
    }
}

internal abstract class PlaygroundPage<T> : LegacyViewBase<T>, IIdProvider, ITitleProvider, ISelectedIconProvider, IUnselectedIconProvider where T : ProbeViewModel
{
    protected PlaygroundPage(T viewModel) : base(viewModel)
    {
        BackgroundColor = Colors.White;
        Title = typeof(T).Name.Replace("ViewModel", "");
    }
    public virtual string GetId() => typeof(T).Name;
    public string GetTitle() => ViewModel.Caption == "Navigation instance" ? Title : ViewModel.Caption;
    public string GetSelectedIcon() => "●";
    public string GetUnselectedIcon() => "○";
    protected override Task DisplayToast(ToastEventArgs args) => GetToast(args.Message ?? args.Id, args.ToastType).Show();
    protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Ui.CustomAction(args, ViewModel);
    protected override bool NotifyLanguageChange() { ViewModel.Log.Write("LANGUAGE refreshed by view"); return true; }
    protected override IDisposable ShowLoading(LoadingType loadingType) => Ui.Loading(ViewModel, value => ViewModel.IsBusy = value);
    protected override void HideLoading() => ViewModel.IsBusy = false;

    public Task ShowDelegatedAlertAsync() => DisplayAlertAsync("Handled-page alert", "This alert uses the extracted child's owning page.", "Close");
    public async Task ShowDelegatedConfirmationAsync() => ViewModel.Log.Write($"CONFIRMATION {await DisplayAlertAsync("Guard confirmation", "Continue?", "Continue", "Stay")}");
    public Page HandledPage => GetHandledPage();
    public Task<CommunityToolkit.Maui.Core.IPopupResult<string?>> ShowDelegatedPopupAsync() => ShowPopupAsync(GetService<ResultPopup>());
    public void ShowDelegatedPopup() => ShowPopup(GetService<ResultPopup>());
}
