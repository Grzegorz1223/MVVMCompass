using MVVMCompass.Core;
using MVVMCompass.Services;

namespace MVVMCompass.Sample;

internal sealed partial class UnifiedNativeCoverage
{
    private void CheckPlacement(NavigationView layout, View shared, TabBarPosition position, SharedContentPosition sharedPosition, string name)
    {
        var selector = NativeCoveragePlatform.Bounds(position is TabBarPosition.Left or TabBarPosition.Right ? layout.RailSelector : layout.TabSelector);
        var body = NativeCoveragePlatform.Bounds(layout.Presenter);
        var viewport = NativeCoveragePlatform.Bounds(layout);
        var onRequestedEdge = position switch
        {
            TabBarPosition.Top => selector.Bottom <= body.Top + 2,
            TabBarPosition.Bottom => selector.Top >= body.Bottom - 2,
            TabBarPosition.Left => selector.Right <= body.Left + 2,
            _ => selector.Left >= body.Right - 2
        };
        Check(onRequestedEdge && selector.Left >= viewport.Left - 2 && selector.Top >= viewport.Top - 2
            && selector.Right <= viewport.Right + 2 && selector.Bottom <= viewport.Bottom + 2,
            name + $": native selector is on the requested edge inside the viewport; selector={selector}, viewport={viewport}");
        CheckSharedPlacement(layout, shared, sharedPosition, name);
    }

    private void CheckSharedPlacement(NavigationView layout, View shared, SharedContentPosition position, string name)
    {
        var panel = NativeCoveragePlatform.Bounds(shared); var body = NativeCoveragePlatform.Bounds(layout.Presenter);
        Check(position == SharedContentPosition.Top ? panel.Bottom <= body.Top + 2 : panel.Top >= body.Bottom - 2,
            name + ": native shared content is above or below the navigating body");
    }

    private async Task AdvancedLayoutsAsync()
    {
        Success(await Current.Navigation.SetRoot<TabsDemoViewModel>(), "prepare layout mutation coverage");
        var parent = (TabsDemoViewModel)Root.Current!.ViewModel;
        var view = (TabsDemoView)Root.Current.View; var child = Root.Current.Children!;
        var originalSelected = view.SelectedTabItemTemplate; var originalUnselected = view.UnselectedTabItemTemplate;
        var brush = new LinearGradientBrush(new GradientStopCollection
        { new(Colors.LightBlue, 0), new(Colors.LightPink, 1) }, new(0, 0), new(1, 1));
        view.TabBarBackground = brush;
        foreach (var position in Enum.GetValues<TabBarPosition>())
        {
            parent.TabPosition = position; parent.ItemSizing = TabItemSizing.Content;
            Success(await parent.ReplaceItems(Enumerable.Range(0, 20).Select(index => new NavigationItem(typeof(DocumentViewModel),
                "Long destination " + index, "overflow-" + index))), position + ": ViewModel adds overflow destinations");
            var selector = position is TabBarPosition.Left or TabBarPosition.Right ? child.View.RailSelector : child.View.TabSelector;
            await Ready(selector); await Task.Delay(100);
            var scroll = (ScrollView)selector.Content;
            var first = Descendants(selector).OfType<Button>().First(button => button.AutomationId?.StartsWith("destination-", StringComparison.Ordinal) == true);
            var horizontal = position is TabBarPosition.Top or TabBarPosition.Bottom;
            await scroll.ScrollToAsync(0, 0, false);
            await Task.Delay(80);
            var before = NativeCoveragePlatform.Bounds(first);
            await scroll.ScrollToAsync(horizontal ? scroll.Content.Width : 0, horizontal ? 0 : scroll.Content.Height, false);
            await Task.Delay(100);
            var after = NativeCoveragePlatform.Bounds(first);
            Check(horizontal ? after.X < before.X - 20 : after.Y < before.Y - 20, position + ": natural-size overflow scrolls native items");
            Check(ReferenceEquals(selector.Background, brush) && NativeCoveragePlatform.Visible(selector), position + ": native strip accepts a gradient brush");
            var retained = child.Current;
            view.SelectedTabItemTemplate = new DataTemplate(() => new Label { Text = "Selected replacement", Padding = 10 });
            view.UnselectedTabItemTemplate = new DataTemplate(() => new Label { Text = "Unselected replacement", Padding = 10 });
            await Task.Delay(60);
            Check(Descendants(selector).OfType<Label>().Any(label => label.Text == "Selected replacement") && ReferenceEquals(retained, child.Current),
                position + ": runtime retemplating preserves the selected model and history");
            view.SelectedTabItemTemplate = originalSelected; view.UnselectedTabItemTemplate = originalUnselected;
        }
        Success(await parent.ReplaceItems(TabsDemoViewModel.Items(false)), "restore normal tabs after overflow coverage");
        parent.TabPosition = TabBarPosition.Bottom; parent.ItemSizing = TabItemSizing.Equal;
        view.HorizontalOptions = LayoutOptions.Center;
        double previousWidth = 0;
        foreach (var fraction in new[] { 0.7, 0.95 })
        {
            view.WidthRequest = Root.View.Width * fraction;
            await Task.Delay(100);
            var buttons = Descendants(child.View.TabSelector).OfType<Button>().Where(button => button.AutomationId?.StartsWith("destination-", StringComparison.Ordinal) == true).ToArray();
            var widths = buttons.Select(button => NativeCoveragePlatform.Bounds(button).Width).ToArray();
            Check(widths.Max() - widths.Min() <= 2 && widths[0] > previousWidth, "equal native shares recompute when available width changes");
            previousWidth = widths[0];
        }
        view.WidthRequest = -1; view.HorizontalOptions = LayoutOptions.Fill;
    }

