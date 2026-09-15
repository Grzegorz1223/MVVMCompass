using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class TerminalNativeCallbackTests
{
    [Theory]
    [InlineData("View_Loaded", true)]
    [InlineData("View_Unloaded", true)]
    [InlineData("Page_Appearing", false)]
    [InlineData("Page_NavigatedTo", false)]
    [InlineData("Page_Loaded", false)]
    [InlineData("Page_Unloaded", false)]
    public async Task Late_native_callbacks_cannot_restart_a_terminal_model_while_teardown_is_pending(string callback, bool content)
    {
        var model = new Model();
        VisualElement view = content ? new ModelView(model) : new ModelPage(model);
        var service = new LegacyNavigationService(new Locator());
        // Exercise each real native adapter entry point with a sender the locator recognizes.
        // Reflection is confined to this managed test; production dispatch stays typed/AOT safe.
        var method = typeof(LegacyNavigationService).GetMethod(callback, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var args = callback == "Page_NavigatedTo"
            ? RuntimeHelpers.GetUninitializedObject(typeof(NavigatedToEventArgs)) : EventArgs.Empty;
        void Raise() => method.Invoke(service, [view, args]);
        Raise();
        Assert.Equal(1, model.Callbacks);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Cleanup = () => release.Task;
        var dismissing = model.DismissAsync();
        try
        {
            Assert.True(model.IsPermanentlyDismissed);
            Assert.False(dismissing.IsCompleted);
            Raise();
            Assert.Equal(1, model.Callbacks);
        }
        finally { release.TrySetResult(); }
        await dismissing;
        Raise();
        Assert.Equal(1, model.Callbacks);
        Assert.Equal(1, model.Detachments);
    }

    [Fact]
    public async Task A_missing_back_model_is_a_no_op_without_a_presented_root()
    {
        var service = new LegacyNavigationService(new Locator());
        await service.NavigateBack(null!);
    }

    [Fact]
    public async Task Legacy_pop_to_root_keeps_earlier_views_connected_for_later_callbacks()
    {
        var previous = Application.Current;
        var application = new TestApplication();
        var root = new Model();
        var stack = TestNavigationHandler.Create(new ModelPage(root));
        var window = application.Add(stack);
        NativeNavigationObserver.Attach(stack);
        var first = new Model(); var later = new Model();
        first.SendCustomActionEvent += _ => Task.FromResult<object?>("closed");
        object? observed = null;
        later.Cleanup = async () => { await Task.Yield(); observed = await first.Action(); };
        var service = new LegacyNavigationService(new Locator(), new(), window);
        try
        {
            await stack.PushAsync(new ModelPage(later), false);
            await stack.PushAsync(new ModelPage(first), false);
            await service.NavigateBackToRoot().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("closed", observed);
            Assert.Null(await first.Action());
            Assert.Equal(1, first.Detachments);
            Assert.Equal(1, later.Detachments);
            Assert.False(root.IsPermanentlyDismissed);
            Assert.False(first.IsHostReplaced);
            Assert.False(later.IsHostReplaced);
            Assert.Single(stack.Navigation.NavigationStack);
        }
        finally { NativeNavigationObserver.Detach(stack); Application.Current = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_back_awaits_teardown_and_detachment_without_marking_host_replacement(bool modal)
    {
        var previous = Application.Current;
        var application = new TestApplication();
        var root = new Model();
        var stack = TestNavigationHandler.Create(new ModelPage(root));
        var window = application.Add(stack);
        NativeNavigationObserver.Attach(stack);
        var model = new Model { IsModal = modal };
        var page = new ModelPage(model);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Cleanup = () => release.Task;
        var service = new LegacyNavigationService(new Locator(), new(), window);
        try
        {
            if (modal) await window.Navigation.PushModalAsync(page, false);
            else await stack.PushAsync(page, false);
            var pending = service.NavigateBack(model);
            Assert.False(pending.IsCompleted);
            Assert.True(model.IsPermanentlyDismissed);
            Assert.False(model.IsHostReplaced);
            release.SetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, model.Detachments);
            Assert.False(root.IsPermanentlyDismissed);
        }
        finally
        {
            release.TrySetResult();
            NativeNavigationObserver.Detach(stack);
            Application.Current = previous;
        }
    }

    private sealed class Model : ViewModelBase
    {
        public Func<Task>? Cleanup { get; set; }
        public int Callbacks { get; private set; }
        public int Detachments { get; private set; }
        private Task Observe() { Callbacks++; return Task.CompletedTask; }
        public override Task Appearing() => Observe();
        public override Task NavigatedTo() => Observe();
        public override Task Loaded() => Observe();
        public override Task Unloaded() => Observe();
        public override Task AfterDismissed() => Cleanup?.Invoke() ?? Task.CompletedTask;
        public Task<object?> Action() => SendCustomAction(new("close-window"));
        protected internal override void DetachViewEventHandlers() { Detachments++; base.DetachViewEventHandlers(); }
    }
    private sealed class ModelPage(Model model) : ContentPage, IHasVM
    {
        public ViewModelBase ViewModel => model;
    }
    private sealed class ModelView(Model model) : ContentView, IHasVM
    {
        public ViewModelBase ViewModel => model;
    }
    private sealed class Locator : IViewLocator
    {
        public void Initialize(Dictionary<Type, Type> pairs) { }
        public Type FindViewModelForVE(Type view) => typeof(Model);
        public Type FindVEForViewModel(Type model) => typeof(ModelPage);
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => new ModelPage(new());
        public VisualElement CreateAndBindVEFor(Type type) => new ModelPage(new());
    }
    private sealed class TestApplication : Application
    {
        private Page next = null!;
        public Window Add(Page page) { next = page; return (Window)((IApplication)this).CreateWindow(null); }
        protected override Window CreateWindow(IActivationState? activationState) => new(next);
    }
}
