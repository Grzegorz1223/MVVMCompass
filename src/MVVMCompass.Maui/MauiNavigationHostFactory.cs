using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass;

/// <summary>Resolves one coordinated host for each explicit window, including during CreateWindow.</summary>
public sealed class MauiNavigationHostFactory
{
    /// <summary>Creates the application window factory from services configured by UseMVVMCompass.</summary>
    public MauiNavigationHostFactory(IServiceProvider services) : this(services.GetRequiredService<IViewLocator>(),
        services.GetRequiredService<NavigationOptions>(), services.GetRequiredService<IServiceScopeFactory>(),
        services.GetRequiredService<NavigationRegistrationBuilder>()) { }

    private readonly RegisteredScreenFactory? registry;
    private readonly ConditionalWeakTable<Window, Task<NavigationResult>> initialization = new();
    private readonly IViewLocator viewLocator;
    private readonly NavigationOptions options;
    private readonly IServiceScopeFactory? scopeFactory;
    private static readonly ConditionalWeakTable<Window, MauiNavigationHost> hosts = new();

    /// <summary>Creates a factory for explicit windows using the preserved locator and options configuration.</summary>
    internal MauiNavigationHostFactory(IViewLocator viewLocator, NavigationOptions options)
    { this.viewLocator = viewLocator; this.options = options; }

    /// <summary>Enables ordinary model factories to resolve from an explicitly owned entry scope.</summary>
    internal MauiNavigationHostFactory(IViewLocator viewLocator, NavigationOptions options, IServiceScopeFactory scopeFactory)
        : this(viewLocator, options) => this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    /// <summary>Creates a factory with typed, scoped destination registration.</summary>
    internal MauiNavigationHostFactory(IViewLocator viewLocator, NavigationOptions options, IServiceScopeFactory scopeFactory,
        NavigationRegistrationBuilder registration) : this(viewLocator, options, scopeFactory) => registry = new(registration, scopeFactory);

    /// <summary>Creates a window and initializes its registered root on the owning dispatcher.</summary>
    public Window CreateWindow<TViewModel>(Dictionary<string, object>? parameters = null) where TViewModel : ViewModelBase
    {
        if (registry == null) throw new InvalidOperationException("Register navigation with UseMVVMCompass before creating a window.");
        var window = new Window(new ContentPage { Content = new ActivityIndicator { IsRunning = true } });
        var host = ForWindow(window);
        initialization.Add(window, InitializeAsync());
        return window;
        async Task<NavigationResult> InitializeAsync()
        {
            NavigationResult result;
            try { result = NavigationResult.From(await host.ReplaceContentRootAsync(new(registry.Definition(typeof(TViewModel), parameters)))); }
            catch (Exception error) { result = new(MVVMCompass.Core.NavigationStatus.Failed, error: error); }
            if (!result.IsSuccess)
            {
                NavigationDiagnostics.Report(result.Error ?? new InvalidOperationException(result.Status.ToString()), "Window initialization");
                if (!host.IsClosed) window.Page = new ContentPage { Content = new Label { Text = "The application could not open its initial view." } };
            }
            return result;
        }
    }

    /// <summary>Awaits initialization of a window created by this factory.</summary>
    public Task<NavigationResult> WaitForInitializationAsync(Window window, CancellationToken cancellationToken = default) =>
        initialization.TryGetValue(window, out var task) ? task.WaitAsync(cancellationToken)
            : throw new ArgumentException("The window was not created by this factory.", nameof(window));

    /// <summary>Gets the window's host. A window cannot be rebound to another service configuration.</summary>
    internal MauiNavigationHost ForWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewLocator);
        ArgumentNullException.ThrowIfNull(options);
        lock (hosts)
        {
            var host = hosts.GetValue(window, key => new(key, viewLocator, options, scopeFactory));
            host.ValidateConfiguration(viewLocator, options, scopeFactory);
            return host;
        }
    }

    internal static ViewModelBase[] ExistingModels(Window target)
    {
        lock (hosts)
            return hosts.Select(pair => pair.Key).Concat(Application.Current?.Windows ?? []).Append(target).Distinct()
                .SelectMany(window => ViewModelTree.Collect(window.Page, true)).ToArray();
    }

    internal static MauiNavigationHost? Find(Window window)
    { lock (hosts) return hosts.TryGetValue(window, out var host) ? host : null; }
}
