using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

public sealed class ModalTabsProbeModel : DemoViewModel
{
    public ModalTabsProbeModel(INavigationService navigation, DemoResource resource) : base(navigation, resource) => IsModal = true;
    public override async Task BeforeFirstShown() => await Apply(SetTabs(TabsDemoViewModel.Items(false)));
}
public sealed class ModalTabsProbeView(ModalTabsProbeModel model) : TabbedViewBase<ModalTabsProbeModel>(model);

public sealed class FlyoutTabProbeModel(INavigationService navigation, DemoResource resource) : DemoViewModel(navigation, resource)
{
    public override async Task BeforeFirstShown() => await Apply(SetTabs([
        new(typeof(FlyoutDemoViewModel), "Workspace", "workspace"), new(typeof(DocumentViewModel), "Other", "other")]));
}
public sealed class FlyoutTabProbeView(FlyoutTabProbeModel model) : TabbedViewBase<FlyoutTabProbeModel>(model);

internal sealed partial class UnifiedNativeCoverage
{
    internal async Task TabRegressionsAsync(PopupLifecycleProbe probe, MauiNavigationHostFactory factory)
    {
        await TabStripSettingsAsync(factory);
        foreach (var stage in new[] { "before", "appearing" })
        {
            Success(await factory.SetRoot<TabsDemoViewModel>(host.Window), "tabs: deferred initialization root");
            var root = Root;
            var parent = (TabsDemoViewModel)root.Current!.ViewModel;
            Success(await parent.ReplaceItems([new(typeof(DocumentViewModel), "Inbox", "inbox"),
                new(typeof(DeferredDestinationModel), "Warning", "warning", new() { ["stage"] = stage })]), "tabs: deferred initialization membership");
            var count = probe.Opened.Count;
            Success(await parent.Navigation.Select("warning"), "tabs: activate warning tab from " + stage);
            var owner = (DeferredDestinationModel)root.Deepest.Current!.ViewModel;
            await Until(() => probe.Opened.Count > count);
            var popup = probe.Opened[^1]; await Ready(popup.CountLabel);
            Check(!root.View.IsBusyPresented && owner.Warning is { IsAccepted: true }, "tabs: initialized tab presents popup without a loader");
            Success(await popup.ViewModel.Navigation.ClosePopup("ready"), "tabs: close initialization warning");
            Check((await owner.Warning!.Completion).PopupResult!.Result == "ready", "tabs: initialization warning completes");
        }
        foreach (var nesting in new[] { "plain", "flyout", "modal" }) await TabBranchStateAsync(nesting, probe, factory);
        await FlyoutInsideTabAsync(factory);
        Success(await factory.SetRoot<WelcomeViewModel>(host.Window), "tabs: release qualification root");
        probe.Opened.Clear(); probe.Created.Clear();
    }

