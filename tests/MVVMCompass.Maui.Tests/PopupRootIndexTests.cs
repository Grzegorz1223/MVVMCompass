using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class PopupRootIndexTests : IAsyncDisposable
{
    private readonly Application? previous = Application.Current;
    private readonly TestApplication application = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lookup_preserves_nested_order_window_isolation_and_reopening()
    {
        var first = application.Add(); var second = application.Add();
        var outer = new TestPopup(new Model()); var inner = new TestPopup(new Model()); var other = new TestPopup(new Model());
        var outerResult = await Open(first, outer); var innerResult = await Open(first, inner); var otherResult = await Open(second, other);
        Assert.Equal(new[] { inner.ViewModel, outer.ViewModel }, PopupOwnership.ModelsFor(first.Page!));
        Assert.Equal(new[] { other.ViewModel }, PopupOwnership.ModelsFor(second.Page!));
        await inner.CloseAsync(1, Token); await innerResult;
        Assert.Equal(new[] { outer.ViewModel }, PopupOwnership.ModelsFor(first.Page!));
        await outer.CloseAsync(2, Token); await outerResult;
        Assert.Empty(PopupOwnership.ModelsFor(first.Page!));
        Assert.Equal(new[] { other.ViewModel }, PopupOwnership.ModelsFor(second.Page!));
        var reopened = new TestPopup(new Model()); var reopenedResult = await Open(first, reopened);
        Assert.Equal(new[] { reopened.ViewModel }, PopupOwnership.ModelsFor(first.Page!));
        await reopened.CloseAsync(3, Token); await reopenedResult;
        await other.CloseAsync(4, Token); await otherResult;
        Assert.Empty(PopupOwnership.ModelsFor(first.Page!));
        Assert.Empty(PopupOwnership.ModelsFor(second.Page!));
    }

    [Fact]
    public async Task Detached_root_still_finds_popup_until_asynchronous_cleanup_finishes()
    {
        var window = application.Add(); var root = window.Page!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Model { Cleanup = async () => { entered.SetResult(); await release.Task; } };
        var popup = new TestPopup(model); var result = await Open(window, popup);
        window.Page = new ContentPage();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(new[] { model }, PopupOwnership.ModelsFor(root));
            Assert.Empty(PopupOwnership.ModelsFor(window.Page));
            Assert.False(result.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(DismissalReason.RootReplaced, (await result.WaitAsync(TimeSpan.FromSeconds(10), Token)).Reason);
        Assert.Empty(PopupOwnership.ModelsFor(root));
    }

    private static async Task<Task<PopupNavigationResult<int>>> Open(Window window, TestPopup popup)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        popup.Opened += (_, _) => opened.TrySetResult();
        var showing = PopupOwnership.ShowAsync<int>(window, popup, cancellationToken: Token);
        await Task.WhenAny(opened.Task, showing).WaitAsync(TimeSpan.FromSeconds(10), Token);
        if (showing.IsFaulted) await showing;
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        return showing;
    }

    [Fact]
    public async Task Root_shutdown_cancels_every_nested_popup_before_waiting_for_the_first_cleanup()
    {
        var window = application.Add();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outerCleaned = false;
        var outerModel = new Model { Cleanup = () => { outerCleaned = true; return Task.CompletedTask; } };
        var innerModel = new Model { Cleanup = async () => { entered.SetResult(); await release.Task; } };
        var outer = new TestPopup(outerModel); var inner = new TestPopup(innerModel);
        Task? reentry = null;
        using var registration = innerModel.Lifetime.Token.Register(() =>
        {
            Assert.True(outerModel.IsDismissed);
            reentry = inner.CloseAsync(0, Token);
        });
        var outerResult = await Open(window, outer); var innerResult = await Open(window, inner);
        window.Page = new ContentPage();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.True(outerModel.Lifetime.Token.IsCancellationRequested);
            Assert.False(outerCleaned); Assert.False(outerResult.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reentry!);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(DismissalReason.RootReplaced, (await innerResult.WaitAsync(TimeSpan.FromSeconds(10), Token)).Reason);
        Assert.Equal(DismissalReason.RootReplaced, (await outerResult.WaitAsync(TimeSpan.FromSeconds(10), Token)).Reason);
        Assert.True(outerCleaned);
    }

    [Fact]
    public async Task Direct_lifetime_cancellation_cannot_reenter_its_popup_close()
    {
        var window = application.Add(); var model = new Model(); var popup = new TestPopup(model);
        Task? reentry = null;
        using var registration = model.Lifetime.Token.Register(() => reentry = popup.CloseAsync(0, Token));
        var showing = await Open(window, popup);
        await model.DismissAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reentry!);
        Assert.False(showing.IsCompleted);
        await popup.CloseAsync(1, Token); await showing;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var window in application.Windows.ToArray())
                while (window.Navigation.ModalStack.Count != 0) await window.Navigation.PopModalAsync(false);
        }
        finally { Application.Current = previous; }
    }

    private sealed class TestApplication : Application
    {
        internal Window Add() => (Window)((IApplication)this).CreateWindow(null);
        protected override Window CreateWindow(IActivationState? state) => new(new ContentPage());
    }
    private sealed class Model : ViewModelBase
    {
        internal Func<Task>? Cleanup;
        public override Task AfterDismissed() => Cleanup?.Invoke() ?? Task.CompletedTask;
    }
    private sealed class TestPopup(Model model) : PopupViewBase<Model, int>(model)
    {
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => true;
        protected override IDisposable ShowLoading(LoadingType type) => throw new NotSupportedException();
        protected override void HideLoading() { }
    }
}
