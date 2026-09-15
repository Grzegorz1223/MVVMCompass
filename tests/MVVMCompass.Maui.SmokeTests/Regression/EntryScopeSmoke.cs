using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

internal sealed class EntryScopeSmoke(MauiNavigationHostFactory hosts, NavigationOptions options, ScopeProbeState state)
{
    public async Task RunAsync(Action<bool, string> check)
    {
        var previous = options.UseEntryScopes;
        options.UseEntryScopes = true;
        var host = hosts.ForWindow(Application.Current!.Windows[0]);
        try
        {
            foreach (var profile in Enum.GetValues<RetainedViewLifecycleBehavior>())
            {
                options.RetainedViewLifecycleBehavior = profile;
                void Check(bool value, string name) => check(value, $"{profile}: entry scope {name}");
                var root = Require(await host.ReplaceRootAsync<ScopeProbeModel>(new(null), navigable: true));
                var page = (ScopeProbePage)((NavigationPage)root.Page).RootPage;
                Check(ReferenceEquals(page.ConstructorResource, root.Entry.ViewModel.Resource)
                    && ReferenceEquals(page.GetService<ScopeProbeResource>(), page.ConstructorResource), "view/model/constructor provider identity");
                var child = new NavigationLifetime(() => { state.Order.Add("child"); return Task.CompletedTask; });
                root.Entry.Ownership.Adopt(child.Ownership);
                Check(host.OwnershipRoots.Single().Snapshot().Count == 2, "inspectable explicit child ownership");

                var pushed = Require(await host.PushAsync<ScopeProbeModel>(new(null), animated: false));
                Check(pushed.Entry.ViewModel.Resource != root.Entry.ViewModel.Resource && root.Entry.ViewModel.Resource.Disposals == 0,
                    "independent stack scope retains covered root");
                var back = await host.BackAsync(animated: false);
                Check(back.IsSuccess && pushed.Entry.ViewModel.Resource.Disposals == 1 && root.Entry.ViewModel.Resource.Disposals == 0,
                    "back awaits scope release");
                var modal = Require(await host.OpenModalAsync<ScopeProbeModel>(new(null), animated: false));
                await host.Window.Navigation.PopModalAsync(false); await host.ReconcileNativeAsync();
                Check(modal.Entry.ViewModel.Resource.Disposals == 1 && modal.Entry.ViewModel.CleanupOnUi,
                    "native modal removal finishes UI callback before dependency disposal");

                state.FailPage = true;
                var failed = await host.ReplaceRootAsync<ScopeProbeModel>(new(null)); state.FailPage = false;
                Check(failed.Status == NavigationStatus.Failed && state.Resources.Last().Disposals == 1
                    && ReferenceEquals(host.CurrentRoot, root), "failed constructor releases candidate and preserves root");

                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                state.Initializing = entered;
                using var cancellation = new CancellationTokenSource();
                var cancelled = host.ReplaceRootAsync<ScopeProbeModel, int>(new(1), cancellationToken: cancellation.Token);
                await entered.Task; cancellation.Cancel();
                Check((await cancelled).Status == NavigationStatus.Cancelled && state.Resources.Last().Disposals == 1,
                    "cancelled initialization awaits scope disposal"); state.Initializing = null;

                state.Order.Clear();
                var retained = Require(await host.ReplaceRootAsync<ScopeTabsModel>(new(null)));
                Check(child.IsDismissed && state.Order.IndexOf("child") < state.Order.IndexOf("model")
                    && state.Order.IndexOf("model") < state.Order.IndexOf("scope"), "children finish before root callback and scope");
                var tabs = (ScopeTabsPage)retained.Page;
                var first = (ScopeProbePage)tabs.Children[0]; var second = (ScopeProbePage)tabs.Children[1];
                Check(first.ViewModel.Resource != second.ViewModel.Resource
                    && ReferenceEquals(first.ViewModel.ConstructorParent, retained.Entry.ViewModel), "retained children get scopes and constructor parent");
                var selected = await host.SelectTabAsync(tabs, new(1));
                Check(selected.IsSuccess && first.ViewModel.Resource.Disposals == 0 && second.ViewModel.Resource.Disposals == 0,
                    "retained selection keeps both scopes live");
                tabs.Children.Remove(first); await host.ReconcileNativeAsync();
                Check(first.ViewModel.Resource.Disposals == 1 && second.ViewModel.Resource.Disposals == 0,
                    "native tab removal releases only the removed scope");

                state.PopupOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var showing = host.DisplayPopupAsync<ScopePopupModel, string>(); await state.PopupOpened.Task;
                var popup = state.Popup!; var releasing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                popup.ViewModel.Resource.Release = async () => { releasing.SetResult(); await release.Task; };
                var closing = popup.CloseAsync("owned");
                try { await releasing.Task; Check(!showing.IsCompleted && !closing.IsCompleted, "popup result waits for async scope release"); }
                finally { release.SetResult(); }
                await closing;
                Check((await showing).Result == "owned" && popup.ViewModel.Resource.Disposals == 1, "popup completion includes scope disposal");

                ScopePlainModel? ordinary = null;
                var plain = Require(await host.OpenScopedModalAsync(new NavigationRequest<int>(42), services =>
                    ordinary = services.GetRequiredService<ScopePlainModel>(), (services, model) =>
                    {
                        Check(ReferenceEquals(model.Resource, services.GetRequiredService<ScopeProbeResource>()), "ordinary factories share their provider");
                        return new ContentPage { Content = new Label { Text = "Ordinary scoped modal" } };
                    }, animated: false));
                await host.CloseModalAsync(animated: false);
                Check(ordinary is { Parameter: 42, CleanupOnUi: true } && ordinary.Resource.Disposals == 1
                    && plain.Entry.Lifetime.IsDismissed, "ordinary typed modal owns asynchronous cleanup");
                Require(await host.ReplaceRootAsync<CatalogViewModel>(new(null)));
                Check(second.ViewModel.Resource.Disposals == 1 && retained.Entry.ViewModel.Resource.Disposals == 1
                    && state.Resources.All(resource => resource.Disposals == 1), "replacement releases all retained scopes exactly once");
            }
        }
        finally { options.UseEntryScopes = previous; state.FailPage = false; state.Initializing = null; }
    }
    private static T Require<T>(NavigationOutcome<T> result) where T : class
    { if (!result.IsSuccess) throw result.Error ?? new InvalidOperationException(result.Status.ToString()); return result.Value!; }
}

