using MVVMCompass.Core;

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
    public Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel)
    {
        if (!Context.IsActive || !Context.RootContext.ContainsActiveOrigin(Screen.Entry))
            throw new InvalidOperationException("A hidden or dismissed screen cannot present a dialog.");
        return Context.HandledPage.DisplayAlertAsync(title, message, accept, cancel);
    }
}
