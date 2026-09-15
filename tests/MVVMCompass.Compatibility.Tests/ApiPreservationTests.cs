using MVVMCompass.Interfaces;

namespace MVVMCompass.Compatibility.Tests;

public sealed class ApiPreservationTests
{
    [Fact]
    public void Unified_public_surface_exposes_one_ViewModel_navigation_service()
    {
        var exported = typeof(ViewModelBase).Assembly.GetExportedTypes();
        var required = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "PublicApi.txt"));
        Assert.NotEmpty(required);
        Assert.Empty(required.Except(exported.Select(type => type.FullName!)));
        Assert.DoesNotContain(exported, type => type.Name is "ScreenFactory" or "ScreenNavigator" or "NavigationDefinition"
            or "NavigationDestination" or "NavigationContext" or "MauiNavigationHost" or "ILegacyNavigationService" or "LegacyNavigationService");
        Assert.Null(typeof(ViewModelBase).GetProperty("Navigator"));
        Assert.Null(typeof(TabbedViewBase<>).GetProperty("Items"));
        Assert.Null(typeof(FlyoutViewBase<>).GetProperty("Items"));
        foreach (var method in typeof(INavigationService).GetMethods())
        {
            Assert.DoesNotContain(method.GetParameters(), parameter => typeof(Microsoft.Maui.Controls.Element).IsAssignableFrom(parameter.ParameterType)
                || typeof(ViewModelBase).IsAssignableFrom(parameter.ParameterType));
        }
        Assert.Contains(typeof(INavigationService).GetMethods(), method => method.Name == "Select" && !method.IsGenericMethod
            && method.GetParameters()[0].ParameterType == typeof(string));
        Assert.Equal(typeof(Microsoft.Maui.Controls.ContentView), typeof(ViewBase).BaseType);
    }

    [Fact]
    public void Toast_durations_and_notification_categories_keep_their_explicit_values()
    {
        Assert.Equal(new[] { "Default", "Reminder", "Alarm", "IncomingCall", "Urgent", "Short", "Long" }, Enum.GetNames<ToastType>());
        Assert.Equal(Enumerable.Range(0, 7), Enum.GetValues<ToastType>().Select(value => (int)value));
    }
}
