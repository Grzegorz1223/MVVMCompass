namespace MVVMCompass.Interfaces;

/// <summary>ViewModel-first navigation bound to the injecting destination and its window.</summary>
public interface INavigationService
{
    /// <summary>Pushes a fresh destination; IsModal requests an independent modal scope.</summary>
    Task<NavigationResult> NavigateTo<TViewModel>(Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase;
    /// <summary>Selects an existing tab or flyout item by ID, or a slash-separated path through nested containers.</summary>
    Task<NavigationResult> Select(string id, Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default);
    /// <summary>Selects an existing destination only when its model type is unambiguous in the nearest container.</summary>
    Task<NavigationResult> Select<TViewModel>(Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase;
    /// <summary>Returns within the active branch, or closes its modal root.</summary>
    Task<NavigationResult> NavigateBack(CancellationToken cancellationToken = default);
    /// <summary>Returns to the active branch's root after guarding the removed details.</summary>
    Task<NavigationResult> NavigateBackToRoot(CancellationToken cancellationToken = default);
    /// <summary>Replaces this window's complete root after its guards permit removal.</summary>
    Task<NavigationResult> SetRoot<TViewModel>(Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase;
    /// <summary>Resets a retained destination and its history after its guards permit removal.</summary>
    Task<NavigationResult> Reset(string id, CancellationToken cancellationToken = default);
    /// <summary>Removes a retained destination after its guards permit removal.</summary>
    Task<NavigationResult> Remove(string id, string? replacementId = null, CancellationToken cancellationToken = default);
    /// <summary>Presents a Toolkit popup and awaits its result and cleanup.</summary>
    Task<PopupNavigationResult<TResult>> DisplayPopup<TViewModel, TResult>(Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase;
    /// <summary>Closes the popup belonging to this model with a result.</summary>
    Task<NavigationResult> ClosePopup<TResult>(TResult result, CancellationToken cancellationToken = default);
    /// <summary>Dismisses the popup belonging to this model without a result.</summary>
    Task<NavigationResult> ClosePopup(CancellationToken cancellationToken = default);
    /// <summary>Creates an independent window using the same typed registrations.</summary>
    Task<NavigationResult> OpenNewWindow<TViewModel>(Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default) where TViewModel : ViewModelBase;
}