internal sealed class ScopeProbeState
{
    public List<ScopeProbeResource> Resources { get; } = [];
    public List<string> Order { get; } = [];
    public bool FailPage { get; set; }
    public TaskCompletionSource? Initializing { get; set; }
    public TaskCompletionSource? PopupOpened { get; set; }
    public ScopePopup? Popup { get; set; }
}
internal sealed class ScopeProbeResource : IAsyncDisposable
{
    private readonly ScopeProbeState state;
    public ScopeProbeResource(ScopeProbeState state) { this.state = state; state.Resources.Add(this); }
    public int Disposals { get; private set; }
    public Func<Task>? Release { get; set; }
    public async ValueTask DisposeAsync()
    { if (Release != null) await Release(); await Task.Yield(); Disposals++; state.Order.Add("scope"); }
}
internal class ScopeProbeModel : ViewModelBase, INavigationInitializable<int>
{
    private readonly ScopeProbeState state;
    public ScopeProbeModel(ScopeProbeResource resource, ScopeProbeState state)
    { Resource = resource; this.state = state; ConstructorParent = ParentViewModel; }
    public ScopeProbeResource Resource { get; }
    public ViewModelBase? ConstructorParent { get; }
    public bool CleanupOnUi { get; private set; }
    public async Task InitializeAsync(int parameter, CancellationToken cancellationToken)
    { if (state.Initializing is { } signal) { signal.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); } }
    public override Task AfterDismissed()
    { CleanupOnUi = MainThread.IsMainThread && Resource.Disposals == 0; state.Order.Add("model"); return Task.CompletedTask; }
}
internal sealed class ScopeProbePage : LegacyViewBase<ScopeProbeModel>, IViewContentProvider
{
    public ScopeProbePage(ScopeProbeModel model, ScopeProbeState state) : base(model)
    {
        ConstructorResource = GetService<ScopeProbeResource>();
        if (state.FailPage) throw new InvalidOperationException("Scope constructor probe");
        Title = "Scoped destination"; Content = new Label { Text = "Entry-owned scoped dependencies", Margin = 24 };
    }
    public ScopeProbeResource ConstructorResource { get; }
    public IView? ViewContent { get => Content; set => Content = (View?)value; }
    protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
    protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
    protected override bool NotifyLanguageChange() => true;
    protected override IDisposable ShowLoading(LoadingType type) => throw new NotSupportedException();
    protected override void HideLoading() { }
}
internal sealed class ScopeTabsModel(ScopeProbeResource resource, ScopeProbeState state) : ScopeProbeModel(resource, state)
{
    public override Task BeforeFirstShown() => AddTabbedViewModels(new(new[] { typeof(ScopeProbeModel), typeof(ScopeProbeModel) }, this));
}
internal sealed class ScopeTabsPage : TabbedPage, IHasVM
{
    public ScopeTabsPage(ScopeTabsModel model) { BindingContext = ViewModel = model; }
    public ViewModelBase ViewModel { get; }
}
internal sealed class ScopePopupModel(ScopeProbeResource resource, ScopeProbeState state) : ScopeProbeModel(resource, state);
internal sealed class ScopePopup : PopupViewBase<ScopePopupModel, string>
{
    public ScopePopup(ScopePopupModel model, ScopeProbeState state) : base(model)
    {
        state.Popup = this; Content = new Label { Text = "Scoped popup", Margin = 24 };
        Opened += (_, _) => state.PopupOpened?.TrySetResult();
    }
    protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
    protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
    protected override bool NotifyLanguageChange() => true;
    protected override IDisposable ShowLoading(LoadingType type) => throw new NotSupportedException();
    protected override void HideLoading() { }
}
internal sealed class ScopePlainModel(ScopeProbeResource resource, ScopeProbeState state) : INavigationInitializable<int>, INavigationAware
{
    public ScopeProbeResource Resource => resource;
    public int Parameter { get; private set; }
    public bool CleanupOnUi { get; private set; }
    public Task InitializeAsync(int parameter, CancellationToken cancellationToken) { Parameter = parameter; return Task.CompletedTask; }
    public Task DismissAsync(DismissalReason reason)
    { CleanupOnUi = MainThread.IsMainThread && resource.Disposals == 0; state.Order.Add("ordinary"); return Task.CompletedTask; }
}
