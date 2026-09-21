namespace MVVMCompass;

/// <summary>Configures the temporary, non-navigable presentation used before a window's first root.</summary>
public sealed record WindowBootstrapOptions
{
    /// <summary>Creates fresh, detached loading content on the window's UI thread. Null uses the default activity indicator.</summary>
    public Func<View>? LoadingContentFactory { get; init; }
    /// <summary>Creates fresh, detached fallback content after unsuccessful initialization with no installed or pending root.</summary>
    /// <remarks>The result includes failure/cancellation status and any error. A throwing or invalid factory falls back to the library's error label.</remarks>
    public Func<NavigationResult, View>? FailureContentFactory { get; init; }
}
