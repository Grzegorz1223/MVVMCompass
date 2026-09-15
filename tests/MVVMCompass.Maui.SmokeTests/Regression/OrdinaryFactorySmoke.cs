using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Core;

namespace MVVMCompass.Sample;

internal static class OrdinaryFactorySmoke
{
    public static async Task RunAsync(MauiNavigationHost host, NavigationOptions options, Action<bool, string> check)
    {
        var previous = options.RetainedViewLifecycleBehavior;
        try
        {
            foreach (var profile in Enum.GetValues<RetainedViewLifecycleBehavior>())
            foreach (var scoped in new[] { false, true })
            foreach (var aware in new[] { false, true })
            {
                options.RetainedViewLifecycleBehavior = profile;
                void Check(bool value, string name) => check(value, $"{profile}: parameterless {(scoped ? "scoped" : "explicit")} {(aware ? "lifecycle" : "plain")} {name}");
                Model Create(IServiceProvider? provider = null) => aware
                    ? new AwareModel(provider?.GetRequiredService<ScopeProbeResource>())
                    : new Model(provider?.GetRequiredService<ScopeProbeResource>());
                Page Page(Model model) => new ContentPage
                { Content = new Label { Text = "Ordinary model without an initializer", Margin = 24 } };
                Page ScopedPage(IServiceProvider provider, Model model)
                {
                    if (provider.GetRequiredService<ScopeProbeResource>() != model.Resource)
                        throw new InvalidOperationException("Factory provider mismatch.");
                    return Page(model);
                }
                var root = scoped
                    ? Require(await host.ReplaceScopedRootAsync(new NavigationRequestOptions(), provider => Create(provider), ScopedPage, navigable: true))
                    : Require(await host.ReplaceRootAsync(new NavigationRequestOptions(), () => Create(), Page, navigable: true, cleanup: model => model.Release()));
                Check(ReferenceEquals(((NavigationPage)root.Page).RootPage.BindingContext, root.Entry.ViewModel) && MainThread.IsMainThread, "root binds on UI");
                var pushed = scoped
                    ? Require(await host.PushScopedAsync(new NavigationRequestOptions { Origin = root.Entry }, provider => Create(provider), ScopedPage, animated: false))
                    : Require(await host.PushAsync(new NavigationRequestOptions { Origin = root.Entry }, () => Create(), Page, animated: false, cleanup: model => model.Release()));
                Check(root.Entry.State == NavigationEntryState.Inactive && root.Entry.ViewModel.Resource?.Disposals is null or 0, "push retains root");
                var modal = scoped
                    ? Require(await host.OpenScopedModalAsync(new NavigationRequestOptions { Origin = pushed.Entry }, provider => Create(provider), ScopedPage, animated: false))
                    : Require(await host.OpenModalAsync(new NavigationRequestOptions { Origin = pushed.Entry }, () => Create(), Page, animated: false, cleanup: model => model.Release()));
                Check(pushed.Entry.State == NavigationEntryState.Inactive && modal.Entry.State == NavigationEntryState.Active, "modal activation");
                var closed = await host.CloseModalAsync(animated: false);
                Check(closed.IsSuccess && Ended(modal.Entry.ViewModel) && modal.Entry.State == NavigationEntryState.Dismissed, "modal cleanup");
                var back = await host.BackAsync(animated: false);
                Check(back.IsSuccess && Ended(pushed.Entry.ViewModel) && root.Entry.State == NavigationEntryState.Active, "back and reactivation");
                Require(await host.ReplaceRootAsync<CatalogViewModel>(new(null), navigable: true));
                Check(Ended(root.Entry.ViewModel) && root.Entry.State == NavigationEntryState.Dismissed, "root teardown");

                bool Ended(Model model) => (scoped ? model.Resource?.Disposals == 1 : model.Releases == 1)
                    && (!aware || model is AwareModel { Dismissals: 1, CleanupOnUi: true });
            }
        }
        finally { options.RetainedViewLifecycleBehavior = previous; }
    }

    private static T Require<T>(NavigationOutcome<T> result) where T : class => result.IsSuccess ? result.Value!
        : throw result.Error ?? new InvalidOperationException(result.Status.ToString());
    private class Model(ScopeProbeResource? resource)
    {
        internal ScopeProbeResource? Resource => resource;
        internal int Releases;
        internal Task Release() { Releases++; return Task.CompletedTask; }
    }
    private sealed class AwareModel(ScopeProbeResource? resource) : Model(resource), INavigationAware
    {
        internal int Dismissals;
        internal bool CleanupOnUi;
        public Task DismissAsync(DismissalReason reason)
        { Dismissals++; CleanupOnUi = MainThread.IsMainThread && (Resource?.Disposals is null or 0); return Task.CompletedTask; }
    }
}