    private async Task NativePopupBackAsync(DemoViewModel origin)
    {
        var contentContext = Root;
        var showing = origin.Navigation.DisplayPopup<DemoPopupViewModel, string>();
        await Until(() => ViewModelTree.Collect(host.Window.Page, true).OfType<DemoPopupViewModel>().Any());
        var page = host.Window.Navigation.ModalStack[^1];
        var view = Descendants(page).OfType<DemoPopupView>().Single(); var model = view.ViewModel;
        await Ready(view); await Task.Delay(150);
        model.AllowNavigation = false;
        await host.Window.Navigation.PopModalAsync(false);
        await PopupOwnership.NativeBackCompletion(view);
        Check(!model.IsDismissed && !showing.IsCompleted && host.Window.Navigation.ModalStack.Contains(page), "native popup Back is vetoed before dismissal");
        model.AllowNavigation = true;
        await host.Window.Navigation.PopModalAsync(false);
        await PopupOwnership.NativeBackCompletion(view);
        var result = await showing;
        Check(result.Reason == DismissalReason.Back && !result.HasResult && model.Resource.Disposals == 1, "native popup Back closes through Toolkit and cleans its scope once");
        foreach (var outsideTap in new[] { false, true })
        {
            showing = origin.Navigation.DisplayPopup<DemoPopupViewModel, string>();
            await Until(() => ViewModelTree.Collect(host.Window.Page, true).OfType<DemoPopupViewModel>().Any());
            page = host.Window.Navigation.ModalStack[^1];
            view = Descendants(page).OfType<DemoPopupView>().Single(); model = view.ViewModel;
            view.CanBeDismissedByTappingOutsideOfPopup = outsideTap;
            await Ready(view); await Task.Delay(150);
            var guardedModel = model; var guardCalls = 0;
            model.Guard = () => { guardCalls++; return Task.FromResult(guardedModel.AllowNavigation); };
            model.AllowNavigation = false;
            SendPopupBack(); await Until(() => guardCalls > 0); await PopupOwnership.NativeBackCompletion(view);
            Check(!showing.IsCompleted && !model.IsDismissed, $"popup outside={outsideTap}: platform Back reaches CanNavigate");
            if (outsideTap)
            {
                var previousCalls = guardCalls;
                NativeCoveragePlatform.TapPopupOverlay((ContentPage)page);
                await Until(() => guardCalls > previousCalls);
                await PopupOwnership.NativeBackCompletion(view);
                Check(!showing.IsCompleted, "outside-tap guard denial retains the Toolkit popup");
            }
            model.AllowNavigation = true;
            if (outsideTap) NativeCoveragePlatform.TapPopupOverlay((ContentPage)page);
            else SendPopupBack();
            await PopupOwnership.NativeBackCompletion(view);
            result = await showing.WaitAsync(TimeSpan.FromSeconds(12));
            Check(result.WasDismissedByTappingOutsideOfPopup == outsideTap && !result.HasResult && model.Resource.Disposals == 1,
                $"popup outside={outsideTap}: allowed input returns the correct dismissal result and releases its scope");
        }
        showing = origin.Navigation.DisplayPopup<DemoPopupViewModel, string>();
        await Until(() => ViewModelTree.Collect(host.Window.Page, true).OfType<DemoPopupViewModel>().Any());
        model = ViewModelTree.Collect(host.Window.Page, true).OfType<DemoPopupViewModel>().Single();
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        model.Guard = async () => { entered.TrySetResult(); await release.Task; return true; };
        using var cancellation = new CancellationTokenSource();
        var closing = model.Navigation.ClosePopup(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(12)); cancellation.Cancel();
            Check((await closing.WaitAsync(TimeSpan.FromSeconds(12))).Status == NavigationStatus.Cancelled && !showing.IsCompleted,
                "cancelling a popup guard preserves the handled popup");
            model.Guard = null;
            Success(await model.Navigation.ClosePopup(), "popup close remains usable after guard cancellation");
            await showing;
        }
        finally { release.TrySetResult(); }
        void SendPopupBack()
        {
#if ANDROID
            ((AndroidX.Activity.ComponentActivity)host.Window.Handler!.PlatformView!).OnBackPressedDispatcher.OnBackPressed();
#else
            Check(contentContext.RequestPlatformBack(), "popup Back is routed by its window");
#endif
        }
    }
}
