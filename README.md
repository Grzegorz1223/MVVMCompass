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

The injected service belongs to the originating screen/container and window. Toolkit popup visuals use `PopupViewBase<TViewModel, TResult>`; the popup ViewModel calls `navigation.ClosePopup(result)` or `navigation.ClosePopup()`. Check `answer.HasResult` to distinguish an explicit result—including `null`—from dismissal. An active top popup can use its own injected service to present a child popup. A covered parent cannot close or present over its child. Canceling the result wait leaves an already displayed popup owned until it closes.

### Warnings requested during navigation

Use `RequestPopup` from `BeforeFirstShown`, `Appearing` or `CanNavigate`. It returns a handle immediately; the host presents after navigation and its callbacks settle, including rejected guards. Returning from the callback is required: awaiting `request.Completion` there would prevent the operation from settling. An independent observer may await completion without being joined by the callback.

```csharp
public override Task<bool> CanNavigate()
{
    if (QuantityIsValid) return Task.FromResult(true);
    var request = navigation.RequestPopup<WarningViewModel, string?>(
        new() { ["message"] = "Enter a valid quantity." }, requestKey: "invalid-quantity");
    _ = ObserveWarningAsync(request); // The guard returns without awaiting this observer.
    return Task.FromResult(false);
}

private async Task ObserveWarningAsync(PopupRequest<string?> request)
{
    var outcome = await request.Completion;
    // Handle outcome.Status / outcome.Error; a completed popup supplies PopupResult.
}
```

`IsAccepted` acknowledges an owned request, not guaranteed presentation. The window, selected branch and originating activation are rechecked before opening. Failed preparation, leaving the activation, root replacement and window closure complete stale requests with an explicit status. Screen requests wait behind existing popups; requests from an owned popup may open a child when it becomes the top popup. Eligible requests retain submission order. Navigation alone, including a queued warning, does not request loading.

A request key shares an outstanding request for the same activation, popup type and result type. The first submission's parameters and cancellation token apply. Once it completes, the key can be used again; keep feature-level state for warnings intended to appear only once across repeated activations. Parameter dictionaries are copied; their values remain application-owned.

Cancellation before opening prevents presentation. After opening it cancels the result wait and reports `WasPresented = true`; the visible popup and subscriptions remain owned until actual dismissal. `Completed` supplies `PopupResult`, whose `Reason` also describes forced root/window dismissal and whose `HasResult` distinguishes an explicit null result.

Navigation adapters can forward the optional `IDeferredPopupNavigationService` capability alongside `INavigationService`. The `RequestPopup` extension throws `NotSupportedException` for adapters that do not expose it.

### Registered popup lifecycle

Registered popups receive scoped construction/binding, parameters when supplied, `BeforeFirstShown`, native presentation, then terminal cleanup. Page `Appearing` is not a popup activation contract. Subscribe during `BeforeFirstShown`, register unsubscription through `Ownership.RegisterCleanup`, then refresh the current snapshot. This also releases subscriptions if preparation fails before the popup opens.

`AfterDismissed` calls `Deactivated` by default. Preserve that base call when using the deactivation cleanup path, and avoid unsubscribing the same resource through multiple competing paths. Owned cleanup runs once, before disposal of the enclosing DI scope. Use the popup View's `Opened` event for actions requiring native visibility.

For an About/footer action, `await navigation.CloseFlyout()` hides the enclosing drawer without running leave guards or discarding any history. An already closed or pinned drawer succeeds without commitment. The extension uses the optional `IFlyoutNavigationService` capability; existing implementations of `INavigationService` remain compatible and unsupported implementations throw `NotSupportedException`.

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
    TabBarPadding="4"
    TabItemSpacing="4"
    TabScrollBarVisibility="Default"
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

`TabBarPadding`, `TabItemSpacing` and `TabScrollBarVisibility` apply to all four tab-bar edges, including left/right rails, and can change through bindings without replacing tab models or their histories. Padding and spacing default to 4 DIP and accept zero; scrollbar visibility defaults to `Never`. `Content` sizing scrolls on the strip's axis; `Equal` sizing shares the available space and does not scroll. Center and trailing content keep the container's binding context.

