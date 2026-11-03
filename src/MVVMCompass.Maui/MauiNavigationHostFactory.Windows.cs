using Microsoft.Maui.Dispatching;
using MVVMCompass.Core;

namespace MVVMCompass;

public sealed partial class MauiNavigationHostFactory
{
    /// <summary>Configures and opens an independent window on the application's dispatcher, returning its initialization and closure.</summary>
    /// <remarks>Configure runs before OpenWindow; use Window.Created/HandlerChanged for native placement. Cancellation after creation closes the new window.
    /// A failed root initialization keeps the window accessible for recovery. No desktop placement or sizing policy is imposed.</remarks>
    public Task<WindowNavigationResult> OpenWindow<TViewModel>(Action<Window> configure,
        Dictionary<string, object>? parameters = null, WindowBootstrapOptions? options = null,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase
    {
        ArgumentNullException.ThrowIfNull(configure);
        var application = Application.Current ?? throw new InvalidOperationException("An application is required to open a window.");
        return application.Dispatcher.DispatchAsync(() => OpenWindowCore<TViewModel>(configure, parameters, options,
            cancellationToken, application.OpenWindow, window => application.CloseWindow(window)));
    }

    internal async Task<WindowNavigationResult> OpenWindowCore<TViewModel>(Action<Window> configure,
        Dictionary<string, object>? parameters, WindowBootstrapOptions? options, CancellationToken token,
        Action<Window> open, Action<Window> close) where TViewModel : ViewModelBase
    {
        if (token.IsCancellationRequested) return new(new(NavigationStatus.Cancelled), null, Task.CompletedTask);
        Window? window = null;
        MauiNavigationHost? host = null;
        var opening = false;
        try
        {
            window = CreateKnownWindow<TViewModel>(parameters, options);
            host = ForWindow(window);
            configure(window);
            token.ThrowIfCancellationRequested();
            if (host.IsClosed) return new(new(NavigationStatus.Cancelled), window, host.Completion);
            opening = true;
            open(window);
            var result = await WaitForInitializationAsync(window, token);
            return new(result, window, host.Completion);
        }
        catch (Exception error)
        {
            List<Exception> cleanup = [];
            if (opening && window != null)
                try { close(window); }
                catch (Exception closeError) { cleanup.Add(closeError); NavigationDiagnostics.Report(closeError, "New window close"); }
            if (host != null)
                try { await host.DisposeAsync(); }
                catch (Exception cleanupError) { cleanup.Add(cleanupError); }
            var cancelled = error is OperationCanceledException && token.IsCancellationRequested;
            return new(new(cancelled ? NavigationStatus.Cancelled : NavigationStatus.Failed,
                error: cancelled ? null : error, cleanupErrors: cleanup), window, host?.Completion ?? Task.CompletedTask);
        }
    }
}
