using System.Reflection;
using System.Text.Json;
using MVVMCompass.Core;

namespace MVVMCompass.Sample;

public sealed class SmokeRunner(IServiceProvider services, MauiNavigationHostFactory factory, ScenarioLog log)
{
    public async Task RunAsync(Window window)
    {
        List<string> checks = [], skipped = [];
        string? failure = null;
        var suite = Environment.GetEnvironmentVariable("MVVMCOMPASS_SMOKE_SUITE") ?? "full";
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks.Add(name); log.Write("SMOKE PASS: " + name);
        }
        try
        {
            Check(MainThread.IsMainThread, "Unified: native UI dispatcher");
            if (suite != "unified")
            {
                var regression = services.GetRequiredService<RegressionSmokeRunner>();
                await regression.RunAsync(writeReport: false);
                checks.AddRange(regression.Checks); skipped.AddRange(regression.Skipped);
                if (regression.Failure != null) throw new InvalidOperationException(regression.Failure);
            }
            var host = factory.ForWindow(window);
            var registry = services.GetRequiredService<MVVMCompass.Services.RegisteredScreenFactory>();
            var root = await host.ReplaceContentRootAsync(new(registry.Definition(typeof(WelcomeViewModel))));
            Check(root.IsSuccess, "Unified: initialize registered XAML sample");
            await new UnifiedNativeCoverage(host, Check).RunAsync();
        }
        catch (Exception error) { failure = error.ToString(); log.Write("SMOKE FAILED: " + error); }
        var report = JsonSerializer.Serialize(new SmokeResult(failure == null ? "passed" : "failed", checks, failure,
            DeviceInfo.Platform.ToString(), DeviceInfo.VersionString,
            typeof(ViewModelBase).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        { Suite = suite, Skipped = skipped }, SmokeResultJsonContext.Default.SmokeResult);
        var output = Environment.GetEnvironmentVariable("MVVMCOMPASS_SMOKE_OUTPUT")
            ?? Path.Combine(FileSystem.AppDataDirectory, "smoke-result.json");
        File.WriteAllText(output, report);
#if ANDROID
        if (Guid.TryParseExact(Environment.GetEnvironmentVariable("MVVMCOMPASS_SMOKE_RUN_ID"), "N", out var runId))
        {
            var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(report));
            const int size = 3000;
            var count = (encoded.Length + size - 1) / size;
            for (var index = 0; index < count; index++)
                Android.Util.Log.Info("MVVMCompassSmoke", $"{runId:N}:{index}/{count}:" + encoded.Substring(index * size, Math.Min(size, encoded.Length - index * size)));
        }
#endif
        log.Write($"SMOKE {(failure == null ? "PASSED" : "FAILED")}: {checks.Count} checks");
    }
}

internal sealed partial class UnifiedNativeCoverage(MauiNavigationHost host, Action<bool, string> check)
{
    private NavigationContext Root => host.CurrentContentNavigation!;
    private DemoViewModel Current => (DemoViewModel)Root.Deepest.Current!.ViewModel;
    private void Check(bool value, string message) => check(value, "Unified: " + message);
    private void Success(NavigationResult result, string message) => Check(result.IsSuccess, message + $" ({result.Status}: {result.Error})");

    internal async Task RunAsync()
    {
        await TabsAsync();
        await AdvancedLayoutsAsync();
        await FlyoutAsync();
        await ModalAndPopupAsync();
        await GuardsAsync();
        Success(await Current.Navigation.SetRoot<WelcomeViewModel>(), "restore sample home");
        Check(host.Window.Navigation.ModalStack.Count == 0, "no modal or popup left behind");
    }

