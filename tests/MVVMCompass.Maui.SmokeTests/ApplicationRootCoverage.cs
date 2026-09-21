using MVVMCompass.Core;
using MVVMCompass.Services;

namespace MVVMCompass.Sample;

internal sealed partial class UnifiedNativeCoverage
{
    internal async Task ApplicationRootsAsync(MauiNavigationHostFactory factory)
    {
        Success(await factory.SetRoot<FlyoutDemoViewModel>(host.Window), "factory-owned flyout root");
        var root = (FlyoutDemoViewModel)Root.Current!.ViewModel;
        var leaf = Current;
        await Ready(Root.View.Toolbar);
        Root.View.Toolbar.LeadingCommand.Execute(null);
        var closed = await leaf.Navigation.CloseFlyout();
        Success(closed, "public CloseFlyout command");
        Check(closed.HasCommitted, "CloseFlyout hides an open native overlay");
        Check(!(await leaf.Navigation.CloseFlyout()).HasCommitted, "CloseFlyout is idempotent");

        Success(await leaf.Navigation.NavigateTo<DemoModalViewModel>(), "modal before application block");
        var modal = Current;
        var parentResult = modal.Navigation.DisplayPopup<DemoPopupViewModel, string>();
        await Until(() => ViewModelTree.Collect(host.Window.Page, true).OfType<DemoPopupViewModel>().Any());
        var parent = ViewModelTree.Collect(host.Window.Page, true).OfType<DemoPopupViewModel>().Single();
        await Task.Delay(150);
        var childResult = parent.Navigation.DisplayPopup<DemoPopupViewModel, string>();
        await Until(() => ViewModelTree.Collect(host.Window.Page, true).OfType<DemoPopupViewModel>().Count() == 2);
        var child = ViewModelTree.Collect(host.Window.Page, true).OfType<DemoPopupViewModel>().Single(item => item != parent);
        await Task.Delay(150);
        Check((await parent.Navigation.ClosePopup()).Status == NavigationStatus.InvalidOrigin, "covered popup cannot close its child");
        child.AllowNavigation = false;
        Check((await factory.SetRoot<WelcomeViewModel>(host.Window)).Status == NavigationStatus.GuardRejected,
            "normal application root guards a nested popup");
        var request = factory.RequestRoot<WelcomeViewModel>(host.Window, options: new() { Mode = RootTransitionMode.Enforced });
        Success(await request.Completion, "enforced application root with modal and nested popups");
        Check((await parentResult).Reason == DismissalReason.RootReplaced && (await childResult).Reason == DismissalReason.RootReplaced,
            "application root settles both popup waiters");
        Check(host.Window.Navigation.ModalStack.Count == 0, "application root removes old native modal stack");
        Check(new DemoViewModel[] { root, leaf, modal, parent, child }.All(model => model.IsDismissed && model.Resource.Disposals == 1),
            "application root disposes every outgoing scoped resource once");
        Check((await leaf.Navigation.NavigateBack()).Status == NavigationStatus.InvalidOrigin, "old root navigation is invalid after blocking");
        await Ready(Root.View.Toolbar);
    }
}
