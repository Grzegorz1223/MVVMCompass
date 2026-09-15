using System.Collections.ObjectModel;
using System.Windows.Input;
using MVVMCompass.Core;

namespace MVVMCompass.Sample;

internal static class ContentNavigationSamples
{
    internal static async Task OpenAsync(MauiNavigationHost host, ScenarioLog log, NavigationPresentation presentation, bool nested = false)
    {
        var outcome = await host.ReplaceContentRootAsync(new(Definition(host, log, presentation, nested)), view => Configure(view, host, log));
        Log(outcome, log, "Open custom navigation");
    }

    internal static NavigationDefinition Definition(MauiNavigationHost host, ScenarioLog log, NavigationPresentation presentation, bool nested = false)
    {
        NavigationDestination Screen(string id, string title) => new(id, title, Factory(host, log, title, 0));
        NavigationDestination[] destinations = presentation == NavigationPresentation.Plain
            ? [Screen("home", "Ordinary navigation")]
            : nested
                ? [new("workspace", "Workspace", new NavigationDestination[] { Screen("notes", "Notes"), Screen("tasks", "Tasks") }), Screen("settings", "Settings")]
                : [Screen("notes", "Notes"), Screen("tasks", "Tasks"), Screen("settings", "Settings")];
        return new(destinations, presentation);
    }

    internal static ScreenFactory Factory(MauiNavigationHost host, ScenarioLog log, string title, int depth) =>
        ScreenFactory.Create(() => new ContentLabModel(title, depth, log), model => new ContentLabScreen(model, host, log),
            new() { ["caption"] = title });