Tab switching retains models and their histories. `BeforeFirstShown` initializes each model once; `Appearing` runs on activation, and `Deactivated` runs when leaving. Use `Ownership.RegisterCleanup` for subscriptions that should last until permanent removal, root replacement or window closure. Activation-only subscriptions need matching activation/deactivation handling. The shared toolbar follows the selected branch, and navigation alone never requests a loading overlay.

Flyouts have the same template-driven customization:

```xml
<nav:FlyoutViewBase
    x:Class="ExampleApp.Views.AppView"
    x:TypeArguments="vm:AppViewModel"
    x:DataType="vm:AppViewModel"
    FlyoutPanelBackground="#F1F5F9"
    FlyoutListPadding="0"
    FlyoutItemSpacing="0"
    FlyoutScrollBarVisibility="Default"
    SharedContentPosition="Bottom"
    SelectedFlyoutItemTemplate="{StaticResource SelectedDestinationTemplate}"
    UnselectedFlyoutItemTemplate="{StaticResource UnselectedDestinationTemplate}">

    <nav:FlyoutViewBase.FlyoutHeaderContent>
        <Label Text="My application" />
    </nav:FlyoutViewBase.FlyoutHeaderContent>

    <nav:FlyoutViewBase.FlyoutTrailingContent>
        <Button Text="About" Command="{Binding AboutCommand}" />
    </nav:FlyoutViewBase.FlyoutTrailingContent>

    <nav:FlyoutViewBase.FlyoutFooterContent>
        <Label Text="{Binding InstalledVersion}" />
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
| `FlyoutTrailingContent` | Interactive content immediately after visible destinations, inside the same scroller. It inherits the container model and local resources. |
| `FlyoutListPadding` | Insets inside the scrollable destination list; defaults to `4`. Set `0` for a selection background flush with the panel edge. Header and footer padding remain independent. |
| `FlyoutItemSpacing` | Nonnegative gap between destination rows, in device-independent units; defaults to `4`. |
| `FlyoutScrollBarVisibility` | `Default` (platform policy), `Always`, or `Never` for the list's vertical scrollbar. |
| `SharedContent` | Persistent content available in **both tabs and flyouts**, bound to the owning container ViewModel. |
| `SharedContentPosition` | `Top` (default) or `Bottom`, placing shared content above or below the navigating body, independently of tab position. |

`Equal` divides the space remaining after padding, spacing, and reserved tab-bar center/trailing content. Hidden items receive no share. `Content` allows scrolling along the tab bar when items overflow.

Shared content stays mounted while destinations switch or details navigate. In a flyout it belongs to the persistent detail area. `FlyoutHeaderContent` stays above the scrolling list, `FlyoutTrailingContent` scrolls with destinations, and `FlyoutFooterContent` stays at the panel bottom. List padding surrounds destinations and trailing content; item spacing also supplies the gap before trailing content when destinations are visible. Changing these properties preserves destination histories.

Trailing content is ordinary application UI: it has no destination ID or automatic selection behavior. For an About popup, its command should await the container's scoped `navigation.CloseFlyout()` and then open the popup through the active screen's navigation service. Replacing or clearing the slot detaches its previous content. Explicit content binding contexts remain intact.

An open flyout covers the toolbar and body of its owning window or modal. Background controls lose input and accessibility focus until the drawer closes. Nested flyouts present the innermost open menu; Back closes that menu first. Header and footer content retain the declaring container's binding context. Selector hit targets and the dimming surface use neutral button styling, so application-wide primary-button styles do not change their geometry or state colors. Item templates and header/footer content keep their application styling.

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

Set screen or container colors on its toolbar definition:

```xml
<nav:ViewBase.Toolbar>
    <nav:NavigationToolbarDefinition Title="Invoices" ForegroundColor="White">
        <nav:NavigationToolbarDefinition.Background>
            <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                <GradientStop Color="Orange" Offset="0" />
                <GradientStop Color="Purple" Offset="1" />
            </LinearGradientBrush>
        </nav:NavigationToolbarDefinition.Background>
    </nav:NavigationToolbarDefinition>