    private async Task TabStripSettingsAsync(MauiNavigationHostFactory factory)
    {
        Success(await factory.SetRoot<TabsDemoViewModel>(host.Window), "tabs: strip settings root");
        var root = Root; var owner = (TabsDemoView)root.Current!.View; var child = root.Current.Children!;
        owner.SharedContent = null; owner.TabBarCenterContent = null; owner.TabBarTrailingContent = null;
        owner.HorizontalOptions = LayoutOptions.Center;
        Success(await owner.ViewModel.ReplaceItems([
            new(typeof(DocumentViewModel), "Inbox", "inbox"), new(typeof(DocumentViewModel), "Archive", "archive"),
            new(typeof(DocumentViewModel), "Hidden", "hidden") { IsVisible = false }]), "tabs: compact strip membership");
        root.View.Resources.Add(new Style(typeof(Button)) { Setters =
        {
            new() { Property = VisualElement.HeightRequestProperty, Value = 88d },
            new() { Property = VisualElement.MinimumWidthRequestProperty, Value = 160d },
            new() { Property = Button.CornerRadiusProperty, Value = 20 },
            new() { Property = VisualElement.BackgroundColorProperty, Value = Colors.Purple }
        } });
        foreach (var position in Enum.GetValues<TabBarPosition>())
        foreach (var sizing in Enum.GetValues<TabItemSizing>())
        foreach (var direction in new[] { FlowDirection.LeftToRight, FlowDirection.RightToLeft })
        foreach (var font in new[] { 14d, 18.2d })
        {
            var name = $"tabs: {position}/{sizing}/{direction}/{font}";
            owner.WidthRequest = font == 14 ? 320 : 360;
            owner.FlowDirection = direction; owner.TabBarPosition = position; owner.TabItemSizing = sizing;
            owner.TabBarPadding = new Thickness(7, 3, 11, 5); owner.TabItemSpacing = 9;
            owner.TabScrollBarVisibility = ScrollBarVisibility.Always;
            var template = new DataTemplate(() =>
            {
                var label = new Label { FontSize = font, Padding = 6, VerticalTextAlignment = TextAlignment.Center };
                label.SetBinding(Label.TextProperty, nameof(NavigationItemContext.Title)); return label;
            });
            owner.SelectedTabItemTemplate = owner.UnselectedTabItemTemplate = template;
            var horizontal = position is TabBarPosition.Top or TabBarPosition.Bottom;
            var selector = horizontal ? child.View.TabSelector : child.View.RailSelector;
            await Ready(selector); await Task.Delay(50);
            var buttons = Descendants(selector).OfType<Button>().Where(button => button.AutomationId?.StartsWith("destination-") == true).ToArray();
            Check(buttons.Length == 2, name + ": hidden tab has no native target");
            var first = NativeCoveragePlatform.Bounds(buttons[0]); var second = NativeCoveragePlatform.Bounds(buttons[1]);
            var bounds = NativeCoveragePlatform.Bounds(selector);
            var gap = horizontal ? Math.Max(first.Left, second.Left) - Math.Min(first.Right, second.Right)
                : Math.Max(first.Top, second.Top) - Math.Min(first.Bottom, second.Bottom);
            Check(Math.Abs(gap - 9) <= 2, name + $": native item gap is 9 DIP ({gap})");
            Check(first.Width >= 43 && first.Height >= 43 && second.Width >= 43 && second.Height >= 43,
                name + ": native targets retain minimum size");
            Check(buttons.All(button => button.HeightRequest != 88 && button.MinimumWidthRequest == 44 && button.CornerRadius == 0),
                name + ": app primary-button style does not alter navigation inputs");
            Check(first.Left >= bounds.Left + 2 && first.Right <= bounds.Right - 2 && first.Top >= bounds.Top + 2 && first.Bottom <= bounds.Bottom - 2,
                name + ": asymmetric padding reserves native insets");
            var archive = buttons.Single(button => button.AutomationId == "destination-archive");
            Check(NativeCoveragePlatform.Label(archive)?.Contains("Archive") == true, name + ": native accessibility label");
            var handler = archive.Handler; var content = selector.Content; var retained = child.Current;
            owner.TabBarPadding = 0; owner.TabItemSpacing = 0;
            await Task.Delay(35);
            Check(ReferenceEquals(handler, archive.Handler) && ReferenceEquals(content, selector.Content) && ReferenceEquals(retained, child.Current),
                name + ": zero settings preserve native handlers, scroller and model");
            first = NativeCoveragePlatform.Bounds(buttons[0]); second = NativeCoveragePlatform.Bounds(buttons[1]);
            gap = horizontal ? Math.Max(first.Left, second.Left) - Math.Min(first.Right, second.Right)
                : Math.Max(first.Top, second.Top) - Math.Min(first.Bottom, second.Bottom);
            Check(Math.Abs(gap) <= 2, name + ": zero gap is rendered");
            NativeCoveragePlatform.TapAt(archive);
            await Until(() => child.SelectedDestinationId == "archive" && !root.IsNavigating);
            Check(!root.View.IsBusyPresented, name + ": tab input requests no loader");
            Success(await owner.ViewModel.Navigation.Select("inbox"), name + ": restore retained tab");
        }
        // Scroll through long natural-size lists on both axes with the public policy.
        owner.FlowDirection = FlowDirection.LeftToRight;
        owner.TabItemSizing = TabItemSizing.Content; owner.TabBarPadding = 4; owner.TabItemSpacing = 6;
        foreach (var position in Enum.GetValues<TabBarPosition>())
        {
            owner.TabBarPosition = position;
            Success(await owner.ViewModel.ReplaceItems(Enumerable.Range(0, 24).Select(index =>
                new NavigationItem(typeof(DocumentViewModel), "Folder " + index, "folder-" + index))), "tabs: add overflow destinations");
            var horizontal = position is TabBarPosition.Top or TabBarPosition.Bottom;
            var selector = horizontal ? child.View.TabSelector : child.View.RailSelector;
            await Ready(selector); await Task.Delay(60);
            var scroll = (ScrollView)selector.Content;
            Check((horizontal ? scroll.HorizontalScrollBarVisibility : scroll.VerticalScrollBarVisibility) == ScrollBarVisibility.Always,
                position + ": public scrollbar policy reaches the active native scroller");
            var last = Descendants(selector).OfType<Button>().Single(button => button.AutomationId == "destination-folder-23");
            await scroll.ScrollToAsync(horizontal ? scroll.Content.Width : 0, horizontal ? 0 : scroll.Content.Height, false);
            await Task.Delay(70);
            var target = NativeCoveragePlatform.Bounds(last); var viewport = NativeCoveragePlatform.Bounds(selector);
            Check(horizontal ? target.Right <= viewport.Right + 2 && target.Left >= viewport.Left - 2
                : target.Bottom <= viewport.Bottom + 2 && target.Top >= viewport.Top - 2,
                position + ": last overflow destination is reachable");
            await CaptureUiAsync("tabs-" + position.ToString().ToLowerInvariant());
        }
    }

