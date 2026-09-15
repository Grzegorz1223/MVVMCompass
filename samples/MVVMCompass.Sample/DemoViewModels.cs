using CommunityToolkit.Mvvm.Input;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

public sealed class DemoResource : IAsyncDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public int Disposals { get; private set; }
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}

public abstract class DemoViewModel : ViewModelBase
{
    protected DemoViewModel(INavigationService navigation, DemoResource resource)
    {
        Navigation = navigation; Resource = resource;
        HomeCommand = new AsyncRelayCommand(() => Apply(navigation.SetRoot<WelcomeViewModel>()));
        DetailCommand = new AsyncRelayCommand(() => Apply(navigation.NavigateTo<DemoDetailViewModel>(new() { ["caption"] = Caption + " detail" })));
        ModalCommand = new AsyncRelayCommand(() => Apply(navigation.NavigateTo<DemoModalViewModel>()));
        PopupCommand = new AsyncRelayCommand(async () =>
        {
            var result = await navigation.DisplayPopup<DemoPopupViewModel, string>();
            Status = result.HasResult ? "Popup returned: " + result.Result : "Popup dismissed";
        });
        BackCommand = new AsyncRelayCommand(() => Apply(navigation.NavigateBack()));
        RootCommand = new AsyncRelayCommand(() => Apply(navigation.NavigateBackToRoot()));
        SelectCommand = new AsyncRelayCommand(() => Apply(navigation.Select(DestinationId)));
        WindowCommand = new AsyncRelayCommand(() => Apply(navigation.OpenNewWindow<WelcomeViewModel>()));
    }
    internal INavigationService Navigation { get; }
    public DemoResource Resource { get; }
    public Guid InstanceId { get; } = Guid.NewGuid();
    private string caption = "Navigation playground", status = "Ready", toolbarText = "Persistent toolbar input", destinationId = "archive";
    private bool allowNavigation = true;
    public string Caption { get => caption; set => SetProperty(ref caption, value); }
    public string Status { get => status; set => SetProperty(ref status, value); }
    public string ToolbarText { get => toolbarText; set => SetProperty(ref toolbarText, value); }
    public string DestinationId { get => destinationId; set => SetProperty(ref destinationId, value); }
    public bool AllowNavigation { get => allowNavigation; set => SetProperty(ref allowNavigation, value); }
    public int Dismissals { get; private set; }
    public IAsyncRelayCommand HomeCommand { get; }
    public IAsyncRelayCommand DetailCommand { get; }
    public IAsyncRelayCommand ModalCommand { get; }
    public IAsyncRelayCommand PopupCommand { get; }
    public IAsyncRelayCommand BackCommand { get; }
    public IAsyncRelayCommand RootCommand { get; }
    public IAsyncRelayCommand SelectCommand { get; }
    public IAsyncRelayCommand WindowCommand { get; }
    internal Func<Task<bool>>? Guard;
    public override Task<bool> CanNavigate() => Guard?.Invoke() ?? Task.FromResult(AllowNavigation);
    public override Task GetParameters(Dictionary<string, object> parameters)
    { Caption = (string)parameters.GetValueOrDefault("caption", Caption); return Task.CompletedTask; }
    public override Task AfterDismissed() { Dismissals++; return Task.CompletedTask; }
    protected async Task Apply(Task<NavigationResult> operation)
    {
        var result = await operation;
        Status = result.Error?.Message ?? result.Status.ToString();
    }
}

public sealed class WelcomeViewModel : DemoViewModel
{
    public WelcomeViewModel(INavigationService navigation, DemoResource resource) : base(navigation, resource)
    {
        TabsCommand = new AsyncRelayCommand(() => Apply(navigation.SetRoot<TabsDemoViewModel>()));
        FlyoutCommand = new AsyncRelayCommand(() => Apply(navigation.SetRoot<FlyoutDemoViewModel>()));
        PlainCommand = new AsyncRelayCommand(() => Apply(navigation.NavigateTo<DocumentViewModel>()));
    }
    public IAsyncRelayCommand TabsCommand { get; }
    public IAsyncRelayCommand FlyoutCommand { get; }
    public IAsyncRelayCommand PlainCommand { get; }
}

public abstract class ContainerDemoViewModel : DemoViewModel
{
    protected ContainerDemoViewModel(INavigationService navigation, DemoResource resource) : base(navigation, resource) { }
    private SharedContentPosition sharedPosition;
    public SharedContentPosition SharedPosition { get => sharedPosition; set => SetProperty(ref sharedPosition, value); }
    public IReadOnlyList<SharedContentPosition> SharedPositions { get; } = Enum.GetValues<SharedContentPosition>();
}

