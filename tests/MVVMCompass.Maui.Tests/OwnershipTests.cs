using System.Collections.ObjectModel;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

public sealed class OwnershipTests
{
    [Fact]
    public void Retained_flyout_items_and_inactive_standard_tabs_are_collected_once_before_the_host()
    {
        var active = new ViewModelTests.Probe();
        var inactive = new ViewModelTests.Probe();
        var tabsVm = new ViewModelTests.Probe();
        var tabs = new TabbedPage { BindingContext = tabsVm };
        tabs.Children.Add(new ContentPage { BindingContext = active });
        tabs.Children.Add(new ContentPage { BindingContext = inactive });
        var hiddenVm = new ViewModelTests.Probe();
        var hiddenPage = new ContentPage { BindingContext = hiddenVm };
        var menuVm = new ViewModelTests.Probe();
        var menu = new Menu(menuVm)
        {
            Title = "Menu",
            MenuItems = new ObservableCollection<FlyoutMenuItem>
            {
                new("active", () => "Active", () => "", () => "", tabs, _ => true),
                new("hidden", () => "Hidden", () => "", () => "", hiddenPage, _ => true)
            }
        };
        var rootVm = new ViewModelTests.Probe();
        var root = new FlyoutPage { Flyout = menu, Detail = tabs, BindingContext = rootVm };
        var collected = ViewModelTree.Collect(root);
        Assert.Equal(6, collected.Count);
        Assert.Equal(6, collected.Distinct().Count());
        Assert.Contains(hiddenVm, collected);
        Assert.True(collected.IndexOf(active) < collected.IndexOf(tabsVm));
        Assert.True(collected.IndexOf(inactive) < collected.IndexOf(tabsVm));
        Assert.Same(rootVm, collected[^1]);
    }

    [Fact]
    public async Task Host_replacement_marks_all_children_before_awaiting_and_continues_after_a_failure()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ViewModelTests.Probe { Cleanup = () => release.Task };
        var last = new ViewModelTests.Probe();
        var failing = new ViewModelTests.Probe { Cleanup = () => throw new InvalidOperationException("cleanup") };
        var pending = LegacyNavigationService.DismissAllAsync([first, failing, last, first]);
        Assert.All(new[] { first, failing, last }, vm => { Assert.True(vm.IsHostReplaced); Assert.True(vm.IsDismissed); });
        Assert.False(pending.IsCompleted);
        release.SetResult();
        await pending;
        Assert.All(new[] { first, failing, last }, vm => Assert.Equal(1, vm.Dismissals));
    }

    [Fact]
    public async Task A_back_dismissal_is_terminal_without_marking_the_host_replaced()
    {
        var vm = new ViewModelTests.Probe();
        await LegacyNavigationService.DismissViewModelsAsync([vm], DismissalReason.Back);
        Assert.True(vm.IsDismissed);
        Assert.False(vm.IsHostReplaced);
        Assert.Equal(DismissalReason.Back, vm.Lifetime.Reason);
    }

    [Fact]
    public async Task Native_pop_and_direct_cleanup_share_the_same_completion()
    {
        var root = new ContentPage();
        var navigation = TestNavigationHandler.Create(root);
        NativeNavigationObserver.Attach(navigation);
        var vm = new ViewModelTests.Probe();
        var page = new ContentPage { BindingContext = vm };
        await navigation.PushAsync(page, false);
        await navigation.PopAsync(false);
        await vm.DismissAsync();
        Assert.Equal(1, vm.Dismissals);
        Assert.Equal(DismissalReason.Back, vm.Lifetime.Reason);
        NativeNavigationObserver.Detach(navigation);
    }

    [Fact]
    public async Task Native_pop_to_root_dismisses_every_removed_page()
    {
        var navigation = TestNavigationHandler.Create(new ContentPage());
        NativeNavigationObserver.Attach(navigation);
        var first = new ViewModelTests.Probe();
        var second = new ViewModelTests.Probe();
        await navigation.PushAsync(new ContentPage { BindingContext = first }, false);
        await navigation.PushAsync(new ContentPage { BindingContext = second }, false);
        await navigation.PopToRootAsync(false);
        Assert.True(first.IsDismissed);
        Assert.True(second.IsDismissed);
        await Task.WhenAll(first.DismissAsync(), second.DismissAsync());
        Assert.Equal(1, first.Dismissals);
        Assert.Equal(1, second.Dismissals);
        NativeNavigationObserver.Detach(navigation);
    }

    private sealed class Menu(ViewModelTests.Probe vm) : FlyoutViewFlyoutBase<ViewModelTests.Probe>(vm);
}
