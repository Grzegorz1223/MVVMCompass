namespace MVVMCompass;

/// <summary>The terminal outcome of a deferred popup request.</summary>
public enum PopupRequestStatus
{
    /// <summary>The popup returned a result or an owned dismissal reason.</summary>
    Completed,
    /// <summary>The caller cancelled the request or its result wait.</summary>
    Cancelled,
    /// <summary>The originating activation no longer owns the presentation.</summary>
    InvalidOrigin,
    /// <summary>The originating destination or requested popup failed preparation.</summary>
    PreparationFailed,
    /// <summary>The origin's root was replaced before presentation.</summary>
    RootReplaced,
    /// <summary>The owning window closed before presentation.</summary>
    WindowClosed,
    /// <summary>Presentation, dispatch or cleanup failed; inspect Error.</summary>
    Failed
}

/// <summary>Acceptance and eventual completion of a popup requested from a navigation origin.</summary>
public sealed class PopupRequest<TResult>
{
    private readonly TaskCompletionSource<PopupRequestResult<TResult>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal PopupRequest(bool accepted) => IsAccepted = accepted;

    /// <summary>Gets whether an owned request was accepted; presentation still depends on origin validity.</summary>
    public bool IsAccepted { get; }

    /// <summary>Gets the single terminal outcome. Await only after returning from navigation callbacks.</summary>
    /// <remarks>Awaiting this task from a callback that navigation itself awaits prevents settlement.
    /// Store the handle or observe completion independently; the callback must return promptly.</remarks>
    public Task<PopupRequestResult<TResult>> Completion => completion.Task;

    internal void Complete(PopupRequestResult<TResult> result)
    { completion.TrySetResult(result); }
}

/// <summary>A popup result, or the explicit reason a queued request could not return one.</summary>
public sealed class PopupRequestResult<TResult>
{
    internal PopupRequestResult(PopupRequestStatus status, bool wasPresented = false,
        PopupNavigationResult<TResult>? popupResult = null, Exception? error = null)
    { Status = status; WasPresented = wasPresented; PopupResult = popupResult; Error = error; }

    /// <summary>Gets the terminal request status.</summary>
    public PopupRequestStatus Status { get; }
    /// <summary>Gets whether native presentation occurred, including a cancelled wait for a visible popup.</summary>
    public bool WasPresented { get; }
    /// <summary>Gets the popup's typed result and dismissal reason when Status is Completed.</summary>
    public PopupNavigationResult<TResult>? PopupResult { get; }
    /// <summary>Gets a preparation, presentation or cleanup failure, when available.</summary>
    public Exception? Error { get; }
}