    private async Task TabBranchStateAsync(string nesting, PopupLifecycleProbe probe, MauiNavigationHostFactory factory)
    {
        if (nesting == "flyout") Success(await factory.SetRoot<FlyoutDemoViewModel>(host.Window), "tabs: nested root");
        else Success(await factory.SetRoot<TabsDemoViewModel>(host.Window), "tabs: standalone root");
        if (nesting == "modal") Success(await Current.Navigation.NavigateTo<ModalTabsProbeModel>(), "tabs: modal root");
        var root = Root;
        var parentContext = root.ActiveChain().Single(context => context.Current?.View is TabbedViewBase<TabsDemoViewModel> or ModalTabsProbeView);
        var parent = (DemoViewModel)parentContext.Current!.ViewModel; var owner = parentContext.Current.View;
        var child = parentContext.Current.Children!; var first = child.Current!; var firstModel = (DemoViewModel)first.ViewModel;
        var toolbar = root.View.Toolbar;
        first.View.Toolbar = new() { Title = "Inbox", Background = new SolidColorBrush(Colors.Red), Padding = new Thickness(9, 4),
            RightItems = [new() { Icon = "oversize_refresh.png", AccessibilityLabel = "Refresh", Command = new Command(() => { }) }] };
        owner.LoadingPresentationTemplate = new DataTemplate(() => new Label { Text = "App loading", VerticalOptions = LayoutOptions.Center });
        first.View.LoadingBackdrop = new SolidColorBrush(Colors.Pink);
        await Ready(toolbar); await Task.Delay(50);
        var oldAction = Descendants(toolbar).OfType<Button>().Single(button => button.BindingContext is ToolbarButton).Command!;
        first.View.IsBusy = true; await Ready(root.View.BusyContent!);
        Check(root.View.IsBusyPresented && ReferenceEquals(root.View.BusyBackdrop, first.View.LoadingBackdrop), nesting + ": app loader and backdrop");
        Success(await parent.Navigation.Select("archive"), nesting + ": select second tab");
        var second = child.Current!; var secondModel = (DemoViewModel)second.ViewModel;
        second.View.Toolbar = new() { Title = "Archive with a longer heading", Background = new SolidColorBrush(Colors.Blue),
            Padding = new Thickness(20, 4, 10, 4), LeadingSlotWidth = 30, CenterPlacement = ToolbarCenterPlacement.Middle,
            RightItems = [new() { Icon = "oversize_refresh.png", AccessibilityLabel = "Reload" }, new() { Icon = "oversize_phone.png", AccessibilityLabel = "Status" }] };
        await Task.Delay(60);
        Check(!root.View.IsBusyPresented && ReferenceEquals(toolbar.EffectiveBackground, second.View.Toolbar.Background) && !oldAction.CanExecute(null),
            nesting + ": new tab owns toolbar and source loading stays hidden");
        first.View.Toolbar.Background = new SolidColorBrush(Colors.Green);
        Check(ReferenceEquals(toolbar.EffectiveBackground, second.View.Toolbar.Background), nesting + ": inactive tab appearance is ignored");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PopupRequest<string?>? warning = null;
        secondModel.Guard = async () =>
        {
            warning = secondModel.Navigation.RequestPopup<DeferredNoticeModel, string?>(new() { ["caption"] = "tab-" + nesting });
            entered.TrySetResult(); return await release.Task;
        };
        var target = Descendants(child.View.TabSelector).OfType<Button>().Single(button => button.AutomationId == "destination-inbox");
        await Ready(target); NativeCoveragePlatform.TapAt(target);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Check(!probe.Opened.Any(view => view.ViewModel.Caption == "tab-" + nesting) && !root.View.IsBusyPresented,
            nesting + ": native tab press waits for guard without popup or loader");
        release.TrySetResult(false);
        await Until(() => probe.Opened.Any(view => view.ViewModel.Caption == "tab-" + nesting));
        var popup = probe.Opened.Last(view => view.ViewModel.Caption == "tab-" + nesting); await Ready(popup.CountLabel);
        Check(ReferenceEquals(second, child.Current) && ReferenceEquals(toolbar.EffectiveBackground, second.View.Toolbar.Background),
            nesting + ": rejected tab preserves selection and toolbar");
        Success(await popup.ViewModel.Navigation.ClosePopup("explained"), nesting + ": close tab warning");
        Check((await warning!.Completion).PopupResult!.Result == "explained", nesting + ": warning result");
        secondModel.Guard = () =>
        {
            warning = secondModel.Navigation.RequestPopup<DeferredNoticeModel, string?>(new() { ["caption"] = "stale-tab-" + nesting });
            return Task.FromResult(true);
        };
        Success(await parent.Navigation.Select("inbox"), nesting + ": accept switch");
        Check(root.View.IsBusyPresented && ReferenceEquals(root.View.BusyBackdrop, first.View.LoadingBackdrop), nesting + ": retained tab loader restores");
        Success(await parent.Navigation.Select("archive"), nesting + ": rapid retained return");
        Check((await warning!.Completion).Status == PopupRequestStatus.InvalidOrigin && !probe.Opened.Any(view => view.ViewModel.Caption == "stale-tab-" + nesting),
            nesting + ": departed activation never presents a stale warning");
        secondModel.Guard = null; first.View.IsBusy = false;
        var pending = secondModel.Navigation.RequestPopup<DeferredNoticeModel, string?>(new() { ["caption"] = "replace-tab-" + nesting });
        Success(await factory.SetRoot<WelcomeViewModel>(host.Window, options: new() { Mode = RootTransitionMode.Enforced }), nesting + ": force root replacement");
        var ending = await pending.Completion;
        Check(ending.Status == PopupRequestStatus.RootReplaced || ending is { Status: PopupRequestStatus.Completed, PopupResult.Reason: DismissalReason.RootReplaced },
            nesting + ": forced root settles queued or presented warning");
        Check(firstModel.Resource.Disposals == 1 && secondModel.Resource.Disposals == 1 && probe.Subscribers == 0,
            nesting + ": root replacement releases retained tabs and popup subscriptions");
    }

