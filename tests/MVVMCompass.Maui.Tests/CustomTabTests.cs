using Microsoft.Maui.Controls;
using MVVMCompass.Interfaces;
using MVVMCompass.Services;

namespace MVVMCompass.Maui.Tests;

public sealed class CustomTabTests
{
    [Fact]
    public async Task Duplicate_view_model_types_can_be_selected_by_index_including_hidden_tabs()
    {
        var parent = new TabVm();
        var host = new Host(parent);
        var first = Child();
        var second = Child(hidden: true);
        host.AddChild(first);
        host.AddChild(second);
        var tabs = (ICustomTabbedViewBase)host;
        Assert.True(await tabs.SwitchToAsync(0));
        Assert.True(await tabs.SwitchToAsync(1));
        Assert.Same(second, tabs.CurrentTab);
        Assert.Equal(1, ((TabVm)first.ViewModel).Deactivations);
        Assert.False(first.ViewModel.IsDismissed);
        Assert.Same(second.ViewModel, parent.ActiveChild);
        Assert.True(second.HideInTabBar);
    }

    [Fact]
    public async Task Veto_and_disabled_tab_bar_preserve_the_selected_child()
    {
        var host = new Host(new TabVm());
        var first = Child();
        var second = Child();
        host.AddChild(first);
        host.AddChild(second);
        var tabs = (ICustomTabbedViewBase)host;
        await tabs.SwitchToAsync(0);
        ((TabVm)first.ViewModel).AllowNavigation = false;
        Assert.False(await tabs.SwitchToAsync(1));
        Assert.Same(first, tabs.CurrentTab);
        ((TabVm)first.ViewModel).AllowNavigation = true;
        tabs.IsTabBarEnabled = false;
        Assert.False(await tabs.SwitchToAsync(1));
        Assert.Same(first, tabs.CurrentTab);
        Assert.Equal(0, ((TabVm)first.ViewModel).Deactivations);
    }

    [Fact]
    public async Task Legacy_profile_preserves_tab_dismissal_callback_without_ending_the_lifetime()
    {
        var host = new Host(new TabVm());
        var first = Child();
        first.ViewModel.RetainedViewLifecycleBehavior = RetainedViewLifecycleBehavior.LegacyAfterDismissed;
        host.AddChild(first);
        host.AddChild(Child());
        var tabs = (ICustomTabbedViewBase)host;
        await tabs.SwitchToAsync(0);
        await tabs.SwitchToAsync(1);
        Assert.Equal(1, ((TabVm)first.ViewModel).Dismissals);
        Assert.False(first.ViewModel.IsDismissed);
        Assert.True(await tabs.SwitchToAsync(0));
    }

    [Fact]
    public void Ownership_includes_never_selected_custom_children()
    {
        var vm = new TabVm();
        var host = new Host(vm);
        var first = Child();
        var second = Child(true);
        host.AddChild(first);
        host.AddChild(second);
        Assert.Equal([first.ViewModel, second.ViewModel, vm], ViewModelTree.Collect(host));
    }

    private static ChildTabInfo Child(bool hidden = false)
    {
        var vm = new TabVm();
        return new ChildTabInfo { View = new ContentPage { BindingContext = vm }, ViewModel = vm, Content = new Label(), HideInTabBar = hidden };
    }
    private sealed class TabVm : ViewModelBase
    {
        public bool AllowNavigation { get; set; } = true;
        public int Deactivations { get; private set; }
        public int Dismissals { get; private set; }
        public ViewModelBase? ActiveChild { get; private set; }
        public override Task<bool> CanNavigate() => Task.FromResult(AllowNavigation);
        public override Task Deactivated() { Deactivations++; return Task.CompletedTask; }
        public override Task AfterDismissed() { Dismissals++; return Task.CompletedTask; }
        public override Task OnActiveTabChanged(ViewModelBase child) { ActiveChild = child; return Task.CompletedTask; }
    }
    private sealed class Host(TabVm vm) : CustomTabbedViewBase<TabVm>(vm)
    {
        protected override Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;
        protected override Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
        protected override bool NotifyLanguageChange() => true;
        protected override IDisposable ShowLoading(LoadingType loadingType) => new EmptyDisposable();
        protected override void HideLoading() { }
        private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    }
}
