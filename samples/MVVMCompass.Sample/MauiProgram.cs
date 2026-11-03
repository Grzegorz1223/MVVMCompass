using CommunityToolkit.Maui;

namespace MVVMCompass.Sample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>().UseMauiCommunityToolkit();
        builder.Services.AddSingleton<ScenarioLog>();
        builder.Services.AddScoped<DemoResource>();
#if NAVIGATION_SMOKE
        builder.Services.AddSingleton<SmokeRunner>();
        builder.Services.AddSingleton<PopupLifecycleProbe>();
#endif
        builder.UseMVVMCompass(pairs =>
        {
            pairs.Add<WelcomeViewModel, WelcomeView>();
            pairs.Add<TabsDemoViewModel, TabsDemoView>();
            pairs.Add<SharedTabsDemoViewModel, SharedTabsDemoView>();
            pairs.Add<FlyoutDemoViewModel, FlyoutDemoView>();
            pairs.Add<DocumentViewModel, DocumentView>();
            pairs.Add<DemoDetailViewModel, DemoDetailView>();
            pairs.Add<DemoModalViewModel, DemoModalView>();
            pairs.Add<DemoPopupViewModel, DemoPopupView>();
#if NAVIGATION_SMOKE
            pairs.Add<StartupFinalModel, StartupFinalView>();
            pairs.Add<StartupTrampolineModel, StartupTrampolineView>();
            pairs.Add<StartupBlockedModel, StartupBlockedView>();
            pairs.Add<DeferredDestinationModel, DeferredDestinationView>();
            pairs.Add<DeferredNoticeModel, DeferredNoticeView>();
            pairs.Add<ModalTabsProbeModel, ModalTabsProbeView>();
            pairs.Add<FlyoutTabProbeModel, FlyoutTabProbeView>();
#endif
        });
        return builder.Build();
    }
}
