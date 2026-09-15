using System.Diagnostics;

namespace MVVMCompass;

/// <summary>Reports failures in platform events and best-effort teardown.</summary>
public static class NavigationDiagnostics
{
    /// <summary>Raised when an event cannot return a failure to an awaiting caller.</summary>
    public static event Action<Exception, string>? Error;

    internal static void Report(Exception exception, string operation)
    {
        Debug.WriteLine($"MVVMCompass {operation}: {exception}");
        if (Error is not { } observers) return;
        foreach (Action<Exception, string> observer in observers.GetInvocationList())
        {
            try { observer(exception, operation); }
            catch (Exception observerError) { Debug.WriteLine(observerError); }
        }
    }
}
