using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;

namespace MVVMCompass;

public sealed partial class MauiNavigationHostFactory
{
    /// <summary>Presents an alert on this factory's window, including when its root container is covered.</summary>
    /// <remarks>Alerts may be awaited from guards on an attached, live root. Cancellation stops waiting, not native presentation.
    /// Observers remain active until dismissal, root replacement or window destruction. Overlap throws ModalPresentationBusyException.</remarks>
    public Task<bool> DisplayAlertAsync(Window window, string title, string message, string accept, string cancel,
        CancellationToken cancellationToken = default)
    {
        var (host, root) = DialogOwner(window);
        return host.PresentWindowAlertAsync(root, page => page.DisplayAlertAsync(title, message, accept, cancel), cancellationToken);
    }

    /// <summary>Presents an informational alert on this factory's window. Cancellation stops waiting, not native presentation.</summary>
    public Task DisplayAlertAsync(Window window, string title, string message, string cancel,
        CancellationToken cancellationToken = default)
    {
        var (host, root) = DialogOwner(window);
        return host.PresentWindowAlertAsync(root, async page => { await page.DisplayAlertAsync(title, message, cancel); return true; }, cancellationToken);
    }

    /// <summary>Presents a popup on this factory's window independently of the selected tab or covered screen.</summary>
    /// <remarks>Call outside navigation callbacks. Cancellation stops waiting, not presentation; ownership lasts until actual closure.
    /// An existing popup or window-level alert causes ModalPresentationBusyException. Pass the caller's lifetime token for session work.</remarks>
    public async Task<IPopupResult> ShowPopupAsync(Window window, Popup popup, IPopupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(popup);
        var (host, root) = DialogOwner(window);
        return await host.PresentWindowPopupAsync<object>(root, popup, options, cancellationToken);
    }

    /// <summary>Presents a typed popup on this factory's window. The same ownership, overlap and callback rules apply as for untyped popups.</summary>
    public async Task<IPopupResult<TResult>> ShowPopupAsync<TResult>(Window window, Popup<TResult> popup, IPopupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(popup);
        var (host, root) = DialogOwner(window);
        return await host.PresentWindowPopupAsync<TResult>(root, popup, options, cancellationToken);
    }

    private (MauiNavigationHost Host, MauiNavigationRoot<NavigationContext> Root) DialogOwner(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!initialization.TryGetValue(window, out var state))
            throw new ArgumentException("The window was not created by this factory.", nameof(window));
        ObjectDisposedException.ThrowIf(state.Host.IsClosed, window);
        return (state.Host, state.Host.CurrentRoot as MauiNavigationRoot<NavigationContext>
            ?? throw new InvalidOperationException("Wait for successful window initialization before presenting a dialog."));
    }
}