</nav:ViewBase.Toolbar>
```

Background and foreground inherit independently from the deepest active screen, enclosing containers, and host defaults, then fall back to the toolbar's configured `Background` and `ForegroundColor`. `null` or `ClearValue` restores inheritance; transparent brushes and white foregrounds are explicit overrides. The background covers the whole bar, including padding. Color changes preserve existing action controls and commands. Foreground colors apply to the default title and button text; icons and custom center content keep their own colors. Leading templates can bind to the toolbar's read-only `EffectiveBackground` and `EffectiveForegroundColor` properties.

Default action icons fit a **24 × 24** box inside native buttons with a minimum **44 × 44** touch target. Icon-only, text-only, and combined actions support live bindings. The title stays centered when space permits; compact widths reclaim unused leading space. Overflow actions scroll horizontally with a native scroll indicator and an accessibility hint. Default navigation buttons are isolated from implicit application button styles; use `NavigationToolbar.ButtonStyle` or the action/leading templates for deliberate customization.

Toolbar geometry also inherits per property through `NavigationToolbarDefinition`. `null` restores inheritance; zero is an explicit override. For a compact application layout:

```csharp
Toolbar = new NavigationToolbarDefinition
{
    Padding = new Thickness(20, 0, 10, 0),
    HeightRequest = 55,
    MinimumHeightRequest = 0,
    LeadingSlotWidth = 30,
    ColumnSpacing = 0,
    ActionSpacing = 4,
    ActionAreaSpacing = 8,
    CenterPlacement = ToolbarCenterPlacement.Middle
};
```

`Balanced` preserves the default symmetric/compact placement. `Middle` centers content in the remaining space between the independently sized sides. `FullWidth` spans the entire toolbar, including its padding, and makes the center host input-transparent so a logo cannot block navigation buttons. Each mode centers content vertically. `LeadingSlotWidth` reserves a fixed logical leading column even when its control is hidden; without it, automatic sizing keeps the 44-DIP floor. `ActionAreaSpacing` is an additional logical gap before the trailing action area, including an empty area. Action templates retain their own natural sizes.

An explicit height replaces the automatic 56-DIP minimum unless a minimum is explicitly configured. The other defaults are 8/4-DIP horizontal/vertical padding, 4-DIP column/action spacing, and no additional action-area gap. Values must be finite and nonnegative. Custom templates should fit their configured slots. Choosing compact targets is an application styling decision; the built-in controls retain their 44-DIP minimum.

| Customize | API |
| --- | --- |
| Center content and right buttons | `ToolbarCenterContent`, `ToolbarItems` |
| Center, action, and leading-button templates | `ToolbarCenterTemplate`, `ToolbarItemTemplate`, `ToolbarLeadingTemplate` |
| Screen/container toolbar colors | `Toolbar.Background`, `Toolbar.ForegroundColor` |
| Inherited toolbar geometry | `Toolbar.Padding`, `HeightRequest`, `MinimumHeightRequest`, `LeadingSlotWidth`, `ColumnSpacing`, `ActionSpacing`, `ActionAreaSpacing`, `CenterPlacement` |
| Tab bar content slots | `TabBarCenterContent`, `TabBarTrailingContent` |
| Flyout content slots | `FlyoutHeaderContent`, `FlyoutTrailingContent`, `FlyoutFooterContent` |
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

### Asynchronous startup and branded loading (1.1)

Choose a registered initial root before constructing any screen. The resolver runs once inside the window's navigation queue; `CreateWindow` returns immediately:

```csharp
protected override Window CreateWindow(IActivationState? activationState)
{
    var window = hosts.CreateWindow(async cancellationToken =>
    {
        await startup.InitializeAsync(cancellationToken); // Application service.
        return startup.IsActivated
            ? InitialRoot.For<MainViewModel>()
            : InitialRoot.For<ActivationViewModel>();
    }, new WindowBootstrapOptions
    {
        LoadingContentFactory = () => new Image { Source = "brand_loading.png" },
        FailureContentFactory = result => new Label { Text = "Unable to start. Please retry." }
    });
    appCoordinator.Attach(window); // Attach application policy before awaiting startup.
    return window;
}
```

The application supplies `startup`, `appCoordinator`, and a runtime image resource in this example. Register every possible root with `UseMVVMCompass`. `InitialRoot` copies its parameter dictionary; referenced values are not deep-cloned. Typed registrations provide construction delegates, so selecting a root by type adds no reflection-based activation.

`WaitForInitializationAsync` includes resolution, preparation, and activation of the original attempt. Window closure cancels the resolver. An enforced root request can supersede an unresolved startup even when the resolver ignores cancellation; its late result cannot install a stale root. Respect the token to stop application work too. Return an `InitialRoot`; do not await another root transition from inside the resolver. A resolver cancellation settles with `Cancelled`; an exception or invalid/unregistered selection settles with `Failed`. After supersession, observe the winning request's `Completion` separately.

Loading and failure factories create fresh ordinary views on the UI thread, with no navigation model or lifecycle. Return detached content, not `ViewBase`. Custom loading content replaces the default spinner completely. Failure content receives the `NavigationResult` when no root or pending successor remains; invalid or throwing failure content falls back to the standard error label and is recorded in `CleanupErrors`. Bootstrap content is detached when startup settles. Handle its `Unloaded` event if a custom animation needs to stop. The original generic `CreateWindow<T>()` remains available; use `CreateWindow<T>(parameters: null, options: options)` to customize a known root's bootstrap.

There are three separate loading surfaces:

- the operating-system splash, configured by the MAUI application;
- `WindowBootstrapOptions.LoadingContentFactory`, used only while the initial root is selected and prepared;
- application-requested runtime loading on active screens.

For typed runtime loading, set `LoadingPresentationTemplate` on any `ViewBase`, including flyout/tab containers and plain roots. Its binding context is `LoadingPresentationContext`: `LoadingType` is `Loading` or `LogingIn`, and `Owner` is the declaring view's binding context. This keeps localization in the customer application:

```xml
<nav:ViewBase.LoadingPresentationTemplate>
    <DataTemplate x:DataType="nav:LoadingPresentationContext">
        <Grid>
            <ActivityIndicator IsRunning="True"
                               HorizontalOptions="Center"
                               VerticalOptions="Center" />
            <Label Text="{Binding LoadingType,
                         Converter={StaticResource LoadingTypeToLocalizedTextConverter}}"
                   HorizontalOptions="Center"
                   VerticalOptions="Center"
                   Margin="0,72,0,0" />
        </Grid>
    </DataTemplate>
