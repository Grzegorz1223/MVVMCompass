using System.Text.Json;
using CommunityToolkit.Maui.Views;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using MVVMCompass.Interfaces;
using MVVMCompass.Core;

namespace MVVMCompass.Sample;

/// <summary>Exercises actual package-backed controls on the owning UI dispatcher.</summary>
internal sealed class RegressionSmokeRunner(ILegacyNavigationService navigation, NavigationOptions options, ScenarioLog log, SharedSession session, MauiNavigationHostFactory hosts, EntryScopeSmoke entryScopes)
{
    private bool running;
    private readonly List<string> passed = new();
    private readonly List<string> skipped = new();
    internal IReadOnlyList<string> Checks => passed;
    internal IReadOnlyList<string> Skipped => skipped;
    internal string? Failure { get; private set; }
    public bool AutoClosePopups { get; private set; }
    public string ResultPath => Path.Combine(FileSystem.AppDataDirectory, "smoke-result.json");

    public async Task RunAsync(bool writeReport = true)
    {
        if (running) return;
        running = true;
        passed.Clear(); Failure = null;
        skipped.Clear();
        AutoClosePopups = true;
        var originalProfile = options.RetainedViewLifecycleBehavior;
        var originalScopes = options.UseEntryScopes;
        options.UseEntryScopes = false;
        var result = "failed";
        string? failure = null;
        var customOnly = Environment.GetEnvironmentVariable("MVVMCOMPASS_SMOKE_SUITE") == "custom";
        try
        {
            Check(MainThread.IsMainThread, "UI dispatcher");
            if (customOnly)
            {
                foreach (var name in await ContentNavigationSamples.RunSmokeAsync(hosts.ForWindow(Application.Current!.Windows[0]), log))
                    Check(true, name);
                await new ContentNavigationCoverage(hosts.ForWindow(Application.Current!.Windows[0]), Check,
                    name => { skipped.Add(name); log.Write($"SMOKE SKIP: {name}"); }).RunAsync();
                result = "passed";
                return;
            }
            await navigation.PresentAsNavigableMainPage<CatalogViewModel>();
            var root = (NavigationPage)navigation.GetPresentationRoot()!;
            await navigation.NavigateTo<DetailViewModel>(new() { ["caption"] = "A" });
            var first = ((DetailPage)root.CurrentPage).ViewModel;
            await navigation.NavigateTo<DetailViewModel>(new() { ["caption"] = "B" });
            var second = ((DetailPage)root.CurrentPage).ViewModel;
            Check(first.Id != second.Id && first.Caption == "A" && second.Caption == "B", "Repeated VM types receive independent parameters");
            await navigation.NavigateBack(second);
            Check(second.IsDismissed && !second.IsHostReplaced && second.IsLifetimeCancelled, "Back awaits terminal cleanup without root-replacement marking");
            await navigation.NavigateBackToRoot();
            Check(first.IsDismissed, "Pop-to-root dismisses removed entries");

            await navigation.NavigateTo<ModalViewModel>();
            var modal = ((ModalPage)root.Navigation.ModalStack[^1]).ViewModel;
            await navigation.NavigateBack(modal);
            Check(modal.IsDismissed && root.Navigation.ModalStack.Count == 0, "Real modal push/pop cleanup");
            await navigation.NavigateTo<LateViewModel>();
            Check(root.CurrentPage is LatePage, "Late module registration resolves in the initialized MAUI app");
            await navigation.NavigateBackToRoot();

            var originalCatalog = ((CatalogPage)root.CurrentPage).ViewModel;
            var preparationFailed = false;
            try { await navigation.PresentAsMainPage<FailingViewModel>(); }
            catch (InvalidOperationException error) { preparationFailed = error.Message == "Deliberate root preparation failure"; }
            Check(preparationFailed && ReferenceEquals(root, navigation.GetPresentationRoot())
                && !originalCatalog.IsDismissed && !originalCatalog.IsHostReplaced && !originalCatalog.IsLifetimeCancelled,
                "Failed root preparation preserves the visible catalog and its lifetime");
            await navigation.NavigateTo<DetailViewModel>();
            await navigation.NavigateBackToRoot();
            Check(ReferenceEquals(((CatalogPage)root.CurrentPage).ViewModel, originalCatalog), "The preserved root still supports forward and back navigation");

            foreach (var profile in Enum.GetValues<RetainedViewLifecycleBehavior>())
            {
                options.RetainedViewLifecycleBehavior = profile;
                for (var cycle = 0; cycle < 2; cycle++)
                {
                    await navigation.PresentAsNavigableMainPage<CustomTabsViewModel>();
                    var host = (CustomTabsPage)((NavigationPage)navigation.GetPresentationRoot()!).CurrentPage;
                    var tabs = (ICustomTabbedViewBase)host;
                    var children = host.Children.Select(child => (TabViewModel)child.ViewModel).ToArray();
                    if (cycle == 0)
                    {
                        var outgoingHost = host;
                        var outgoingChildren = children;
                        var composedFailure = false;
                        try { await navigation.PresentAsNavigableMainPage<CustomTabsViewModel>(new() { ["failPreparation"] = true }); }
                        catch (InvalidOperationException error) { composedFailure = error.Message == "Deliberate composed root preparation failure"; }
                        Check(composedFailure && ReferenceEquals(outgoingHost, ((NavigationPage)navigation.GetPresentationRoot()!).CurrentPage)
                            && outgoingChildren.All(child => !child.IsDismissed) && session.SubscriberCount == 3 && session.ActiveOwner == outgoingChildren[1].Id,
                            $"{profile}: failed composed root cleans only candidates and preserves the old integration owner");
                        await navigation.PresentAsNavigableMainPage<CustomTabsViewModel>();
                        host = (CustomTabsPage)((NavigationPage)navigation.GetPresentationRoot()!).CurrentPage;
                        tabs = (ICustomTabbedViewBase)host;
                        children = host.Children.Select(child => (TabViewModel)child.ViewModel).ToArray();
                        Check(outgoingHost.ViewModel.IsDismissed && outgoingChildren.All(child => child.IsDismissed && child.IsHostReplaced)
                            && session.SubscriberCount == 3 && session.ActiveOwner == children[1].Id,
                            $"{profile}: direct custom-root replacement releases outgoing ownership before new default activation");
                    }
                    Check(children.Length == 3 && session.SubscriberCount == 3 && ReferenceEquals(tabs.CurrentTab?.ViewModel, children[1]), $"{profile}/{cycle}: all children and metadata default selection");
                    Check(children.All(child => ReferenceEquals(child.ParentDuringConstruction, host.ViewModel)), $"{profile}/{cycle}: constructor-time parent");
                    await tabs.SwitchToAsync(0);
                    Check(!children[1].IsDismissed && (profile == RetainedViewLifecycleBehavior.Deactivate ? children[1].Deactivations == 1 : children[1].DismissalCallbacks == 1), $"{profile}/{cycle}: retained callback contract");
                    children[0].AllowNavigation = false;
                    Check(!await tabs.SwitchToAsync(2) && ReferenceEquals(tabs.CurrentTab?.ViewModel, children[0]), $"{profile}/{cycle}: async guard veto");
                    children[0].AllowNavigation = true;
                    Check(await tabs.SwitchToAsync(2) && tabs.CurrentTab!.HideInTabBar, $"{profile}/{cycle}: hidden duplicate-type tab");
                    children[2].IsTabBusy = true;
                    Check(host.TabItems[2].IsBusy, $"{profile}/{cycle}: per-tab activity forwarding");
                    tabs.IsTabBarEnabled = false;
                    Check(!await tabs.SwitchToAsync(0), $"{profile}/{cycle}: disabled tab bar");
                    tabs.IsTabBarEnabled = true;
                    await WaitUntil(() => host.Handler != null);
                    var childPage = (TabPage)tabs.CurrentTab!.View;
                    Check(ReferenceEquals(childPage.HandledPage, host), $"{profile}/{cycle}: extracted child finds native host");
                    if (cycle == 0)
                    {
                        var popup = await childPage.ShowDelegatedPopupAsync();
                        Check(popup.Result == "accepted" && host.ModalOpenings == 1 && host.ModalClosings == 1 && host.ModalDepth == 0,
                            $"{profile}: delegated popup result and balanced host notifications");
                    }
                    var callbacksBeforeLogout = children.Select(child => child.DismissalCallbacks).ToArray();
                    await navigation.PresentAsNavigableMainPage<CatalogViewModel>();
                    Check(children.Select((child, index) => child.IsDismissed && child.IsHostReplaced && child.IsLifetimeCancelled && child.DismissalCallbacks == callbacksBeforeLogout[index] + 1).All(value => value)
                        && host.ViewModel.IsDismissed && session.SubscriberCount == 0 && session.ActiveOwner == null,
                        $"{profile}/{cycle}: logout clears active and never-shown children and shared ownership");
                }

                RootReplacementException? activationFailure = null;
                try { await navigation.PresentAsNavigableMainPage<CustomTabsViewModel>(new() { ["failActivation"] = true }); }
                catch (RootReplacementException error) { activationFailure = error; }
                var failedHost = (CustomTabsPage)((NavigationPage)navigation.GetPresentationRoot()!).CurrentPage;
                var failedChildren = failedHost.Children.Select(child => (TabViewModel)child.ViewModel).ToArray();
                Check(activationFailure is { Stage: RootReplacementStage.Activation, IsReplacementPresented: true }
                    && !failedHost.ViewModel.IsDismissed && session.SubscriberCount == 3 && session.ActiveOwner == failedChildren[1].Id,
                    $"{profile}: activation failure reports a committed live root for application recovery");
                await navigation.PresentAsNavigableMainPage<CatalogViewModel>();
                Check(failedHost.ViewModel.IsDismissed && failedChildren.All(child => child.IsDismissed && child.DismissalCallbacks == 1)
                    && session.SubscriberCount == 0 && session.ActiveOwner == null,
                    $"{profile}: recovery from activation failure releases every candidate exactly once");

                await navigation.PresentAsMainPage<StandardTabsViewModel>();
                var standard = (StandardTabsPage)navigation.GetPresentationRoot()!;
                var previous = (ProbeViewModel)((IHasVM)standard.CurrentPage).ViewModel;
                standard.CurrentPage = standard.Children[1];
                await WaitUntil(() => ((ProbeViewModel)((IHasVM)standard.CurrentPage).ViewModel).Appearances > 0);
                Check(!previous.IsDismissed && (profile == RetainedViewLifecycleBehavior.Deactivate ? previous.Deactivations > 0 : previous.DismissalCallbacks > 0), $"{profile}: standard tab switch retains lifetime");
                await navigation.PresentAsNavigableMainPage<CatalogViewModel>();
                Check(previous.IsDismissed, $"{profile}: standard tab root teardown");
            }

            var catalog = ((CatalogPage)((NavigationPage)navigation.GetPresentationRoot()!).CurrentPage).ViewModel;
            Check(await catalog.RequestActionAsync() is string message && message.Contains("owning dispatcher"), "Custom action returns its UI result");
            var commandFailed = false;
            try { await ((IAsyncRelayCommand)catalog.FailingCommand).ExecuteAsync(null); }
            catch (InvalidOperationException error) { commandFailed = error.Message == "Expected command failure"; }
            Check(commandFailed && !catalog.IsBusy, "Command failure restores busy state");
            var resultValue = await navigation.DisplayPopupWithResult<PopupViewModel, string>(catalog);
            Check(resultValue == "accepted" && !catalog.IsDismissed, "Service popup result retains underlying page");

            await navigation.PresentAsMainPage<FlyoutViewModel>();
            var flyout = (FlyoutPage)navigation.GetPresentationRoot()!;
            var menu = (MenuPage)flyout.Flyout;
            var inactive = (ProbeViewModel)((IHasVM)menu.MenuItems[0].Content!).ViewModel;
            await navigation.NavigateToFlyoutItem(nameof(SecondViewModel), new() { ["caption"] = "Selected by ID" });
            await WaitUntil(() => flyout.Detail is SecondPage);
            Check(!inactive.IsDismissed, "Flyout ID selection retains inactive entries");
            Check(flyout.IsPresented == ((IFlyoutPageController)flyout).ShouldShowSplitMode,
                "Flyout ID selection preserves the platform's pinned or transient layout");
            options.RetainedViewLifecycleBehavior = originalProfile;
            options.UseEntryScopes = originalScopes;
            await navigation.PresentAsNavigableMainPage<CatalogViewModel>();
            Check(inactive.IsDismissed && inactive.IsHostReplaced, "Flyout root replacement cleans inactive entries");
            await RunWindowHostAsync();
            await RunContainerHostAsync();
            await RunRetainedHostAsync();
            await RunNativeReconciliationAsync();
            await RunPopupOwnershipAsync();
            ContentNavigationSamples.AssertNoNativeModal(Application.Current!.Windows[0]);
            await entryScopes.RunAsync(Check);
            await OrdinaryFactorySmoke.RunAsync(hosts.ForWindow(Application.Current!.Windows[0]), options, Check);
            await CancellationSmoke.RunAsync(hosts.ForWindow(Application.Current!.Windows[0]), options, Check);
            foreach (var check in await ContentNavigationSamples.RunSmokeAsync(hosts.ForWindow(Application.Current!.Windows[0]), log))
                Check(true, check);
            await new ContentNavigationCoverage(hosts.ForWindow(Application.Current!.Windows[0]), Check,
                name => { skipped.Add(name); log.Write($"SMOKE SKIP: {name}"); }).RunAsync();
            result = "passed";
        }
        catch (Exception error)
        {
            failure = error.ToString(); Failure = failure;
            log.Write($"SMOKE FAILED: {error}");
        }
        finally
        {
            options.RetainedViewLifecycleBehavior = originalProfile;
            options.UseEntryScopes = originalScopes;
            AutoClosePopups = false;
            running = false;
            var report = JsonSerializer.Serialize(new SmokeResult(
                result, passed, failure, DeviceInfo.Platform.ToString(), DeviceInfo.VersionString,
                typeof(ViewModelBase).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
                { Skipped = skipped, Suite = customOnly ? "custom" : "full" },
                SmokeResultJsonContext.Default.SmokeResult);
            if (writeReport) File.WriteAllText(ResultPath, report);
#if ANDROID
            // Release applications cannot use adb run-as. Export only explicitly requested
            // smoke reports, with a fresh run identity and chunks below Android's log limit.
            if (writeReport && Guid.TryParseExact(Environment.GetEnvironmentVariable("MVVMCOMPASS_SMOKE_RUN_ID"), "N", out var runId))
            {
                var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(report));
                const int chunkSize = 3000;
                var count = (encoded.Length + chunkSize - 1) / chunkSize;
                for (var index = 0; index < count; index++)
                    Android.Util.Log.Info("MVVMCompassSmoke", $"{runId:N}:{index}/{count}:"
                        + encoded.Substring(index * chunkSize, Math.Min(chunkSize, encoded.Length - index * chunkSize)));
            }
#endif
            log.Write($"SMOKE {result.ToUpperInvariant()}: {passed.Count} checks; {ResultPath}");
        }
    }

