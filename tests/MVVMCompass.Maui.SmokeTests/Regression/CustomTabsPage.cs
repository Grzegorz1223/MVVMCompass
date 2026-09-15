using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

internal sealed class CustomTabsPage : CustomTabbedViewBase<CustomTabsViewModel>, IHandledPageModalHost
{
    private int layout;
    private readonly View fullOverlay;
    private readonly View contentOverlay;
    public int ModalDepth { get; private set; }
    public int ModalOpenings { get; private set; }
    public int ModalClosings { get; private set; }

    public CustomTabsPage(CustomTabsViewModel vm) : base(vm)
    {
        Title = "Custom tabs";
        SelectedTabTextColor = Colors.White;
        SelectedTabBackgroundColor = Color.FromArgb("274E77");
        UnselectedTabTextColor = Color.FromArgb("274E77");
        UnselectedTabBackgroundColor = Color.FromArgb("E5EDF5");
        TabBarBackground = new SolidColorBrush(Color.FromArgb("F4F7FB"));
        TabBarRowBackground = new SolidColorBrush(Color.FromArgb("F4F7FB"));
        BusyOverlayColor = Color.FromArgb("274E77");
        BusyOverlayBackgroundColor = Color.FromArgb("CDE5EDF5");
        BusyOverlayDescription = "Tab content is loading. Navigation controls remain available.";
        SharedContent = new Label { Text = "Shared content · duplicate VM types, one hidden tab", Margin = 12 };
        TabBarTrailingContent = Ui.Button("Hidden", vm, async () => { await SwitchToAsync(2); });
        var controls = new HorizontalStackLayout { Spacing = 8, Padding = 8 };
        controls.Children.Add(Ui.Home(vm));
        controls.Children.Add(Ui.Button("Layouts", vm, () => { CycleLayout(); return Task.CompletedTask; }));
        controls.Children.Add(Ui.Button("Disable / enable tabs", vm, () => { IsTabBarEnabled = !IsTabBarEnabled; return Task.CompletedTask; }));
        controls.Children.Add(Ui.Button("Busy overlay", vm, () => { ActivityIndicatorIsRunning = !ActivityIndicatorIsRunning; return Task.CompletedTask; }));
        controls.Children.Add(Ui.Button("Custom templates", vm, () => { ToggleTemplates(); return Task.CompletedTask; }));
        controls.Children.Add(Ui.Button("Replace this root", vm, () => vm.Navigation.PresentAsNavigableMainPage<CustomTabsViewModel>()));
        SetHeaderContent(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = controls });
        SetHeaderContentVisible(true);
        SetLeadingContent(Ui.Stack(
            Ui.Button("1", vm, async () => { await SwitchToAsync(0); }),
            Ui.Button("2", vm, async () => { await SwitchToAsync(1); }),
            Ui.Button("3", vm, async () => { await SwitchToAsync(2); })));
        fullOverlay = Overlay("Full-page overlay", () => { fullOverlay!.IsVisible = false; SetPrimaryContentVisible(true); });
        contentOverlay = Overlay("Content overlay", () => contentOverlay!.IsVisible = false);
        AddOverlay(fullOverlay);
        AddContentOverlay(contentOverlay);
        CurrentTabChanged += (_, _) => vm.Log.Write($"TAB selected={CurrentTab?.ViewModel.GetType().Name}; count={Children.Count}");
    }

    private View Overlay(string title, Action close)
    {
        var view = Ui.Stack(Ui.Heading(title), Ui.Button("Close overlay", ViewModel, () => { close(); return Task.CompletedTask; }));
        view.BackgroundColor = Colors.AliceBlue;
        view.IsVisible = false;
        return view;
    }

    private void CycleLayout()
    {
        layout = (layout + 1) % 6;
        SetLeadingContentVisible(layout is 1 or 2);
        SetLeadingColumnWidth(layout is 1 or 2 ? new GridLength(88) : GridLength.Auto);
        SetTabBarRowVisible(layout != 2);
        SetSharedContentOverride(layout == 3 ? new Label { Text = "Temporary shared-content override", Margin = 12 } : null);
        fullOverlay.IsVisible = layout == 4;
        SetPrimaryContentVisible(layout != 4);
        contentOverlay.IsVisible = layout == 5;
        ViewModel.Log.Write($"LAYOUT {layout}: rail / fixed width / visibility / shared override / overlays");
    }

    private void ToggleTemplates()
    {
        if (SelectedTabItemTemplate != null)
        {
            SelectedTabItemTemplate = UnselectedTabItemTemplate = null;
            return;
        }
        SelectedTabItemTemplate = Template(Colors.White, Color.FromArgb("274E77"));
        UnselectedTabItemTemplate = Template(Color.FromArgb("274E77"), Colors.LightBlue);
        static DataTemplate Template(Color foreground, Color background) => new(() =>
        {
            var label = new Label { Padding = 14, TextColor = foreground, BackgroundColor = background };
            label.SetBinding(Label.TextProperty, static (TabItemContext source) => source.Title);
            return label;
        });
    }

    public void OnHandledPageModalOpening() { ModalOpenings++; ModalDepth++; ViewModel.Log.Write($"HANDLED MODAL opening; depth={ModalDepth}"); }
    public void OnHandledPageModalClosed() { ModalClosings++; ModalDepth--; ViewModel.Log.Write($"HANDLED MODAL closed; depth={ModalDepth}"); }
    protected override Task DisplayToast(ToastEventArgs args) => Ui.Toast(args);
    protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Ui.CustomAction(args, ViewModel);
    protected override bool NotifyLanguageChange() { ViewModel.Log.Write("LANGUAGE custom tabs"); return true; }
    protected override IDisposable ShowLoading(LoadingType loadingType) => Ui.Loading(ViewModel, value => ActivityIndicatorIsRunning = value);
    protected override void HideLoading() => ActivityIndicatorIsRunning = false;
}
