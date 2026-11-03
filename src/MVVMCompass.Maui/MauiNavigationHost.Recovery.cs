using MVVMCompass.Core;

namespace MVVMCompass;

internal sealed partial class MauiNavigationHost
{
    // Runs inside the original committed operation and root gate. Never restore a terminal outgoing root.
    private async Task RecoverCommittedRootAsync(Page expected, MauiNavigationRoot? candidate, NavigationEntry? entry,
        IEnumerable<ViewModelBase> models, Exception failure, List<Exception> errors, Action<Exception> recordError)
    {
        try { await DismissTreeAsync(candidate?.Page, entry, models, DismissalReason.PreparationFailed, recordError, dismissModals: true); }
        catch (Exception error) { recordError(error); NavigationDiagnostics.Report(error, "Failed root cleanup"); }
        if (IsClosed || !ReferenceEquals(Window.Page, expected)) return;
        CurrentRoot = null;
        View content;
        try
        {
            content = RootFailureContentFactory!(new(NavigationStatus.Failed, true, failure, errors.ToArray()));
            if (content == null || content.Parent != null || content is ViewBase)
                throw new InvalidOperationException("Root failure content must be a fresh, detached ordinary View.");
        }
        catch (Exception error)
        {
            recordError(error);
            NavigationDiagnostics.Report(error, "Root failure content");
            content = new Label { Text = "The application could not open this view." };
        }
        // A content factory may change or close its window synchronously.
        if (IsClosed || !ReferenceEquals(Window.Page, expected)) return;
        var fallback = new ContentPage { Content = content };
        try
        {
            installing = true;
            Window.Page = fallback;
        }
        catch (Exception error) { recordError(error); NavigationDiagnostics.Report(error, "Root failure presentation"); }
        finally { installing = false; }
    }
}
