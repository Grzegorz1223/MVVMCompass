using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.Maui.Controls.Shapes;
using MVVMCompass.Core;

namespace MVVMCompass.Sample;

// Runs against real handlers. Each assertion is reported individually by the full smoke runner.
internal sealed class ContentNavigationCoverage(MauiNavigationHost host, Action<bool, string> check, Action<string> skip)
{
    internal static Task RunInteractiveAsync(MauiNavigationHost host, ScenarioLog log) => new ContentNavigationCoverage(host,
        (value, name) => { if (!value) throw new InvalidOperationException(name); log.Write($"PASS {name}"); },
        name => log.Write($"SKIP {name}")).RunAsync();

    internal async Task RunAsync()
    {
        try
        {
            await ToolbarBindingsAsync();
            await ToolbarActionsAsync(false);
            await ToolbarActionsAsync(true);
            await GuardRecoveryAsync();
            await DestinationsAsync();
            await SharedLayoutAsync();
            await AnimationAndFocusAsync();
            await StandaloneAsync();
            await IndependentWindowsAsync();
        }
        finally
        {
            Success(await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true), "restore catalog after coverage");
            ContentNavigationSamples.AssertNoNativeModal(host.Window);
        }
    }

    private void Check(bool value, string name) => check(value, $"Custom coverage: {name}");
    private void Skip(string name) => skip($"Custom coverage: {name}");
    private void Success<T>(NavigationOutcome<T> result, string name) => Check(result.IsSuccess, $"{name} ({result.Status}: {result.Error?.Message})");

    private async Task<NavigationContext> OpenAsync(CoverageModel model, Action<NavigationView>? configure = null)
    {
        var result = await host.ReplaceContentRootAsync(new(Plain(model)), view =>
        {
            view.Toolbar.BackgroundColor = Color.FromArgb("#173A5E"); view.Toolbar.ForegroundColor = Colors.White;
            view.BackgroundColor = Colors.White; configure?.Invoke(view);
        });
        Success(result, "install coverage scope");
        await ReadyAsync(result.Value!.Entry.ViewModel.View);
        return result.Value.Entry.ViewModel;
    }

    private static NavigationDefinition Plain(CoverageModel model) => new([new("root", "Root", Factory(model))]);
    private static ScreenFactory Factory(CoverageModel model, NavigationToolbarDefinition? toolbar = null) =>
        ScreenFactory.Create(() => model, value =>
        {
            var input = new Entry { AutomationId = "CoverageInput", Placeholder = "Retained input" };
            input.SetBinding(Entry.TextProperty, Binding.Create(static (CoverageModel item) => item.Caption, BindingMode.TwoWay));
            return new ViewBase<CoverageModel>(value)
            {
                Toolbar = toolbar,
                Content = new Grid { Padding = 12, RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
                    Children = { input } }
            };
        });

    private async Task ToolbarBindingsAsync()
    {
        var shared = new CoverageModel { Caption = "Host center" };
        var root = new CoverageModel { Caption = "Root body" };
        var header = CaptionLabel("Header"); var footer = CaptionLabel("Footer");
        var context = await OpenAsync(root, view =>
        {
            view.BindingContext = shared; view.HeaderContent = header; view.FooterContent = footer;
            view.ToolbarDefaults.CenterContentTemplate = new DataTemplate(() => CaptionLabel("HostCenter"));
        });
        var toolbar = context.View.Toolbar;
        var hostCenter = Find<Label>(toolbar, "HostCenter");
        Check(hostCenter.Text == "Host center" && header.Text == "Host center" && footer.Text == "Host center", "host template and shared panels bind to host model");
        var detail = new CoverageModel { Caption = "Screen center" };
        var definition = new NavigationToolbarDefinition { CenterContentTemplate = new DataTemplate(() => CaptionLabel("ScreenCenter")) };
        Success(await context.PushAsync(Factory(detail, definition)), "push templated screen");
        var screenCenter = Find<Label>(toolbar, "ScreenCenter");
        Check(screenCenter.Text == "Screen center" && ReferenceEquals(screenCenter.BindingContext, detail), "screen center template owns screen bindings");
        context.View.BindingContext = new CoverageModel { Caption = "New host" };
        detail.Caption = "Edited screen";
        Check(screenCenter.Text == "Edited screen" && header.Text == "New host", "host replacement does not steal screen bindings");
        definition.CenterContentTemplate = new DataTemplate(() => CaptionLabel("ReplacementCenter"));
        var replacement = Find<Label>(toolbar, "ReplacementCenter");
        await ReadyAsync(replacement);
        Check(replacement.Text == "Edited screen" && !ReferenceEquals(replacement, screenCenter), "new center template preserves the screen model");
        Success(await context.BackAsync(), "back from templated screen");
        Check(ReferenceEquals(hostCenter, Find<Label>(toolbar, "HostCenter")) && hostCenter.Text == "New host", "Back restores cached host center and updated binding owner");
        var overrideCenter = CaptionLabel("ExplicitCenter");
        overrideCenter.BindingContext = new CoverageModel { Caption = "Independent content" };
        Success(await context.PushAsync(Factory(new CoverageModel(), new() { CenterContent = overrideCenter })), "push explicit center owner");
        Check(overrideCenter.Text == "Independent content", "explicit custom center binding context is retained");
        context.View.ToolbarDefaults.IsVisible = false;
        Check(!toolbar.IsVisible, "toolbar visibility inherits host state");
        context.Current!.View.Toolbar!.IsVisible = true;
        Check(toolbar.IsVisible, "screen can override hidden host toolbar");
        Success(await context.BackAsync(), "Back while host toolbar is hidden");
        Check(!toolbar.IsVisible, "Back restores toolbar visibility defaults");
    }

    private async Task ToolbarActionsAsync(bool templated)
    {
        var model = new CoverageModel { Caption = "Action", ActionLabel = "Perform coverage action", ActionParameter = new object() };
        object? received = null; var executions = 0;
        model.Action = new Command<object>(value => { received = value; executions++; });
        var context = await OpenAsync(new CoverageModel());
        var item = BoundAction();
        var definition = new NavigationToolbarDefinition { Title = "Actions", RightItems = new() { item },
            RightItemTemplate = templated ? new DataTemplate(ActionTemplate) : null };
        Success(await context.PushAsync(Factory(model, definition)), $"push actions, template={templated}");
        var toolbar = context.View.Toolbar;
        var button = Action(toolbar, "Action");
        await ReadyAsync(button);
        button.Command!.Execute(button.CommandParameter);
        Check(executions == 1 && ReferenceEquals(received, model.ActionParameter), $"action parameter forwarding, template={templated}");
        model.Caption = "Renamed"; model.ActionLabel = "Updated action label";
        await FramesAsync();
        var nativeLabel = NativeCoveragePlatform.Label(button);
        Check(button.Text == "Renamed" && nativeLabel == "Updated action label", $"native text and accessibility updates, template={templated}, text={button.Text}, label={nativeLabel}");
        Check(button.Width >= 44 && button.Height >= 44, $"default action touch target, template={templated}");
        model.ActionEnabled = false; await FramesAsync();
        Check(!button.IsEnabled && !NativeCoveragePlatform.Enabled(button) && !button.Command.CanExecute(button.CommandParameter), $"disabled action reaches native control, template={templated}");
        button.Command.Execute(button.CommandParameter); Check(executions == 1, "disabled action does not execute");
        model.ActionEnabled = true; model.ActionVisible = false; await FramesAsync();
        Check(!button.IsVisible && !NativeCoveragePlatform.Visible(button) && !button.Command.CanExecute(button.CommandParameter), $"hidden action reaches native control, template={templated}");
        model.ActionVisible = true; model.ActionParameter = new object(); await FramesAsync();
        button.Command.Execute(button.CommandParameter);
        Check(executions == 2 && ReferenceEquals(received, model.ActionParameter), "restored action uses updated parameter");
        var stale = button.Command;
        definition.RightItems = new();
        await FramesAsync(); Check(!stale.CanExecute(null) && !Descendants(toolbar).Contains(button), "empty replacement collection removes and disables old actions");
        definition.RightItems.Add(new() { Text = "Added", Command = model.Action, AccessibilityLabel = "Added action" });
        await ReadyAsync(Action(toolbar, "Added"));
        Check(Action(toolbar, "Added").Command!.CanExecute(null), "observable action addition is usable");
        definition.RightItems.Clear();
        for (var index = 0; index < 6; index++) definition.RightItems.Add(new()
        { Text = $"Action {index}", Command = model.Action, AccessibilityLabel = $"Action {index}" });
        context.View.WidthRequest = 280; context.View.HorizontalOptions = LayoutOptions.Start;
        await UntilAsync(() => Math.Abs(context.View.Width - 280) < 1, "constrained toolbar layout");
        await FramesAsync();
        var scroller = Descendants(toolbar).OfType<ScrollView>().Single();
        var title = Descendants(toolbar).OfType<Label>().Single(label => label.Text == "Actions");
        var toolbarBounds = NativeCoveragePlatform.Bounds(toolbar); var titleBounds = NativeCoveragePlatform.Bounds(title);
        Check(Math.Abs(titleBounds.Center.X - toolbarBounds.Center.X) <= 1,
            $"center remains centered at narrow width, template={templated}; toolbar={toolbarBounds}, title={titleBounds}, right={NativeCoveragePlatform.Bounds(scroller)}");
        Check(scroller.Width <= toolbar.Width * .4 + 1 && scroller.Content.Width > scroller.Width, "right actions have bounded horizontal overflow");
        Check(titleBounds.Right <= NativeCoveragePlatform.Bounds(scroller).Left + 1, "center does not overlap right action area");
        await scroller.ScrollToAsync(scroller.Content.Width, 0, false);
        await FramesAsync(); Check(scroller.ScrollX > 0, "overflow actions can be scrolled into view");
        context.View.ClearValue(VisualElement.WidthRequestProperty); context.View.HorizontalOptions = LayoutOptions.Fill;
        await FramesAsync();
        Check(ReferenceEquals(toolbar, context.View.Toolbar), "resizing and action updates retain toolbar instance");
        context.View.Toolbar.LeadingButtonTemplate = new DataTemplate(() =>
        {
            var leading = new Button { AutomationId = "CoverageLeading", MinimumWidthRequest = 44, MinimumHeightRequest = 44 };
            leading.SetBinding(Button.TextProperty, Binding.Create(static (NavigationToolbar source) => source.LeadingText));
            leading.SetBinding(Button.CommandProperty, Binding.Create(static (NavigationToolbar source) => source.LeadingCommand));
            leading.SetBinding(SemanticProperties.DescriptionProperty, Binding.Create(static (NavigationToolbar source) => source.LeadingText));
            return leading;
        });
        var leadingButton = Find<Button>(toolbar, "CoverageLeading"); await ReadyAsync(leadingButton);
        Check(leadingButton.Text == "Back" && NativeCoveragePlatform.Label(leadingButton) == "Back", "custom leading template exposes Back semantics");
        leadingButton.Command!.Execute(null);
        await UntilAsync(() => model.IsDismissed && !context.IsNavigating, "custom leading Back command");
        Check(!context.CanGoBack, "custom leading template executes library navigation");
    }

    private async Task GuardRecoveryAsync()
    {
        var context = await OpenAsync(new CoverageModel()); var model = new CoverageModel();
        Success(await context.PushAsync(Factory(model)), "push guard test screen");
        var screen = context.Current; var entered = Signal(); var release = Signal();
        model.Guard = async () => { entered.TrySetResult(); await release.Task; return true; };
        using var cancellation = new CancellationTokenSource();
        var pending = context.BackAsync(cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Check(!context.View.Toolbar.LeadingCommand.CanExecute(null), "pending native guard disables toolbar navigation");
            var duplicate = await context.BackAsync(new() { RejectIfBusy = true });
            Check(duplicate.Status == NavigationStatus.Busy && model.GuardCalls == 1, "duplicate Back cannot enter pending guard");
            cancellation.Cancel();
        }
        finally { release.TrySetResult(); }
        Check((await pending).Status == NavigationStatus.Cancelled && ReferenceEquals(screen, context.Current) && !model.IsDismissed, "native cancellation preserves current screen");
        model.Guard = () => throw new InvalidOperationException("Coverage guard failure");
        var failed = await context.BackAsync();
        Check(failed.Status == NavigationStatus.Failed && !failed.HasCommitted && ReferenceEquals(screen, context.Current), "native guard exception leaves state unchanged");
        NavigationStatus? inner = null;
        model.Guard = async () => { inner = (await model.Navigator!.BackAsync()).Status; return false; };
        var denied = await context.BackAsync();
        Check(denied.Status == NavigationStatus.GuardRejected && inner == NavigationStatus.Reentrant, "native guard reentrancy is rejected without deadlock");
        model.Guard = null; model.Allowed = false;
        context.View.IsBackSwipeEnabled = true;
        var swipe = (SwipeGestureRecognizer)context.View.Presenter.GestureRecognizers.Single();
        swipe.SendSwiped(context.View.Presenter, SwipeDirection.Right); await context.NativeBackCompletion;
        Check(ReferenceEquals(screen, context.Current), "public smoke exercises guarded body swipe denial");
        model.Allowed = true;
        swipe.SendSwiped(context.View.Presenter, SwipeDirection.Right); await context.NativeBackCompletion;
        Check(model.IsDismissed && !context.CanGoBack, "public smoke exercises guarded body swipe acceptance");
    }

    private async Task DestinationsAsync()
    {
        var roots = new List<CoverageModel>();
        ScreenFactory Fresh() => ScreenFactory.Create(() => { var model = new CoverageModel(); roots.Add(model); return model; },
            model => new ViewBase<CoverageModel>(model) { Content = new Label { Text = "Destination" } });
        var result = await host.ReplaceContentRootAsync(new(new NavigationDefinition([
            new("group", "Group", new NavigationDestination[] { new("a", "A", Fresh()), new("b", "B", Fresh()) }),
            new("c", "C", Fresh())], NavigationPresentation.Flyout, "a")));
        Success(result, "install reset/removal destinations");
        var context = result.Value!.Entry.ViewModel; await ReadyAsync(context.View);
        var root = context.Current!; var first = new CoverageModel(); var second = new CoverageModel();
        Success(await context.PushAsync(Factory(first)), "push first reset detail");
        Success(await context.PushAsync(Factory(second)), "push second reset detail");
        first.Allowed = false;
        Check((await context.PopToRootAsync()).Status == NavigationStatus.GuardRejected && !second.IsDismissed, "native pop-to-root is atomic on guard denial");
        first.Allowed = true; Success(await context.PopToRootAsync(), "native pop-to-root");
        Check(ReferenceEquals(root, context.Current) && first.Dismissals == 1 && second.Dismissals == 1, "pop-to-root retains root and disposes details once");
        Success(await context.ResetDestinationAsync("a"), "native active destination recreation");
        Check(root.ViewModel.IsDismissed && !ReferenceEquals(root, context.Current), "reset installs a fresh root");
        var reset = context.Current;
        Success(await context.SelectAsync("b"), "visit second tab");
        Success(await context.ResetDestinationAsync("a"), "reset inactive destination");
        var count = roots.Count;
        Check(reset!.ViewModel.IsDismissed && context.SelectedDestinationId == "b", "inactive reset leaves selection unchanged");
        Success(await context.SelectDefaultAsync(), "select default after inactive reset");
        Check(roots.Count == count + 1 && context.SelectedDestinationId == "a", "inactive root is recreated lazily");
        context.View.TabSelector.ItemTemplate = new DataTemplate(() =>
        {
            var button = new Button { MinimumHeightRequest = 44 };
            button.SetBinding(Button.TextProperty, Binding.Create(static (NavigationItemContext item) => item.Title));
            button.SetBinding(Button.CommandProperty, Binding.Create(static (NavigationItemContext item) => item.SelectCommand));
            return button;
        });
        var selector = Action(context.View.TabSelector, "B"); await ReadyAsync(selector);
        var metadata = context.TabItems.Single(item => item.Id == "b");
        metadata.IsVisible = false; await FramesAsync(); Check(!NativeCoveragePlatform.Visible(selector), "selector template visibility reaches native control");
        metadata.IsVisible = true; metadata.IsEnabled = false; await FramesAsync(); Check(!NativeCoveragePlatform.Enabled(selector), "selector template availability reaches native control");
        metadata.IsEnabled = true; selector.Command!.Execute(null);
        await UntilAsync(() => context.SelectedDestinationId == "b" && !context.IsNavigating, "templated selector command");
        Check(metadata.IsSelected, "templated selector uses library selection state");
        var active = (CoverageModel)context.Current!.ViewModel; active.Allowed = false;
        Check((await context.RemoveDestinationAsync("group")).Status == NavigationStatus.GuardRejected && context.SelectedDestinationId == "b", "group removal guard preserves native selection");
        active.Allowed = true; Success(await context.RemoveDestinationAsync("group"), "native group removal");
        Check(context.SelectedDestinationId == "c" && context.TabItems.Count == 0 && active.IsDismissed, "group removal cleans histories and selects survivor");
        Check((await context.RemoveDestinationAsync("c")).Status == NavigationStatus.Failed && context.IsActive, "last native destination cannot be removed");
    }

    private async Task SharedLayoutAsync()
    {
        var shared = new CoverageModel { Caption = "Shared panels" };
        var header = CaptionLabel("Header"); var footer = CaptionLabel("Footer");
        var context = await OpenAsync(new CoverageModel(), view =>
        { view.BindingContext = shared; view.HeaderContent = header; view.FooterContent = footer; });
        var toolbar = context.View.Toolbar; var headerParent = header.Parent; var footerParent = footer.Parent;
        var model = new CoverageModel { Allowed = false };
        Success(await context.PushAsync(Factory(model)), "push shared-layout detail");
        context.View.IsBusy = true; await FramesAsync();
        var busy = Descendants(context.View).OfType<ActivityIndicator>().Single();
        Check(NativeCoveragePlatform.Visible(busy) && ((VisualElement)busy.Parent).InputTransparent, "busy indicator is visible and does not capture input");
        Check((await context.BackAsync()).Status == NavigationStatus.GuardRejected, "busy state preserves CanNavigate denial");
        model.Allowed = true; Success(await context.BackAsync(), "Back remains available while visually busy");
        context.View.IsBusy = false;
        Check(ReferenceEquals(header.Parent, headerParent) && ReferenceEquals(footer.Parent, footerParent) && ReferenceEquals(context.View.Toolbar, toolbar), "shared header/footer and toolbar remain mounted across navigation");
        var bodyClicks = 0; var toolbarClicks = 0;
        context.View.ToolbarDefaults.RightItems = new() { new() { Text = "Outside", Command = new Command(() => toolbarClicks++) } };
        var overlay = new Button { Text = "Body overlay", BackgroundColor = Colors.LightBlue, Command = new Command(() => bodyClicks++) };
        context.View.BodyOverlayContent = overlay; await ReadyAsync(overlay); await FramesAsync();
        Check(Close(NativeCoveragePlatform.Bounds(overlay), NativeCoveragePlatform.Bounds(context.View.Presenter)), "body overlay is confined to content bounds");
        Check(NativeCoveragePlatform.Bounds(overlay).Top >= NativeCoveragePlatform.Bounds(toolbar).Bottom, "body overlay leaves toolbar uncovered");
        NativeCoveragePlatform.TapAt(context.View.Presenter);
        await FramesAsync();
        NativeCoveragePlatform.TapAt(Action(toolbar, "Outside"));
        await FramesAsync();
        Check(bodyClicks == 1 && toolbarClicks == 1,
            $"body overlay receives native input while toolbar remains usable; body={bodyClicks}, toolbar={toolbarClicks}, action={NativeCoveragePlatform.Bounds(Action(toolbar, "Outside"))}, scroller={NativeCoveragePlatform.Bounds(Descendants(toolbar).OfType<ScrollView>().Single())}");
        var bodySurface = (ContentView)overlay.Parent;
        context.View.BodyOverlayContent = null;
        Check(bodySurface.InputTransparent && !bodySurface.IsVisible, "removed body overlay stops intercepting input");
        var fullClicks = 0;
        var full = new Button { Text = "Full overlay", BackgroundColor = Colors.LightBlue, Command = new Command(() => fullClicks++) };
        context.View.OverlayContent = full; await ReadyAsync(full); await FramesAsync();
        Check(Close(NativeCoveragePlatform.Bounds(full), NativeCoveragePlatform.Bounds(context.View)), "full overlay covers the toolbar and body");
        NativeCoveragePlatform.TapAt(Action(toolbar, "Outside"));
        await FramesAsync();
        Check(fullClicks == 1 && toolbarClicks == 1, "full overlay receives native input above toolbar");
        context.View.OverlayContent = null; await FramesAsync();
        Check(full.Parent == null && context.View.OverlayContent == null, "full overlay content detaches after dismissal");
        shared.Caption = "Updated shared panels";
        Check(header.Text == "Updated shared panels" && footer.Text == "Updated shared panels", "shared panel bindings remain live");
        NativeCoveragePlatform.CheckSafeArea(context, footer, Check);
    }

    private async Task AnimationAndFocusAsync()
    {
        var root = new CoverageModel { Caption = "Retained keyboard input" };
        var context = await OpenAsync(root, view => view.Presenter.TransitionDuration = 500);
        var input = Find<Entry>(context.Current!.View, "CoverageInput"); await ReadyAsync(input);
        Check(input.Focus(), "body entry accepts native focus");
        await UntilAsync(() => input.IsFocused, "focused body entry");
        var keyboard = await input.ShowSoftInputAsync(CancellationToken.None);
        if (keyboard) { await UntilAsync(input.IsSoftInputShowing, "soft keyboard"); Check(true, "soft keyboard is shown before navigation"); }
        else Skip("soft keyboard display is unavailable on this device; focus assertions still run");
        root.Allowed = false;
        Check((await context.PushAsync(Factory(new CoverageModel()))).Status == NavigationStatus.GuardRejected && input.IsFocused, "guard denial preserves body focus and input");
        root.Allowed = true;
        var toolbar = context.View.Toolbar; var bounds = NativeCoveragePlatform.Bounds(toolbar); var outgoing = context.Current!.View;
        var detail = new CoverageModel { Caption = "Animated detail" };
        var push = context.PushAsync(Factory(detail));
        var samples = await ObserveAnimationAsync(push, context, outgoing, bounds);
        Success(await push, "animated push");
        Check(samples > 0 && !input.IsFocused, "animated push moves body and releases outgoing focus");
        outgoing = context.Current!.View;
        var back = context.BackAsync(); samples = await ObserveAnimationAsync(back, context, outgoing, bounds);
        Success(await back, "animated Back");
        Check(samples > 0 && ReferenceEquals(input, Find<Entry>(context.Current!.View, "CoverageInput")) && input.Text == "Retained keyboard input", "animated Back restores the same editable body");
        await input.HideSoftInputAsync(CancellationToken.None);
    }

    private async Task<int> ObserveAnimationAsync(Task navigation, NavigationContext context, View outgoing, Rect toolbarBounds)
    {
        var movingFrames = 0; var stable = true; var clipped = true;
        while (!navigation.IsCompleted)
        {
            await Task.Delay(16);
            stable &= Close(NativeCoveragePlatform.Bounds(context.View.Toolbar), toolbarBounds)
                && context.View.Toolbar.TranslationX == 0 && context.View.Toolbar.TranslationY == 0;
            if (Math.Abs(outgoing.TranslationX) > .1)
            {
                movingFrames++;
                var bodies = (Grid)context.View.Presenter.Content;
                clipped &= bodies.Children.Count == 2 && bodies.Clip is RectangleGeometry clip
                    && Close(clip.Rect, new Rect(0, 0, context.View.Presenter.Width, context.View.Presenter.Height));
            }
        }
        Check(stable, "toolbar stays fixed during every sampled transition frame");
        Check(clipped && movingFrames > 0, "both animated bodies stay inside a sized clipping region");
        return movingFrames;
    }

    private async Task StandaloneAsync()
    {
        var model = new CoverageModel { Caption = "Standalone bound title", ActionLabel = "Standalone action" }; var count = 0;
        model.Action = new Command(() => count++);
        var toolbar = new NavigationToolbar { BindingContext = model, Definition = new() { CenterContentTemplate = new DataTemplate(() => CaptionLabel("StandaloneCenter")), RightItems = new() { BoundAction() } } };
        var result = await host.ReplaceRootAsync(new NavigationRequestOptions(), () => new object(),
            _ => new ContentPage { Content = new VerticalStackLayout { Children = { toolbar } } });
        Success(result, "install standalone toolbar in regular content");
        await ReadyAsync(toolbar); var button = Action(toolbar, model.Caption); await ReadyAsync(button);
        button.Command!.Execute(null);
        Check(count == 1 && Find<Label>(toolbar, "StandaloneCenter").Text == model.Caption && !toolbar.LeadingCommand.CanExecute(null), "standalone toolbar binds and executes without a navigation scope");
        model.ActionEnabled = false; await FramesAsync();
        Check(!NativeCoveragePlatform.Enabled(button), "standalone action availability reaches native control");
    }

    private async Task IndependentWindowsAsync()
    {
#if WINDOWS
        var primary = await OpenAsync(new CoverageModel());
        var secondaryWindow = new Window(new ContentPage()) { Title = "Navigation coverage window" };
        var factory = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<MauiNavigationHostFactory>(host.Window.Handler!.MauiContext!.Services);
        var secondaryHost = factory.ForWindow(secondaryWindow);
        Application.Current!.OpenWindow(secondaryWindow);
        try
        {
            await UntilAsync(() => secondaryWindow.Handler != null, "secondary native window");
            var secondary = await secondaryHost.ReplaceContentRootAsync(new(Plain(new CoverageModel())));
            Success(secondary, "install second window navigation");
            var second = secondary.Value!.Entry.ViewModel; await ReadyAsync(second.View);
            var firstDetail = new CoverageModel(); var secondDetail = new CoverageModel();
            Success(await primary.PushAsync(Factory(firstDetail)), "primary window detail");
            Success(await second.PushAsync(Factory(secondDetail)), "secondary window detail");
            var modal = await primary.OpenModalAsync(Plain(new CoverageModel())); Success(modal, "primary window modal");
            Check(secondaryWindow.Navigation.ModalStack.Count == 0 && second.IsActive, "modal stays in its owning native window");
            Success(await second.BackAsync(), "Back in secondary window");
            Check(secondDetail.IsDismissed && !firstDetail.IsDismissed && modal.Value!.IsActive, "native window histories are independent");
            Success(await modal.Value!.CloseModalAsync(), "close primary window modal");
        }
        finally
        {
            Application.Current!.CloseWindow(secondaryWindow);
            await secondaryHost.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Check(primary.IsActive && primary.CanGoBack, "closing secondary native window preserves primary history");
#else
        Skip("native multiple-window coverage requires the Windows sample target; managed window isolation runs on all hosts");
#endif
    }

    private static ToolbarButton BoundAction()
    {
        var item = new ToolbarButton();
        item.SetBinding(ToolbarButton.TextProperty, Binding.Create(static (CoverageModel model) => model.Caption));
        item.SetBinding(ToolbarButton.CommandProperty, Binding.Create(static (CoverageModel model) => model.Action));
        item.SetBinding(ToolbarButton.CommandParameterProperty, Binding.Create(static (CoverageModel model) => model.ActionParameter));
        item.SetBinding(ToolbarButton.IsEnabledProperty, Binding.Create(static (CoverageModel model) => model.ActionEnabled));
        item.SetBinding(ToolbarButton.IsVisibleProperty, Binding.Create(static (CoverageModel model) => model.ActionVisible));
        item.SetBinding(ToolbarButton.AccessibilityLabelProperty, Binding.Create(static (CoverageModel model) => model.ActionLabel));
        return item;
    }

    private static object ActionTemplate()
    {
        var button = new Button { MinimumWidthRequest = 44, MinimumHeightRequest = 44, Padding = 4 };
        button.SetBinding(Button.TextProperty, Binding.Create(static (ToolbarButton item) => item.Text));
        button.SetBinding(Button.CommandProperty, Binding.Create(static (ToolbarButton item) => item.Command));
        button.SetBinding(Button.CommandParameterProperty, Binding.Create(static (ToolbarButton item) => item.CommandParameter));
        button.SetBinding(VisualElement.IsEnabledProperty, Binding.Create(static (ToolbarButton item) => item.IsEnabled));
        button.SetBinding(SemanticProperties.DescriptionProperty, Binding.Create(static (ToolbarButton item) => item.AccessibilityLabel));
        return button;
    }
    private static Label CaptionLabel(string id)
    {
        var label = new Label { AutomationId = id, HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center, HeightRequest = 36, LineBreakMode = LineBreakMode.TailTruncation };
        label.SetBinding(Label.TextProperty, Binding.Create(static (CoverageModel model) => model.Caption)); return label;
    }
    private static T Find<T>(Element parent, string id) where T : Element => Descendants(parent).OfType<T>().Single(item => item.AutomationId == id);
    private static Button Action(Element parent, string text) => Descendants(parent).OfType<Button>().Single(item => item.Text == text);
    private static IEnumerable<Element> Descendants(Element parent)
    {
        yield return parent;
        foreach (var child in ((IElementController)parent).LogicalChildren)
            foreach (var item in Descendants(child)) yield return item;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task ReadyAsync(VisualElement view) => UntilAsync(() => view.IsLoaded && view.Width > 0 && view.Height > 0, $"layout of {view.AutomationId ?? view.GetType().Name}");
    private static async Task FramesAsync() { await Task.Delay(32); }
    private static bool Close(Rect first, Rect second) => Math.Abs(first.X - second.X) <= 1 && Math.Abs(first.Y - second.Y) <= 1
        && Math.Abs(first.Width - second.Width) <= 1 && Math.Abs(first.Height - second.Height) <= 1;
    private static async Task UntilAsync(Func<bool> ready, string name)
    {
        var watch = Stopwatch.StartNew();
        while (!ready()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"Coverage timed out: {name}"); await Task.Delay(16); }
    }

    internal sealed class CoverageModel : ViewModelBase
    {
        private string caption = string.Empty, actionLabel = string.Empty;
        private bool enabled = true, visible = true;
        private object? parameter; private ICommand? action;
        public string Caption { get => caption; set => SetProperty(ref caption, value); }
        public string ActionLabel { get => actionLabel; set => SetProperty(ref actionLabel, value); }
        public bool ActionEnabled { get => enabled; set => SetProperty(ref enabled, value); }
        public bool ActionVisible { get => visible; set => SetProperty(ref visible, value); }
        public object? ActionParameter { get => parameter; set => SetProperty(ref parameter, value); }
        public ICommand? Action { get => action; set => SetProperty(ref action, value); }
        internal bool Allowed = true;
        internal int GuardCalls, Dismissals;
        internal Func<Task<bool>>? Guard;
        public override Task<bool> CanNavigate() { GuardCalls++; return Guard?.Invoke() ?? Task.FromResult(Allowed); }
        public override Task AfterDismissed() { Dismissals++; return Task.CompletedTask; }
    }
}