public sealed class TabsDemoViewModel : ContainerDemoViewModel
{
    public TabsDemoViewModel(INavigationService navigation, DemoResource resource) : base(navigation, resource)
    {
        Caption = "Tabs";
        ToggleFeatureCommand = new AsyncRelayCommand(async () =>
        {
            var next = !feature;
            var result = await ReplaceItems(Items(next, overflow));
            if (result.IsSuccess) { feature = next; OnPropertyChanged(nameof(FeatureEnabled)); }
            Status = result.Status.ToString();
        });
        ToggleOverflowCommand = new AsyncRelayCommand(async () =>
        {
            var next = !overflow;
            var result = await ReplaceItems(Items(feature, next));
            if (result.IsSuccess) overflow = next;
            Status = result.Status.ToString();
        });
    }
    private TabBarPosition tabPosition;
    private TabItemSizing itemSizing;
    private bool feature, overflow;
    public TabBarPosition TabPosition { get => tabPosition; set => SetProperty(ref tabPosition, value); }
    public TabItemSizing ItemSizing { get => itemSizing; set => SetProperty(ref itemSizing, value); }
    public IReadOnlyList<TabBarPosition> TabPositions { get; } = Enum.GetValues<TabBarPosition>();
    public IReadOnlyList<TabItemSizing> ItemSizes { get; } = Enum.GetValues<TabItemSizing>();
    public bool FeatureEnabled => feature;
    public IAsyncRelayCommand ToggleFeatureCommand { get; }
    public IAsyncRelayCommand ToggleOverflowCommand { get; }
    internal Task<NavigationResult> ReplaceItems(IEnumerable<NavigationItem> items) => SetTabs(items);
    public override async Task BeforeFirstShown() => await Apply(SetTabs(Items(false)));
    internal static IReadOnlyList<NavigationItem> Items(bool reports, bool overflow = false) =>
    [
        new(typeof(DocumentViewModel), "Inbox", "inbox", new() { ["caption"] = "Inbox" }),
        new(typeof(DocumentViewModel), "Archive", "archive", new() { ["caption"] = "Archive" }),
        .. reports ? new NavigationItem[] { new(typeof(DocumentViewModel), "Reports", "reports", new() { ["caption"] = "Reports" }) } : [],
        .. overflow ? Enumerable.Range(1, 16).Select(index => new NavigationItem(typeof(DocumentViewModel), "Folder " + index,
            "folder-" + index, new() { ["caption"] = "Folder " + index })) : [],
        new(typeof(DocumentViewModel), "Hidden", "hidden", new() { ["caption"] = "Hidden destination" }) { IsVisible = false },
        new(typeof(DocumentViewModel), "Unavailable", "disabled") { IsEnabled = false }
    ];
}

public sealed class FlyoutDemoViewModel : ContainerDemoViewModel
{
    public FlyoutDemoViewModel(INavigationService navigation, DemoResource resource) : base(navigation, resource)
    {
        Caption = "Flyout"; DestinationId = "workspace/archive";
        ToggleFeatureCommand = new AsyncRelayCommand(async () =>
        {
            var next = !feature;
            var result = await ReplaceItems(Items(next));
            if (result.IsSuccess) feature = next;
            Status = result.Status.ToString();
        });
    }
    private bool feature;
    public IAsyncRelayCommand ToggleFeatureCommand { get; }
    internal Task<NavigationResult> ReplaceItems(IEnumerable<NavigationItem> items) => SetFlyoutItems(items);
    public override async Task BeforeFirstShown() => await Apply(SetFlyoutItems(Items(false)));
    internal static IReadOnlyList<NavigationItem> Items(bool reports) =>
    [
        new(typeof(TabsDemoViewModel), "Workspace tabs", "workspace"),
        new(typeof(DocumentViewModel), "Personal", "personal", new() { ["caption"] = "Personal documents" }),
        new(typeof(DocumentViewModel), "Shared", "shared", new() { ["caption"] = "Shared documents" }),
        new(typeof(TabsDemoViewModel), "Another workspace", "secondary"),
        .. reports ? new NavigationItem[] { new(typeof(DocumentViewModel), "Reports", "reports", new() { ["caption"] = "Flyout reports" }) } : [],
        new(typeof(DocumentViewModel), "Hidden", "hidden") { IsVisible = false },
        new(typeof(DocumentViewModel), "Unavailable", "disabled") { IsEnabled = false }
    ];
}

public sealed class DocumentViewModel(INavigationService navigation, DemoResource resource) : DemoViewModel(navigation, resource);
public sealed class DemoDetailViewModel(INavigationService navigation, DemoResource resource) : DemoViewModel(navigation, resource);
public sealed class DemoModalViewModel : DemoViewModel
{
    public DemoModalViewModel(INavigationService navigation, DemoResource resource) : base(navigation, resource)
    { IsModal = true; Caption = "Independent modal"; }
}
public sealed class DemoPopupViewModel : DemoViewModel
{
    public DemoPopupViewModel(INavigationService navigation, DemoResource resource) : base(navigation, resource)
    {
        Caption = "Toolkit popup";
        AcceptCommand = new AsyncRelayCommand(() => Apply(navigation.ClosePopup("accepted")));
        CancelCommand = new AsyncRelayCommand(() => Apply(navigation.ClosePopup()));
    }
    public IAsyncRelayCommand AcceptCommand { get; }
    public IAsyncRelayCommand CancelCommand { get; }
}
