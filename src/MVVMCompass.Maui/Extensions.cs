namespace MVVMCompass
{
    /// <summary>Task completion helpers for event-driven callers.</summary>
    public static class Extensions
    {
        /// <summary>Observes task completion and reports failures through the supplied callback or navigation diagnostics.</summary>
        public async static void FireAndForget(this Task task, Action? completedCallBack = null, Action<Exception>? exceptionCallBack = null)
        {
            try
            {
                await task;
                completedCallBack?.Invoke();
            }
            catch (Exception e)
            {
                if (exceptionCallBack != null) exceptionCallBack(e);
                else NavigationDiagnostics.Report(e, nameof(FireAndForget));
            }
        }
    }
}
