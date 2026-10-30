using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;

namespace MVVMCompass;

public abstract partial class ViewBase
{
    /// <summary>Shows a popup on this active, attached view's own window. Asynchronous failures are reported through NavigationDiagnostics.</summary>
    public void ShowPopup(Popup popup, IPopupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(popup);
        _ = ShowAndReportAsync();
        async Task ShowAndReportAsync()
        {
            try { await ShowPopupAsync(popup, options); }
            catch (Exception error) { NavigationDiagnostics.Report(error, "View popup presentation"); }
        }
    }

    /// <summary>Shows a popup on this active, attached view's own window. Cancellation stops waiting, leaving observation active until actual closure.</summary>
    public async Task<IPopupResult> ShowPopupAsync(Popup popup, IPopupOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(popup);
        if (Navigator == null) throw new InvalidOperationException("A popup requires an attached navigation entry.");
        return await Navigator.ShowPopupAsync<object>(popup, options, token);
    }

    /// <summary>Shows a typed popup on this active, attached view's own window. Cancellation does not close it.</summary>
    public Task<IPopupResult<TResult>> ShowPopupAsync<TResult>(Popup<TResult> popup, IPopupOptions? options = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(popup);
        if (Navigator == null) throw new InvalidOperationException("A popup requires an attached navigation entry.");
        return Navigator.ShowPopupAsync<TResult>(popup, options, token);
    }
}
