using System.Collections.Concurrent;
using Microsoft.Maui.Controls;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class NativeCallbackConcurrencyTests
{
    [Fact]
    public async Task Deferred_callbacks_arriving_during_reconciliation_are_delivered_once_without_loss()
    {
        var token = TestContext.Current.CancellationToken;
        var window = new Window(new ContentPage());
        await using var host = new MauiNavigationHostFactory(new Locator(), new()).ForWindow(window);
        var result = await host.ReplaceRootAsync<Model>(new(null), navigable: true, cancellationToken: token);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        var root = result.Value!;
        ((NavigationPage)root.Page).Handler = new TestNavigationHandler();
        var pushed = await host.PushAsync<Model>(new(null), animated: false, cancellationToken: token);
        Assert.True(pushed.IsSuccess, pushed.Error?.ToString());
        var defer = root.Entry.ViewModel.NativeLifecycleObserver;
        Assert.NotNull(defer);
        var delivered = new ConcurrentDictionary<int, int>();
        const int producers = 8, perProducer = 500;
        await Task.WhenAll(Enumerable.Range(0, producers).Select(producer => Task.Run(() =>
        {
            for (var index = 0; index < perProducer; index++)
            {
                token.ThrowIfCancellationRequested();
                var id = producer * perProducer + index;
                // Distinct operation IDs ensure legitimate per-batch lifecycle coalescing
                // cannot conceal callbacks lost at the enqueue/snapshot boundary.
                Assert.True(defer(() => { delivered.AddOrUpdate(id, 1, (_, count) => count + 1); return Task.CompletedTask; }, $"probe-{id}"));
            }
        }, token))).WaitAsync(TimeSpan.FromSeconds(20), token);
        await host.ReconcileNativeAsync().WaitAsync(TimeSpan.FromSeconds(20), token);
        Assert.Equal(producers * perProducer, delivered.Count);
        Assert.All(delivered.Values, count => Assert.Equal(1, count));
    }

    private sealed class Model : ViewModelBase;
    private sealed class ModelPage : ContentPage, IHasVM
    {
        public ModelPage() { BindingContext = ViewModel = new Model(); }
        public ViewModelBase ViewModel { get; }
    }
    private sealed class Locator : IViewLocator
    {
        public void Initialize(Dictionary<Type, Type> registerPairs) { }
        public VisualElement CreateAndBindVEFor<TViewModel>() where TViewModel : ViewModelBase => new ModelPage();
        public VisualElement CreateAndBindVEFor(Type type) => new ModelPage();
        public Type FindVEForViewModel(Type viewModelType) => typeof(ModelPage);
        public Type FindViewModelForVE(Type page) => typeof(Model);
    }
}
