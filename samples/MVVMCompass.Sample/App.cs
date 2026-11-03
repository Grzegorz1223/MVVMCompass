namespace MVVMCompass.Sample;

public sealed class App : Application
{
    private readonly MauiNavigationHostFactory navigation;
#if NAVIGATION_SMOKE
    private readonly SmokeRunner smoke;
#endif
    private readonly ScenarioLog log;
    public App(MauiNavigationHostFactory navigation, ScenarioLog log
#if NAVIGATION_SMOKE
        , SmokeRunner smoke
#endif
        )
    {
        this.navigation = navigation; this.log = log;
#if NAVIGATION_SMOKE
        this.smoke = smoke;
#endif
        Resources = new DemoResources();
        NavigationDiagnostics.Error += (error, operation) => log.Write($"DIAGNOSTIC {operation}: {error}");
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
#if NAVIGATION_SMOKE
        var window = StartupLoadingProbe.Enabled ? StartupLoadingProbe.CreateWindow(navigation) : navigation.CreateWindow<WelcomeViewModel>();
#else
        var window = navigation.CreateWindow<WelcomeViewModel>(null, new WindowBootstrapOptions
        { RootFailureContentFactory = result => new Label { Text = "Navigation failed: " + result.Error?.Message, Margin = 24 } });
#endif
        window.Created += Started;
        return window;
        async void Started(object? sender, EventArgs args)
        {
            window.Created -= Started;
            try
            {
#if NAVIGATION_SMOKE
                if (StartupLoadingProbe.Enabled) { await StartupLoadingProbe.CompleteAsync(); return; }
#endif
                var result = await navigation.WaitForInitializationAsync(window);
                if (!result.IsSuccess) throw result.Error ?? new InvalidOperationException(result.Status.ToString());
                log.Write("READY: MAUI window is visible");
#if NAVIGATION_SMOKE
                if (Environment.GetEnvironmentVariable("MVVMCOMPASS_SMOKE") == "1") await smoke.RunAsync(window);
#endif
            }
            catch (Exception error) { log.Write($"STARTUP FAILED: {error}"); }
        }
    }
}