    private async Task FlyoutInsideTabAsync(MauiNavigationHostFactory factory)
    {
        Success(await factory.SetRoot<FlyoutTabProbeModel>(host.Window), "tabs: flyout inside retained tab");
        var root = Root; var tabs = root.Current!.Children!;
        var flyout = root.ActiveChain().Single(context => context.HasFlyout);
        var target = Descendants(tabs.View.TabSelector).OfType<Button>().Single(button => button.AutomationId == "destination-other");
        await Ready(target); target.Focus(); await Task.Delay(40);
        var heldFocus = target.IsFocused;
        flyout.SetFlyout(true); await Task.Delay(60);
        Check(!target.IsFocused && !NativeCoveragePlatform.Enabled(target), "tabs: nested drawer removes tab focus and blocks native tab input");
        Check(AutomationProperties.GetExcludedWithChildren(((Grid)root.View.Content).Children.OfType<Grid>().First()) == true,
            "tabs: nested drawer excludes underlying chrome from accessibility");
        Check(root.LeadingAction == NavigationLeadingAction.Menu, "tabs: one root toolbar owns nested menu action");
        Success(await Current.Navigation.CloseFlyout(), "tabs: close nearest nested flyout");
        await Task.Delay(60);
        Check(NativeCoveragePlatform.Enabled(target), "tabs: closing drawer restores tab input");
        Check(!heldFocus || target.IsFocused, "tabs: closing drawer restores previously held tab focus");
        NativeCoveragePlatform.TapAt(target);
        await Until(() => tabs.SelectedDestinationId == "other" && !root.IsNavigating);
        Check(root.LeadingAction == NavigationLeadingAction.None, "tabs: switching away from flyout removes menu leading action");
    }
}
