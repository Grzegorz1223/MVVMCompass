using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Core;
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
    private readonly ConditionalWeakTable<Window, WindowState> initialization = new();
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
        => CreateKnownWindow<TViewModel>(parameters, null);

    /// <summary>Creates a known initial root with custom bootstrap content, preserving the original generic overload.</summary>
    public Window CreateWindow<TViewModel>(Dictionary<string, object>? parameters, WindowBootstrapOptions options) where TViewModel : ViewModelBase
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateKnownWindow<TViewModel>(parameters, options);
    }

    /// <summary>Creates a window immediately and resolves its registered initial root inside the window's coordinated navigation operation.</summary>
    /// <remarks>The resolver runs once on the owning dispatcher. Respect its cancellation token; a late result after cancellation or supersession is ignored.
    /// Return a root selection instead of awaiting another root operation inside the resolver. Loading/failure factories create ordinary detached views.</remarks>
    public Window CreateWindow(Func<CancellationToken, Task<InitialRoot>> resolveInitialRoot, WindowBootstrapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(resolveInitialRoot);
        return CreateWindowCore(resolveInitialRoot, options, submitted: true);
    }

    private Window CreateKnownWindow<TViewModel>(Dictionary<string, object>? parameters, WindowBootstrapOptions? options) where TViewModel : ViewModelBase
    {
        var submittedParameters = CopyParameters(parameters);
        return CreateWindowCore(_ => Task.FromResult(InitialRoot.For<TViewModel>(submittedParameters)), options, submitted: false);
    }

    private Window CreateWindowCore(Func<CancellationToken, Task<InitialRoot>> resolve, WindowBootstrapOptions? bootstrapOptions, bool submitted)
    {
        if (registry == null) throw new InvalidOperationException("Register navigation with UseMVVMCompass before creating a window.");
        var bootstrap = new ContentPage();
        Exception? loadingError = null;
        try
        {
            bootstrap.Content = bootstrapOptions?.LoadingContentFactory is { } loading
                ? ValidateBootstrapContent(loading()) : new ActivityIndicator { IsRunning = true };
        }
        catch (Exception error) { loadingError = error; }
        var window = new Window(bootstrap);
        var host = ForWindow(window);
        var state = new WindowState(host) { Bootstrap = bootstrap, FailureContentFactory = bootstrapOptions?.FailureContentFactory };
        initialization.Add(window, state);
        _ = InitializeAsync();
        return window;
        async Task InitializeAsync()
        {
            NavigationResult result;
            try { result = NavigationResult.From(await host.InitializeContentRootAsync(
                loadingError == null ? resolve : _ => Task.FromException<InitialRoot>(loadingError), registry, submitted)); }
            catch (Exception error) { result = new(NavigationStatus.Failed, error: error); }
            if (result.Status == NavigationStatus.Failed)
                NavigationDiagnostics.Report(result.Error ?? new InvalidOperationException(result.Status.ToString()), "Window initialization");
            result = await CompleteRootAsync(state, result);
            state.Initialization.TrySetResult(result);
        }
    }

    /// <summary>Awaits the original initial-root attempt, including asynchronous resolution, root preparation and activation.</summary>
    /// <remarks>Cancelling this wait cancels only the waiter. A superseded attempt returns Superseded; await the successor's Completion separately.</remarks>
    public Task<NavigationResult> WaitForInitializationAsync(Window window, CancellationToken cancellationToken = default) =>
        initialization.TryGetValue(window, out var state) ? state.Initialization.Task.WaitAsync(cancellationToken)
            : throw new ArgumentException("The window was not created by this factory.", nameof(window));

    /// <summary>Replaces this factory's window root independently of any destination's navigation service.</summary>
    /// <remarks>Use RequestRoot inside navigation callbacks. An awaited call from such a callback returns Reentrant and submits nothing.</remarks>
    public Task<NavigationResult> SetRoot<TViewModel>(Window window, Dictionary<string, object>? parameters = null,
        RootTransitionOptions? options = null, CancellationToken cancellationToken = default) where TViewModel : ViewModelBase =>
        ChangeRoot<TViewModel>(window, parameters, options, cancellationToken, submitted: false);

    /// <summary>Submits a root change without waiting for execution, including from awaited lifecycle or guard callbacks.</summary>
    /// <remarks>Return from the submitting callback before observing Completion. Cancellation is cooperative before commitment.</remarks>
    public RootTransitionHandle RequestRoot<TViewModel>(Window window, Dictionary<string, object>? parameters = null,
        RootTransitionOptions? options = null, CancellationToken cancellationToken = default) where TViewModel : ViewModelBase =>
        new(ChangeRoot<TViewModel>(window, parameters, options, cancellationToken, submitted: true));

    private Task<NavigationResult> ChangeRoot<TViewModel>(Window window, Dictionary<string, object>? parameters,
        RootTransitionOptions? options, CancellationToken token, bool submitted) where TViewModel : ViewModelBase
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!initialization.TryGetValue(window, out var state))
            throw new ArgumentException("The window was not created by this factory.", nameof(window));
        options ??= new();
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.CoalescingKey != null && (string.IsNullOrWhiteSpace(options.CoalescingKey) || options.Mode != RootTransitionMode.Normal))
            throw new ArgumentException("Only normal requests can have a nonempty coalescing key.", nameof(options));
        if (state.Host.IsClosed) return Task.FromResult(new NavigationResult(NavigationStatus.InvalidOrigin));
        if (!submitted && state.Host.IsContentCallback) return Task.FromResult(new NavigationResult(NavigationStatus.Reentrant));
        var request = new NavigationRequest<NavigationDefinition>(registry!.Definition(typeof(TViewModel), CopyParameters(parameters)))
        {
            Priority = options.Mode == RootTransitionMode.Enforced ? NavigationPriority.Required : NavigationPriority.Normal,
            CoalescingKey = options.CoalescingKey
        };
        if (token.IsCancellationRequested) return Task.FromResult(new NavigationResult(NavigationStatus.Cancelled));
        Interlocked.Increment(ref state.PendingRoots);
        return ExecuteAsync();

        async Task<NavigationResult> ExecuteAsync()
        {
            NavigationResult result;
            try { result = NavigationResult.From(await state.Host.ReplaceContentRootAsync(request, cancellationToken: token,
                applicationMode: options.Mode, submitted: submitted)); }
            catch (Exception error) { result = new(NavigationStatus.Failed, error: error); }
            return await CompleteRootAsync(state, result);
        }
    }

    private static async ValueTask<NavigationResult> CompleteRootAsync(WindowState state, NavigationResult result)
    {
        // A completed root has replaced bootstrap permanently. Keep subsequent navigation on its usual path.
        var release = result.IsSuccess ? Interlocked.Exchange(ref state.Bootstrap, null) : null;
        var remaining = Interlocked.Decrement(ref state.PendingRoots);
        List<Exception> errors = [];
        try
        {
            if (remaining == 0 && Volatile.Read(ref state.Bootstrap) is { } bootstrap)
            {
                // Recovery rechecks pending requests and the actual page on the owning dispatcher.
                if (state.Host.IsClosed || await state.Host.ShowBootstrapFailureAsync(bootstrap,
                        () => Volatile.Read(ref state.PendingRoots) == 0, FailureContent))
                    release = Interlocked.CompareExchange(ref state.Bootstrap, null, bootstrap);
            }
        }
        catch (Exception error)
        {
            NavigationDiagnostics.Report(error, "Window bootstrap recovery");
            errors.Add(error);
        }
        if (release is ContentPage released)
        {
            state.FailureContentFactory = null;
            try
            {
                await state.Host.DispatchContentAsync(() =>
                {
                    if (released.Content is ActivityIndicator indicator) indicator.IsRunning = false;
                    released.Content = null;
                });
            }
            catch (Exception error) { NavigationDiagnostics.Report(error, "Window bootstrap cleanup"); errors.Add(error); }
        }
        if (errors.Count != 0) result = new(result.Status, result.HasCommitted, result.Error, [.. result.CleanupErrors, .. errors]);
        return result;

        View FailureContent()
        {
            if (state.FailureContentFactory is { } create)
            {
                try { return ValidateBootstrapContent(create(result)); }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Window bootstrap failure content"); errors.Add(error); }
            }
            return new Label { Text = "The application could not open its initial view." };
        }
    }

    private static View ValidateBootstrapContent(View? content) => content is { Parent: null } && content is not ViewBase
        ? content : throw new InvalidOperationException("Bootstrap factories must return a fresh, detached ordinary View, not a navigation screen.");

    private static Dictionary<string, object>? CopyParameters(Dictionary<string, object>? parameters) =>
        parameters == null ? null : new(parameters, parameters.Comparer);

    private sealed class WindowState(MauiNavigationHost host)
    {
        internal MauiNavigationHost Host { get; } = host;
        internal TaskCompletionSource<NavigationResult> Initialization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Page? Bootstrap;
        internal Func<NavigationResult, View>? FailureContentFactory;
        // Includes the initial root before CreateWindow can admit any successors.
        internal int PendingRoots = 1;
    }

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
