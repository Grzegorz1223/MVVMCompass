using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class AsyncResolutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("root")]
    [InlineData("push")]
    [InlineData("modal")]
    [InlineData("popup")]
    public async Task Caller_cancellation_reaches_a_custom_locator_before_it_produces_a_view(string action)
    {
        var locator = new AsyncLocator();
        var window = new Window(new ContentPage());
        await using var host = new MauiNavigationHostFactory(locator, new()).ForWindow(window);
        var initial = await host.ReplaceRootAsync<Model>(new(null), navigable: true, cancellationToken: Token);
        Assert.True(initial.IsSuccess, initial.Error?.ToString());
        locator.Hold = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<NavigationStatus> Start() => action switch
        {
            "root" => Status(host.ReplaceRootAsync<Model>(new(null), cancellationToken: cancellation.Token)),
            "push" => Status(host.PushAsync<Model>(new(null), animated: false, cancellationToken: cancellation.Token)),
            "modal" => Status(host.OpenModalAsync<Model>(new(null), animated: false, cancellationToken: cancellation.Token)),
            _ => Popup()
        };
        async Task<NavigationStatus> Popup()
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.DisplayPopupAsync<Model, string>(cancellationToken: cancellation.Token));
            return NavigationStatus.Cancelled;
        }
        var pending = Start();
        try
        {
            await locator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            cancellation.Cancel();
            Assert.True(locator.ReceivedToken.IsCancellationRequested);
            Assert.Equal(NavigationStatus.Cancelled, await pending.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.Same(initial.Value, host.CurrentRoot);
            Assert.False(initial.Value!.Entry.Lifetime.IsDismissed);
        }
        finally { locator.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Concurrent_async_locators_keep_their_own_constructor_parent_and_restore_the_caller_context()
    {
        var firstParent = new Model(); var secondParent = new Model();
        var locator = new AsyncLocator();
        var first = NavigationViewFactory.CreateWithCancellationAsync(locator, typeof(Model), firstParent, Token);
        var second = NavigationViewFactory.CreateWithCancellationAsync(locator, typeof(Model), secondParent, Token);
        Assert.Null(new Model().ParentViewModel);
        var pages = await Task.WhenAll(first, second);
        Assert.Same(firstParent, ((Model)pages[0].BindingContext).ConstructorParent);
        Assert.Same(secondParent, ((Model)pages[1].BindingContext).ConstructorParent);
        Assert.Null(new Model().ParentViewModel);
    }

    [Fact]
    public async Task Background_work_cannot_inherit_a_parent_after_its_construction_scope_has_closed()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Model> delayed;
        using (ViewModelBase.SetPendingParent(new Model()))
            delayed = Task.Run(async () => { await release.Task.WaitAsync(Token); return new Model(); }, Token);
        release.SetResult();
        Assert.Null((await delayed.WaitAsync(Token)).ConstructorParent);
    }

    [Fact]
    public async Task Failed_async_creation_does_not_leak_its_constructor_parent()
    {
        var locator = new AsyncLocator { Fail = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => NavigationViewFactory.CreateWithCancellationAsync(locator, typeof(Model), new Model(), Token));
        Assert.Null(new Model().ParentViewModel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Preserved_and_DI_factory_constructors_share_the_same_window_and_enable_scoped_factories(bool manualFirst)
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var locator = new AsyncLocator(); var options = new NavigationOptions(); var window = new Window(new ContentPage());
        var manual = new MauiNavigationHostFactory(locator, options);
        var scoped = new MauiNavigationHostFactory(locator, options, provider.GetRequiredService<IServiceScopeFactory>());
        await using var host = (manualFirst ? manual : scoped).ForWindow(window);
        Assert.Same(host, (manualFirst ? scoped : manual).ForWindow(window));
        var result = await host.ReplaceScopedRootAsync(new NavigationRequest<int>(42), _ => new Plain(),
            (_, _) => new ContentPage(), cancellationToken: Token);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(42, result.Value!.Entry.ViewModel.Parameter);
    }

    [Fact]
    public async Task Two_explicit_different_scope_providers_cannot_take_over_the_same_window()
    {
        await using var first = new ServiceCollection().BuildServiceProvider();
        await using var second = new ServiceCollection().BuildServiceProvider();
        var locator = new AsyncLocator(); var options = new NavigationOptions(); var window = new Window(new ContentPage());
        await using var host = new MauiNavigationHostFactory(locator, options, first.GetRequiredService<IServiceScopeFactory>()).ForWindow(window);
        Assert.Throws<InvalidOperationException>(() => new MauiNavigationHostFactory(locator, options, second.GetRequiredService<IServiceScopeFactory>()).ForWindow(window));
    }

    private static async Task<NavigationStatus> Status<T>(Task<NavigationOutcome<T>> task) => (await task).Status;
    private sealed class Model : ViewModelBase
    {
        public Model() => ConstructorParent = ParentViewModel;
        public ViewModelBase? ConstructorParent { get; }
    }
    private sealed class Plain : INavigationInitializable<int>
    {
        public int Parameter { get; private set; }
        public Task InitializeAsync(int parameter, CancellationToken cancellationToken) { Parameter = parameter; return Task.CompletedTask; }
    }
    private sealed class ModelPage : ContentPage, IHasVM
    {
        public ModelPage(Model model) { BindingContext = ViewModel = model; }
        public ViewModelBase ViewModel { get; }
    }
    private sealed class AsyncLocator : IAsyncViewLocator
    {
        public bool Hold; public bool Fail;
        public CancellationToken ReceivedToken;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<VisualElement> CreateAndBindVEForAsync(Type viewModelType, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            ReceivedToken = cancellationToken;
            if (Hold) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new InvalidOperationException("Requested locator failure");
            return new ModelPage(new Model());
        }
        public void Initialize(Dictionary<Type, Type> registerPairs) { }
        public VisualElement CreateAndBindVEFor<TViewModel>() where TViewModel : ViewModelBase => throw new InvalidOperationException("Async resolution was expected");
        public VisualElement CreateAndBindVEFor(Type type) => throw new InvalidOperationException("Async resolution was expected");
        public Type FindVEForViewModel(Type viewModelType) => typeof(ModelPage);
        public Type FindViewModelForVE(Type page) => typeof(Model);
    }
}