    internal static void Configure(NavigationView view, MauiNavigationHost host, ScenarioLog log)
    {
        view.AutomationId = "CustomNavigation";
        view.Toolbar.AutomationId = "CustomToolbar";
        view.Presenter.AutomationId = "CustomContent";
        view.BackgroundColor = Colors.White;
        view.Toolbar.BackgroundColor = Color.FromArgb("#173A5E");
        view.Toolbar.ForegroundColor = Colors.White;
        view.IsBackSwipeEnabled = true;
        view.ToolbarDefaults = new()
        {
            CenterContent = new Label { Text = "Navigation lab", FontSize = 18, FontAttributes = FontAttributes.Bold,
                TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center },
            RightItems = new()
            {
                new() { Text = "Home", AccessibilityLabel = "Return to scenario catalog", Command = new Command(async () =>
                    Log(await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true), log, "Return to catalog")) }
            }
        };
        view.HeaderContent = new Label { Text = "The toolbar stays here while the content below changes.", Padding = new Thickness(14, 8),
            BackgroundColor = Color.FromArgb("#EAF2F8"), TextColor = Color.FromArgb("#173A5E"), FontSize = 13 };
        view.FooterContent = new Label { Text = "Try Back, switch destinations, then return to check retained input.",
            Padding = new Thickness(14, 8), TextColor = Color.FromArgb("#475569"), FontSize = 12 };
    }

    internal static void Log<T>(NavigationOutcome<T> result, ScenarioLog log, string operation)
    {
        log.Write($"CUSTOM {operation}: {result.Status}, committed={result.HasCommitted}");
        if (result.Error != null) log.Write($"CUSTOM error: {result.Error.Message}");
    }

    internal static async Task OpenWindowAsync(MauiNavigationHostFactory hosts, ScenarioLog log)
    {
        var window = new Window(new ContentPage { Content = new Label { Text = "Opening custom navigation…" } }) { Title = "Custom navigation window" };
        var host = hosts.ForWindow(window);
        Application.Current!.OpenWindow(window);
        await OpenAsync(host, log, NavigationPresentation.Tabs);
    }

    internal static async Task OpenStandaloneAsync(MauiNavigationHost host, ScenarioLog log)
    {
        var result = new Label { Text = "Tap an action in the standalone toolbar.", Margin = 16 };
        var count = 0;
        var toolbar = new NavigationToolbar
        {
            BackgroundColor = Color.FromArgb("#173A5E"), ForegroundColor = Colors.White,
            Definition = new()
            {
                CenterContent = new Entry { Placeholder = "Custom center", Text = "Standalone", TextColor = Colors.White },
                RightItems = new() { new() { Text = "+", AccessibilityLabel = "Increment counter",
                    Command = new Command(() => result.Text = $"Standalone action #{++count}") } }
            }
        };
        var body = Ui.Stack(Ui.Heading("A reusable toolbar"),
            Ui.Note("This toolbar is inside an ordinary ContentView. It uses bindable content and commands without a navigation scope."),
            new ContentView { Content = toolbar }, result,
            Ui.Button("Return to scenario catalog", log, async () => Log(
                await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true), log, "Return to catalog")));
        Log(await host.ReplaceRootAsync(new NavigationRequestOptions(), () => new object(), _ => new ContentPage { Content = body }), log, "Standalone toolbar");
    }

    internal static async Task<IReadOnlyList<string>> RunSmokeAsync(MauiNavigationHost host, ScenarioLog log)
    {
        AssertNoNativeModal(host.Window);
        List<string> passed = [];
        foreach (var mode in new[] { NavigationPresentation.Plain, NavigationPresentation.Tabs, NavigationPresentation.Rail, NavigationPresentation.Flyout })
        {
            await OpenAsync(host, log, mode);
            var context = host.CurrentContentNavigation ?? throw new InvalidOperationException("Custom scope was not installed.");
            await Task.Delay(150);
            var toolbar = context.View.Toolbar;
            var bounds = toolbar.Bounds;
            var root = context.Current!;
            Require((await context.PushAsync(Factory(host, log, "Detail", 1))).IsSuccess, "push");
            var detail = (ContentLabModel)context.Current!.ViewModel;
            detail.AllowNavigation = false;
            Require((await context.BackAsync()).Status == NavigationStatus.GuardRejected, "guard denial");
            Require(ReferenceEquals(context.Current.ViewModel, detail), "guard preserves current body");
            await PlatformBackAsync(context);
            Require(ReferenceEquals(context.Current.ViewModel, detail), "platform Back respects guard denial");
            detail.AllowNavigation = true;
            if (mode != NavigationPresentation.Plain)
            {
                var detailEntry = context.Current;
                Require((await context.SelectAsync("tasks")).IsSuccess, "select another destination");
                Require((await context.SelectAsync("notes")).IsSuccess && ReferenceEquals(context.Current, detailEntry), "retained history");
            }
            await PlatformBackAsync(context);
            Require(ReferenceEquals(context.Current, root), "platform Back returns to retained root");
            Require(ReferenceEquals(toolbar, context.View.Toolbar), "toolbar identity");
            Require(bounds == toolbar.Bounds, "toolbar bounds");
            Require(detail.IsDismissed, "detail cleanup");
            passed.Add($"Custom {mode}: toolbar, guard, retained body and cleanup");
        }
        await OpenAsync(host, log, NavigationPresentation.Flyout, true);
        var nested = host.CurrentContentNavigation!;
        var detailPage = ((NavigationFlyoutPage)host.Window.Page!).Detail;
        Require((await nested.SelectAsync("tasks")).IsSuccess, "nested tab selection");
        Require((await nested.SelectAsync("settings")).IsSuccess, "nested flyout selection");
        Require((await nested.SelectAsync("workspace")).IsSuccess && nested.SelectedDestinationId == "tasks", "restore nested tab");
        Require(ReferenceEquals(detailPage, ((NavigationFlyoutPage)host.Window.Page!).Detail), "persistent flyout detail");
        var modalResult = await nested.OpenModalAsync(Definition(host, log, NavigationPresentation.Plain), view => Configure(view, host, log));
        Require(modalResult.IsSuccess, "custom modal");
        var modal = modalResult.Value!;
        Require(modal.LeadingAction == NavigationLeadingAction.Close, "modal close button");
        Require((await modal.PushAsync(Factory(host, log, "Modal detail", 1))).IsSuccess, "modal detail");
        Require((await modal.BackAsync()).IsSuccess && modal.LeadingAction == NavigationLeadingAction.Close, "modal detail back");
        ((ContentLabModel)modal.Current!.ViewModel).AllowNavigation = false;
        await host.Window.Navigation.PopModalAsync(false);
        await modal.NativeBackCompletion;
        Require(modal.IsActive, "native modal pop respects guard denial");
        ((ContentLabModel)modal.Current!.ViewModel).AllowNavigation = true;
        Require((await modal.CloseModalAsync()).IsSuccess && nested.IsActive, "parent reactivation");
        passed.Add("Custom nested flyout/tabs and modal navigation");
        Require((await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true)).IsSuccess, "catalog recovery");
        AssertNoNativeModal(host.Window);
        foreach (var item in passed) log.Write($"PASS {item}");
        return passed;

        static void Require(bool condition, string label)
        { if (!condition) throw new InvalidOperationException($"Custom navigation smoke failed: {label}"); }
    }

    private static async Task PlatformBackAsync(NavigationContext context)
    {
#if ANDROID
        var activity = (AndroidX.Activity.ComponentActivity)context.Window.Handler!.PlatformView!;
        activity.OnBackPressedDispatcher.OnBackPressed();
#else
        (context.Window.Navigation.ModalStack.LastOrDefault() ?? context.Window.Page)!.SendBackButtonPressed();
#endif
        await context.NativeBackCompletion;
    }

    internal static void AssertNoNativeModal(Window window)
    {
#if ANDROID
        var activity = (AndroidX.Fragment.App.FragmentActivity)window.Handler!.PlatformView!;
        var remaining = activity.SupportFragmentManager.Fragments.OfType<AndroidX.Fragment.App.DialogFragment>()
            .Where(fragment => fragment.Dialog?.IsShowing == true).Select(fragment => fragment.Tag).ToArray();
        if (remaining.Length != 0) throw new InvalidOperationException($"Native modal fragments remain after navigation: {string.Join(", ", remaining)}");
#endif
    }
}

