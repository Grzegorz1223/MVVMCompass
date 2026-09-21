using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

public sealed class PopupLifecycleProbe
{
    internal readonly List<DeferredNoticeView> Opened = [];
    internal readonly List<DeferredNoticeModel> Created = [];
    internal event Action<int>? Changed;
    internal int Subscribers;
    internal void Publish(int count) => Changed?.Invoke(count);
    internal void Subscribe(Action<int> callback) { Changed += callback; Subscribers++; }
    internal void Unsubscribe(Action<int> callback) { Changed -= callback; Subscribers--; }
}

public sealed class DeferredDestinationModel(INavigationService navigation, DemoResource resource) : DemoViewModel(navigation, resource)
{
    private string stage = "before";
    internal PopupRequest<string?>? Warning;
    public override async Task GetParameters(Dictionary<string, object> parameters)
    { await base.GetParameters(parameters); stage = (string)parameters.GetValueOrDefault("stage", "before"); }
    public override Task BeforeFirstShown() { if (stage == "before") Request(); return Task.CompletedTask; }
    public override Task Appearing() { if (stage == "appearing") Request(); return Task.CompletedTask; }
    private void Request() => Warning ??= Navigation.RequestPopup<DeferredNoticeModel, string?>(new() { ["caption"] = stage }, "activation-warning");
}
public sealed class DeferredDestinationView(DeferredDestinationModel model) : ViewBase<DeferredDestinationModel>(model)
{
    // A real native view ensures the warning follows the destination's installed handler.
    protected override void OnBindingContextChanged()
    { base.OnBindingContextChanged(); Content ??= new Label { Text = "Destination with deferred warning", Margin = 20 }; }
}

public sealed class DeferredNoticeModel : ViewModelBase
{
    internal INavigationService Navigation { get; }
    internal DemoResource Resource { get; }
    private readonly PopupLifecycleProbe probe;
    internal string Caption = "warning";
    internal bool FailPreparation;
    internal int Initializations, Appearances, Deactivations, Releases;
    internal Func<Task<bool>>? Guard;
    private int pendingCount;
    public int PendingCount { get => pendingCount; set => SetProperty(ref pendingCount, value); }
    public DeferredNoticeModel(INavigationService navigation, DemoResource resource, PopupLifecycleProbe probe)
    { Navigation = navigation; Resource = resource; this.probe = probe; probe.Created.Add(this); }
    public override Task GetParameters(Dictionary<string, object> parameters)
    {
        Caption = (string)parameters.GetValueOrDefault("caption", "warning");
        FailPreparation = (bool)parameters.GetValueOrDefault("fail", false); return Task.CompletedTask;
    }
    public override Task BeforeFirstShown()
    {
        Initializations++; probe.Subscribe(Update);
        Ownership.RegisterCleanup(() => { probe.Unsubscribe(Update); Releases++; return Task.CompletedTask; });
        Update(1);
        if (FailPreparation) throw new InvalidOperationException("native popup preparation failed after subscription");
        return Task.CompletedTask;
    }
    private void Update(int count) => PendingCount = count;
    public override Task Appearing() { Appearances++; return Task.CompletedTask; }
    public override Task Deactivated() { Deactivations++; return Task.CompletedTask; }
    public override Task<bool> CanNavigate() => Guard?.Invoke() ?? Task.FromResult(true);
}
public sealed class DeferredNoticeView : PopupViewBase<DeferredNoticeModel, string?>
{
    internal Label CountLabel { get; } = new();
    internal bool OnMainThread;
    public DeferredNoticeView(DeferredNoticeModel model, PopupLifecycleProbe probe) : base(model)
    {
        CountLabel.SetBinding(Label.TextProperty, nameof(DeferredNoticeModel.PendingCount));
        var ok = new Button { Text = "OK", Command = new Command(async () => await model.Navigation.ClosePopup("OK")) };
        Content = new VerticalStackLayout { Padding = 20, WidthRequest = 220, Children = { new Label { Text = "Connection status" }, CountLabel, ok } };
        Opened += (_, _) => { OnMainThread = MainThread.IsMainThread; probe.Opened.Add(this); };
    }
}

