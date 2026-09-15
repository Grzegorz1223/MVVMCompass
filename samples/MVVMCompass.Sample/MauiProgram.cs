using CommunityToolkit.Maui;

namespace MVVMCompass.Sample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>().UseMauiCommunityToolkit();
        builder.Services.AddSingleton<ScenarioLog>();
        builder.Services.AddScoped<DemoResource>();
        builder.Services.AddSingleton<SmokeRunner>();
        builder.UseMVVMCompass(pairs =>
        {
            pairs.Add<WelcomeViewModel, WelcomeView>();
            pairs.Add<TabsDemoViewModel, TabsDemoView>();
            pairs.Add<FlyoutDemoViewModel, FlyoutDemoView>();
            pairs.Add<DocumentViewModel, DocumentView>();
            pairs.Add<DemoDetailViewModel, DemoDetailView>();
            pairs.Add<DemoModalViewModel, DemoModalView>();
            pairs.Add<DemoPopupViewModel, DemoPopupView>();
        });
        RegressionRegistration.Add(builder);
        return builder.Build();
    }
}
