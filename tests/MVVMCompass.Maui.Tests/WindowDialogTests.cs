using CommunityToolkit.Maui.Views;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    private static void AttachWindowDialogs(NavigationContext context)
    {
        AttachDesktop(context.Window, context.ActiveChain().Select(item => item.Current!.View).ToArray());
        context.HandledPage.Handler ??= new TestNavigationHandler();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Window_popup_notifies_covered_shell_and_active_branch_in_its_own_window(bool covered) => OnRootUI(async () =>
    {
        CompatibilityProfile();
        var other = await Open<ProfileTabs>(); var root = await Open<ProfileTabs>();
        var shell = (ProfileTabsView)root.Current!.View;
        var inactive = (ProfileLeafView)shell.CurrentTab!.View!;
        if (covered) Success(await shell.ViewModel.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        else Success(await shell.ViewModel.Navigation.Select("two", cancellationToken: Token));
        AttachWindowDialogs(root);
        var popup = new Popup<string> { Content = new Label { Text = "Background result" } };
        var opened = RootSignal(); popup.Opened += (_, _) => opened.TrySetResult();
        var showing = factory.ShowPopupAsync(root.Window, popup, cancellationToken: Token);
        await Settle(opened.Task);
        Assert.Single(shell.ModalStarts);
        Assert.Same(root.Deepest.Current!.View, shell.ModalStarts[0].Origin);
        Assert.Empty(inactive.ModalStarts);
        Assert.Empty(((ProfileTabsView)other.Current!.View).ModalStarts);
        if (!covered) Assert.Single(((ProfileLeafView)shell.CurrentTab!.View!).ModalStarts);
        await Settle(popup.CloseAsync("sent", Token));
        Assert.Equal("sent", (await Settle(showing)).Result);
        Assert.Equal(shell.ModalStarts, shell.ModalEnds);
    });

    [Theory]
    [InlineData("close")]
    [InlineData("root")]
    [InlineData("window")]
    public Task Window_popup_cancellation_keeps_observers_until_actual_end(string end) => OnRootUI(async () =>
    {
        var root = await Open<ProfileTabs>(); var shell = (ProfileTabsView)root.Current!.View;
        AttachWindowDialogs(root);
        var popup = new Popup { Content = new Label() };
        var opened = RootSignal(); popup.Opened += (_, _) => opened.TrySetResult();
        using var cancellation = new CancellationTokenSource();
        var showing = factory.ShowPopupAsync(root.Window, popup, cancellationToken: cancellation.Token);
        await Settle(opened.Task);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Settle(showing));
        Assert.Empty(shell.ModalEnds);
        await Assert.ThrowsAsync<ModalPresentationBusyException>(() => factory.ShowPopupAsync(root.Window, new Popup(), cancellationToken: Token));
        await Assert.ThrowsAsync<ModalPresentationBusyException>(() => factory.DisplayAlertAsync(root.Window, "Title", "Message", "OK", Token));
        if (end == "close") await Settle(popup.CloseAsync(Token));
        else if (end == "root") Success(await Settle(factory.SetRoot<Leaf>(root.Window, options: Enforced, cancellationToken: Token)));
        else { ((IWindow)root.Window).Destroying(); await Settle(root.Host.Completion); }
        Assert.Single(shell.ModalStarts); Assert.Equal(shell.ModalStarts, shell.ModalEnds);
    });

    [Fact]
    public Task Window_popup_failure_balances_observers_and_foreign_closed_detached_windows_are_rejected() => OnRootUI(async () =>
    {
        var root = await Open<ProfileTabs>(); var shell = (ProfileTabsView)root.Current!.View;
        await Assert.ThrowsAsync<ArgumentException>(() => factory.ShowPopupAsync(new Window(new ContentPage()), new Popup(), cancellationToken: Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.ShowPopupAsync(root.Window, new Popup(), cancellationToken: Token));
        AttachWindowDialogs(root); shell.RejectModal = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.ShowPopupAsync(root.Window, new Popup(), cancellationToken: Token));
        Assert.Single(shell.ModalStarts); Assert.Equal(shell.ModalStarts, shell.ModalEnds);
        Assert.Empty(root.Window.Navigation.ModalStack);
        ((IWindow)root.Window).Destroying(); await Settle(root.Host.Completion);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.ShowPopupAsync(root.Window, new Popup(), cancellationToken: Token));
    });

    [Fact]
    public Task Window_popup_rejects_navigation_callback_and_stale_root_waiting_behind_replacement() => OnRootUI(async () =>
    {
        var root = await Open<Leaf>(); AttachWindowDialogs(root);
        var leaf = (Leaf)root.Current!.ViewModel;
        var entered = RootSignal(); var release = RootSignal();
        leaf.Guard = async () =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => factory.ShowPopupAsync(root.Window, new Popup(), cancellationToken: Token));
            entered.TrySetResult(); await release.Task; return true;
        };
        var changing = factory.SetRoot<ProfileTabs>(root.Window, cancellationToken: Token);
        await Settle(entered.Task);
        var popup = new Popup(); var wasOpened = false; popup.Opened += (_, _) => wasOpened = true;
        var stale = factory.ShowPopupAsync(root.Window, popup, cancellationToken: Token);
        release.TrySetResult();
        Success(await Settle(changing));
        var error = await Record.ExceptionAsync(() => Settle(stale));
        Assert.True(error is OperationCanceledException or InvalidOperationException, error?.ToString());
        Assert.False(wasOpened); Assert.Empty(root.Window.Navigation.ModalStack);
    });

    [Theory]
    [InlineData("close")]
    [InlineData("root")]
    [InlineData("window")]
    [InlineData("failure")]
    public Task Window_alert_wait_cancellation_and_terminal_events_balance_observation(string end) => OnRootUI(async () =>
    {
        var root = await Open<ProfileTabs>(); var shell = (ProfileTabsView)root.Current!.View;
        Success(await shell.ViewModel.Navigation.NavigateTo<Detail>(cancellationToken: Token));
        AttachWindowDialogs(root);
        var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = RootSignal();
        using var cancellation = new CancellationTokenSource();
        var showing = root.Host.PresentWindowAlertAsync((MauiNavigationRoot<NavigationContext>)root.Host.CurrentRoot!, page =>
        { Assert.Same(root.HandledPage, page); entered.TrySetResult(); return native.Task; }, cancellation.Token);
        await Settle(entered.Task); Assert.Single(shell.ModalStarts);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Settle(showing));
        Assert.Empty(shell.ModalEnds);
        await Assert.ThrowsAsync<ModalPresentationBusyException>(() => factory.ShowPopupAsync(root.Window, new Popup(), cancellationToken: Token));
        if (end == "root") Success(await Settle(factory.SetRoot<Leaf>(root.Window, options: Enforced, cancellationToken: Token)));
        if (end == "window") { ((IWindow)root.Window).Destroying(); await Settle(root.Host.Completion); }
        if (end is "root" or "window") Assert.Equal(shell.ModalStarts, shell.ModalEnds);
        var dismissed = RootSignal();
        shell.ModalDismissed = () => dismissed.TrySetResult();
        if (shell.ModalEnds.Count != 0) dismissed.TrySetResult();
        if (end == "failure") native.SetException(new InvalidOperationException("native failure")); else native.SetResult(true);
        await Settle(dismissed.Task);
        Assert.Equal(shell.ModalStarts, shell.ModalEnds);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Window_alert_can_be_awaited_by_a_guard_without_reentering_the_window_gate(bool background) => OnRootUI(async () =>
    {
        var root = await Open<ProfileTabs>(); var shell = (ProfileTabsView)root.Current!.View;
        AttachWindowDialogs(root);
        Task<bool> Alert() => root.Host.PresentWindowAlertAsync((MauiNavigationRoot<NavigationContext>)root.Host.CurrentRoot!,
            _ => Task.FromResult(false), Token);
        shell.ViewModel.Guard = () => background ? Task.Run(Alert, Token) : Alert();
        var result = await Settle(factory.SetRoot<Leaf>(root.Window, cancellationToken: Token));
        Assert.Equal(NavigationStatus.GuardRejected, result.Status);
        Assert.Single(shell.ModalStarts); Assert.Equal(shell.ModalStarts, shell.ModalEnds);
    });
}
