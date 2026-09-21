using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MVVMCompass.Core;

namespace MVVMCompass.Sample;

// A fresh-process fixture. The tools runner compares both startup patterns in the
// same Release binary and measures launch through the final native layout checkpoint.
internal static class StartupLoadingProbe
{
    internal static bool Enabled => Environment.GetEnvironmentVariable("MVVMCOMPASS_STARTUP_MODE") != null;
    private static readonly Stopwatch elapsed = new();
    private static readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource<InitialRoot> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static MauiNavigationHostFactory factory = null!;
    private static Window window = null!;
    private static ContentPage bootstrap = null!;
    private static View loading = null!;
    internal static View? FinalView;
    internal static int Constructed, Activated, ResolverCalls, FailureCalls;
    internal static RootTransitionHandle? Successor;
    private static string Mode => Environment.GetEnvironmentVariable("MVVMCOMPASS_STARTUP_MODE")!;

    internal static Window CreateWindow(MauiNavigationHostFactory navigation)
    {
        factory = navigation; elapsed.Start();
        var options = new WindowBootstrapOptions
        {
            LoadingContentFactory = () => loading = new Grid
            {
                BackgroundColor = Colors.White,
                Children = { new Label { Text = "MVVMCompass", TextColor = Colors.DarkSlateBlue,
                    HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, FontSize = 28 } }
            },
            FailureContentFactory = result =>
            {
                FailureCalls++;
                return FinalView = new Label { Text = "Startup: " + result.Status, AutomationId = "startup-fallback" };
            }
        };
        window = Mode == "trampoline" ? factory.CreateWindow<StartupTrampolineModel>(null, options) : factory.CreateWindow(Resolve, options);
        bootstrap = (ContentPage)window.Page!;
        return window;
    }

    private static async Task<InitialRoot> Resolve(CancellationToken token)
    {
        ResolverCalls++; entered.TrySetResult();
        if (Mode == "blocked") return await late.Task;
        await Task.Delay(250, token);
        if (Mode == "failure") throw new InvalidOperationException("Startup fixture failure");
        if (Mode == "cancelled") throw new OperationCanceledException(new CancellationToken(true));
        return InitialRoot.For<StartupFinalModel>();
    }

    internal static async Task PrepareTrampoline()
    {
        await Task.Delay(250);
        Successor = factory.RequestRoot<StartupFinalModel>(window);
    }

    internal static async Task CompleteAsync()
    {
        string? failure = null;
        var status = "unknown";
        try
        {
            if (Mode == "blocked")
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Successor = factory.RequestRoot<StartupBlockedModel>(window, options: new() { Mode = RootTransitionMode.Enforced });
            }
            var result = await factory.WaitForInitializationAsync(window).WaitAsync(TimeSpan.FromSeconds(10));
            status = result.Status.ToString();
            if (Successor != null && !(await Successor.Completion.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess)
                throw new InvalidOperationException("Successor failed");
            if (Mode == "blocked") late.SetResult(InitialRoot.For<StartupFinalModel>());
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (FinalView?.Handler == null || FinalView.Width <= 0 || FinalView.Height <= 0 || !NativeCoveragePlatform.Visible(FinalView))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Final native view was not presented");
                await Task.Delay(10);
            }
            // Fixed presentation settling allowance, included in every reported duration.
            await Task.Delay(34);
            if (bootstrap.Content != null || loading.Parent != null) throw new InvalidOperationException("Bootstrap content was retained");
            var expected = Mode == "trampoline" ? 2 : Mode is "failure" or "cancelled" ? 0 : 1;
            if (Constructed != expected || Activated != expected) throw new InvalidOperationException($"Root counts: {Constructed}/{Activated}, expected {expected}");
            var expectedStatus = Mode switch { "blocked" => NavigationStatus.Superseded, "failure" => NavigationStatus.Failed,
                "cancelled" => NavigationStatus.Cancelled, _ => NavigationStatus.Completed };
            if (result.Status != expectedStatus) throw new InvalidOperationException("Unexpected initial result " + result.Status);
            if (Mode == "blocked" && FinalView is not StartupBlockedView) throw new InvalidOperationException("Late startup replaced Blocked");
            if (ResolverCalls != (Mode == "trampoline" ? 0 : 1)) throw new InvalidOperationException("Resolver call count");
        }
        catch (Exception error) { failure = error.ToString(); }
        var report = new StartupProbeResult(failure == null ? "passed" : "failed", Mode, status, Constructed, Activated,
            ResolverCalls, FailureCalls, elapsed.Elapsed.TotalMilliseconds, failure);
        var json = JsonSerializer.Serialize(report, StartupProbeJsonContext.Default.StartupProbeResult);
        File.WriteAllText(Path.Combine(FileSystem.AppDataDirectory, "startup-result.json"), json);
#if ANDROID
        Android.Util.Log.Info("MVVMCompassStartup", Environment.GetEnvironmentVariable("MVVMCOMPASS_SMOKE_RUN_ID") + ":" +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)));
#endif
    }
}