</nav:ViewBase.LoadingPresentationTemplate>
```

The converter belongs to the app and can map `Loading` and `LogingIn` to its localized resources. Direct `IsBusy` uses `Loading`. Calls to the protected `ShowLoading(LoadingType)` create independent scopes; the most recently opened active scope supplies the type:

```csharp
using var refresh = ShowLoading(LoadingType.Loading);
using var signIn = ShowLoading(LoadingType.LogingIn);
await AuthenticateAsync();
// Disposing signIn falls back to refresh; disposing refresh hides the presentation.
```

`HideLoading()` clears every managed scope and direct busy state for compatibility with existing force-hide code. Scope handles are idempotent and may be disposed out of order or from a background thread.

`BusyOverlayTemplate` remains available for existing applications. It binds directly to the declaring view's binding context:

```xml
<nav:ViewBase.BusyOverlayTemplate>
    <DataTemplate>
        <Image Source="brand_loading.png"
               WidthRequest="64" HeightRequest="64"
               HorizontalOptions="Center" VerticalOptions="Center"
               SemanticProperties.Description="Loading" />
    </DataTemplate>
</nav:ViewBase.BusyOverlayTemplate>
```

Each active window or modal hierarchy has one busy presenter. The closest active view that declares either template wins; `LoadingPresentationTemplate` wins when the same view declares both. The presenter inherits that declaring view's resources. Content is created lazily and reused while its owner/template stays active, including loading-type changes. A covered window root hides its busy content. Templates must produce fresh detached ordinary views.

Runtime loading is owned by the customer app through `IsBusy`, `ShowLoading`, or `NavigationView.IsBusy`. Navigation never starts a loader. While `IsNavigating` is true, MVVMCompass hides an existing runtime presentation and stops its default indicator without clearing application state or recreating custom content. Rejected, cancelled, or failed navigation restores the same content when the same busy screen remains active; committed navigation resolves busy state from the new active branch.

Set `ViewBase.LoadingBackdrop` to choose the brush behind runtime loading content. The nearest active declaration wins independently of template selection; `null` inherits and an explicit transparent brush removes the default `#33FFFFFF` tint. Changing the brush keeps the same loading content and scope state:

