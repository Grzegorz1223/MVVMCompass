using Microsoft.Extensions.DependencyInjection.Extensions;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

internal static class RegressionRegistration
{
    internal static void Add(MauiAppBuilder builder)
    {
        builder.Services.AddSingleton<SharedSession>();
        builder.Services.AddSingleton<RegressionSmokeRunner>();
        builder.Services.AddSingleton<EntryScopeSmoke>();
        builder.Services.AddSingleton<ScopeProbeState>();
        builder.Services.AddScoped<ScopeProbeResource>();
        builder.Services.AddTransient<ScopePlainModel>();
        builder.Services.AddScoped<WorkbenchResource>();
        builder.Services.AddTransient<WorkbenchModel>();
        builder.Services.AddSingleton<INotificationService, SampleNotifications>();
        // Both profiles are selectable before creating a scenario's next root.
        ConfigurePairs(builder);
    }
    private static void ConfigurePairs(MauiAppBuilder builder)
    {
        var pairs = builder.Services.Single(item => item.ServiceType == typeof(NavigationRegistrationBuilder)).ImplementationInstance as NavigationRegistrationBuilder
            ?? throw new InvalidOperationException();
            pairs.Add<CatalogViewModel, CatalogPage>();
            pairs.Add<WorkbenchLegacyModel, WorkbenchLegacyPage>();
            pairs.Add<WorkbenchTabsModel, WorkbenchTabsPage>();
            pairs.Add<WorkbenchPopupModel, WorkbenchPopup>();
            pairs.Add<ScopeProbeModel, ScopeProbePage>();
            pairs.Add<ScopeTabsModel, ScopeTabsPage>();
            pairs.Add<ScopePopupModel, ScopePopup>();
            pairs.Add<DetailViewModel, DetailPage>();
            pairs.Add<ModalViewModel, ModalPage>();
            pairs.Add<CustomTabsViewModel, CustomTabsPage>();
            pairs.Add<TabViewModel, TabPage>();
            pairs.Add<StandardTabsViewModel, StandardTabsPage>();
            pairs.Add<FirstViewModel, FirstPage>();
            pairs.Add<SecondViewModel, SecondPage>();
            pairs.Add<FlyoutViewModel, FlyoutPage>();
            pairs.Add<MenuViewModel, MenuPage>();
            pairs.Add<PopupViewModel, ResultPopup>();
            pairs.Add<WindowViewModel, WindowPage>();
            pairs.Add<FailingViewModel, FailingPage>();

        builder.AddViewModelViewPair<LateViewModel, LatePage>();
        builder.Services.Replace(ServiceDescriptor.Transient<CatalogViewModel, CatalogViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<WorkbenchLegacyModel, WorkbenchLegacyModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<WorkbenchTabsModel, WorkbenchTabsModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<WorkbenchPopupModel, WorkbenchPopupModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<ScopeProbeModel, ScopeProbeModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<ScopeTabsModel, ScopeTabsModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<ScopePopupModel, ScopePopupModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<DetailViewModel, DetailViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<ModalViewModel, ModalViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<CustomTabsViewModel, CustomTabsViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<TabViewModel, TabViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<StandardTabsViewModel, StandardTabsViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<FirstViewModel, FirstViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<SecondViewModel, SecondViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<FlyoutViewModel, FlyoutViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<MenuViewModel, MenuViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<PopupViewModel, PopupViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<WindowViewModel, WindowViewModel>());
        builder.Services.Replace(ServiceDescriptor.Transient<FailingViewModel, FailingViewModel>());
    }
}
