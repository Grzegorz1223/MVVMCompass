namespace MVVMCompass;

/// <summary>Configures the temporary, non-navigable presentation used before a window's first root.</summary>
public sealed record WindowBootstrapOptions
{
    /// <summary>Creates fresh, detached loading content on the window's UI thread. Null uses the default activity indicator.</summary>
    public Func<View>? LoadingContentFactory { get; init; }
    /// <summary>Creates fresh, detached fallback content after unsuccessful initialization with no installed or pending root.</summary>
    /// <remarks>The result includes failure/cancellation status and any error. A throwing or invalid factory falls back to the library's error label.</remarks>
    public Func<NavigationResult, View>? FailureContentFactory { get; init; }
    /// <summary>Creates failure content after a committed root installation or activation fails, for this window's entire lifetime.</summary>
    /// <remarks>Opt-in. Preparation failures preserve the old root. Cleanup-only failures preserve the successfully activated replacement.
    /// The failed candidate is dismissed before this factory runs. Return a fresh detached ordinary View; a failing factory uses an error label.</remarks>
    public Func<NavigationResult, View>? RootFailureContentFactory { get; init; }
}
