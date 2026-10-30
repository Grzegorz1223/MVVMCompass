using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Compatibility.Tests;

// Compiled consumer contracts complement reflection. These methods intentionally do
// not execute UI: the MAUI suite and package consumers cover runtime presentation.
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

    internal static void FrameWorkspace(WorkspaceView view)
    {
        view.BodyTemplate = new ControlTemplate(() => new Border
        {
            StrokeThickness = 0, Margin = new Thickness(8, 0, 8, 8),
            Content = new ContentPresenter()
        });
        view.DetailMargin = new Thickness(0, 0, 8, 8);
    }
}
