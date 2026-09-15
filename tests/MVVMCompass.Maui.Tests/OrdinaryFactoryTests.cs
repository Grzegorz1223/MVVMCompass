using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class OrdinaryFactoryTests : IAsyncDisposable
{
    private readonly Application? previous = Application.Current;
    private readonly TestApplication application = new();
    private readonly List<MauiNavigationHost> hosts = [];
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public async ValueTask DisposeAsync()
    {
        try { foreach (var host in hosts) await host.DisposeAsync(); }
        finally { Application.Current = previous; }
    }
    private MauiNavigationHost Host(IServiceScopeFactory? scopes = null, IViewLocator? locator = null)
    {
        var factory = scopes == null ? new MauiNavigationHostFactory(locator ?? new Locator(), new())
            : new MauiNavigationHostFactory(locator ?? new Locator(), new(), scopes);
        var host = factory.ForWindow(application.Add());
        hosts.Add(host); return host;
    }
    private static T Success<T>(NavigationOutcome<T> result) where T : class
    { Assert.True(result.IsSuccess, result.Error?.ToString() ?? result.Status.ToString()); return result.Value!; }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Plain_and_lifecycle_only_models_support_root_push_and_modal_with_optional_scopes(bool scoped, bool aware)
    {
        await using var provider = new ServiceCollection().AddScoped<Resource>().BuildServiceProvider();
        var host = Host(provider.GetRequiredService<IServiceScopeFactory>());
        List<Model> models = [];
        Model Create(IServiceProvider? services = null)
        {
            Model model = aware ? new AwareModel() : new Model();
            model.Resource = services?.GetRequiredService<Resource>();
            models.Add(model); return model;
        }
        Page Page(IServiceProvider services, Model model)
        { Assert.Same(model.Resource, services.GetRequiredService<Resource>()); return new ContentPage(); }

        var root = scoped
            ? Success(await host.ReplaceScopedRootAsync(new NavigationRequestOptions(), services => Create(services), Page, navigable: true, cancellationToken: Token))
            : Success(await host.ReplaceRootAsync(new NavigationRequestOptions(), () => Create(), _ => new ContentPage(), navigable: true, cleanup: model => model.Release(), cancellationToken: Token));
        ((NavigationPage)root.Page).Handler = new TestNavigationHandler();
        Assert.Same(root.Entry.ViewModel, root.Content.BindingContext);
        var push = scoped
            ? Success(await host.PushScopedAsync(new NavigationRequestOptions { Origin = root.Entry }, services => Create(services), Page, animated: false, cancellationToken: Token))
            : Success(await host.PushAsync(new NavigationRequestOptions { Origin = root.Entry }, () => Create(), _ => new ContentPage(), animated: false, cleanup: model => model.Release(), cancellationToken: Token));
        var modal = scoped
            ? Success(await host.OpenScopedModalAsync(new NavigationRequestOptions { Origin = push.Entry }, services => Create(services), Page, animated: false, cancellationToken: Token))
            : Success(await host.OpenModalAsync(new NavigationRequestOptions { Origin = push.Entry }, () => Create(), _ => new ContentPage(), animated: false, cleanup: model => model.Release(), cancellationToken: Token));
        Assert.All(models, model => Assert.Equal(0, model.Resource?.Disposals ?? model.Releases));
        Assert.True((await host.CloseModalAsync(new(null) { Origin = modal.Entry }, animated: false, cancellationToken: Token)).IsSuccess);
        Assert.Equal(NavigationEntryState.Dismissed, modal.Entry.State);
        Assert.True((await host.BackAsync(new(null) { Origin = push.Entry }, animated: false, cancellationToken: Token)).IsSuccess);
        Assert.Equal(NavigationEntryState.Active, root.Entry.State);
        await host.DisposeAsync();
        Assert.All(models, model => Assert.Equal(1, model.Resource?.Disposals ?? model.Releases));
        if (aware)
        {
            Assert.All(models.Cast<AwareModel>(), model => Assert.Equal(1, model.Dismissals));
            Assert.True(((AwareModel)root.Entry.ViewModel).Activations > 1);
        }
        if (scoped) Assert.Equal(3, models.Select(model => model.Resource).Distinct().Count());
    }

    [Fact]
    public async Task Parameterless_requests_preserve_origin_busy_and_cancellation_policy()
    {
        var host = Host(); var other = Host();
        var root = Success(await host.ReplaceRootAsync(new NavigationRequestOptions(), () => new Model(), _ => new ContentPage(), cancellationToken: Token));
        var calls = 0;
        var foreign = await other.ReplaceRootAsync(new NavigationRequestOptions { Origin = root.Entry },
            () => { calls++; return new Model(); }, _ => new ContentPage(), cancellationToken: Token);
        Assert.Equal(NavigationStatus.InvalidOrigin, foreign.Status); Assert.Equal(0, calls);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token); cancelled.Cancel();
        var stopped = await host.ReplaceRootAsync(new NavigationRequestOptions { Priority = NavigationPriority.Required },
            () => { calls++; return new Model(); }, _ => new ContentPage(), cancellationToken: cancelled.Token);
        Assert.Equal(NavigationStatus.Cancelled, stopped.Status); Assert.Equal(0, calls);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = host.ReplaceRootAsync(new NavigationRequestOptions(), () => new AwareModel
        { Activate = async () => { entered.SetResult(); await release.Task; } }, _ => new ContentPage(), cancellationToken: Token);
        await entered.Task.WaitAsync(Token);
        try
        {
            var busy = await host.ReplaceRootAsync(new NavigationRequestOptions { RejectIfBusy = true },
                () => { calls++; return new Model(); }, _ => new ContentPage(), cancellationToken: Token);
            Assert.Equal(NavigationStatus.Busy, busy.Status); Assert.Equal(0, calls);
        }
        finally { release.SetResult(); }
        Success(await holding);
    }

    [Fact]
    public async Task No_initialization_is_requested_even_when_the_model_offers_a_typed_initializer()
    {
        var host = Host(); var model = new InitializedModel();
        Success(await host.ReplaceRootAsync(new NavigationRequestOptions(), () => model, _ => new ContentPage(), cancellationToken: Token));
        Assert.Equal(0, model.Initializations);
        var typed = new InitializedModel();
        Success(await host.ReplaceRootAsync(new NavigationRequest<int>(42), () => typed, _ => new ContentPage(), cancellationToken: Token));
        Assert.Equal(1, typed.Initializations); Assert.Equal(42, typed.Value);
    }

    private sealed class TestApplication : Application
    {
        internal Window Add() => (Window)((IApplication)this).CreateWindow(null);
        protected override Window CreateWindow(IActivationState? state) => new(new ContentPage());
    }
    private class Model
    {
        internal Resource? Resource;
        internal int Releases;
        internal Task Release() { Releases++; return Task.CompletedTask; }
    }
    private sealed class AwareModel : Model, INavigationAware
    {
        internal int Activations, Dismissals;
        internal Func<Task>? Activate;
        public Task ActivateAsync(CancellationToken lifetimeToken) { Activations++; return Activate?.Invoke() ?? Task.CompletedTask; }
        public Task DismissAsync(DismissalReason reason)
        { Assert.Equal(0, Resource?.Disposals ?? Releases); Dismissals++; return Task.CompletedTask; }
    }
    private sealed class InitializedModel : INavigationInitializable<int>
    {
        internal int Initializations, Value;
        public Task InitializeAsync(int parameter, CancellationToken cancellationToken)
        { Initializations++; Value = parameter; return Task.CompletedTask; }
    }
    private sealed class Locator : IViewLocator
    {
        public void Initialize(Dictionary<Type, Type> pairs) { }
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => throw new NotSupportedException();
        public VisualElement CreateAndBindVEFor(Type type) => throw new NotSupportedException();
        public Type FindVEForViewModel(Type type) => throw new NotSupportedException();
        public Type FindViewModelForVE(Type type) => throw new NotSupportedException();
    }
    public sealed class Resource : IAsyncDisposable
    { public int Disposals; public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; } }
}