```csharp
LoadingBackdrop = new SolidColorBrush(Colors.Transparent);
```

With no custom template, the default indicator remains available. `UseDefaultBusyIndicator="False"` on any active ancestor disables that fallback for its subtree; custom templates still display. These options do not change `IsBusy`, input blocking, or `CanNavigate`. Use one app-owned runtime template instead of combining it with another HUD for the same operation. A hidden cached custom animation should pause when its presentation becomes hidden; only the built-in indicator's `IsRunning` is managed automatically.

### Application-owned root transitions (1.1)

Use the factory and an explicit window for startup, session changes, or application blocking:

```csharp
var result = await hosts.SetRoot<MainViewModel>(window);
if (!result.IsSuccess)
    ReportNavigationFailure(result); // Your application logging/recovery policy.
```

Normal replacement checks leave guards across the outgoing tree, including retained destinations and covered content. `RootTransitionMode.Enforced` bypasses leave vetoes and supersedes earlier uncommitted normal work. Both policies own the complete outgoing tree, dismiss its modals/popups, await cleanup, and invalidate its old scoped services. Parameters are copied at submission; their values remain application-owned objects.

Authorization events can arrive inside an awaited lifecycle callback or guard. Submit the intent, return from that callback, and observe completion through application-owned state:

```csharp
appState.MarkBlocked(); // Deny business operations immediately.
var request = hosts.RequestRoot<BlockedViewModel>(window,
    options: new() { Mode = RootTransitionMode.Enforced });
appCoordinator.Observe(request.Completion); // Retain and handle the task/result.
return Task.CompletedTask;
```

`appState`, `appCoordinator`, and `ReportNavigationFailure` above represent application policy. Do not await `request.Completion` inside the callback that submitted it. Directly awaiting `hosts.SetRoot` from a navigation callback returns `Reentrant` and queues nothing. Cancellation is cooperative before commitment: an arbitrary application guard must return before its screen can be safely replaced. Already committed work finishes first. The app must also suppress future stale Main/resume requests after blocking; enforced priority only supersedes work already admitted.

Normal requests may use a nonempty `CoalescingKey` to replace older uncommitted requests with the same key. Enforced requests cannot use one. Closed factory windows return `InvalidOrigin`; null or foreign windows are argument errors. `WaitForInitializationAsync` reports the original initialization outcome even when a later request supersedes it; canceling that wait does not cancel initialization. If initialization and all submitted replacements settle without installing a root, the factory replaces its loading indicator with an error page. A late failure cannot overwrite a newer root. Applications can recover with another root request and must keep their authorization policy enforced during recovery.

Content views also provide protected informational `DisplayAlertAsync(title, message, cancel)` and confirmation `DisplayAlertAsync(title, message, accept, cancel)` helpers. Both dispatch to the owning window and reject hidden or dismissed origins. Awaited guards should use bounded validation or an owned page alert; do not await registered popup navigation inside navigation callbacks.

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
