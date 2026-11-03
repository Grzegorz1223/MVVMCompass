using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using System.Runtime.CompilerServices;
using MVVMCompass.Core;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

public sealed class TerminalViewCleanupTests
{
    [Theory]
    [InlineData(DismissalReason.RootReplaced)]
    [InlineData(DismissalReason.Back)]
    public async Task A_later_sibling_can_await_an_earlier_siblings_view_action_until_all_callbacks_finish(DismissalReason reason)
    {
        var first = new Model();
        var last = new Model();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object? observed = null;
        first.SendCustomActionEvent += _ => Task.FromResult<object?>("closed");
        last.Cleanup = async () =>
        {
            entered.SetResult();
            await release.Task;
            observed = await first.Action();
        };

        var pending = ViewModelCleanup.DismissAsync([first, last], reason);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(first.IsDismissed);
            Assert.True(last.IsDismissed);
            Assert.False(pending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await pending;

        Assert.Equal("closed", observed);
        Assert.Null(await first.Action());
    }

    [Fact]
    public async Task An_explicit_owner_can_use_its_childs_view_during_terminal_cleanup()
    {
        var child = new Model();
        var parent = new Model();
        parent.Ownership.Adopt(child.Ownership);
        child.SendCustomActionEvent += _ => Task.FromResult<object?>("closed");
        object? observed = null;
        parent.Cleanup = async () => observed = await child.Action();

        await parent.DismissAsync();

        Assert.Equal("closed", observed);
        Assert.Null(await child.Action());
    }

    [Fact]
    public async Task A_failing_child_keeps_its_view_available_to_later_cleanup_and_then_disconnects()
    {
        var failing = new Model { Cleanup = () => throw new InvalidOperationException("teardown") };
        var later = new Model();
        failing.SendCustomActionEvent += _ => Task.FromResult<object?>("closed");
        object? observed = null;
        later.Cleanup = async () => observed = await failing.Action();

        await ViewModelCleanup.DismissAsync([failing, later], DismissalReason.RootReplaced);

        Assert.Equal("closed", observed);
        Assert.Null(await failing.Action());
    }

    [Theory]
    [InlineData(DismissalReason.RootReplaced, false)]
    [InlineData(DismissalReason.RootReplaced, true)]
    [InlineData(DismissalReason.Back, true)]
    [InlineData(DismissalReason.Removed, false)]
    public async Task Terminal_hook_unregisters_a_recipient_even_when_cleanup_omits_base_or_throws(DismissalReason reason, bool fail)
    {
        var messenger = new WeakReferenceMessenger();
        var stale = new MessengerModel(messenger) { Cleanup = () => fail ? throw new InvalidOperationException("teardown") : Task.CompletedTask };
        var staleCalls = 0;
        messenger.Register<SessionEndingMessage>(stale, (_, message) => { staleCalls++; message.Reply(true); });

        await ViewModelCleanup.DismissAsync([stale, stale], reason);
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(stale.DismissAsync);
        else await stale.DismissAsync();
        Assert.True(stale.IsPermanentlyDismissed);
        Assert.Equal(reason == DismissalReason.RootReplaced, stale.IsHostReplaced);
        Assert.Equal(1, stale.Detachments);
        Assert.False(messenger.IsRegistered<SessionEndingMessage>(stale));

        var current = new MessengerModel(messenger);
        messenger.Register<SessionEndingMessage>(current, (_, message) => message.Reply(true));
        Assert.True(await messenger.Send(new SessionEndingMessage()).Response);
        Assert.Equal(0, staleCalls);
        await current.DismissAsync();
    }

    [Fact]
    public void An_application_base_can_release_its_own_messenger_recipients_through_the_hook()
    {
        var messenger = new WeakReferenceMessenger();
        var model = new MessengerModel(messenger);
        var received = 0;
        messenger.Register<SessionEndingMessage>(model, (_, _) => received++);
        model.DetachViewEventHandlers();
        messenger.Send(new SessionEndingMessage());
        Assert.Equal(0, received);
    }

    [Fact]
    public async Task A_throwing_detach_override_cannot_skip_library_cleanup_or_later_models()
    {
        var first = new Model { Detach = () => throw new InvalidOperationException("detach") };
        var last = new Model();
        var unsubscribed = 0;
        first.SendCustomActionEvent += _ => Task.FromResult<object?>("attached");
        first.RegisterSubscriptionCleanup(() => unsubscribed++);

        await ViewModelCleanup.DismissAsync([first, last, first], DismissalReason.RootReplaced);

        Assert.Equal(1, first.Detachments);
        Assert.Equal(1, last.Detachments);
        Assert.Equal(1, unsubscribed);
        Assert.Null(await first.Action());
    }

    [Fact]
    public async Task Independent_dismissal_callers_wait_for_the_original_batchs_view_detachment()
    {
        var first = new Model();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var last = new Model { Cleanup = () => release.Task };
        var original = ViewModelCleanup.DismissAsync([first, last], DismissalReason.RootReplaced);
        var overlapping = ViewModelCleanup.DismissAsync([first], DismissalReason.RootReplaced);
        var direct = first.DismissAsync();
        try
        {
            Assert.False(original.IsCompleted);
            Assert.False(overlapping.IsCompleted);
            Assert.False(direct.IsCompleted);
            Assert.Same(direct, first.DismissAsync());
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(original, overlapping, direct).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, first.Detachments);
        Assert.Equal(1, last.Detachments);
    }

    [Fact]
    public async Task Overlapping_batches_with_opposite_order_finish_without_waiting_on_each_other()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Model { Cleanup = () => release.Task };
        var second = new Model { Cleanup = () => release.Task };
        var batch1 = ViewModelCleanup.DismissAsync([first, second], DismissalReason.RootReplaced);
        var batch2 = ViewModelCleanup.DismissAsync([second, first], DismissalReason.RootReplaced);
        release.SetResult();
        await Task.WhenAll(batch1, batch2).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, first.Detachments);
        Assert.Equal(1, second.Detachments);
    }

    [Fact]
    public void A_captured_cleanup_context_does_not_retain_unrelated_outgoing_siblings()
    {
        var (context, sibling) = CaptureCleanupContext();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(sibling.TryGetTarget(out _));
        GC.KeepAlive(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ExecutionContext Context, WeakReference<Model> Sibling) CaptureCleanupContext()
    {
        ExecutionContext? context = null;
        var first = new Model { Cleanup = () => { context = ExecutionContext.Capture(); return Task.CompletedTask; } };
        var later = new Model();
        ViewModelCleanup.DismissAsync([first, later], DismissalReason.RootReplaced).GetAwaiter().GetResult();
        return (context!, new(later));
    }

    private sealed class SessionEndingMessage : AsyncRequestMessage<bool>;
    private sealed class MessengerModel(IMessenger messenger) : Model
    {
        protected internal override void DetachViewEventHandlers()
        {
            try { base.DetachViewEventHandlers(); }
            finally { messenger.UnregisterAll(this); }
        }
    }

    private class Model : ViewModelBase
    {
        public Func<Task>? Cleanup { get; set; }
        public Action? Detach { get; set; }
        public int Detachments { get; private set; }
        public override Task AfterDismissed() => Cleanup?.Invoke() ?? Task.CompletedTask;
        public Task<object?> Action() => SendCustomAction(new CustomActionEventArgs("close-auxiliary-window"));
        protected internal override void DetachViewEventHandlers()
        {
            Detachments++;
            Detach?.Invoke();
            base.DetachViewEventHandlers();
        }
    }
}
