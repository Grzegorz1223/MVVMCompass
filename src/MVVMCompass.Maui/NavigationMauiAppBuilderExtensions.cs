using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass;

/// <summary>Registers navigation services and view/model pairs on one MAUI application builder.</summary>
public static class NavigationMauiAppBuilderExtensions
{
    /// <summary>Registers view/model pairs and navigation services before application initialization. The typed delegate defaults to retained deactivation.</summary>
    public static MauiAppBuilder UseMVVMCompass(this MauiAppBuilder builder,
        Action<NavigationRegistrationBuilder> configureDelegate) =>
        UseMVVMCompass(builder, configureDelegate, new NavigationOptions());

    /// <summary>Registers navigation with an explicit retained-view lifecycle policy.</summary>
    internal static MauiAppBuilder UseMVVMCompass(this MauiAppBuilder builder,
        Action<NavigationRegistrationBuilder> configureDelegate, NavigationOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureDelegate);
        ArgumentNullException.ThrowIfNull(options);
        if (builder.Services.Any(service => service.ServiceType == typeof(NavigationRegistrationBuilder)))
            throw new InvalidOperationException("Navigation is already registered on this builder. Use AddViewModelViewPair for additional modules.");

        var registration = new NavigationRegistrationBuilder(builder.Services);
        configureDelegate(registration);
        builder.Services.AddSingleton(registration);
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton<IViewLocator, ViewLocator>();
        builder.Services.TryAddSingleton<ILegacyNavigationService, LegacyNavigationService>();
        builder.Services.TryAddSingleton<MauiNavigationHostFactory>();
        builder.Services.TryAddScoped<INavigationService, NavigationService>();
        builder.Services.TryAddSingleton<RegisteredScreenFactory>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Transient<IMauiInitializeService, NavigationInitializer>());
        return builder;
    }

    /// <summary>Adds a typed module registration before application initialization freezes the registry.</summary>
    public static MauiAppBuilder AddViewModelViewPair<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView>(this MauiAppBuilder builder)
        where TViewModel : ViewModelBase where TView : VisualElement
    {
        ArgumentNullException.ThrowIfNull(builder);
        var registration = builder.Services.FirstOrDefault(service =>
            service.ServiceType == typeof(NavigationRegistrationBuilder))?.ImplementationInstance as NavigationRegistrationBuilder
            ?? throw new InvalidOperationException("Call UseMVVMCompass on this builder first.");
        registration.Add<TViewModel, TView>();
        return builder;
    }
}

/// <summary>Builder-local typed registrations with constructor preservation for trimming and NativeAOT.</summary>
public sealed class NavigationRegistrationBuilder : IReadOnlyDictionary<Type, Type>
{
    private readonly IServiceCollection services;
    private readonly Dictionary<Type, Type> pairs = new();
    private bool frozen;
    private readonly Dictionary<Type, RegisteredDestination> activators = new();
    internal RegisteredDestination Destination(Type type) => activators.TryGetValue(type, out var value) ? value
        : throw new ArgumentException("The destination model is not registered.", nameof(type));

    internal NavigationRegistrationBuilder(IServiceCollection services) => this.services = services;

    /// <summary>Adds a scoped model and transient view before the registry is frozen.</summary>
    public void Add<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView>()
        where TViewModel : ViewModelBase where TView : VisualElement
    {
        CheckMutable();
        Validate(typeof(TViewModel), typeof(TView));
        pairs.Add(typeof(TViewModel), typeof(TView));
        services.TryAddScoped<TViewModel>();
        activators.Add(typeof(TViewModel), new(provider => provider.GetRequiredService<TViewModel>(), provider => provider.GetRequiredService<TView>()));
        services.TryAddTransient<TView>();
    }

    /// <summary>Gets the registered view type for a model type.</summary>
    public Type this[Type key] => pairs[key];
    /// <summary>Gets the registered model types.</summary>
    public ICollection<Type> Keys => pairs.Keys;
    /// <summary>Gets the registered visual element types.</summary>
    public ICollection<Type> Values => pairs.Values;
    IEnumerable<Type> IReadOnlyDictionary<Type, Type>.Keys => pairs.Keys;
    IEnumerable<Type> IReadOnlyDictionary<Type, Type>.Values => pairs.Values;
    /// <summary>Gets the number of registered pairs.</summary>
    public int Count => pairs.Count;
    /// <summary>Gets whether application initialization has frozen the registry.</summary>
    public bool IsReadOnly => frozen;

    /// <summary>Determines whether a model type is registered.</summary>
    public bool ContainsKey(Type key) => pairs.ContainsKey(key);
    /// <summary>Finds a model's registered view type; returns false and a null output when the model is absent.</summary>
    public bool TryGetValue(Type key, [MaybeNullWhen(false)] out Type value) => pairs.TryGetValue(key, out value);
    /// <summary>Determines whether the exact model/view pair is registered.</summary>
    public bool Contains(KeyValuePair<Type, Type> item) => ((ICollection<KeyValuePair<Type, Type>>)pairs).Contains(item);
    /// <summary>Copies the registered pairs into an existing array at the supplied index.</summary>
    public void CopyTo(KeyValuePair<Type, Type>[] array, int arrayIndex) => ((ICollection<KeyValuePair<Type, Type>>)pairs).CopyTo(array, arrayIndex);
    /// <summary>Enumerates the registered model/view pairs.</summary>
    public IEnumerator<KeyValuePair<Type, Type>> GetEnumerator() => pairs.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    /// <summary>Removes a lookup pair before initialization; existing DI service registrations are retained.</summary>
    public bool Remove(Type key) { CheckMutable(); activators.Remove(key); return pairs.Remove(key); }
    /// <summary>Removes a lookup pair before initialization; existing DI service registrations are retained.</summary>
    public bool Remove(KeyValuePair<Type, Type> item)
    {
        CheckMutable();
        if (!((ICollection<KeyValuePair<Type, Type>>)pairs).Remove(item)) return false;
        activators.Remove(item.Key);
        return true;
    }
    /// <summary>Clears lookup pairs before initialization; existing DI service registrations are retained.</summary>
    public void Clear() { CheckMutable(); pairs.Clear(); activators.Clear(); }

    internal Dictionary<Type, Type> Freeze()
    {
        frozen = true;
        return new Dictionary<Type, Type>(pairs);
    }
    private void CheckMutable()
    {
        if (frozen) throw new InvalidOperationException("Navigation registrations are frozen after application initialization.");
    }
    private static void Validate(Type viewModel, Type view)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(view);
        if (!typeof(ViewModelBase).IsAssignableFrom(viewModel) || viewModel.IsAbstract || viewModel.ContainsGenericParameters)
            throw new ArgumentException("Register a concrete ViewModelBase type.", nameof(viewModel));
        if (!typeof(VisualElement).IsAssignableFrom(view) || view.IsAbstract || view.ContainsGenericParameters)
            throw new ArgumentException("Register a concrete VisualElement type.", nameof(view));
    }
}

internal sealed class NavigationInitializer(NavigationRegistrationBuilder registration) : IMauiInitializeService
{
    public void Initialize(IServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<ILegacyNavigationService>().Initialize(registration.Freeze());
}
