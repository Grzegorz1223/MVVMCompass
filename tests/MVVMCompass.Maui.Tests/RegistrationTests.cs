using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void Removing_a_pair_removes_its_activator_and_allows_replacing_the_registered_view()
    {
        var registration = new NavigationRegistrationBuilder(new ServiceCollection());
        registration.Add<FirstVm, FirstPage>();
        Assert.False(registration.Remove(new KeyValuePair<Type, Type>(typeof(FirstVm), typeof(SecondPage))));
        Assert.NotNull(registration.Destination(typeof(FirstVm)));
        Assert.True(registration.Remove(new KeyValuePair<Type, Type>(typeof(FirstVm), typeof(FirstPage))));
        Assert.Throws<ArgumentException>(() => registration.Destination(typeof(FirstVm)));
        registration.Add<FirstVm, SecondPage>();
        Assert.Equal(typeof(SecondPage), registration[typeof(FirstVm)]);
    }

    [Fact]
    public void Typed_registration_defaults_to_separated_deactivation()
    {
        var typed = MauiApp.CreateBuilder();
        typed.UseMVVMCompass(registrations => registrations.Add<FirstVm, FirstPage>());
        using var typedServices = typed.Services.BuildServiceProvider();
        Assert.Equal(RetainedViewLifecycleBehavior.Deactivate, typedServices.GetRequiredService<NavigationOptions>().RetainedViewLifecycleBehavior);
        Assert.IsType<FirstPage>(typedServices.GetRequiredService<FirstPage>());
    }

    [Fact]
    public void Registered_types_remain_queryable_without_untyped_registration()
    {
        var registration = new NavigationRegistrationBuilder(new ServiceCollection());
        registration.Add<FirstVm, FirstPage>();
        IReadOnlyDictionary<Type, Type> pairs = registration;
        Assert.Equal(typeof(FirstPage), pairs[typeof(FirstVm)]);
        Assert.Single(pairs);
        Assert.False(typeof(IDictionary<Type, Type>).IsAssignableFrom(registration.GetType()));
    }

    [Fact]
    public void Late_module_registration_is_local_to_its_builder()
    {
        var first = MauiApp.CreateBuilder();
        var second = MauiApp.CreateBuilder();
        first.UseMVVMCompass(registrations => registrations.Add<FirstVm, FirstPage>());
        second.UseMVVMCompass(registrations => registrations.Add<SecondVm, SecondPage>());
        first.AddViewModelViewPair<ThirdVm, ThirdPage>();
        using var firstServices = first.Services.BuildServiceProvider();
        using var secondServices = second.Services.BuildServiceProvider();
        var firstPairs = firstServices.GetRequiredService<NavigationRegistrationBuilder>().Freeze();
        var secondPairs = secondServices.GetRequiredService<NavigationRegistrationBuilder>().Freeze();
        Assert.True(firstPairs.ContainsKey(typeof(ThirdVm)));
        Assert.False(secondPairs.ContainsKey(typeof(ThirdVm)));
        Assert.Null(secondServices.GetService<ThirdPage>());
        Assert.IsType<ThirdPage>(firstServices.GetRequiredService<ThirdPage>());
    }

    [Fact]
    public void Registration_on_an_unconfigured_builder_fails_even_if_another_builder_exists()
    {
        MauiApp.CreateBuilder().UseMVVMCompass(registrations => registrations.Add<FirstVm, FirstPage>());
        var other = MauiApp.CreateBuilder();
        Assert.Throws<InvalidOperationException>(() => other.AddViewModelViewPair<ThirdVm, ThirdPage>());
    }

    [Fact]
    public void Frozen_registration_and_locator_are_isolated_from_later_mutation()
    {
        var services = new ServiceCollection();
        var registration = new NavigationRegistrationBuilder(services);
        registration.Add<FirstVm, FirstPage>();
        var snapshot = registration.Freeze();
        using var provider = services.BuildServiceProvider();
        var locator = new ViewLocator(provider);
        locator.Initialize(snapshot);
        snapshot.Clear();
        Assert.Equal(typeof(FirstPage), locator.FindVEForViewModel(typeof(FirstVm)));
        Assert.Throws<InvalidOperationException>(() => registration.Add<SecondVm, SecondPage>());
    }

    [Fact]
    public void Existing_DI_factories_are_preserved_and_transient_pairs_get_fresh_instances()
    {
        var builder = MauiApp.CreateBuilder();
        var expected = new FirstVm();
        builder.Services.AddSingleton(expected);
        builder.UseMVVMCompass(registrations => registrations.Add<FirstVm, FirstPage>());
        using var services = builder.Services.BuildServiceProvider();
        Assert.Same(expected, services.GetRequiredService<FirstVm>());
        Assert.NotSame(services.GetRequiredService<FirstPage>(), services.GetRequiredService<FirstPage>());
    }

    [Theory]
    [InlineData((int)RetainedViewLifecycleBehavior.Deactivate)]
    [InlineData((int)RetainedViewLifecycleBehavior.LegacyAfterDismissed)]
    public void Typed_registration_preserves_explicit_lifecycle_profiles(int profileValue)
    {
        var profile = (RetainedViewLifecycleBehavior)profileValue;

        var builder = MauiApp.CreateBuilder();
        builder.UseMVVMCompass(registrations => registrations.Add<FirstVm, FirstPage>(),
            new NavigationOptions { RetainedViewLifecycleBehavior = profile });
        using var services = builder.Services.BuildServiceProvider();
        Assert.Equal(profile, services.GetRequiredService<NavigationOptions>().RetainedViewLifecycleBehavior);
    }

    [Fact]
    public void Abstract_destinations_are_rejected_before_they_reach_DI()
    {
        var services = new ServiceCollection();
        var registration = new NavigationRegistrationBuilder(services);
        Assert.Throws<ArgumentException>(() => registration.Add<AbstractVm, FirstPage>());
        Assert.Throws<ArgumentException>(() => registration.Add<FirstVm, AbstractPage>());
        Assert.Empty(registration);
        Assert.Empty(services);
    }

    [Fact]
    public void Duplicate_late_registration_does_not_replace_the_original_mapping_or_services()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMVVMCompass(registration => registration.Add<FirstVm, FirstPage>());
        Assert.Throws<ArgumentException>(() => builder.AddViewModelViewPair<FirstVm, SecondPage>());
        using var services = builder.Services.BuildServiceProvider();
        Assert.Equal(typeof(FirstPage), services.GetRequiredService<NavigationRegistrationBuilder>()[typeof(FirstVm)]);
        Assert.Null(services.GetService<SecondPage>());
    }

    [Fact]
    public void Explicit_factories_can_construct_destinations_without_public_constructors()
    {
        var builder = MauiApp.CreateBuilder();
        builder.Services.AddTransient(_ => FactoryVm.Create());
        builder.Services.AddTransient(provider => FactoryPage.Create(provider.GetRequiredService<FactoryVm>()));
        builder.UseMVVMCompass(registration => registration.Add<FactoryVm, FactoryPage>());
        using var services = builder.Services.BuildServiceProvider();
        var first = services.GetRequiredService<FactoryPage>();
        var second = services.GetRequiredService<FactoryPage>();
        Assert.IsType<FactoryVm>(first.BindingContext);
        Assert.NotSame(first.BindingContext, second.BindingContext);
    }

    public abstract class AbstractVm : ViewModelBase;
    public abstract class AbstractPage : ContentPage;
    public sealed class FactoryVm : ViewModelBase
    {
        private FactoryVm() { }
        public static FactoryVm Create() => new();
    }
    public sealed class FactoryPage : ContentPage
    {
        private FactoryPage(FactoryVm model) { BindingContext = model; }
        public static FactoryPage Create(FactoryVm model) => new(model);
    }
    public sealed class FirstVm : ViewModelBase;
    public sealed class SecondVm : ViewModelBase;
    public sealed class ThirdVm : ViewModelBase;
    public sealed class FirstPage : ContentPage;
    public sealed class SecondPage : ContentPage;
    public sealed class ThirdPage : ContentPage;
}
