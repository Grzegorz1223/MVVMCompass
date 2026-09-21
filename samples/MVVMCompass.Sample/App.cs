namespace MVVMCompass.Sample;

public sealed class App : Application
{
    private readonly MauiNavigationHostFactory navigation;
    private readonly SmokeRunner smoke;
    private readonly ScenarioLog log;
    public App(MauiNavigationHostFactory navigation, SmokeRunner smoke, ScenarioLog log)
    {
        this.navigation = navigation; this.smoke = smoke; this.log = log;
        Resources = new DemoResources();
        NavigationDiagnostics.Error += (error, operation) => log.Write($"DIAGNOSTIC {operation}: {error}");
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = StartupLoadingProbe.Enabled ? StartupLoadingProbe.CreateWindow(navigation) : navigation.CreateWindow<WelcomeViewModel>();
        window.Created += Started;
        return window;
        async void Started(object? sender, EventArgs args)
        {
            window.Created -= Started;
            try
            {
                if (StartupLoadingProbe.Enabled) { await StartupLoadingProbe.CompleteAsync(); return; }
                var result = await navigation.WaitForInitializationAsync(window);
                if (!result.IsSuccess) throw result.Error ?? new InvalidOperationException(result.Status.ToString());
                log.Write("READY: MAUI window is visible");
                if (Environment.GetEnvironmentVariable("MVVMCOMPASS_SMOKE") == "1") await smoke.RunAsync(window);
            }
            catch (Exception error) { log.Write($"STARTUP FAILED: {error}"); }
        }
    }
}
