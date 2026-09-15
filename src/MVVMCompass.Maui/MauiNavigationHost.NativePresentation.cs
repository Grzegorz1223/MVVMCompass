namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    private async Task WaitForNativePresentationAsync(Page page, CancellationToken cancellationToken)
    {
        if (Window.Handler == null) return;
        if (!page.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(object? sender, EventArgs args) => loaded.TrySetResult();
            page.Loaded += OnLoaded;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closingToken);
            try
            {
                if (!page.IsLoaded) await loaded.Task.WaitAsync(cancellation.Token);
            }
            finally { page.Loaded -= OnLoaded; }
        }
        // Android nonanimated modal presentation can complete inside Fragment.OnResume.
        // Let that native callback finish before another operation can remove its fragment.
        await Task.Yield();
    }

    private async Task FinishNativeRemovalAsync()
    {
        if (Window.Handler != null)
            // MAUI queues DialogFragment.Dismiss before completing PopModalAsync.
            // Its removal must run before view cleanup or a following root replacement.
            await Task.Yield();
    }
}
