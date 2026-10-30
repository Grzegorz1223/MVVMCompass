namespace MVVMCompass;

/// <summary>The result of opening a window, including its independent lifetime.</summary>
public sealed class WindowNavigationResult
{
    internal WindowNavigationResult(NavigationResult navigation, Window? window, Task closed)
    { Navigation = navigation; Window = window; Closed = closed; }
    /// <summary>Gets initialization or opening status. A failed initialization can leave a window showing failure content.</summary>
    public NavigationResult Navigation { get; }
    /// <summary>Gets the created window, even if initialization failed; null if no window was created.</summary>
    public Window? Window { get; }
    /// <summary>Completes after window destruction and library-owned cleanup. Cleanup errors fault this task. Completed if no window was created.</summary>
    public Task Closed { get; }
}
