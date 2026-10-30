using System.Collections.ObjectModel;
using Microsoft.Maui.Controls;
using MVVMCompass.Core;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

public sealed class OwnershipTests
{
    [Fact]
    public void Native_visual_children_are_collected_once_before_their_parent()
    {
        var active = new ViewModelTests.Probe();
        var inactive = new ViewModelTests.Probe();
        var tabsVm = new ViewModelTests.Probe();
        var tabs = new TabbedPage { BindingContext = tabsVm };
        tabs.Children.Add(new ContentPage { BindingContext = active });
        tabs.Children.Add(new ContentPage { BindingContext = inactive });
        var menuVm = new ViewModelTests.Probe();
        var menu = new ContentPage { BindingContext = menuVm, Title = "Menu" };
        var rootVm = new ViewModelTests.Probe();
        var root = new FlyoutPage { Flyout = menu, Detail = tabs, BindingContext = rootVm };
        var collected = ViewModelTree.Collect(root);
        Assert.Equal(5, collected.Count);
        Assert.Equal(5, collected.Distinct().Count());
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
        var pending = ViewModelCleanup.DismissAsync([first, failing, last, first], DismissalReason.RootReplaced);
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
        await ViewModelCleanup.DismissAsync([vm], DismissalReason.Back);
        Assert.True(vm.IsDismissed);
        Assert.False(vm.IsHostReplaced);
        Assert.Equal(DismissalReason.Back, vm.Lifetime.Reason);
    }

}