    private async Task RunWindowHostAsync()
    {
        var window = navigation.GetPresentationRoot()?.Window ?? throw new InvalidOperationException("The playground requires a native window.");
        var host = hosts.ForWindow(window);
        Check(ReferenceEquals(host, hosts.ForWindow(window)), "One coordinator is reused for the explicit native window");
        var first = new RootModel();
        var initial = await Task.Run(() => host.ReplaceRootAsync(new NavigationRequest<string>("background request"),
            () => { first.FactoryOnUi = MainThread.IsMainThread; return first; }, RootPage));
        Check(initial.IsSuccess && initial.Value!.Entry.ViewModel.Parameter == "background request"
            && first.FactoryOnUi && first.InitializedOnUi && first.ActivatedOnUi,
            "Background typed root request resolves, initializes and activates on the native UI dispatcher");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = new RootModel { Initialize = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
        var pending = host.ReplaceRootAsync(new NavigationRequest<string>("obsolete bootstrap"), () => abandoned, RootPage);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(ReferenceEquals(initial.Value!.Page, window.Page) && first.Dismissals == 0, "Root preparation retains the current native root and its resources");
        var authorized = new RootModel { Activate = () =>
        {
            if (first.Dismissals != 1) throw new InvalidOperationException("Outgoing ownership was not released before activation.");
        } };
        var required = host.ReplaceRootAsync(new NavigationRequest<string>("authorized") { Priority = NavigationPriority.Required }, () => authorized, RootPage);
        Check((await pending).Status == NavigationStatus.Superseded && abandoned.Dismissals == 1
            && abandoned.Reason == DismissalReason.PreparationFailed && abandoned.Activations == 0,
            "Required authorization abandons the obsolete native bootstrap without activating it");
        Check((await required).IsSuccess && authorized.Activations == 1 && first.CleanedOnUi,
            "Required native root waits for outgoing cleanup and activates the new owner");
        var stale = await host.ReplaceRootAsync(new NavigationRequest<string>("late callback") { Origin = initial.Value.Entry }, () => new RootModel(), RootPage);
        Check(stale.Status == NavigationStatus.InvalidOrigin, "An obsolete root callback cannot replace the native window");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new RootModel { Initialize = _ => release.Task };
        var blocker = host.ReplaceRootAsync(new NavigationRequest<string>("ordered required") { Priority = NavigationPriority.Required }, () => holding, RootPage);
        var obsoleteResolutions = 0;
        var obsolete = host.ReplaceRootAsync(new NavigationRequest<string>("old menu") { CoalescingKey = "menu" }, () => { obsoleteResolutions++; return new RootModel(); }, RootPage);
        var latest = host.ReplaceRootAsync(new NavigationRequest<string>("latest menu") { CoalescingKey = "menu" }, () => new RootModel(), RootPage);
        try { Check((await obsolete).Status == NavigationStatus.Superseded && obsoleteResolutions == 0, "Native menu requests coalesce before resolving stale candidates"); }
        finally { release.TrySetResult(); }
        Check((await blocker).IsSuccess && (await latest).Value!.Entry.ViewModel.Parameter == "latest menu",
            "The newest native menu request runs after earlier required work");

        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptedModel = new RootModel { Initialize = async token =>
        {
            preparing.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        } };
        var interrupted = host.ReplaceRootAsync(new NavigationRequest<string>("interrupted required")
            { Priority = NavigationPriority.Required, Origin = host.CurrentRoot!.Entry }, () => interruptedModel, RootPage);
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var surviving = host.ReplaceRootAsync(new NavigationRequest<string>("queued required")
            { Priority = NavigationPriority.Required }, () => new RootModel(), RootPage);
        window.Page = new ContentPage { Content = new Label { Text = "External root change" } };
        Check((await interrupted).Status == NavigationStatus.Cancelled && interruptedModel.Dismissals == 1
            && (await surviving).IsSuccess,
            "External native root changes cancel active preparation and preserve queued required work");

        foreach (var profile in Enum.GetValues<RetainedViewLifecycleBehavior>())
        {
            options.RetainedViewLifecycleBehavior = profile;
            var tabs = await host.ReplaceRootAsync<CustomTabsViewModel>(new(null), navigable: true);
            Check(tabs.IsSuccess && session.SubscriberCount == 3 && session.ActiveOwner != null,
                $"{profile}: coordinated registered roots preserve native custom-tab composition");
            var children = ((CustomTabsPage)((NavigationPage)tabs.Value!.Page).CurrentPage).Children.Select(child => child.ViewModel).ToArray();
            var recovery = await host.ReplaceRootAsync<CatalogViewModel>(new(null) { Priority = NavigationPriority.Required }, navigable: true);
            Check(recovery.IsSuccess && children.All(child => child.IsDismissed && child.IsHostReplaced)
                && session.SubscriberCount == 0 && session.ActiveOwner == null,
                $"{profile}: coordinated root teardown includes every retained native child");
        }

        var failedModel = new RootModel { Activate = () => throw new InvalidOperationException("Deliberate portable activation failure") };
        var failed = await host.ReplaceRootAsync(new NavigationRequest<string>("failed activation"), () => failedModel, RootPage);
        Check(failed.Status == NavigationStatus.Failed && failed.HasCommitted
            && failed.Error is RootReplacementException { Stage: RootReplacementStage.Activation, IsReplacementPresented: true }
            && ReferenceEquals(host.CurrentRoot!.Page, window.Page) && failedModel.Dismissals == 0,
            "Committed native activation failure retains ownership for recovery");
        var recovered = await host.ReplaceRootAsync<CatalogViewModel>(new(null) { Priority = NavigationPriority.Required }, navigable: true);
        Check(recovered.IsSuccess && failedModel.Dismissals == 1 && failedModel.Reason == DismissalReason.RootReplaced,
            "Fresh native recovery releases the failed portable root exactly once");

        static Page RootPage(RootModel model) => new ContentPage
        {
            BindingContext = model,
            Content = new Label { Text = "Coordinated window root", Margin = 24 }
        };
    }

    private async Task RunContainerHostAsync()
    {
        var window = navigation.GetPresentationRoot()?.Window ?? throw new InvalidOperationException("The playground requires a native window.");
        var host = hosts.ForWindow(window);
        var rootModel = new ContainerModel();
        var root = await host.ReplaceRootAsync(new NavigationRequest<string>("stack root"), () => rootModel, ContainerPage, navigable: true);
        Check(root.IsSuccess && rootModel.InitializedOnUi, "Coordinated native stack root accepts ordinary typed models");
        await WaitUntil(() => root.Value!.Page.Handler != null);
        var firstModel = new ContainerModel();
        var first = await Task.Run(() => host.PushAsync(new NavigationRequest<string>("first") { Origin = root.Value!.Entry }, () => firstModel, ContainerPage, animated: false));
        Check(first.IsSuccess && firstModel.InitializedOnUi && firstModel.ActivatedOnUi && firstModel.Parameter == "first",
            "Background stack request initializes and activates through the native window dispatcher");
        Check(root.Value!.Entry.State == NavigationEntryState.Inactive && !rootModel.Token.IsCancellationRequested && rootModel.Deactivations == 1,
            "Covering a native stack page retains its lifetime and releases active resources once");
        firstModel.Allowed = false;
        var refused = await host.BackAsync(new(null) { Origin = first.Value!.Entry }, animated: false);
        Check(refused.Status == NavigationStatus.GuardRejected && !refused.HasCommitted && firstModel.Dismissals == 0,
            "Native host back respects the awaited guard without dismissing its page");
        firstModel.Allowed = true;
        var second = await Push("second");
        Check(second.Entry.Id != first.Value.Entry.Id && second.Entry.ViewModel.Parameter == "second", "Repeated coordinated destinations keep distinct native page identities");
        var popped = await host.PopToRootAsync(new(null) { Origin = second.Entry }, animated: false);
        Check(popped.IsSuccess && firstModel.Dismissals == 1 && second.Entry.ViewModel.Dismissals == 1 && rootModel.Activations == 2,
            "Coordinated native pop-to-root awaits every terminal owner and reactivates its retained root");
        Check((await host.BackAsync(new(null) { Origin = second.Entry }, animated: false)).Status == NavigationStatus.InvalidOrigin,
            "A removed native page cannot originate a later back operation");

        var modal = await Modal("modal", navigable: true);
        await WaitUntil(() => modal.Page.Handler != null);
        var modalChild = await Push("modal child");
        Check(ReferenceEquals(((NavigationPage)modal.Page).CurrentPage, modalChild.Page)
            && ((NavigationPage)root.Value.Page).Navigation.NavigationStack.Count == 1,
            "A modal child is pushed into its own native stack");
        var modalPop = await host.PopToRootAsync(animated: false);
        Check(modalPop.IsSuccess && window.Navigation.ModalStack.Count == 1 && modalChild.Entry.ViewModel.Dismissals == 1,
            "Pop-to-root inside a native modal keeps that modal open");
        var nested = await Modal("nested");
        var nestedClose = await host.CloseModalAsync(new(null) { Origin = nested.Entry }, animated: false);
        Check(nestedClose.IsSuccess && nested.Entry.ViewModel.Dismissals == 1 && ReferenceEquals(host.CurrentPage, modal),
            "Closing a nested native modal restores its owning modal");
        var lastChild = await Push("last modal child");
        var closed = await host.CloseModalAsync(animated: false);
        Check(closed.IsSuccess && modal.Entry.ViewModel.Dismissals == 1 && lastChild.Entry.ViewModel.Dismissals == 1
            && window.Navigation.ModalStack.Count == 0 && root.Value.Entry.State == NavigationEntryState.Active,
            "Closing a native modal cleans its complete stack before reactivating the underlying root");

        var nativeDetail = await Push("native back");
        await ((NavigationPage)root.Value.Page).PopAsync(false);
        await host.NativeNavigationCompletion;
        Check(nativeDetail.Entry.ViewModel.Dismissals == 1 && nativeDetail.Entry.ViewModel.CleanedOnUi && root.Value.Entry.State == NavigationEntryState.Active,
            "Native stack back cleans ordinary models and exposes asynchronous reconciliation completion");
        var nativeFirst = await Push("native pop first");
        var nativeSecond = await Push("native pop second");
        await ((NavigationPage)root.Value.Page).PopToRootAsync(false);
        await host.NativeNavigationCompletion;
        Check(nativeFirst.Entry.ViewModel.Dismissals == 1 && nativeSecond.Entry.ViewModel.Dismissals == 1,
            "Native pop-to-root reconciles all owned ordinary models exactly once");
        var nativeModal = await Modal("native modal close");
        await window.Navigation.PopModalAsync(false);
        await host.NativeNavigationCompletion;
        Check(nativeModal.Entry.ViewModel.Dismissals == 1 && root.Value.Entry.State == NavigationEntryState.Active,
            "Native modal removal reconciles its owner and retained underlying entry");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var obsolete = new ContainerModel { Initialize = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
        var preparing = host.PushAsync(new NavigationRequest<string>("obsolete detail"), () => obsolete, ContainerPage, animated: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var authorized = host.ReplaceRootAsync(new NavigationRequest<string>("authorized stack") { Priority = NavigationPriority.Required },
            () => new ContainerModel(), ContainerPage, navigable: true);
        Check((await preparing).Status == NavigationStatus.Superseded && obsolete.Dismissals == 1 && obsolete.Reason == DismissalReason.PreparationFailed
            && (await authorized).IsSuccess,
            "Required native root replacement abandons uncommitted stack preparation");
        await WaitUntil(() => host.CurrentRoot!.Page.Handler != null);
        var failedModel = new ContainerModel { FailActivation = true };
        var failed = await host.PushAsync(new NavigationRequest<string>("failed page activation"), () => failedModel, ContainerPage, animated: false);
        Check(failed.Status == NavigationStatus.Failed && failed.HasCommitted && failed.Error is MauiNavigationException { HasPresentationChanged: true }
            && failedModel.Dismissals == 0 && ReferenceEquals(host.CurrentPage!.Entry.ViewModel, failedModel),
            "Committed native page activation failure retains the installed entry for recovery");
        Check((await host.BackAsync(animated: false)).IsSuccess && failedModel.Dismissals == 1,
            "Back recovers from a failed native activation without reviving an ended lifetime");

        foreach (var profile in Enum.GetValues<RetainedViewLifecycleBehavior>())
        {
            options.RetainedViewLifecycleBehavior = profile;
            var catalog = await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true);
            await WaitUntil(() => catalog.Value!.Page.Handler != null);
            var detail = await host.PushAsync<DetailViewModel>(new(new() { ["caption"] = "Coordinated registered detail" }), animated: false);
            Check(detail.IsSuccess && !catalog.Value!.Entry.ViewModel.IsDismissed
                && (profile == RetainedViewLifecycleBehavior.Deactivate ? catalog.Value.Entry.ViewModel.Deactivations == 1 : catalog.Value.Entry.ViewModel.DismissalCallbacks == 1),
                $"{profile}: coordinated native stacks preserve the registered retained callback contract");
            var returned = await host.BackAsync(new(null) { Origin = detail.Value!.Entry }, animated: false);
            Check(returned.IsSuccess && detail.Value.Entry.ViewModel.IsDismissed && detail.Value.Entry.ViewModel.DismissalCallbacks == 1
                && catalog.Value!.Entry.State == NavigationEntryState.Active,
                $"{profile}: coordinated registered back awaits cleanup and retains its root");
        }

        async Task<MauiNavigationPage<ContainerModel>> Push(string parameter)
        {
            var result = await host.PushAsync(new NavigationRequest<string>(parameter), () => new ContainerModel(), ContainerPage, animated: false);
            if (!result.IsSuccess) throw result.Error ?? new InvalidOperationException($"Coordinated push ended: {result.Status}");
            return result.Value!;
        }
        async Task<MauiNavigationPage<ContainerModel>> Modal(string parameter, bool navigable = false)
        {
            var result = await host.OpenModalAsync(new NavigationRequest<string>(parameter), () => new ContainerModel(), ContainerPage,
                navigable: navigable, animated: false);
            if (!result.IsSuccess) throw result.Error ?? new InvalidOperationException($"Coordinated modal ended: {result.Status}");
            return result.Value!;
        }
        static Page ContainerPage(ContainerModel model) => new ContentPage
        { BindingContext = model, Title = "Coordinated destination", Content = new Label { Text = "Window, stack and modal ownership", Margin = 24 } };
    }

    private async Task RunRetainedHostAsync()
    {
        var window = navigation.GetPresentationRoot()!.Window;
        var host = hosts.ForWindow(window);
        foreach (var profile in Enum.GetValues<RetainedViewLifecycleBehavior>())
        {
            options.RetainedViewLifecycleBehavior = profile;
            var root = await host.ReplaceRootAsync<CustomTabsViewModel>(new(null));
            if (!root.IsSuccess) throw root.Error!;
            var custom = (CustomTabsPage)root.Value!.Page;
            var entries = host.GetItems(custom);
            Check(entries.Count == 3 && ReferenceEquals(host.CurrentEntry, entries[1].Entry), $"{profile}: coordinated custom tabs expose the declared default and retained entries");
            var old = (TabViewModel)entries[1].Entry!.ViewModel;
            old.AllowNavigation = false;
            var veto = await host.SelectTabAsync(custom, new(2) { Origin = entries[1].Entry });
            Check(veto.Status == NavigationStatus.GuardRejected && !veto.HasCommitted, $"{profile}: coordinated hidden-tab selection respects an asynchronous guard");
            old.AllowNavigation = true;
            Check(await ((ICustomTabbedViewBase)custom).SwitchToAsync(2), $"{profile}: existing custom-tab commands enter the host coordinator");
            Check(ReferenceEquals(host.CurrentEntry, entries[2].Entry) && !old.IsDismissed && !old.IsLifetimeCancelled,
                $"{profile}: selecting a hidden duplicate-type tab retains the previous owner");
            Check(profile == RetainedViewLifecycleBehavior.Deactivate ? old.Deactivations == 1 : old.DismissalCallbacks == 1,
                $"{profile}: coordinated custom tabs invoke exactly one retained callback");
            var stale = await host.SelectTabAsync(custom, new(0) { Origin = entries[1].Entry });
            Check(stale.Status == NavigationStatus.InvalidOrigin, $"{profile}: an inactive child cannot originate a native selection");
            var modal = await host.OpenModalAsync(new NavigationRequest<string>("retained modal") { Origin = host.CurrentEntry },
                () => new ContainerModel(), _ => new ContentPage { Content = new Label { Text = "Retained child modal" } }, animated: false);
            Check(modal.IsSuccess && entries[2].Entry!.State == NavigationEntryState.Inactive, $"{profile}: a native modal covers the active custom child");
            var closed = await host.CloseModalAsync(animated: false);
            Check(closed.IsSuccess && ReferenceEquals(host.CurrentEntry, entries[2].Entry), $"{profile}: closing a native modal restores the same retained child");
            await root.Value.Entry.ViewModel.BeforeFirstShown();
            Check(host.GetItems(custom).Count == 6 && ReferenceEquals(host.GetItems(custom)[1], entries[1]),
                $"{profile}: repeated custom composition keeps existing entries and appends new instances");

            var standardRoot = await host.ReplaceRootAsync<StandardTabsViewModel>(new(null));
            if (!standardRoot.IsSuccess) throw standardRoot.Error!;
            var standard = (StandardTabsPage)standardRoot.Value!.Page;
            await WaitUntil(() => standard.Handler != null);
            var standardItems = host.GetItems(standard);
            var first = (ProbeViewModel)standardItems[0].Entry!.ViewModel;
            standard.CurrentPage = standard.Children[1];
            await host.NativeSelectionCompletion;
            Check(ReferenceEquals(host.CurrentEntry, standardItems[1].Entry), $"{profile}: native standard-tab selection awaits the host queue");
            Check(((ProbeViewModel)host.CurrentEntry!.ViewModel).Appearances == 1,
                $"{profile}: native standard-tab activation is delivered once");
            await standardRoot.Value.Entry.ViewModel.BeforeFirstShown();
            standard.CurrentPage = standard.Children[0];
            await host.NativeSelectionCompletion;
            Check(standard.Children.Count == 4 && first.Appearances == 2, $"{profile}: repeated standard composition keeps a single selection subscription");

            var flyoutRoot = await host.ReplaceRootAsync<FlyoutViewModel>(new(null));
            if (!flyoutRoot.IsSuccess) throw flyoutRoot.Error!;
            var flyout = (FlyoutPage)flyoutRoot.Value!.Page;
            await WaitUntil(() => flyout.Handler != null);
            var flyoutItems = host.GetItems(flyout);
            var menu = (MenuPage)flyout.Flyout;
            await menu.SelectMenuItem(menu.MenuItems[1], new() { ["caption"] = "Coordinated flyout" });
            Check(ReferenceEquals(host.CurrentEntry, flyoutItems[1].Entry)
                && ((ProbeViewModel)host.CurrentEntry!.ViewModel).Caption == "Coordinated flyout",
                $"{profile}: existing flyout commands route parameters through the explicit window host");
            Check(((ProbeViewModel)host.CurrentEntry!.ViewModel).Appearances == 1,
                $"{profile}: native flyout activation is delivered once");
            await flyoutRoot.Value.Entry.ViewModel.BeforeFirstShown();
            Check(flyoutItems.All(item => item.Entry!.Lifetime.IsDismissed) && menu.ViewModel.IsDismissed
                && host.GetItems(flyout).Count == 3, $"{profile}: flyout rebuild ends every obsolete page, popup and menu owner");
            await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true);
            Check(session.SubscriberCount == 0, $"{profile}: coordinated retained teardown releases shared subscriptions");
        }
    }

    private async Task RunNativeReconciliationAsync()
    {
        var host = hosts.ForWindow(navigation.GetPresentationRoot()!.Window);
        foreach (var profile in Enum.GetValues<RetainedViewLifecycleBehavior>())
        {
            options.RetainedViewLifecycleBehavior = profile;
            var first = new ContainerModel();
            var root = await host.ReplaceRootAsync(new NavigationRequest<string>("Native root"), () => first, ContainerPage, navigable: true);
            if (!root.IsSuccess) throw root.Error!;
            var stack = (NavigationPage)root.Value!.Page;
            var originalRootPage = stack.RootPage;
            await WaitUntil(() => stack.Handler != null);
            var top = new ContainerModel();
            await stack.PushAsync(ContainerPage(top), false); await host.ReconcileNativeAsync();
            Check(ReferenceEquals(host.CurrentEntry!.ViewModel, top) && top.ActivatedOnUi && first.Deactivations == 1,
                $"{profile}: native push adopts an ordinary owner on the UI dispatcher");
            var inserted = new ContainerModel(); var insertedPage = ContainerPage(inserted);
            stack.Navigation.InsertPageBefore(insertedPage, stack.RootPage); await host.ReconcileNativeAsync();
            Check(ReferenceEquals(host.CurrentRoot!.Entry.ViewModel, inserted) && inserted.Activations == 0,
                $"{profile}: native insertion promotes the prepared root without activating it");
            stack.Navigation.RemovePage(originalRootPage); await host.ReconcileNativeAsync();
            Check(first.Dismissals == 1 && first.CleanedOnUi && top.Dismissals == 0,
                $"{profile}: direct inactive removal releases only the removed owner");
            await stack.PopToRootAsync(false); await host.ReconcileNativeAsync();
            Check(top.Dismissals == 1 && ReferenceEquals(host.CurrentEntry!.ViewModel, inserted) && inserted.Activations == 1,
                $"{profile}: native pop-to-root restores the inserted root");

            var modal = new ContainerModel(); var modalPage = ContainerPage(modal);
            await host.Window.Navigation.PushModalAsync(modalPage, false); await host.ReconcileNativeAsync();
            var modalEntry = host.CurrentEntry!;
            Check(ReferenceEquals(modalEntry.ViewModel, modal) && modal.ActivatedOnUi,
                $"{profile}: direct native modal presentation acquires ownership");
            void Veto(object? sender, ModalPoppingEventArgs args) => args.Cancel = true;
            host.Window.ModalPopping += Veto;
            try { await host.Window.Navigation.PopModalAsync(false); await host.ReconcileNativeAsync(); }
            finally { host.Window.ModalPopping -= Veto; }
            Check(ReferenceEquals(host.CurrentEntry, modalEntry) && !modalEntry.Lifetime.IsDismissed
                && host.Window.Navigation.ModalStack.Contains(modalPage), $"{profile}: native modal veto retains its entry");
            await host.Window.Navigation.PopModalAsync(false); await host.ReconcileNativeAsync();
            Check(modal.Dismissals == 1 && modal.CleanedOnUi && ReferenceEquals(host.CurrentEntry!.ViewModel, inserted),
                $"{profile}: completed native modal removal cleans up before reactivation");

            var standardRoot = await host.ReplaceRootAsync<StandardTabsViewModel>(new(null));
            if (!standardRoot.IsSuccess) throw standardRoot.Error!;
            var tabs = (StandardTabsPage)standardRoot.Value!.Page;
            await WaitUntil(() => tabs.Handler != null);
            var entries = host.GetItems(tabs); var added = new ContainerModel(); var addedPage = ContainerPage(added);
            tabs.Children.Add(addedPage); await host.ReconcileNativeAsync();
            Check(host.GetItems(tabs).Count == entries.Count + 1 && ReferenceEquals(host.GetItems(tabs)[0], entries[0]),
                $"{profile}: direct native tab addition preserves existing entries");
            tabs.Children.Remove(addedPage); await host.ReconcileNativeAsync();
            Check(added.Dismissals == 1 && added.Activations == 0, $"{profile}: removing an inactive native tab releases its prepared owner");
            tabs.Children.Remove((Page)entries[0].View); await host.ReconcileNativeAsync();
            Check(entries[0].Entry!.Lifetime.IsDismissed && ReferenceEquals(host.CurrentEntry, entries[1].Entry)
                && host.GetItems(tabs).Count == entries.Count - 1, $"{profile}: active native tab removal selects the surviving entry");

            var flyoutRoot = await host.ReplaceRootAsync<FlyoutViewModel>(new(null));
            if (!flyoutRoot.IsSuccess) throw flyoutRoot.Error!;
            var flyout = (FlyoutPage)flyoutRoot.Value!.Page;
            await WaitUntil(() => flyout.Handler != null);
            var menu = (MenuPage)flyout.Flyout; var items = host.GetItems(flyout);
            flyout.Detail = (Page)items[1].View; await host.ReconcileNativeAsync();
            Check(ReferenceEquals(host.CurrentEntry, items[1].Entry) && menu.MenuItems[1].IsSelected && !menu.MenuItems[0].IsSelected,
                $"{profile}: direct flyout detail selection reconciles entries and selection flags");
            menu.MenuItems.RemoveAt(0); await host.ReconcileNativeAsync();
            Check(items[0].Entry!.Lifetime.IsDismissed && !items[1].Entry!.Lifetime.IsDismissed,
                $"{profile}: native menu removal ends the inactive page owner");
            flyout.Flyout = new ContentPage { Title = "Replacement menu" }; await host.ReconcileNativeAsync();
            Check(menu.ViewModel.IsDismissed && host.GetItems(flyout).Count == 0 && !items[1].Entry!.Lifetime.IsDismissed,
                $"{profile}: replacing the menu releases obsolete owners and keeps the displayed detail");
            await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true);
            Check(session.SubscriberCount == 0, $"{profile}: native reconciliation preserves complete retained teardown");

            stack = (NavigationPage)host.CurrentRoot!.Page;
            await WaitUntil(() => stack.Handler != null);
            var catalog = ((CatalogPage)stack.RootPage).ViewModel;
            top = new ContainerModel(); await stack.PushAsync(ContainerPage(top), false); await host.ReconcileNativeAsync();
            var before = catalog.Appearances;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            top.Cleanup = async () => { entered.TrySetResult(); await release.Task; };
            await stack.PopAsync(false);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(catalog.Appearances == before && !host.NativeNavigationCompletion.IsCompleted,
                    $"{profile}: native incoming appearance waits for asynchronous owner cleanup");
            }
            finally { release.TrySetResult(); }
            await host.ReconcileNativeAsync();
            Check(catalog.Appearances == before + 1 && top.CleanedOnUi,
                $"{profile}: native completion delivers incoming appearance once after cleanup");
        }
        static Page ContainerPage(ContainerModel model) => new ContentPage
            { BindingContext = model, Content = new Label { Text = "Native reconciliation" } };
    }

    private async Task RunPopupOwnershipAsync()
    {
        AutoClosePopups = false;
        var window = Application.Current!.Windows[0]; var host = hosts.ForWindow(window);
        foreach (var profile in Enum.GetValues<RetainedViewLifecycleBehavior>())
        {
            options.RetainedViewLifecycleBehavior = profile;
            var root = await host.ReplaceRootAsync<CatalogViewModel>(new(null));
            Check(root.IsSuccess, $"{profile}: popup owner root installed");
            await WaitUntil(() => root.Value!.Page.IsLoaded && root.Value.Page.Width > 0 && root.Value.Page.Height > 0);
            var origin = root.Value!.Entry.ViewModel;
            async Task<(ResultPopup Popup, Task<PopupNavigationResult<string?>> Result)> Open(CancellationToken token = default)
            {
                var previousPopup = FindPopup(window.Navigation.ModalStack.LastOrDefault());
                var showing = Task.Run(() => host.DisplayPopupAsync<PopupViewModel, string?>(origin, cancellationToken: token));
                await WaitUntil(() => showing.IsFaulted || FindPopup(window.Navigation.ModalStack.LastOrDefault()) is ResultPopup current && current != previousPopup);
                if (showing.IsFaulted) await showing;
                // ModalStack changes before the native dialog is attached. Exercise Back and
                // root replacement only after the popup has actually reached the screen.
                try { await WaitUntil(() => window.Navigation.ModalStack.LastOrDefault()?.IsLoaded == true); }
                catch (OperationCanceledException error)
                {
                    var page = window.Navigation.ModalStack.LastOrDefault();
                    var nativePopup = FindPopup(page);
                    throw new InvalidOperationException($"Popup attachment: page={page?.GetType().Name}, loaded={page?.IsLoaded}, handler={page?.Handler != null}, bounds={page?.Bounds}; " +
                        $"popup handler={nativePopup?.Handler != null}, bounds={nativePopup?.Bounds}; root loaded={window.Page?.IsLoaded}, bounds={window.Page?.Bounds}", error);
                }
                await Task.Yield();
                return ((ResultPopup)FindPopup(window.Navigation.ModalStack[^1])!, showing);
            }
            var (popup, showing) = await Open(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
            var closed = 0; popup.Closed += (_, _) => closed++;
            var cleanupOnUi = false;
            popup.ViewModel.Cleanup = async () => { cleanupOnUi = MainThread.IsMainThread; entered.TrySetResult(); await release.Task; };
            var closing = popup.CloseAsync("owned"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                Check(popup.ViewModel.IsDismissed && cleanupOnUi && !showing.IsCompleted && !closing.IsCompleted && closed == 0,
                    $"{profile}: popup result and Closed wait for asynchronous cleanup");
            }
            finally { release.TrySetResult(); }
            await closing; var result = await showing;
            Check(result.Result == "owned" && result.Reason == DismissalReason.DialogClosed && closed == 1
                && popup.ViewModel.DismissalCallbacks == 1 && !origin.IsDismissed && !origin.IsPopupOpen,
                $"{profile}: typed popup result completes once and retains its page");

            (popup, showing) = await Open(); await popup.CloseAsync((string?)null);
            Check((await showing).Result == null && popup.ViewModel.DismissalCallbacks == 1, $"{profile}: explicit null popup result");
            (popup, showing) = await Open(); await host.ClosePopupAsync();
            Check((await showing).Result == null && popup.ViewModel.IsDismissed, $"{profile}: explicit window closes its popup instance");
            (popup, showing) = await Open(); popup.CanBeDismissedByTappingOutsideOfPopup = false;
            window.Navigation.ModalStack[^1].SendBackButtonPressed();
            Check(!showing.IsCompleted && !popup.ViewModel.IsDismissed, $"{profile}: disabled outside/back dismissal retains popup");
            popup.CanBeDismissedByTappingOutsideOfPopup = true;
            window.Navigation.ModalStack[^1].SendBackButtonPressed(); result = await showing;
            Check(result.WasDismissedByTappingOutsideOfPopup && result.Result == null && popup.ViewModel.DismissalCallbacks == 1,
                $"{profile}: Toolkit back/outside command completes owned cleanup");

            (popup, showing) = await Open();
            void Veto(object? sender, ModalPoppingEventArgs args) => args.Cancel = true;
            window.ModalPopping += Veto; var rejected = false;
            try { await popup.CloseAsync("rejected"); } catch (PopupCloseRejectedException) { rejected = true; }
            finally { window.ModalPopping -= Veto; }
            Check(rejected && !showing.IsCompleted && !popup.ViewModel.IsDismissed, $"{profile}: native popup veto preserves ownership");
            await popup.CloseAsync("retry");
            Check((await showing).Result == "retry", $"{profile}: rejected popup close releases Toolkit queue for retry");

            var (outer, first) = await Open(); var (inner, second) = await Open(); var blocked = false;
            try { await outer.CloseAsync("blocked"); } catch (PopupBlockedException) { blocked = true; }
            Check(blocked && !outer.ViewModel.IsDismissed && !inner.ViewModel.IsDismissed, $"{profile}: nested popup protects outer owner");
            await inner.CloseAsync("inner");
            Check((await second).Result == "inner" && !first.IsCompleted && origin.IsPopupOpen,
                $"{profile}: nested popup completes independently and keeps origin popup state");
            await outer.CloseAsync("outer");
            Check((await first).Result == "outer" && !origin.IsPopupOpen, $"{profile}: outer result clears the final origin popup state");

            using var cancellation = new CancellationTokenSource();
            (popup, showing) = await Open(cancellation.Token); cancellation.Cancel(); var cancelled = false;
            try { await showing; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !popup.ViewModel.IsDismissed && window.Navigation.ModalStack.Count == 1,
                $"{profile}: cancelling a popup result wait leaves the visible owner live");
            await host.ClosePopupAsync();
            Check(popup.ViewModel.DismissalCallbacks == 1 && !origin.IsPopupOpen, $"{profile}: cancelled result wait retains eventual cleanup");
            (popup, showing) = await Open(); await window.Navigation.PopModalAsync(false); result = await showing;
            Check(result.Reason == DismissalReason.Back && popup.ViewModel.DismissalCallbacks == 1,
                $"{profile}: direct native window pop completes the library popup result");

            (popup, showing) = await Open();
            Check((await host.ReplaceRootAsync<CatalogViewModel>(new(null))).IsSuccess
                && (await showing).Reason == DismissalReason.RootReplaced && popup.ViewModel.IsHostReplaced,
                $"{profile}: root replacement awaits an open popup and completes its result");
            var customRoot = await host.ReplaceRootAsync<CustomTabsViewModel>(new(null));
            var custom = (CustomTabsPage)customRoot.Value!.Page;
            await WaitUntil(() => custom.Handler != null);
            var child = (TabPage)((ICustomTabbedViewBase)custom).CurrentTab!.View;
            var delegatedModel = new PopupViewModel(navigation, log);
            var delegatedPopup = new ResultPopup(delegatedModel, this);
            var delegated = child.ShowPopupAsync(delegatedPopup);
            await WaitUntil(() => FindPopup(window.Navigation.ModalStack.LastOrDefault()) == delegatedPopup);
            await WaitUntil(() => delegatedPopup.IsLoaded && window.Navigation.ModalStack.LastOrDefault()?.IsLoaded == true);
            entered = new(); release = new();
            delegatedModel.Cleanup = async () => { entered.TrySetResult(); await release.Task; };
            closing = delegatedPopup.CloseAsync("delegated"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try { Check(custom.ModalDepth == 1 && custom.ModalClosings == 0 && !delegated.IsCompleted,
                $"{profile}: delegated host completion waits for popup cleanup"); }
            finally { release.TrySetResult(); }
            await closing;
            Check((await delegated).Result == "delegated" && custom.ModalDepth == 0 && custom.ModalOpenings == 1 && custom.ModalClosings == 1,
                $"{profile}: delegated result balances one host notification pair");

            var flyoutRoot = await host.ReplaceRootAsync<FlyoutViewModel>(new(null));
            var flyout = (FlyoutPage)flyoutRoot.Value!.Page; var menu = (MenuPage)flyout.Flyout;
            var item = menu.MenuItems.Single(item => item.Content is ResultPopup); PopupViewModel? previous = null;
            for (var cycle = 0; cycle < 2; cycle++)
            {
                await menu.SelectMenuItem(item, null);
                popup = (ResultPopup)item.Content!;
                await WaitUntil(() => FindPopup(window.Navigation.ModalStack.LastOrDefault()) == popup);
                // Logical modal insertion can precede Android fragment attachment. This test
                // closes a visible popup; wait for native loading before exercising cleanup.
                await WaitUntil(() => popup.IsLoaded && window.Navigation.ModalStack.LastOrDefault()?.IsLoaded == true);
                await popup.CloseAsync("flyout"); await host.ReconcileNativeAsync();
                Check(popup.ViewModel.DismissalCallbacks == 1 && popup.ViewModel != previous && !menu.ViewModel.IsDismissed,
                    $"{profile}/{cycle}: flyout popup menu remains reusable with a fresh lifetime");
                previous = popup.ViewModel;
            }
        }
        Check((await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true)).IsSuccess,
            "Popup smoke restores a navigable catalog");
    }

    private static Popup? FindPopup(Element? element)
    {
        if (element is Popup popup) return popup;
        if (element is IVisualTreeElement visual)
            foreach (var child in visual.GetVisualChildren().OfType<Element>())
                if (FindPopup(child) is { } found) return found;
        return null;
    }

    private sealed class ContainerModel : INavigationInitializable<string>, INavigationAware, INavigationGuard
    {
        internal string? Parameter;
        internal bool Allowed = true, FailActivation;
        internal bool InitializedOnUi, ActivatedOnUi, CleanedOnUi;
        internal int Activations, Deactivations, Dismissals;
        internal CancellationToken Token;
        internal DismissalReason Reason;
        internal Func<CancellationToken, Task>? Initialize;
        internal Func<Task>? Cleanup;
        public async Task InitializeAsync(string parameter, CancellationToken cancellationToken)
        { Parameter = parameter; await Task.Yield(); InitializedOnUi = MainThread.IsMainThread; if (Initialize != null) await Initialize(cancellationToken); }
        public Task ActivateAsync(CancellationToken lifetimeToken)
        {
            Token = lifetimeToken; Activations++; ActivatedOnUi = MainThread.IsMainThread;
            if (FailActivation) throw new InvalidOperationException("Deliberate coordinated page activation failure");
            return Task.CompletedTask;
        }
        public Task DeactivateAsync() { Deactivations++; return Task.CompletedTask; }
        public Task DismissAsync(DismissalReason reason) { Reason = reason; Dismissals++; CleanedOnUi = MainThread.IsMainThread; return Cleanup?.Invoke() ?? Task.CompletedTask; }
        public async Task<bool> CanNavigateAsync(CancellationToken cancellationToken)
        { await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); return Allowed; }
    }

    private sealed class RootModel : INavigationInitializable<string>, INavigationAware
    {
        internal string? Parameter;
        internal bool FactoryOnUi, InitializedOnUi, ActivatedOnUi, CleanedOnUi;
        internal int Activations, Dismissals;
        internal DismissalReason Reason;
        internal Func<CancellationToken, Task>? Initialize;
        internal Action? Activate;
        public async Task InitializeAsync(string parameter, CancellationToken cancellationToken)
        {
            Parameter = parameter;
            await Task.Yield();
            InitializedOnUi = MainThread.IsMainThread;
            if (Initialize != null) await Initialize(cancellationToken);
        }
        public Task ActivateAsync(CancellationToken lifetimeToken)
        {
            ActivatedOnUi = MainThread.IsMainThread;
            Activations++;
            Activate?.Invoke();
            return Task.CompletedTask;
        }
        public Task DismissAsync(DismissalReason reason)
        {
            CleanedOnUi = MainThread.IsMainThread;
            Dismissals++;
            Reason = reason;
            return Task.CompletedTask;
        }
    }

    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Scenario failed: {name}");
        passed.Add(name);
        log.Write($"CHECK PASS: {name}");
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
