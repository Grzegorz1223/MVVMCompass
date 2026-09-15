using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Compatibility.Tests;

// Compiled consumer contracts complement reflection. These methods intentionally do
// not execute UI: the MAUI suite and package consumers cover runtime presentation.
internal static class ConsumerCompilationFixtures
{
    internal static Task<NavigationOutcome<MauiNavigationRoot<PlainModel>>> PlainRoot(MauiNavigationHost host) =>
        host.ReplaceRootAsync(request: new NavigationRequestOptions { Priority = NavigationPriority.Required },
            createViewModel: () => new PlainModel(), createPage: _ => new ContentPage(), cleanup: null);

    internal static Task<NavigationOutcome<MauiNavigationRoot<DerivedModel>>> RegisteredTyped(MauiNavigationHost host) =>
        host.ReplaceRootAsync<DerivedModel, object?>(request: new(null), navigable: true);

    internal static Task<NavigationOutcome<MauiNavigationPage<PlainModel>>> ScopedModal(MauiNavigationHost host) =>
        host.OpenScopedModalAsync(request: new NavigationRequestOptions(), createViewModel: _ => new PlainModel(),
            createPage: (_, _) => new ContentPage(), animated: false);

    internal sealed class PlainModel;
    internal sealed class DerivedModel : ViewModelBase, INavigationInitializable<object?>
    {
        public Task InitializeAsync(object? parameter, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task BeforeFirstShown() => Task.CompletedTask;
        public override Task AfterDismissed() => Task.CompletedTask;
    }
    internal sealed class DerivedPage(DerivedModel model) : LegacyViewBase<DerivedModel>(model),
        IWindowView, IHandledPageModalHost, IIdProvider, ITitleProvider, IViewContentProvider
    {
        public string WindowTitle => "Consumer fixture";
        public int WindowHeight => 600;
        public int WindowWidth => 800;
        public string GetId() => "fixture";
        public string GetTitle() => WindowTitle;
        public void OnHandledPageModalOpening() { }
        public void OnHandledPageModalClosed() { }
        public Microsoft.Maui.IView? ViewContent { get => Content; set => Content = (View?)value; }
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => true;
        protected override IDisposable ShowLoading(LoadingType loadingType) => new Empty();
        protected override void HideLoading() { }
    }
    private sealed class Empty : IDisposable { public void Dispose() { } }
}

internal static class UnifiedConsumerCompilationFixtures
{
    internal static void Register(Microsoft.Maui.Hosting.MauiAppBuilder builder) => builder.UseMVVMCompass(pairs =>
    {
        pairs.Add<Workspace, WorkspaceView>(); pairs.Add<Note, NoteView>();
    });
    internal sealed class Workspace(MVVMCompass.Interfaces.INavigationService navigation) : ViewModelBase
    {
        public override async Task BeforeFirstShown() => await SetTabs([
            new(typeof(Note), "Open", "open", new() { ["filter"] = "open" }),
            new(typeof(Note), "Archived", "archive", new() { ["filter"] = "archive" })]);
        internal Task<NavigationResult> OpenDetail() => navigation.NavigateTo<Note>();
        internal Task<NavigationResult> SelectArchive() => navigation.Select("archive");
        internal Task<NavigationResult> Back() => navigation.NavigateBack();
    }
    internal sealed class Note : ViewModelBase;
    internal sealed class WorkspaceView(Workspace model) : TabbedViewBase<Workspace>(model);
    internal sealed class NoteView(Note model) : ViewBase<Note>(model);
}
