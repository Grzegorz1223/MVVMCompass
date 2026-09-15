using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

internal sealed class ModalPage : PlaygroundPage<ModalViewModel>
{
    public ModalPage(ModalViewModel vm) : base(vm)
    {
        IsModal = true;
        Content = Ui.Stack(Ui.Heading("Modal navigation"), Ui.Note(vm.Name), Ui.Button("Dismiss modal", vm, () => vm.Navigation.NavigateBack(vm)), Ui.Trace(vm.Log));
    }
}

internal sealed class LatePage : PlaygroundPage<LateViewModel>
{
    public LatePage(LateViewModel vm) : base(vm) => Content = Ui.Stack(Ui.Heading("Late feature registration"), Ui.Note("This pair was registered through AddViewModelViewPair<LateViewModel, LatePage> after UseMVVMCompass."), Ui.Home(vm));
}

internal sealed class FailingPage(FailingViewModel vm) : PlaygroundPage<FailingViewModel>(vm);

internal sealed class WindowPage : PlaygroundPage<WindowViewModel>, IWindowView
{
    public string WindowTitle => "Navigation · second window";
    public int WindowHeight => 640;
    public int WindowWidth => 460;
    public WindowPage(WindowViewModel vm) : base(vm)
    {
        Content = Ui.Stack(Ui.Heading("Secondary window"), Ui.Note("Resize or move this window on a supported platform. Close it to inspect terminal cleanup and the host callback."),
            Ui.Button("Close this window", vm, () => { Application.Current!.CloseWindow(Window); return Task.CompletedTask; }), Ui.Trace(vm.Log));
    }
}

internal sealed class TabPage : PlaygroundPage<TabViewModel>, IViewContentProvider
{
    public IView? ViewContent { get => Content; set => Content = (View?)value; }
    public TabPage(TabViewModel vm) : base(vm)
    {
        Content = new ScrollView { Content = Ui.Stack(Ui.Heading("Extracted tab page"), Ui.Note(vm.Name),
            Ui.Button("C26 · Delegated alert", vm, ShowDelegatedAlertAsync),
            Ui.Button("C26 · Delegated confirmation", vm, ShowDelegatedConfirmationAsync),
            Ui.Button("C26 · Await delegated popup", vm, async () => vm.Log.Write($"DELEGATED result={(await ShowDelegatedPopupAsync()).Result ?? "<null>"}")),
            Ui.Button("C26 · Fire-and-return delegated popup", vm, () => { ShowDelegatedPopup(); return Task.CompletedTask; }),
            Ui.Button("Toggle guard on this tab", vm, () => { vm.AllowNavigation = !vm.AllowNavigation; vm.Log.Write($"GUARD allow={vm.AllowNavigation}"); return Task.CompletedTask; }),
            Ui.Button("Toggle this tab's busy indicator", vm, () => { vm.IsTabBusy = !vm.IsTabBusy; return Task.CompletedTask; }),
            Ui.Home(vm), Ui.Trace(vm.Log)) };
    }
    public override void OnTabNavigatedTo()
    {
        ViewModel.Log.Write($"{ViewModel.Name} extracted VIEW OnTabNavigatedTo");
        base.OnTabNavigatedTo();
    }
}

internal sealed class FirstPage : PlaygroundPage<FirstViewModel>
{
    public FirstPage(FirstViewModel vm) : base(vm)
    {
        Title = "First";
        Content = new ScrollView { Content = Ui.Stack(Ui.Heading("First retained destination"),
            Ui.Note("Switch to the second tab or flyout entry. Use the guard toggle to refuse or allow that switch."),
            Ui.Button("Toggle guard", vm, () => { vm.AllowNavigation = !vm.AllowNavigation; vm.Log.Write($"GUARD allow={vm.AllowNavigation}"); return Task.CompletedTask; }),
            Ui.Home(vm), Ui.Trace(vm.Log)) };
    }
}

internal sealed class SecondPage : PlaygroundPage<SecondViewModel>
{
    public SecondPage(SecondViewModel vm) : base(vm)
    {
        Title = "Second";
        Content = new ScrollView { Content = Ui.Stack(Ui.Heading("Second retained destination"),
            Ui.Note("This instance stays alive when a different tab or flyout entry is selected."),
            Ui.Button("Toggle guard", vm, () => { vm.AllowNavigation = !vm.AllowNavigation; vm.Log.Write($"GUARD allow={vm.AllowNavigation}"); return Task.CompletedTask; }),
            Ui.Home(vm), Ui.Trace(vm.Log)) };
    }
}

internal sealed class StandardTabsPage(StandardTabsViewModel vm) : LegacyTabbedViewBase<StandardTabsViewModel>(vm)
{
    protected override Task DisplayToast(ToastEventArgs args) => Ui.Toast(args);
    protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Ui.CustomAction(args, ViewModel);
    protected override bool NotifyLanguageChange() { ViewModel.Log.Write("LANGUAGE standard tabs"); return true; }
    protected override IDisposable ShowLoading(LoadingType loadingType) => Ui.Loading(ViewModel, value => ViewModel.IsBusy = value);
    protected override void HideLoading() => ViewModel.IsBusy = false;
}

internal sealed class FlyoutPage(FlyoutViewModel vm) : LegacyFlyoutViewBase<FlyoutViewModel>(vm)
{
    protected override Task DisplayToast(ToastEventArgs args) => GetToast(args.Message ?? args.Id, args.ToastType).Show();
    protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Ui.CustomAction(args, ViewModel);
    protected override bool NotifyLanguageChange() { ViewModel.Log.Write("LANGUAGE flyout"); return true; }
    protected override IDisposable ShowLoading(LoadingType loadingType) => Ui.Loading(ViewModel, value => ViewModel.IsBusy = value);
    protected override void HideLoading() => ViewModel.IsBusy = false;
}

internal sealed class MenuPage : FlyoutViewFlyoutBase<MenuViewModel>
{
    public MenuPage(MenuViewModel vm) : base(vm) { Title = "Scenarios"; }
    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName != nameof(MenuItems) || MenuItems == null) return;
        var menu = Ui.Stack(Ui.Heading("Retained flyout"));
        foreach (var item in MenuItems)
        {
            var button = Ui.Button(item.Title, ViewModel, () => SelectMenuItemById(item.Id, parameters: null));
            button.BindingContext = item;
            button.SetBinding(Button.TextProperty, static (FlyoutMenuItem source) => source.Title);
            menu.Children.Add(button);
        }
        menu.Children.Add(Ui.Button("Update first title and icons", ViewModel, () =>
        {
            MenuItems[0].Title = "Updated title";
            MenuItems[0].SelectedIcon = "✓";
            MenuItems[0].UnselectedIcon = "+";
            ViewModel.Log.Write("FLYOUT metadata updated");
            return Task.CompletedTask;
        }));
        menu.Children.Add(Ui.Home(ViewModel));
        Content = new ScrollView { Content = menu };
    }
}
