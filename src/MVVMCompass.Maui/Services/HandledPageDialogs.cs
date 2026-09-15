using Microsoft.Maui.Dispatching;

namespace MVVMCompass.Services;

/// <summary>Balances delegated modal notifications, including failed opens and nested calls.</summary>
internal static class HandledPageDialogs
{
    internal static Task<TResult> RunAsync<TResult>(Page page, Func<Task<TResult>> show) => page.Dispatcher.DispatchAsync(async () =>
    {
        Exception? failure = null;
        try
        {
            (page as IHandledPageModalHost)?.OnHandledPageModalOpening();
            return await show();
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { (page as IHandledPageModalHost)?.OnHandledPageModalClosed(); }
            catch (Exception error) when (failure != null) { NavigationDiagnostics.Report(error, "Handled modal completion"); }
        }
    });

    internal static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception error) { NavigationDiagnostics.Report(error, "Delegated popup"); }
    }
}