internal sealed class ContentLabModel(string caption, int depth, ScenarioLog log) : ViewModelBase
{
    private bool allowNavigation = true;
    private bool actionsEnabled = true;
    private bool extraActionVisible = true;
    private string title = caption;
    private string input = string.Empty;
    private string status = "No toolbar action yet.";
    private int counter;
    internal int Depth { get; } = depth;
    internal int GuardDelay { get; set; }
    internal bool FailNextGuard { get; set; }
    public bool AllowNavigation { get => allowNavigation; set => SetProperty(ref allowNavigation, value); }
    public bool ActionsEnabled { get => actionsEnabled; set => SetProperty(ref actionsEnabled, value); }
    public bool ExtraActionVisible { get => extraActionVisible; set => SetProperty(ref extraActionVisible, value); }
    public string Title { get => title; set => SetProperty(ref title, value); }
    public string Input { get => input; set => SetProperty(ref input, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    private ICommand? incrementCommand;
    public ICommand IncrementCommand => incrementCommand ??= new Command(() => Status = $"Toolbar action #{++counter} on {Title}");
    public override Task GetParameters(Dictionary<string, object> parameters)
    { if (parameters.TryGetValue("caption", out var value)) Title = (string)value; return Task.CompletedTask; }
    public override async Task<bool> CanNavigate()
    {
        log.Write($"CUSTOM {Title}: CanNavigate waiting {GuardDelay} ms");
        if (GuardDelay != 0) await Task.Delay(GuardDelay, LifetimeToken);
        if (FailNextGuard) { FailNextGuard = false; throw new InvalidOperationException("Deliberate guard failure; the current screen should remain usable."); }
        log.Write($"CUSTOM {Title}: CanNavigate = {AllowNavigation}");
        return AllowNavigation;
    }
    public override Task Appearing() { log.Write($"CUSTOM {Title}: activated"); return Task.CompletedTask; }
    public override Task Deactivated() { log.Write($"CUSTOM {Title}: retained/inactive"); return Task.CompletedTask; }
    public override Task AfterDismissed() { log.Write($"CUSTOM {Title}: permanently dismissed"); return Task.CompletedTask; }
}

internal sealed class ContentLabScreen : ViewBase<ContentLabModel>
{
    private readonly MauiNavigationHost host;
    private readonly ScenarioLog log;
    private bool emptyActions;
    private int toolbarMode;

    internal ContentLabScreen(ContentLabModel model, MauiNavigationHost host, ScenarioLog log) : base(model)
    {
        this.host = host; this.log = log;
        var allow = new Switch(); allow.SetBinding(Switch.IsToggledProperty, Binding.Create(static (ContentLabModel model) => model.AllowNavigation, BindingMode.TwoWay));
        var enabled = new Switch(); enabled.SetBinding(Switch.IsToggledProperty, Binding.Create(static (ContentLabModel model) => model.ActionsEnabled, BindingMode.TwoWay));
        var visible = new Switch(); visible.SetBinding(Switch.IsToggledProperty, Binding.Create(static (ContentLabModel model) => model.ExtraActionVisible, BindingMode.TwoWay));
        SemanticProperties.SetDescription(allow, "Allow navigation");
        SemanticProperties.SetDescription(enabled, "Toolbar buttons enabled");
        SemanticProperties.SetDescription(visible, "Extra toolbar button visible");
        var input = new Entry { Placeholder = "Type here, navigate away, and return" };
        input.SetBinding(Entry.TextProperty, Binding.Create(static (ContentLabModel model) => model.Input, BindingMode.TwoWay));
        var title = new Entry { Placeholder = "Screen title" };
        title.SetBinding(Entry.TextProperty, Binding.Create(static (ContentLabModel model) => model.Title, BindingMode.TwoWay));
        var status = new Label { TextColor = Color.FromArgb("#173A5E") };
        status.SetBinding(Label.TextProperty, Binding.Create(static (ContentLabModel model) => model.Status));
        var mode = new Picker { Title = "Toolbar center", ItemsSource = new[] { "Host default", "Screen title", "Custom center layout", "Search field" }, SelectedIndex = model.Depth == 0 ? 0 : 1 };
        toolbarMode = mode.SelectedIndex;
        mode.SelectedIndexChanged += (_, _) => { toolbarMode = mode.SelectedIndex; UpdateToolbar(); };
        var delay = new Picker { Title = "Guard delay", ItemsSource = new[] { "Immediate", "500 ms", "2 seconds" }, SelectedIndex = 0 };
        delay.SelectedIndexChanged += (_, _) => model.GuardDelay = delay.SelectedIndex switch { 1 => 500, 2 => 2000, _ => 0 };
        var empty = new Switch(); empty.Toggled += (_, args) => { emptyActions = args.Value; UpdateToolbar(); };
        var busy = new Switch(); busy.Toggled += (_, args) => Navigator.Context.View.IsBusy = args.Value;
        var templates = new Switch(); templates.Toggled += (_, args) =>
        {
            var view = Navigator.Context.View;
            view.Toolbar.LeadingButtonTemplate = args.Value ? new DataTemplate(() =>
            {
                var button = new Button { BackgroundColor = Colors.Transparent, TextColor = Colors.White, Padding = 0, FontSize = 12 };
                button.SetBinding(Button.TextProperty, Binding.Create(static (NavigationToolbar toolbar) => toolbar.LeadingText));
                button.SetBinding(Button.CommandProperty, Binding.Create(static (NavigationToolbar toolbar) => toolbar.LeadingCommand));
                return button;
            }) : null;
            view.ToolbarDefaults.RightItemTemplate = args.Value ? new DataTemplate(() =>
            {
                var button = new Button { CornerRadius = 22, BackgroundColor = Colors.LightBlue, TextColor = Color.FromArgb("#173A5E"), Padding = 8 };
                button.SetBinding(Button.TextProperty, Binding.Create(static (ToolbarButton item) => item.Text));
                button.SetBinding(Button.CommandProperty, Binding.Create(static (ToolbarButton item) => item.Command));
                button.SetBinding(Button.CommandParameterProperty, Binding.Create(static (ToolbarButton item) => item.CommandParameter));
                button.SetBinding(IsEnabledProperty, Binding.Create(static (ToolbarButton item) => item.IsEnabled));
                button.SetBinding(SemanticProperties.DescriptionProperty, Binding.Create(static (ToolbarButton item) => item.AccessibilityLabel));
                return button;
            }) : null;
        };
        var hideToolbar = new Switch(); hideToolbar.Toggled += (_, args) => Navigator.Context.View.ToolbarDefaults.IsVisible = !args.Value;
        var heading = Ui.Heading(model.Depth == 0 ? model.Title : $"Detail level {model.Depth}");
        Content = new ScrollView
        {
            Content = Ui.Stack(heading,
                Ui.Note("Change the toolbar, type some input, and push a detail. Back and destination switches consult CanNavigate. The toolbar frame stays in place."),
                input, title, mode,
                Ui.Note("Allow navigation"), allow, Ui.Note("Guard delay"), delay,
                Ui.Button("Push next detail", log, async () => ContentNavigationSamples.Log(await Navigator.PushAsync(
                    ContentNavigationSamples.Factory(host, log, $"Detail {model.Depth + 1}", model.Depth + 1)), log, "Push")),
                Ui.Button("Back", log, async () => ContentNavigationSamples.Log(await Navigator.BackAsync(), log, "Back")),
                Ui.Button("Reset this destination to its root", log, async () => ContentNavigationSamples.Log(await Navigator.PopToRootAsync(), log, "Reset stack")),
                Ui.Button("Recreate this destination with a fresh root", log, async () => ContentNavigationSamples.Log(
                    await Navigator.ResetDestinationAsync(Navigator.Context.SelectedDestinationId!), log, "Recreate destination")),
                Ui.Button("Select the default destination", log, async () => ContentNavigationSamples.Log(await Navigator.SelectDefaultAsync(), log, "Select default")),
                Ui.Button("Remove this destination", log, async () => ContentNavigationSamples.Log(
                    await Navigator.RemoveDestinationAsync(Navigator.Context.SelectedDestinationId!), log, "Remove destination")),
                Ui.Note("Removal selects the first surviving destination. The last destination stays registered. Reopen the scenario to restore its full menu."),
                Ui.Button("Toggle Settings selector visibility", log, () =>
                {
                    var settings = Navigator.Context.Destinations.FirstOrDefault(item => item.Id == "settings");
                    if (settings != null) settings.IsVisible = !settings.IsVisible;
                    return Task.CompletedTask;
                }),
                Ui.Button("Fail the next guard", log, () => { model.FailNextGuard = true; log.Write("CUSTOM next navigation will report a guard failure"); return Task.CompletedTask; }),
                Ui.Note("Right-side buttons enabled"), enabled, Ui.Note("Extra right-side button visible"), visible,
                Ui.Note("Use an explicitly empty right-side button collection"), empty, status,
                Ui.Note("Use custom leading-button and right-item templates"), templates,
                Ui.Note("Hide the toolbar (the Back button in the body remains available)"), hideToolbar,
                Ui.Button("Add a toolbar button", log, () =>
                {
                    Toolbar ??= new(); Toolbar.RightItems ??= new();
                    Toolbar.RightItems.Add(new() { Text = "+", AccessibilityLabel = "Increment screen counter", Command = model.IncrementCommand });
                    return Task.CompletedTask;
                }),
                Ui.Note("Show shared busy indicator (navigation still uses CanNavigate)"), busy,
                Ui.Button("Show overlay over content only", log, () => { ShowOverlay(false); return Task.CompletedTask; }),
                Ui.Button("Show overlay over toolbar and content", log, () => { ShowOverlay(true); return Task.CompletedTask; }),
                Ui.Button("Open a custom modal", log, async () => ContentNavigationSamples.Log(await Navigator.OpenModalAsync(
                    ContentNavigationSamples.Definition(host, log, NavigationPresentation.Plain), view => ContentNavigationSamples.Configure(view, host, log)), log, "Open modal")),
                Ui.Button("Show a confirmation on the owning page", log, async () =>
                    log.Write($"CUSTOM dialog result={await Navigator.DisplayAlertAsync("Confirmation", "This dialog belongs to the active custom screen.", "OK", "Cancel")}")),
                Ui.Button("Return to scenario catalog", log, async () => ContentNavigationSamples.Log(
                    await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true), log, "Return to catalog")),
                Ui.Note("On a detail, try the platform Back button or swipe right across the body. Turn Allow navigation off to verify both are rejected. Narrow the window to test the scrollable action area."),
                Ui.Trace(log))
        };
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        if (toolbarMode == 0 && !emptyActions) { Toolbar = null; return; }
        var definition = new NavigationToolbarDefinition();
        if (toolbarMode == 1) definition.SetBinding(NavigationToolbarDefinition.TitleProperty, Binding.Create(static (ContentLabModel model) => model.Title));
        else if (toolbarMode == 2)
        {
            var label = new Label { TextColor = Colors.White, FontSize = 15, HorizontalTextAlignment = TextAlignment.Center };
            label.SetBinding(Label.TextProperty, Binding.Create(static (ContentLabModel model) => model.Title));
            definition.CenterContent = new VerticalStackLayout
            { Spacing = 0, VerticalOptions = LayoutOptions.Center, Children = { label, new Label { Text = "CUSTOM CENTER", TextColor = Colors.LightBlue, FontSize = 9, HorizontalTextAlignment = TextAlignment.Center } } };
        }
        else if (toolbarMode == 3) definition.CenterContent = new SearchBar { Placeholder = "Search…", BackgroundColor = Colors.White, HeightRequest = 42 };
        definition.RightItems = new ObservableCollection<ToolbarButton>();
        if (!emptyActions)
        {
            var first = new ToolbarButton { Text = "+", AccessibilityLabel = "Increment screen counter" };
            first.SetBinding(ToolbarButton.CommandProperty, Binding.Create(static (ContentLabModel model) => model.IncrementCommand));
            first.SetBinding(ToolbarButton.IsEnabledProperty, Binding.Create(static (ContentLabModel model) => model.ActionsEnabled));
            var second = new ToolbarButton { Text = "●", AccessibilityLabel = "Example status action" };
            second.SetBinding(ToolbarButton.CommandProperty, Binding.Create(static (ContentLabModel model) => model.IncrementCommand));
            second.SetBinding(ToolbarButton.IsVisibleProperty, Binding.Create(static (ContentLabModel model) => model.ExtraActionVisible));
            definition.RightItems.Add(first); definition.RightItems.Add(second);
        }
        Toolbar = definition;
    }
    private void ShowOverlay(bool full)
    {
        var view = Navigator.Context.View;
        var content = new Border { BackgroundColor = Color.FromArgb("#E6EFF6FF"), Padding = 24,
            Content = Ui.Stack(Ui.Heading(full ? "Whole-layout overlay" : "Content overlay"),
                Ui.Note(full ? "This overlay also covers the toolbar." : "The toolbar remains visible and available above this overlay."),
                Ui.Button("Dismiss overlay", log, () => { if (full) view.OverlayContent = null; else view.BodyOverlayContent = null; return Task.CompletedTask; })) };
        if (full) view.OverlayContent = content; else view.BodyOverlayContent = content;
    }
}