public sealed class StartupFinalModel : ViewModelBase
{
    public StartupFinalModel() => StartupLoadingProbe.Constructed++;
    public override Task Appearing() { StartupLoadingProbe.Activated++; return Task.CompletedTask; }
}
public sealed class StartupFinalView : ViewBase<StartupFinalModel>
{
    public StartupFinalView(StartupFinalModel model) : base(model)
    {
        StartupLoadingProbe.FinalView = this;
        Content = new Label { Text = "Final root", AutomationId = "startup-final" };
    }
}
public sealed class StartupTrampolineModel : ViewModelBase
{
    public StartupTrampolineModel() => StartupLoadingProbe.Constructed++;
    public override Task BeforeFirstShown() => StartupLoadingProbe.PrepareTrampoline();
    public override Task Appearing() { StartupLoadingProbe.Activated++; return Task.CompletedTask; }
}
public sealed class StartupTrampolineView(StartupTrampolineModel model) : ViewBase<StartupTrampolineModel>(model);
public sealed class StartupBlockedModel : ViewModelBase
{
    public StartupBlockedModel() => StartupLoadingProbe.Constructed++;
    public override Task Appearing() { StartupLoadingProbe.Activated++; return Task.CompletedTask; }
}
public sealed class StartupBlockedView : ViewBase<StartupBlockedModel>
{
    public StartupBlockedView(StartupBlockedModel model) : base(model)
    {
        StartupLoadingProbe.FinalView = this;
        Content = new Label { Text = "Blocked", AutomationId = "startup-blocked" };
    }
}
internal sealed record StartupProbeResult(string Result, string Mode, string InitialStatus, int ConstructedRoots,
    int ActivatedRoots, int ResolverCalls, int FailureCalls, double WindowToPresentationMs, string? Failure);
[JsonSerializable(typeof(StartupProbeResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class StartupProbeJsonContext : JsonSerializerContext;

internal sealed partial class UnifiedNativeCoverage
{
    internal async Task BusyLoadingAsync()
    {
        Success(await Current.Navigation.SetRoot<FlyoutDemoViewModel>(), "busy: nested flyout root");
        var root = Root; var owner = root.Current!.View; var leaf = root.Deepest.Current!.View;
        var parent = (FlyoutDemoViewModel)root.Current.ViewModel;
        var sourceModel = (DemoViewModel)root.Deepest.Current.ViewModel;
        var typedCreations = 0;
        owner.Resources["BusyColor"] = Colors.Purple;
        owner.LoadingPresentationTemplate = new DataTemplate(() =>
        {
            typedCreations++;
            var label = new Label { Text = "Customer loading", HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
            label.SetDynamicResource(Label.TextColorProperty, "BusyColor");
            return label;
        });
        Check(typedCreations == 0, "busy: customer runtime template is lazy");
        leaf.IsBusy = true;
        var typedContent = root.View.BusyContent!; await Ready(typedContent);
        var typedState = (LoadingPresentationContext)typedContent.BindingContext;
        Check(typedCreations == 1 && typedState.LoadingType == LoadingType.Loading &&
            ReferenceEquals(typedState.Owner, owner.BindingContext) && ((Label)typedContent).TextColor == Colors.Purple,
            "busy: typed customer template receives generic direct-busy state, owner, and resources");

        var guardEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guardRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sourceModel.Guard = async () => { guardEntered.TrySetResult(); return await guardRelease.Task; };
        var rejected = parent.Navigation.Select("personal");
        await guardEntered.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Check(root.IsNavigating && !root.View.IsBusyPresented && !NativeCoveragePlatform.Visible(typedContent)
            && typedContent.Parent != null, "busy: customer loader is hidden but retained throughout pending navigation");
        guardRelease.TrySetResult(false);
        Check((await rejected).Status == NavigationStatus.GuardRejected && root.View.IsBusyPresented &&
            ReferenceEquals(typedContent, root.View.BusyContent) && NativeCoveragePlatform.Visible(typedContent),
            "busy: rejected navigation restores the same customer presentation");
        sourceModel.Guard = null;
        Success(await parent.Navigation.Select("personal"), "busy: successful navigation leaves the source loader");
        Check(!root.View.IsBusyPresented, "busy: source customer loader never appears over the committed target");
        leaf.IsBusy = false;
        owner.LoadingPresentationTemplate = null;
        leaf = root.Deepest.Current!.View;

        var creations = 0;
        owner.BusyOverlayTemplate = new DataTemplate(() =>
        {
            creations++;
            var label = new Label { Text = "Branded loading", HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
            label.SetDynamicResource(Label.TextColorProperty, "BusyColor"); return label;
        });
        leaf.IsBusy = true;
        var content = root.View.BusyContent!; await Ready(content);
        Check(NativeCoveragePlatform.Visible(content), "busy: custom content is natively visible");
        Check(ReferenceEquals(content.BindingContext, owner.BindingContext) && ((Label)content).TextColor == Colors.Purple,
            "busy: template uses declaring model and resources");
        // Item templates may deliberately contain their own small busy badges.
        // Count navigation busy presenters, not unrelated app-template controls.
        Check(root.ActiveChain().Count(context => context.View.IsBusyPresented) == 1 &&
            root.ActiveChain().All(context => context.View.BusyContent is not ActivityIndicator), "busy: exactly one nested presentation and no default spinner");
        for (var i = 0; i < 20; i++) { leaf.IsBusy = false; leaf.IsBusy = true; }
        Check(creations == 1 && ReferenceEquals(content, root.View.BusyContent), "busy: repeated toggles reuse native content");
        leaf.IsBusy = false; await Task.Delay(40);
        Check(!NativeCoveragePlatform.Visible(content), "busy: inactive loading content is hidden");
        owner.BusyOverlayTemplate = null; leaf.IsBusy = true;
        var indicator = (ActivityIndicator)root.View.BusyContent!; await Ready(indicator);
        Check(indicator.IsRunning && NativeCoveragePlatform.Visible(indicator), "busy: legacy default remains available");
        Check(root.ActiveChain().Count(context => context.View.BusyContent is ActivityIndicator) == 1, "busy: default also has one nested owner");
        owner.UseDefaultBusyIndicator = false;
        Check(!root.View.IsBusyPresented, "busy: container can disable fallback");
        leaf.IsBusy = false;
        Success(await Current.Navigation.SetRoot<WelcomeViewModel>(), "busy: restore sample");
        Check(content.Parent == null && !indicator.IsRunning, "busy: old content detached and animation stopped");
    }
}