    private async Task TabsAsync()
    {
        Success(await Current.Navigation.SetRoot<TabsDemoViewModel>(), "registered tabs root");
        var context = Root; var parent = (TabsDemoViewModel)context.Current!.ViewModel;
        var view = (TabsDemoView)context.Current.View; var child = context.Current.Children!;
        var first = (DocumentViewModel)child.Current!.ViewModel; var toolbar = context.View.Toolbar;
        var center = view.ToolbarCenterContent; var shared = view.SharedContent;
        await Ready(toolbar); await Ready(child.View.TabSelector);
        Check(first.Caption == "Inbox" && ReferenceEquals(first.ParentViewModel, parent), "parameters and constructor parent from scoped registration");
        foreach (var position in Enum.GetValues<TabBarPosition>())
        foreach (var sizing in Enum.GetValues<TabItemSizing>())
        foreach (var sharedPosition in Enum.GetValues<SharedContentPosition>())
        {
            parent.TabPosition = position; parent.ItemSizing = sizing; parent.SharedPosition = sharedPosition;
            var selector = position is TabBarPosition.Left or TabBarPosition.Right ? child.View.RailSelector : child.View.TabSelector;
            await Ready(selector); await Task.Delay(80);
            var label = $"{position}/{sizing}/{sharedPosition}";
            CheckPlacement(child.View, shared!, position, sharedPosition, label);
            var buttons = Descendants(selector).OfType<Button>().Where(button => button.AutomationId?.StartsWith("destination-", StringComparison.Ordinal) == true).ToArray();
            Check(buttons.Length == 3 && buttons.All(button => button.AutomationId != "destination-hidden"), label + ": hidden items consume no selector slot");
            var inbox = buttons.Single(button => button.AutomationId == "destination-inbox");
            var archive = buttons.Single(button => button.AutomationId == "destination-archive");
            await Ready(archive);
            Check(NativeCoveragePlatform.Label(archive)?.Contains("Archive", StringComparison.Ordinal) == true, label + ": accessible template input");
            if (sizing == TabItemSizing.Equal)
            {
                var a = NativeCoveragePlatform.Bounds(inbox); var b = NativeCoveragePlatform.Bounds(archive);
                Check(Math.Abs(position is TabBarPosition.Top or TabBarPosition.Bottom ? a.Width - b.Width : a.Height - b.Height) <= 2, label + ": equal native item shares");
                var grid = (Grid)selector.Content;
                var horizontal = position is TabBarPosition.Top or TabBarPosition.Bottom;
                var available = horizontal ? grid.Width : grid.Height;
                var reserved = horizontal ? view.TabBarCenterContent!.Width + view.TabBarTrailingContent!.Width
                    : view.TabBarCenterContent!.Height + view.TabBarTrailingContent!.Height;
                var expected = (available - reserved - (buttons.Length + 1) * (horizontal ? grid.ColumnSpacing : grid.RowSpacing)) / buttons.Length;
                Check(Math.Abs((horizontal ? a.Width : a.Height) - expected) <= 2, label + $": equal shares reserve center, trailing, padding, and gaps ({expected})");
            }
            Check(NativeCoveragePlatform.Enabled(archive), label + $": input enabled; active={child.IsActive}, pending={child.IsNavigating}, canExecute={archive.Command!.CanExecute(null)}, bounds={NativeCoveragePlatform.Bounds(archive)}");
            NativeCoveragePlatform.TapAt(archive);
            await Until(() => child.SelectedDestinationId == "archive" && !context.IsNavigating);
            Check(child.Current!.ViewModel is DocumentViewModel { Caption: "Archive" } && !ReferenceEquals(first, child.Current.ViewModel), label + ": native template press selects repeated VM by ID");
            Check(ReferenceEquals(center, view.ToolbarCenterContent) && ReferenceEquals(toolbar, context.View.Toolbar), label + ": persistent toolbar and center");
            Check(ReferenceEquals(shared, view.SharedContent) && ReferenceEquals(shared!.BindingContext, parent), label + ": shared content retains parent binding");
            Check(ReferenceEquals(selector.Background, view.TabBarBackground), label + ": strip background includes unused space");
            Success(await parent.Navigation.Select("inbox"), label + ": restore first destination");
        }
        Success(await parent.Navigation.NavigateTo<DemoDetailViewModel>(), "push detail from shared parent service");
        var detail = Current;
        Check(ReferenceEquals(toolbar, context.View.Toolbar) && ReferenceEquals(center, view.ToolbarCenterContent), "body push retains toolbar");
        Success(await parent.Navigation.Select("archive"), "select other tab while detail is retained");
        Check((await detail.Navigation.NavigateBack()).Status == NavigationStatus.InvalidOrigin, "inactive detail service is rejected");
        Success(await parent.Navigation.Select("inbox"), "restore independent tab history");
        Check(ReferenceEquals(Current, detail), "tab history restores the same detail instance");
        Success(await parent.Navigation.NavigateBack(), "shared toolbar origin follows active branch");
        Check(detail.IsDismissed && detail.Resource.Disposals == 1, "detail cleanup and scope disposal occur once");
        await parent.ToggleFeatureCommand.ExecuteAsync(null);
        Check(child.Destinations.Any(item => item.Id == "reports"), "ViewModel adds conditional destination");
        Success(await parent.Navigation.Select("reports"), "select dynamically added destination");
        Current.AllowNavigation = false;
        await parent.ToggleFeatureCommand.ExecuteAsync(null);
        Check(parent.FeatureEnabled && child.SelectedDestinationId == "reports", "guarded runtime removal preserves collection and selection");
        Current.AllowNavigation = true; await parent.ToggleFeatureCommand.ExecuteAsync(null);
        Check(!parent.FeatureEnabled && child.Destinations.All(item => item.Id != "reports"), "permitted runtime removal commits");
        Success(await parent.Navigation.Select("hidden"), "hidden destination supports explicit ID selection");
        Check((await parent.Navigation.Select("disabled")).Status == NavigationStatus.DestinationUnavailable, "disabled destination rejects explicit selection");
        Check((await parent.Navigation.Select<DocumentViewModel>()).Status == NavigationStatus.AmbiguousDestination, "repeated VM types require explicit ID");
    }

