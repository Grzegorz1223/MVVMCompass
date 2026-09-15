# MVVMCompass

**Build true MVVM apps by navigating through ViewModels.**

Your ViewModels decide where users can go. XAML defines how the app looks. MVVMCompass creates the Views and handles navigation, selection, and lifetimes.

```text
ViewModel commands ── NavigateTo<DetailsViewModel>() ──┐
                                                     ▼
User presses a tab or flyout item ────────────► MVVMCompass
                                                     │
                                                     ▼
                                          Target ViewModel + View
```

**.NET 10 · .NET MAUI · Initial release: 1.0.0 · [MIT](https://github.com/Grzegorz1223/MVVMCompass/blob/main/LICENSE)**

Download both `.nupkg` files from [GitHub Releases](https://github.com/Grzegorz1223/MVVMCompass/releases) into a local directory. NuGet.org publication is pending account setup. Add that directory as a package source before installing:

```sh
dotnet nuget add source /path/to/mvvmcompass-packages --name MVVMCompassRelease
```

Keep NuGet.org enabled for the MAUI dependencies.

| Package | Purpose |
| --- | --- |
| **MVVMCompass.Maui** | Navigation through ViewModels, tabs, flyouts, toolbars, modals, popups, and windows. Includes Core. |
| **MVVMCompass.Core** | Portable navigation coordination, lifetimes, and ownership without a MAUI dependency. Install `MVVMCompass.Core` for Core-only applications. |

Install in your .NET 10 MAUI app:

```sh
dotnet add package MVVMCompass.Maui --version 1.0.0
```

[ViewModel navigation](#navigate-from-a-viewmodel) · [Tabs and flyouts](#tabs-rails-and-flyouts) · [Custom toolbar](#a-toolbar-that-stays-in-place) · [Sample app](#try-the-sample)

| ViewModel decides | XAML defines | Library handles |
| --- | --- | --- |
| Which destinations exist, their IDs, parameters, and availability | Item templates, tab position/sizing, and panel backgrounds | Item presses, selection, and retained histories |
| Commands, data, and `CanNavigate()` | Toolbar content, buttons, shared panels, and overlays | Back/Menu/Close, view creation, and cleanup |

## Navigate from a ViewModel

Inject `INavigationService`. The same call opens a detail within ordinary navigation, a tab, or a flyout destination:

```csharp
using CommunityToolkit.Mvvm.Input;
using MVVMCompass;
using MVVMCompass.Interfaces;

public partial class NotesViewModel(INavigationService navigation) : ViewModelBase
{
    [RelayCommand]
    private Task OpenNoteAsync() =>
        navigation.NavigateTo<NoteDetailsViewModel>(
            new() { ["noteId"] = 42 });
}
```

```xml
<Button Text="Open note" Command="{Binding OpenNoteCommand}" />
```

Register ViewModel/View pairs once in `MauiProgram`. The library creates them through DI:

```csharp
using CommunityToolkit.Maui;
using MVVMCompass;

var builder = MauiApp.CreateBuilder()
    .UseMauiApp<App>()
    .UseMauiCommunityToolkit();

builder.UseMVVMCompass(pairs =>
{
    pairs.Add<AppViewModel, AppView>();
    pairs.Add<WorkspaceViewModel, WorkspaceView>();
    pairs.Add<NotesViewModel, NotesView>();
    pairs.Add<NoteDetailsViewModel, NoteDetailsView>();
    pairs.Add<ReportsViewModel, ReportsView>();
    pairs.Add<StockViewModel, StockView>();
});
```

Views receive their model in the constructor and call `base(viewModel)` and `InitializeComponent()`. Each destination owns a DI scope; its View and ViewModel share that scope until permanent removal. ViewModels never construct Views or supply screen factories.

<details>
<summary>Receive parameters in the destination ViewModel</summary>

Inside `NoteDetailsViewModel`:

```csharp
private int noteId;
public int NoteId { get => noteId; private set => SetProperty(ref noteId, value); }

public override Task GetParameters(Dictionary<string, object> parameters)
{
    NoteId = (int)parameters["noteId"];
    return Task.CompletedTask;
}
```

`GetParameters` runs before `BeforeFirstShown`. Lifecycle overrides include `Appearing`, `Deactivated` for retained destinations, and `AfterDismissed` for permanent cleanup.

</details>

**More ViewModel actions** — register additional destinations as above:

| Action | Example |
| --- | --- |
| Go back | `await navigation.NavigateBack();` |
| Return to the root | `await navigation.NavigateBackToRoot();` |
| Open a modal | `await navigation.NavigateTo<EditorViewModel>();` — the destination model declares `IsModal = true`. |
| Await a popup result | `var answer = await navigation.DisplayPopup<ConfirmViewModel, string?>();` |
| Open another window | `await navigation.OpenNewWindow<DashboardViewModel>();` on platforms supporting multiple windows. |

The injected service belongs to the originating screen/container and window. Toolkit popup visuals use `PopupViewBase<TViewModel, TResult>`; the popup ViewModel calls `navigation.ClosePopup(result)` or `navigation.ClosePopup()`. Check `answer.HasResult` to distinguish an explicit result—including `null`—from dismissal.

### Handle navigation outcomes

Ordinary navigation returns a `NavigationResult`. Check it when the next action depends on reaching the destination:

```csharp
using MVVMCompass.Core;

var result = await navigation.NavigateTo<NoteDetailsViewModel>();
if (result.IsSuccess)
{
    // The requested operation completed.
}
else if (result.Status == NavigationStatus.Failed)
{
    logger.LogError(result.Error, "Navigation failed; committed: {Committed}", result.HasCommitted);
    foreach (var error in result.CleanupErrors)
        logger.LogError(error, "Navigation cleanup failed");
}
```

`GuardRejected`, `Cancelled`, `Superseded`, `Busy`, and unavailable/missing destination statuses represent unsuccessful requests that callers can handle without exceptions. `InvalidOrigin` means the service's screen is inactive, dismissed, or no longer owns the current navigation context. Avoid awaiting new navigation inside its own lifecycle/guard callbacks; this returns `Reentrant`.

`HasCommitted` means the operation crossed its irreversible boundary. A later callback or cleanup can fail after content changed, so do not automatically retry a failed committed operation. Inspect `Error` and `CleanupErrors`; recovery may aggregate several failures.

`DisplayPopup` returns a typed popup result and can throw for invalid origins, preparation/presentation failures, or cancellation. Handle `OperationCanceledException` separately when supplying a cancellation token. `ClosePopup` returns a normal `NavigationResult`, including guard rejection.

## Tabs, rails, and flyouts

**The parent ViewModel builds the destinations.** Permissions, feature flags, modules, and runtime state determine which items exist. Use `SetTabs` and `SetFlyoutItems` on `ViewModelBase`.

For tabs, the same ViewModel type can appear more than once with different IDs and parameters:

```csharp
public sealed class WorkspaceViewModel(IFeatureAccess features) : ViewModelBase
{
    public override async Task BeforeFirstShown()
    {
        var tabs = new List<NavigationItem>
        {
            new(typeof(NotesViewModel), "Notes", id: "notes-open",
                parameters: new() { ["filter"] = "open" }),
            new(typeof(NotesViewModel), "Archive", id: "notes-archive",
                parameters: new() { ["filter"] = "archived" })
        };

        if (await features.CanUseReportsAsync())
            tabs.Add(new(typeof(ReportsViewModel), "Reports", id: "reports"));

        await SetTabs(tabs);
    }
}
```

Flyouts support the same behavior:

```csharp
public sealed class AppViewModel(IFeatureAccess features) : ViewModelBase
{
    public override async Task BeforeFirstShown()
    {
        var items = new List<NavigationItem>
        {
            new(typeof(WorkspaceViewModel), "Workspace", id: "workspace"),
            new(typeof(StockViewModel), "My stock", id: "stock-personal",
                parameters: new() { ["scope"] = "personal" })
        };

        if (await features.CanManageSharedStockAsync())
            items.Add(new(typeof(StockViewModel), "Shared stock", id: "stock-shared",
                parameters: new() { ["scope"] = "shared" }));

        await SetFlyoutItems(items);
    }
}
```

`NavigationItem` is model data: a registered ViewModel type, label, optional ID, and parameters. Feature modules can supply the same data. Use explicit stable IDs for repeated types and programmatic selection; IDs must be unique within a container.

**Each ID owns a separate ViewModel instance, parameters, and retained history**, equally for tabs and flyouts. `SetTabs` / `SetFlyoutItems` apply the collection to its owning container. Later updates preserve surviving histories and guard removals before committing.

Here, `AppViewModel` includes `WorkspaceViewModel`, which supplies its own tabs: **flyout containing tabs**, composed entirely by ViewModels.

```text
ViewModel supplies items → Library renders XAML templates
                                      │
                               User presses an item
                                      │
                                      ▼
                          CanNavigate → switch content

notes-open     Notes → Detail → Editor
notes-archive  Archive → Archived note

Switch to Archive, then back to Notes → Editor is still there.
```

**XAML styles the items.** Use `xmlns:nav="clr-namespace:MVVMCompass;assembly=MVVMCompass"`; `vm` contains application ViewModels. Namespace declarations are omitted below. Resource templates are shared below.

```xml
<nav:TabbedViewBase
    x:Class="ExampleApp.Views.WorkspaceView"
    x:TypeArguments="vm:WorkspaceViewModel"
    x:DataType="vm:WorkspaceViewModel"
    TabBarPosition="Bottom"
    TabItemSizing="Equal"
    TabBarBackground="#EAF2F8"
    SharedContentPosition="Top"
    SelectedTabItemTemplate="{StaticResource SelectedDestinationTemplate}"
    UnselectedTabItemTemplate="{StaticResource UnselectedDestinationTemplate}">

    <nav:TabbedViewBase.TabBarCenterContent>
        <Label Text="My workspace" />
    </nav:TabbedViewBase.TabBarCenterContent>

    <nav:TabbedViewBase.SharedContent>
        <Label Text="Shared workspace controls" />
    </nav:TabbedViewBase.SharedContent>
</nav:TabbedViewBase>
```

Flyouts have the same template-driven customization:

```xml
<nav:FlyoutViewBase
    x:Class="ExampleApp.Views.AppView"
    x:TypeArguments="vm:AppViewModel"
    x:DataType="vm:AppViewModel"
    FlyoutPanelBackground="#F1F5F9"
    SharedContentPosition="Bottom"
    SelectedFlyoutItemTemplate="{StaticResource SelectedDestinationTemplate}"
    UnselectedFlyoutItemTemplate="{StaticResource UnselectedDestinationTemplate}">

    <nav:FlyoutViewBase.FlyoutHeaderContent>
        <Label Text="My application" />
    </nav:FlyoutViewBase.FlyoutHeaderContent>

    <nav:FlyoutViewBase.FlyoutFooterContent>
        <Label Text="Help and support" />
    </nav:FlyoutViewBase.FlyoutFooterContent>

    <nav:FlyoutViewBase.SharedContent>
        <Label Text="Shared application controls" />
    </nav:FlyoutViewBase.SharedContent>
</nav:FlyoutViewBase>
```

Neither XAML file declares destinations or contains selection handlers. Pressing an item runs the library's guarded selection and updates the selected/unselected template. A successful flyout selection also closes its transient drawer.

**Layout stays in XAML:**

| Property | Values / behavior |
| --- | --- |
| `TabBarPosition` | `Top` (default), `Bottom`, `Left`, or `Right`. Left/right create a vertical tab rail. |
| `TabItemSizing` | `Content` (default): each item takes the space its content needs. `Equal`: visible items stretch into equal shares of the available width for top/bottom tabs, or height for left/right tabs. |
| `TabBarBackground` | Background brush for the entire tab row or column, including gaps and unused space. |
| `FlyoutPanelBackground` | Background brush for the flyout menu panel. Item templates can set their own backgrounds. |
| `SharedContent` | Persistent content available in **both tabs and flyouts**, bound to the owning container ViewModel. |
| `SharedContentPosition` | `Top` (default) or `Bottom`, placing shared content above or below the navigating body, independently of tab position. |

`Equal` divides the space remaining after padding, spacing, and reserved tab-bar center/trailing content. Hidden items receive no share. `Content` allows scrolling along the tab bar when items overflow.

Shared content stays mounted while destinations switch or details navigate. In a flyout it belongs to the persistent detail area; `FlyoutHeaderContent` and `FlyoutFooterContent` belong to the menu panel. Changing these layout properties preserves destination histories.

```text
Content: |[Notes][Archive][Reports]                    |
Equal:   |[    Notes    ][   Archive   ][   Reports   ]|

Top / Bottom → divide available width
Left / Right → divide available height
```

<details>
<summary>Selected and unselected item templates</summary>

Add these visual resources to a XAML resource dictionary. The library supplies `NavigationItemContext` with title, icons, selection, and busy state.

```xml
<DataTemplate x:Key="SelectedDestinationTemplate" x:DataType="nav:NavigationItemContext">
    <Border Padding="12,8" BackgroundColor="#173A5E">
        <Label Text="{Binding Title}" TextColor="White" />
    </Border>
</DataTemplate>

<DataTemplate x:Key="UnselectedDestinationTemplate" x:DataType="nav:NavigationItemContext">
    <Border Padding="12,8" BackgroundColor="Transparent">
        <Label Text="{Binding Title}" TextColor="#173A5E" />
    </Border>
</DataTemplate>
```

Templates define appearance; the host owns the item interaction. Custom rails and menu layouts use the same committed selection state and library commands. Styles and layout templates can change their arrangement without defining membership or replacing navigation logic.

</details>

<details>
<summary>Navigate to a tab or flyout item by ID</summary>

Normal tab/flyout presses require no application call to `Select`. For a business workflow, use the same `INavigationService.Select(string id)` overload for either container:

```csharp
// In a ViewModel command, select the existing tab by its ID.
await navigation.Select("notes-archive");
```

```csharp
// In a ViewModel command, select the existing flyout item by its ID.
await navigation.Select("stock-shared");
```

An ID identifies the destination even when several items use the same ViewModel type. Selection searches the caller's container, then its enclosing containers, within the same window. It uses `CanNavigate`, preserves history, and updates the selected template. Missing or unavailable IDs return an unsuccessful result without changing selection.

For a nested destination, select a path atomically:

```csharp
await navigation.Select("workspace/notes-archive");
```

IDs cannot contain `/`. Paths address container roots; a container covered by its own pushed detail returns `DestinationUnavailable`. Selecting the container ID alone restores its selected child and history. Parameters supplied to `Select` update the destination's retained root ViewModel.

`Select<TViewModel>()` is available when the type identifies a single destination. `NavigateTo<T>()` opens a new detail; `Select` selects an existing destination. Set `IsInitiallySelected = true` on one `NavigationItem` to choose the initial destination. `IsVisible = false` hides its selector while permitting ID selection; `IsEnabled = false` rejects selection.

</details>

**The ViewModel decides whether navigation is allowed:**

```csharp
public bool HasUnsavedChanges { get; set; }

public override Task<bool> CanNavigate() =>
    Task.FromResult(!HasUnsavedChanges);
```

`CanNavigate` controls forward navigation, destination switches, popup/modal dismissal, and Back—including toolbar and Android system Back. Set `IsBackSwipeEnabled="True"` on a View or container to enable guarded right-swipe Back on its active body. Rejection preserves content, selection, and toolbar state. `IsBusy` is visual only.

## A toolbar that stays in place

```text
┌──────────────────────────────────────────────┐
│ Back / Menu / Close  [ Search… ]    [ Save ] │  Persistent toolbar
├──────────────────────────────────────────────┤
│ Notes                Reports                 │  Destination selector
├──────────────────────────────────────────────┤
│                                              │
│          Notes → Detail → Editor             │  Navigating body
│                                              │
├──────────────────────────────────────────────┤
│                Shared content                │
└──────────────────────────────────────────────┘
```

Declare the toolbar's visuals in XAML. For example, inside a `ViewBase`:

```xml
<nav:ViewBase.ToolbarCenterContent>
    <SearchBar Text="{Binding SearchText}" Placeholder="Search" />
</nav:ViewBase.ToolbarCenterContent>

<nav:ViewBase.ToolbarItems>
    <nav:ToolbarButton
        Text="Save"
        Command="{Binding SaveCommand}"
        AccessibilityLabel="Save changes" />
</nav:ViewBase.ToolbarItems>
```

The host chooses **Back, Menu, Close, or no leading action**. The toolbar stays mounted while the body navigates. Containers supply shared defaults; screens can override individual areas. Each binding stays with its owning ViewModel.

| Customize | API |
| --- | --- |
| Center content and right buttons | `ToolbarCenterContent`, `ToolbarItems` |
| Center, action, and leading-button templates | `ToolbarCenterTemplate`, `ToolbarItemTemplate`, `ToolbarLeadingTemplate` |
| Tab bar content slots | `TabBarCenterContent`, `TabBarTrailingContent` |
| Flyout content slots | `FlyoutHeaderContent`, `FlyoutFooterContent` |
| Shared content in tabs and flyouts | `SharedContent`, `SharedContentPosition` |
| Additional panels and overlays | `HeaderContent`, `FooterContent`, `BodyOverlayContent`, `OverlayContent` |
| Standalone toolbar | `NavigationToolbar` in ordinary XAML layouts |

<details>
<summary>Application startup</summary>

The MAUI bootstrap helper creates the window and initializes its registered root. With `MauiNavigationHostFactory` injected as `hosts`, `Application.CreateWindow` becomes:

```csharp
protected override Window CreateWindow(IActivationState? activationState) =>
    hosts.CreateWindow<AppViewModel>();
```

Screen ViewModels use only their injected `INavigationService` for navigation.

</details>

## Try the sample

Open **[MVVMCompass.Sample.slnx](https://github.com/Grzegorz1223/MVVMCompass/blob/main/samples/MVVMCompass.Sample.slnx)** with your platform's MAUI workloads and native SDKs. It references the library source.

**Start with [WelcomeView.xaml](https://github.com/Grzegorz1223/MVVMCompass/blob/main/samples/MVVMCompass.Sample/WelcomeView.xaml).** Destination lists and commands live in [DemoViewModels.cs](https://github.com/Grzegorz1223/MVVMCompass/blob/main/samples/MVVMCompass.Sample/DemoViewModels.cs); the Views declare their visuals in XAML.

| Explore | Try it |
| --- | --- |
| [Tabs](https://github.com/Grzegorz1223/MVVMCompass/blob/main/samples/MVVMCompass.Sample/TabsDemoView.xaml) | Try all four edges × natural/equal sizing × shared top/bottom; toggle Reports and overflow folders. |
| [Flyout](https://github.com/Grzegorz1223/MVVMCompass/blob/main/samples/MVVMCompass.Sample/FlyoutDemoView.xaml) | Repeated ViewModel types, nested workspaces, templates, gradient panel, shared panels, dynamic Reports, and ID/path selection. |
| [Details](https://github.com/Grzegorz1223/MVVMCompass/blob/main/samples/MVVMCompass.Sample/DocumentView.xaml) and toolbars | Type, navigate, switch destinations, and restore retained history. |
| [Toolkit popups](https://github.com/Grzegorz1223/MVVMCompass/blob/main/samples/MVVMCompass.Sample/DemoPopupView.xaml) and modals | Return a result, dismiss, and deny closure with `CanNavigate`. |
| Guards and lifecycle | Toggle permission and verify that rejected navigation preserves the screen. |
| Independent windows | Navigate separately on supported platforms. |

<details>
<summary>Build, test, and create local packages</summary>

Use [global.json](https://github.com/Grzegorz1223/MVVMCompass/blob/main/global.json)'s SDK. The root solution contains libraries and tests; the sample has its own solution.

```sh
dotnet workload install maui-android
dotnet restore MVVMCompass.slnx --locked-mode
dotnet build MVVMCompass.slnx --no-restore -c Release
dotnet test --solution MVVMCompass.slnx --no-build --no-restore -c Release

dotnet build samples/MVVMCompass.Sample/MVVMCompass.Sample.csproj -f net10.0-android -c Debug

dotnet pack src/MVVMCompass.Core -c Release -o ./artifacts/packages
dotnet pack src/MVVMCompass.Maui -c Release -o ./artifacts/packages
```

The pack commands create development NuGets and symbol packages in `artifacts/packages`. Both packages use the same version; the MAUI package depends on Core.

</details>

<details>
<summary>Run native smoke checks</summary>

The sample hosts the [native smoke tests](https://github.com/Grzegorz1223/MVVMCompass/tree/main/tests/MVVMCompass.Maui.SmokeTests). They exercise real controls, input, bounds, guards, bindings, retained state, and cleanup. The full suite also runs the preserved engine regression fixtures. Unavailable platform checks report skips; screen-reader interaction and real OS gestures require device testing.

Automate with `MVVMCOMPASS_SMOKE=1` and optionally `MVVMCOMPASS_SMOKE_SUITE=unified` for the new API and layout matrix. Android extras: `mvvmcompass_smoke` (boolean), `mvvmcompass_smoke_suite` (string). Reports include `checks`, `skipped`, `suite`, and `failure`.

Launch the sample with these settings to execute its embedded native suite. Maintainer scripts and performance reports are kept in the separate tools workspace.

</details>