internal sealed partial class UnifiedNativeCoverage
{
    internal async Task DeferredPopupsAsync(PopupLifecycleProbe probe, MauiNavigationHostFactory factory)
    {
        foreach (var stage in new[] { "before", "appearing" })
        {
            Success(await factory.SetRoot<WelcomeViewModel>(host.Window), "deferred warning test root");
            var navigation = Current.Navigation.NavigateTo<DeferredDestinationModel>(new() { ["stage"] = stage });
            Success(await navigation, stage + ": navigation completes independently of dialog acknowledgement");
            var destinationContext = (NavigationContext)host.CurrentRoot!.Entry.ViewModel;
            var origin = (DeferredDestinationModel)destinationContext.Current!.ViewModel;
            var popup = await Notice(stage);
            Check(popup.OnMainThread && navigation.IsCompletedSuccessfully, stage + ": native presentation on UI thread after navigation");
            Check(origin.Warning is { IsAccepted: true } && !origin.Warning.Completion.IsCompleted, stage + ": distinct acceptance and result wait");
            Check(!origin.IsBusy && !destinationContext.View.IsBusyPresented, stage + ": navigation warning requests no loader");
            probe.Publish(7);
            await Until(() => popup.CountLabel.Text == "7");
            Check(probe.Subscribers == 1 && popup.ViewModel.Appearances == 0, stage + ": live popup updates without page Appearing");
            Success(await popup.ViewModel.Navigation.ClosePopup<string?>(null), stage + ": explicit null result");
            var result = await origin.Warning!.Completion;
            Check(result.Status == PopupRequestStatus.Completed && result.WasPresented && result.PopupResult!.HasResult,
                stage + ": typed result and one completion");
            Released(popup.ViewModel, stage);
        }

        foreach (var route in new[] { "app", "toolbar", "native", "flyout" })
        {
            Success(await factory.SetRoot<FlyoutDemoViewModel>(host.Window), route + ": deferred guard root");
            Success(await Current.Navigation.NavigateTo<DemoDetailViewModel>(), route + ": deferred guard detail");
            var context = Root; var origin = Current; PopupRequest<string?>? request = null;
            var guardFinished = false;
            origin.Guard = () =>
            {
                request = origin.Navigation.RequestPopup<DeferredNoticeModel, string?>(new() { ["caption"] = route });
                guardFinished = true; return Task.FromResult(false);
            };
            if (route == "app") Check((await origin.Navigation.NavigateBack()).Status == NavigationStatus.GuardRejected, "deferred app Back rejects");
            else if (route == "toolbar") await context.ExecuteLeadingAsync();
            else if (route == "native")
            {
#if ANDROID
                ((AndroidX.Activity.ComponentActivity)host.Window.Handler!.PlatformView!).OnBackPressedDispatcher.OnBackPressed();
#else
                host.Window.Page!.SendBackButtonPressed();
#endif
                await context.NativeBackCompletion;
            }
            else
            {
                var item = context.ActiveChain().SelectMany(item => item.Destinations)
                    .First(item => item.SelectCommand.CanExecute(null) && !item.IsSelected && item.IsVisible && item.IsEnabled);
                item.SelectCommand.Execute(null);
            }
            var popup = await Notice(route);
            Check(guardFinished && !origin.IsDismissed && ReferenceEquals(context.Deepest.Current!.ViewModel, origin), route + ": rejected guard retains warning origin");
            Success(await popup.ViewModel.Navigation.ClosePopup("explained"), route + ": close explanation");
            Check((await request!.Completion).PopupResult!.Result == "explained", route + ": explanation completes");
            Released(popup.ViewModel, route); origin.Guard = null;
        }

        Success(await factory.SetRoot<WelcomeViewModel>(host.Window), "deferred cancellation root");
        var screen = Current; var owner = Root;
        using (var cancellation = new CancellationTokenSource())
        {
            var request = screen.Navigation.RequestPopup<DeferredNoticeModel, string?>(new() { ["caption"] = "cancel-visible" }, cancellationToken: cancellation.Token);
            var popup = await Notice("cancel-visible"); cancellation.Cancel();
            var result = await request.Completion;
            Check(result.Status == PopupRequestStatus.Cancelled && result.WasPresented && !popup.ViewModel.IsDismissed,
                "cancelled visible result keeps popup owned");
            Check(host.Window.Navigation.ModalStack.Count == 1 && probe.Subscribers == 1, "cancelled visible result retains native popup and subscription");
            Success(await popup.ViewModel.Navigation.ClosePopup("later"), "cancelled visible popup closes later"); Released(popup.ViewModel, "cancel-visible");
        }
        var failed = screen.Navigation.RequestPopup<DeferredNoticeModel, string?>(new() { ["fail"] = true });
        Check((await failed.Completion).Status == PopupRequestStatus.PreparationFailed, "failed preparation completes deferred request");
        Released(probe.Created.Last(), "failed preparation");

        var outerRequest = screen.Navigation.RequestPopup<DeferredNoticeModel, string?>(new() { ["caption"] = "outer" });
        var outer = await Notice("outer");
        var queued = screen.Navigation.RequestPopup<DeferredNoticeModel, string?>(new() { ["caption"] = "stale" });
        PopupRequest<string?>? childRequest = null;
        outer.ViewModel.Guard = () =>
        {
            childRequest = outer.ViewModel.Navigation.RequestPopup<DeferredNoticeModel, string?>(new() { ["caption"] = "nested-guard" });
            return Task.FromResult(false);
        };
        Check((await outer.ViewModel.Navigation.ClosePopup()).Status == NavigationStatus.GuardRejected, "popup close guard rejects before deferred child");
        var child = await Notice("nested-guard");
        Check(host.Window.Navigation.ModalStack.Count == 2 && !queued.Completion.IsCompleted, "nested guard explanation takes eligible order above waiting screen request");
        Success(await child.ViewModel.Navigation.ClosePopup("child"), "nested explanation closes");
        Check((await childRequest!.Completion).PopupResult!.Result == "child", "nested result completes once");
        outer.ViewModel.Guard = null;
        Success(await factory.SetRoot<WelcomeViewModel>(host.Window, options: new() { Mode = RootTransitionMode.Enforced }), "enforced root clears visible and queued warnings");
        Check((await outerRequest.Completion).PopupResult!.Reason == DismissalReason.RootReplaced, "visible warning keeps forced-root reason");
        Check((await queued.Completion).Status == PopupRequestStatus.RootReplaced && probe.Opened.All(view => view.ViewModel.Caption != "stale"), "queued stale warning never opens");
        Check(probe.Subscribers == 0 && host.Window.Navigation.ModalStack.Count == 0, "deferred popup suite releases all native sessions and subscriptions");
        probe.Opened.Clear(); probe.Created.Clear();

        async Task<DeferredNoticeView> Notice(string caption)
        {
            await Until(() => probe.Opened.Any(view => view.ViewModel.Caption == caption));
            var view = probe.Opened.Last(view => view.ViewModel.Caption == caption);
            await Ready(view.CountLabel);
            Check(view.CountLabel.Handler != null && view.ViewModel.Initializations == 1, caption + ": initialized once and native content ready");
            return view;
        }
        void Released(DeferredNoticeModel model, string label) => Check(model.IsDismissed && model.Releases == 1
            && model.Deactivations == 1 && model.Resource.Disposals == 1, label + ": subscription and scoped lifetime released exactly once");
    }
}