    private async Task FlyoutAsync()
    {
        Success(await Current.Navigation.SetRoot<FlyoutDemoViewModel>(), "registered flyout with nested tabs");
        var root = Root; var parent = (FlyoutDemoViewModel)root.Current!.ViewModel;
        var view = (FlyoutDemoView)root.Current.View; var child = root.Current.Children!;
        var shared = view.SharedContent;
        foreach (var position in Enum.GetValues<SharedContentPosition>())
        {
            parent.SharedPosition = position;
            Success(await parent.Navigation.Select("personal"), $"flyout/{position}: select first repeated model");
            var personal = Current;
            await Ready(root.View.Toolbar);
            root.View.Toolbar.LeadingCommand.Execute(null);
            await Until(() => child.IsFlyoutOpen);
            var selector = Descendants(child.View).OfType<NavigationSelector>().Single(item => ReferenceEquals(item.ItemsSource, child.MenuItems));
            await Ready(selector);
            var button = Descendants(selector).OfType<Button>().Single(item => item.AutomationId == "destination-shared");
            NativeCoveragePlatform.TapAt(button);
            await Until(() => child.SelectedDestinationId == "shared" && !child.IsFlyoutOpen && !root.IsNavigating);
            Check(Current.Caption == "Shared documents" && !ReferenceEquals(personal, Current), $"flyout/{position}: native item input, parameters, and ID");
            Check(ReferenceEquals(shared, view.SharedContent) && ReferenceEquals(shared!.BindingContext, parent), $"flyout/{position}: detail shared content retains parent");
            CheckSharedPlacement(child.View, shared!, position, $"flyout/{position}");
            Check(Descendants(child.View).OfType<Grid>().Any(grid => ReferenceEquals(grid.Background, view.FlyoutPanelBackground)), $"flyout/{position}: panel background");
        }
        await parent.ToggleFeatureCommand.ExecuteAsync(null);
        Success(await parent.Navigation.Select("reports"), "select dynamically added flyout item");
        var report = Current; report.AllowNavigation = false;
        await parent.ToggleFeatureCommand.ExecuteAsync(null);
        Check(child.SelectedDestinationId == "reports" && !report.IsDismissed, "guarded flyout removal preserves current content");
        report.AllowNavigation = true;
        await parent.ToggleFeatureCommand.ExecuteAsync(null);
        Check(child.Destinations.All(item => item.Id != "reports") && report.Resource.Disposals == 1, "flyout runtime removal releases the removed scope once");
        Check((await parent.Navigation.Select("disabled")).Status == NavigationStatus.DestinationUnavailable, "disabled flyout destination rejects ID selection");
        Success(await parent.Navigation.Select("hidden"), "hidden flyout destination permits ID selection");
        Success(await parent.Navigation.Select("workspace/archive"), "atomic path selects nested tab");
        Success(await Current.Navigation.NavigateTo<DemoDetailViewModel>(), "nested tab detail"); var detail = Current;
        Success(await parent.Navigation.Select("secondary"), "independent nested container with duplicate local tab IDs");
        Check(Current.Caption == "Inbox", "nested IDs resolve within their owner");
        Success(await parent.Navigation.Select("workspace"), "restore nested workspace");
        Check(ReferenceEquals(Current, detail), "flyout restores selected tab and its detail history");
        var before = Current;
        Check((await parent.Navigation.Select("secondary/missing")).Status == NavigationStatus.DestinationNotFound && ReferenceEquals(before, Current), "invalid path has no partial commit");
        Success(await Current.Navigation.Select("archive"), "leaf ID lookup uses nearest containing tabs");
    }

