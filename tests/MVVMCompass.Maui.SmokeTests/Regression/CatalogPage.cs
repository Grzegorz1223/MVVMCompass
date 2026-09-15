namespace MVVMCompass.Sample;

internal sealed class CatalogPage : PlaygroundPage<CatalogViewModel>
{
    public CatalogPage(CatalogViewModel vm, NavigationOptions options, RegressionSmokeRunner smoke, MauiNavigationHostFactory hosts) : base(vm)
    {
        Title = "Navigation playground";
        var profile = new Picker { Title = "Retained lifecycle profile", ItemsSource = new[] { "Deactivate", "Legacy callbacks" }, SelectedIndex = (int)options.RetainedViewLifecycleBehavior };
        profile.SelectedIndexChanged += (_, _) =>
        {
            options.RetainedViewLifecycleBehavior = (RetainedViewLifecycleBehavior)profile.SelectedIndex;
            vm.Log.Write($"PROFILE for new scenarios: {options.RetainedViewLifecycleBehavior}");
        };
        var scopes = new Switch { IsToggled = options.UseEntryScopes };
        scopes.Toggled += (_, args) => { options.UseEntryScopes = args.Value; vm.Log.Write($"ENTRY SCOPES for new destinations: {args.Value}"); };
        Content = new ScrollView { Content = Ui.Stack(
            Ui.Heading("Explore MVVMCompass"),
            Ui.Note("Custom navigation: a persistent toolbar with configurable center content and right-side buttons. Each scenario includes editable input, guard controls, details, modals, overlays, and a lifecycle trace."),
            Ui.Button("Custom · Ordinary navigation and toolbar", vm, () => ContentNavigationSamples.OpenAsync(hosts.ForWindow(Window!), vm.Log, NavigationPresentation.Plain)),
            Ui.Button("Custom · Tabs with separate histories", vm, () => ContentNavigationSamples.OpenAsync(hosts.ForWindow(Window!), vm.Log, NavigationPresentation.Tabs)),
            Ui.Button("Custom · Vertical destination rail", vm, () => ContentNavigationSamples.OpenAsync(hosts.ForWindow(Window!), vm.Log, NavigationPresentation.Rail)),
            Ui.Button("Custom · Flyout and automatic Menu / Back", vm, () => ContentNavigationSamples.OpenAsync(hosts.ForWindow(Window!), vm.Log, NavigationPresentation.Flyout)),
            Ui.Button("Custom · Flyout containing tabs", vm, () => ContentNavigationSamples.OpenAsync(hosts.ForWindow(Window!), vm.Log, NavigationPresentation.Flyout, true)),
            Ui.Button("Custom · Standalone toolbar in a regular view", vm, () => ContentNavigationSamples.OpenStandaloneAsync(hosts.ForWindow(Window!), vm.Log)),
            Ui.Button("Custom · Independent window", vm, () => ContentNavigationSamples.OpenWindowAsync(hosts, vm.Log)),
            Ui.Button("Run custom navigation smoke scenarios", vm, async () =>
            {
                var host = hosts.ForWindow(Window!);
                await ContentNavigationSamples.RunSmokeAsync(host, vm.Log);
                await ContentNavigationCoverage.RunInteractiveAsync(host, vm.Log);
            }),
            Ui.Note("Choose a lifecycle profile, open a navigation scenario, then inspect its lifecycle trace."),
            profile,
            Ui.Note("Own a DI scope for each new registered destination (existing destinations retain their original ownership):"),
            scopes,
            Ui.Button("Coordination, ordinary models and ownership workbench", vm,
                () => new WorkbenchSession(hosts.ForWindow(Window ?? throw new InvalidOperationException("The catalog has no window.")), vm.Log, options).NavigateAsync("root")),
            Ui.Button("Run native smoke scenarios", vm, () => smoke.RunAsync()),
            Ui.Button("C01–06 · Pages, parameters, stack and modals", vm, () => vm.Navigation.NavigateTo<DetailViewModel>(new() { ["caption"] = "First destination", ["instance"] = "A" })),
            Ui.Button("C03 · Late module registration", vm, () => vm.Navigation.NavigateTo<LateViewModel>()),
            Ui.Button("C10–21 · Custom tabs and retained lifetime", vm, () => vm.Navigation.PresentAsNavigableMainPage<CustomTabsViewModel>()),
            Ui.Button("C14 · Standard MAUI tabs", vm, () => vm.Navigation.PresentAsMainPage<StandardTabsViewModel>()),
            Ui.Button("C22–23 · Flyout and retained ownership", vm, () => vm.Navigation.PresentAsMainPage<FlyoutViewModel>()),
            Ui.Button("C07–09 · Popup result / outside dismissal", vm, async () => vm.Log.Write($"POPUP result={await vm.Navigation.DisplayPopupWithResult<PopupViewModel, string>(vm) ?? "<null>"}")),
            Ui.Button("C07 · Popup without a result", vm, () => vm.Navigation.DisplayPopup<PopupViewModel>(vm)),
            Ui.Button("C24–25 · Secondary window", vm, () => vm.Navigation.OpenNewWindow<WindowViewModel>(new() { ["caption"] = "Secondary window" }, windowClosed: () => vm.Log.Write("WINDOW close callback"), isResizable: true)),
            Ui.Note("Window geometry and multiple windows require a supporting device. Native smoke also exercises coordinated page stacks and nested modals."),
            Ui.Button("C27 · Short toast", vm, () => vm.ToastAsync(ToastType.Short)),
            Ui.Button("C27 · Long toast", vm, () => vm.ToastAsync(ToastType.Long)),
            Ui.Button("C28 · All notification categories", vm, () =>
            {
                foreach (var category in new[] { ToastType.Default, ToastType.Reminder, ToastType.Alarm, ToastType.IncomingCall, ToastType.Urgent }) SendNotification("Host-provided notification example", category);
                return Task.CompletedTask;
            }),
            Ui.Button("C29 · Loading and disposal", vm, vm.LoadingAsync),
            Ui.LoadingIndicator(vm),
            Ui.Button("C29 · Refresh localization", vm, () => { vm.RefreshLanguage(); return Task.CompletedTask; }),
            Ui.Button("C29 · Await custom action result", vm, async () => vm.Log.Write($"ACTION returned: {await vm.RequestActionAsync()}")),
            Ui.Button("C30 · Command failure / busy recovery", vm, () => Ui.CommandFailure(vm)),
            Ui.Button("C04 · Test root-initialization failure", vm, () => vm.Navigation.PresentAsMainPage<FailingViewModel>()),
            Ui.Note("Failed preparation leaves this catalog live and usable. The trace records cleanup of the abandoned candidate."),
            Ui.Button("C04 · Test activation failure and recovery", vm, () => vm.Navigation.PresentAsNavigableMainPage<CustomTabsViewModel>(new() { ["failActivation"] = true })),
            Ui.Note("Activation failure occurs after outgoing teardown. The prepared tabs remain installed; use Return to scenarios to create a fresh root and release their subscriptions."),
            Ui.Home(vm),
            Ui.Button("C25 · Query active view model", vm, () => { vm.Log.Write($"ACTIVE {vm.Navigation.GetActiveViewModelType()?.Name}"); return Task.CompletedTask; }),
            Ui.Trace(vm.Log)) };
    }
}
