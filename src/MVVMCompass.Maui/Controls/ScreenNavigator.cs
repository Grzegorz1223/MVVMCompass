using MVVMCompass.Core;
using Microsoft.Maui.Dispatching;

namespace MVVMCompass;

/// <summary>Navigation originating from one screen. Hidden and dismissed origins are rejected.</summary>
internal sealed class ScreenNavigator
{
    internal ScreenNavigator(NavigationContext context, NavigationScreenEntry screen) { Context = context; Screen = screen; }
    /// <summary>Gets the owning content scope.</summary>
    public NavigationContext Context { get; }
    /// <summary>Gets the originating screen entry.</summary>
    public NavigationScreenEntry Screen { get; }
    private NavigationRequestOptions Options => new() { Origin = Screen.Entry };

    /// <summary>Pushes a screen from this origin.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> PushAsync(ScreenFactory factory,
        Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default) =>
        Context.PushAsync(factory, parameters, Options, cancellationToken);
    /// <summary>Returns to the previous screen after CanNavigate permits leaving this entry.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> BackAsync(CancellationToken cancellationToken = default) =>
        Context.BackAsync(Options, cancellationToken);
    /// <summary>Removes the active stack's details after their guards permit removal.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> PopToRootAsync(CancellationToken cancellationToken = default) =>
        Context.PopToRootAsync(Options, cancellationToken);
    /// <summary>Selects a registered retained destination from this origin.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> SelectAsync(string destinationId,
        Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default) =>
        Context.SelectAsync(destinationId, parameters, Options, cancellationToken);
    /// <summary>Selects the configured default destination.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> SelectDefaultAsync(CancellationToken cancellationToken = default) =>
        Context.SelectDefaultAsync(Options, cancellationToken);
    /// <summary>Resets a destination's entire history after its guards permit removal.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> ResetDestinationAsync(string destinationId, CancellationToken cancellationToken = default) =>
        Context.ResetDestinationAsync(destinationId, Options, cancellationToken);
    /// <summary>Removes a destination or group and, when necessary, selects a surviving destination.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> RemoveDestinationAsync(string destinationId, string? replacementDestinationId = null,
        CancellationToken cancellationToken = default) => Context.RemoveDestinationAsync(destinationId, replacementDestinationId, Options, cancellationToken);
    /// <summary>Opens a modal custom navigation scope.</summary>
    public Task<NavigationOutcome<NavigationContext?>> OpenModalAsync(NavigationDefinition definition,
        Action<NavigationView>? configure = null, CancellationToken cancellationToken = default) =>
        Context.OpenModalAsync(definition, configure, Options, cancellationToken);
    /// <summary>Closes the owning modal after its CanNavigate guards permit dismissal.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> CloseModalAsync(CancellationToken cancellationToken = default) =>
        Context.CloseModalAsync(Options, cancellationToken);
    /// <summary>Shows a confirmation on this screen's owning handled page.</summary>
    public Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel) =>
        Context.Window.Dispatcher.DispatchAsync(async () =>
        {
            ValidateDialogOrigin();
            using var observation = Services.ModalPresentationScope.Begin(Screen.ViewModel, Context.Window, ModalPresentationKind.Alert);
            return await Context.HandledPage.DisplayAlertAsync(title, message, accept, cancel);
        });

    internal Task DisplayAlertAsync(string title, string message, string cancel) =>
        Context.Window.Dispatcher.DispatchAsync(async () =>
        {
            ValidateDialogOrigin();
            using var observation = Services.ModalPresentationScope.Begin(Screen.ViewModel, Context.Window, ModalPresentationKind.Alert);
            await Context.HandledPage.DisplayAlertAsync(title, message, cancel);
        });

    internal Task<CommunityToolkit.Maui.Core.IPopupResult<TResult>> ShowPopupAsync<TResult>(View popup,
        CommunityToolkit.Maui.IPopupOptions? options, CancellationToken token)
    {
        // Native dispatchers need not preserve the navigation callback's ExecutionContext.
        if (Context.Host.IsContentCallback)
            return Task.FromException<CommunityToolkit.Maui.Core.IPopupResult<TResult>>(
                new InvalidOperationException("Present popups outside navigation callbacks, or use RequestPopup for a registered popup."));
        return Context.Window.Dispatcher.DispatchAsync<CommunityToolkit.Maui.Core.IPopupResult<TResult>>(async () =>
        {
            ValidateDialogOrigin();
            if (Context.Host.IsContentCallback)
                throw new InvalidOperationException("Present popups outside navigation callbacks, or use RequestPopup for a registered popup.");
            return await Context.Host.PresentPopupAsync<TResult>(Screen.ViewModel,
                _ => Task.FromResult(popup),
                () => Context.IsActive && Context.RootContext.ContainsActiveOrigin(Screen.Entry) && Screen.View.IsAttachedTo(Context.Window),
                token, options);
        });
    }

    private void ValidateDialogOrigin()
    {
        if (!Context.IsActive || !Context.RootContext.ContainsActiveOrigin(Screen.Entry))
            throw new InvalidOperationException("A hidden or dismissed screen cannot present a dialog.");
        if (!Screen.View.IsAttachedTo(Context.Window))
            throw new InvalidOperationException("Modal presentation requires the origin view and its own window to have native handlers.");
    }
}