    private async Task ModalAndPopupAsync()
    {
        var origin = Current;
        Success(await origin.Navigation.NavigateTo<DemoModalViewModel>(), "open registered modal");
        var modal = Current;
        Check(Root.IsModal && Root.LeadingAction == NavigationLeadingAction.Close, "modal supplies independent automatic close action");
        Check((await origin.Navigation.NavigateBack()).Status == NavigationStatus.InvalidOrigin, "covered parent cannot navigate");
        modal.AllowNavigation = false;
        Check((await modal.Navigation.NavigateBack()).Status == NavigationStatus.GuardRejected, "modal close uses CanNavigate");
        modal.AllowNavigation = true; Success(await modal.Navigation.NavigateBack(), "close registered modal");
        Check(modal.Resource.Disposals == 1 && ReferenceEquals(origin, Current), "modal scope cleanup restores origin");
        foreach (var value in new string?[] { "accepted", null })
        {
            var showing = origin.Navigation.DisplayPopup<DemoPopupViewModel, string>();
            await Until(() => MVVMCompass.Services.ViewModelTree.Collect(host.Window.Page, true).OfType<DemoPopupViewModel>().Any());
            var popup = MVVMCompass.Services.ViewModelTree.Collect(host.Window.Page, true).OfType<DemoPopupViewModel>().Single();
            await Task.Delay(150);
            popup.AllowNavigation = false;
            Check((await popup.Navigation.ClosePopup()).Status == NavigationStatus.GuardRejected, "Toolkit popup close respects CanNavigate");
            popup.AllowNavigation = true;
            Success(value == null ? await popup.Navigation.ClosePopup() : await popup.Navigation.ClosePopup(value), "close through popup ViewModel service");
            var result = await showing;
            Check(result.HasResult == (value != null) && result.Result == value, "typed popup result distinguishes cancellation from a returned value");
            Check(popup.Resource.Disposals == 1 && popup.Dismissals == 1, "popup service scope and model cleaned once");
        }
        await NativePopupBackAsync(origin);
    }

    private async Task GuardsAsync()
    {
        var origin = Current;
        var guardOnUI = false;
        origin.Guard = () => { guardOnUI = MainThread.IsMainThread; return Task.FromResult(true); };
        Success(await Task.Run(() => origin.Navigation.NavigateTo<DemoDetailViewModel>()), "background navigation reaches its owning window");
        Check(guardOnUI, "background navigation executes CanNavigate on the native UI dispatcher");
        origin.Guard = null;
        var detail = Current; var root = Root;
        detail.AllowNavigation = false;
        Check(root.RequestPlatformBack(), "platform Back is handled by custom stack");
        await root.NativeBackCompletion;
        Check(ReferenceEquals(detail, Current), "platform Back veto preserves visible detail");
        detail.Guard = () => throw new InvalidOperationException("Native guard probe");
        Check((await detail.Navigation.NavigateBack()).Status == NavigationStatus.Failed, "throwing guard reports failure without changing body");
        detail.Guard = null; detail.AllowNavigation = true; detail.IsBusy = true;
        Check(root.RequestPlatformBack(), "busy visual still routes platform Back");
        await root.NativeBackCompletion;
        Check(detail.IsDismissed, "CanNavigate remains the only Back veto");
    }

    private static async Task Ready(VisualElement element)
    {
        try { await Until(() => element.Handler != null && element.Width > 0 && element.Height > 0); }
        catch (TimeoutException error)
        {
            var trail = new List<string>();
            for (Element? current = element; current != null; current = current.Parent)
                trail.Add(current is VisualElement view ? $"{view.GetType().Name}: {view.Bounds}, visible={view.IsVisible}, handler={view.Handler != null}" : current.GetType().Name);
            throw new InvalidOperationException(string.Join(" > ", trail), error);
        }
    }
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Native UI condition was not reached."); await Task.Delay(20); }
    }
    private static IEnumerable<Element> Descendants(Element element)
    {
        yield return element;
        foreach (var child in ((IElementController)element).LogicalChildren)
            foreach (var nested in Descendants(child)) yield return nested;
    }
}
