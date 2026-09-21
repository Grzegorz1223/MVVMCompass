using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using MVVMCompass.Core;

namespace MVVMCompass.Maui.Tests;

public sealed partial class UnifiedNavigationTests
{
    [Fact]
    public async Task Nested_busy_screens_have_one_default_indicator_and_reuse_it()
    {
        var root = await Open<Flyout>(); var model = (Leaf)root.Deepest.Current!.ViewModel;
        Assert.All(root.ActiveChain(), context => Assert.Null(context.View.BusyContent));
        model.IsBusy = true;
        var indicator = Assert.IsType<ActivityIndicator>(root.View.BusyContent);
        Assert.True(indicator.IsRunning); Assert.True(root.View.IsBusyPresented);
        Assert.Single(Descendants(root.View).OfType<ActivityIndicator>());
        Assert.All(root.ActiveChain().Skip(1), context => { Assert.Null(context.View.BusyContent); Assert.False(context.View.IsBusyPresented); });
        for (var i = 0; i < 20; i++)
        {
            model.IsBusy = false; Assert.False(indicator.IsRunning); Assert.False(root.View.IsBusyPresented);
            model.IsBusy = true; Assert.Same(indicator, root.View.BusyContent);
        }
        model.IsBusy = false;
    }

    [Fact]
    public async Task Custom_busy_template_inherits_declaring_context_resources_and_is_reused()
    {
        var root = await Open<Flyout>(); var owner = root.Current!.View; var calls = 0;
        owner.Resources["LoadingColor"] = Colors.Orange;
        owner.BusyOverlayTemplate = new DataTemplate(() =>
        {
            calls++;
            var label = new Label(); label.SetBinding(Label.TextProperty, nameof(Leaf.Filter));
            label.SetDynamicResource(Label.TextColorProperty, "LoadingColor"); return label;
        });
        Assert.Equal(0, calls);
        root.Deepest.Current!.View.IsBusy = true;
        var content = Assert.IsType<Label>(root.View.BusyContent);
        Assert.Same(owner.BindingContext, content.BindingContext); Assert.Equal(Colors.Orange, content.TextColor);
        Assert.Empty(Descendants(root.View).OfType<ActivityIndicator>());
        owner.Resources["LoadingColor"] = Colors.Purple;
        Assert.Equal(Colors.Purple, content.TextColor);
        for (var i = 0; i < 20; i++)
        {
            root.Deepest.Current.View.IsBusy = false; root.Deepest.Current.View.IsBusy = true;
            Assert.Same(content, root.View.BusyContent);
        }
        Assert.Equal(1, calls);
        var replacement = new { Filter = "Replacement context" }; owner.BindingContext = replacement;
        Assert.Same(replacement, content.BindingContext); Assert.Equal("Replacement context", content.Text);
        owner.Resources = new ResourceDictionary { ["LoadingColor"] = Colors.Green };
        Assert.Equal(Colors.Green, content.TextColor);
        Success(await factory.SetRoot<Leaf>(root.Window, options: Enforced, cancellationToken: Token));
        Assert.Null(content.Parent); Assert.Null(content.BindingContext); Assert.False(root.View.IsBusyPresented);
    }

    [Fact]
    public async Task Closest_active_template_overrides_ancestor_and_any_ancestor_can_disable_default()
    {
        var root = await Open<Flyout>(); var outer = root.Current!.View; var leaf = root.Deepest.Current!.View;
        outer.UseDefaultBusyIndicator = false; leaf.IsBusy = true;
        Assert.False(root.View.IsBusyPresented); Assert.Null(root.View.BusyContent);
        leaf.UseDefaultBusyIndicator = true;
        Assert.False(root.View.IsBusyPresented);
        outer.UseDefaultBusyIndicator = true;
        Assert.IsType<ActivityIndicator>(root.View.BusyContent); Assert.True(root.View.IsBusyPresented);
        outer.UseDefaultBusyIndicator = false;
        Assert.False(root.View.IsBusyPresented);
        outer.BusyOverlayTemplate = new DataTemplate(() => new Label { Text = "outer" });
        var inherited = Assert.IsType<Label>(root.View.BusyContent);
        Assert.Equal("outer", inherited.Text); Assert.True(root.View.IsBusyPresented);
        leaf.BusyOverlayTemplate = new DataTemplate(() => new Label { Text = "inner" });
        Assert.Equal("inner", Assert.IsType<Label>(root.View.BusyContent).Text); Assert.Null(inherited.Parent);
        leaf.BusyOverlayTemplate = null;
        Assert.Equal("outer", Assert.IsType<Label>(root.View.BusyContent).Text);
        Assert.Same(outer.BindingContext, root.View.BusyContent!.BindingContext);
    }

    [Fact]
    public async Task Busy_visibility_follows_selected_leaf_and_does_not_veto_navigation()
    {
        var root = await Open<Tabs>(); var tabs = (Tabs)root.Current!.ViewModel;
        var first = (Leaf)root.Deepest.Current!.ViewModel; first.IsBusy = true;
        Assert.True(root.View.IsBusyPresented);
        Success(await tabs.Navigation.Select("archive", cancellationToken: Token));
        Assert.False(root.View.IsBusyPresented);
        var second = (Leaf)root.Deepest.Current!.ViewModel; second.IsBusy = true;
        Assert.True(root.View.IsBusyPresented); second.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await tabs.Navigation.Select("open", cancellationToken: Token)).Status);
        Assert.True(root.View.IsBusyPresented); second.Allowed = true; second.IsBusy = false;
        Success(await tabs.Navigation.Select("open", cancellationToken: Token));
        Assert.True(root.View.IsBusyPresented); first.IsBusy = false;
    }

    [Fact]
    public async Task Plain_roots_support_custom_busy_content_and_windows_are_independent()
    {
        var first = await Open<Leaf>(); var second = await Open<Leaf>();
        first.Current!.View.BusyOverlayTemplate = new DataTemplate(() => new Label { Text = "Brand" });
        ((Leaf)first.Current.ViewModel).IsBusy = true;
        Assert.IsType<Label>(first.View.BusyContent); Assert.True(first.View.IsBusyPresented);
        Assert.Null(second.View.BusyContent); Assert.False(second.View.IsBusyPresented);
        ((Leaf)second.Current!.ViewModel).IsBusy = true;
        Assert.IsType<ActivityIndicator>(second.View.BusyContent);
        ((Leaf)first.Current.ViewModel).IsBusy = false; ((Leaf)second.Current.ViewModel).IsBusy = false;
    }

    [Fact]
    public async Task Modal_has_its_own_busy_owner_and_covered_root_stops_its_indicator()
    {
        var root = await Open<Leaf>(); var model = (Leaf)root.Current!.ViewModel;
        model.IsBusy = true;
        var indicator = Assert.IsType<ActivityIndicator>(root.View.BusyContent);
        Success(await model.Navigation.NavigateTo<Modal>(cancellationToken: Token));
        var modal = root.Host.CurrentContentNavigation!;
        Assert.False(root.View.IsBusyPresented); Assert.False(indicator.IsRunning);
        modal.Current!.View.BusyOverlayTemplate = new DataTemplate(() => new Label());
        modal.Current.View.IsBusy = true;
        Assert.IsType<Label>(modal.View.BusyContent); Assert.True(modal.View.IsBusyPresented);
        var content = modal.View.BusyContent!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var modalModel = Assert.IsType<Modal>(modal.Current.ViewModel);
        modalModel.Guard = async () => { entered.TrySetResult(); return await release.Task; };
        var rejected = modalModel.Navigation.NavigateBack(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(modal.IsNavigating); Assert.False(modal.View.IsBusyPresented); Assert.Same(content, modal.View.BusyContent);
        release.TrySetResult(false);
        Assert.Equal(NavigationStatus.GuardRejected, (await rejected).Status);
        Assert.True(modal.View.IsBusyPresented); Assert.Same(content, modal.View.BusyContent);
        modalModel.Guard = null;
        Success(await ((Modal)modal.Current.ViewModel).Navigation.NavigateBack(cancellationToken: Token));
        Assert.Null(content.Parent); Assert.False(modal.View.IsBusyPresented);
        Assert.True(root.View.IsBusyPresented); Assert.Same(indicator, root.View.BusyContent); Assert.True(indicator.IsRunning);
        model.IsBusy = false;
    }

    [Fact]
    public async Task Typed_loading_template_receives_owner_resources_and_reuses_content_when_type_changes()
    {
        var root = await Open<Leaf>();
        var view = Assert.IsType<LeafView>(root.Current!.View);
        var creations = 0;
        view.Resources["LoadingColor"] = Colors.Orange;
        view.BusyOverlayTemplate = new DataTemplate(() => new Label { Text = "legacy" });
        view.LoadingPresentationTemplate = new DataTemplate(() =>
        {
            creations++;
            var label = new Label();
            label.SetDynamicResource(Label.TextColorProperty, "LoadingColor");
            return label;
        });

        Assert.Null(root.View.BusyContent);
        view.IsBusy = true;
        var content = Assert.IsType<Label>(root.View.BusyContent);
        var state = Assert.IsType<LoadingPresentationContext>(content.BindingContext);
        Assert.Equal(LoadingType.Loading, state.LoadingType);
        Assert.Same(view.BindingContext, state.Owner);
        Assert.Equal(Colors.Orange, content.TextColor);
        Assert.Equal(1, creations);

        view.IsBusy = false;
        using (view.BeginLoading(LoadingType.LogingIn))
        {
            Assert.Same(content, root.View.BusyContent);
            Assert.Same(state, content.BindingContext);
            Assert.Equal(LoadingType.LogingIn, state.LoadingType);
            view.Resources["LoadingColor"] = Colors.Purple;
            Assert.Equal(Colors.Purple, content.TextColor);
        }
        Assert.False(root.View.IsBusyPresented);
        Assert.Equal(1, creations);
    }

    [Fact]
    public async Task Managed_loading_scopes_overlap_in_both_disposal_orders_and_force_hide_detaches_stale_handles()
    {
        var root = await Open<Leaf>();
        var view = Assert.IsType<LeafView>(root.Current!.View);
        view.LoadingPresentationTemplate = new DataTemplate(() => new Label());

        var first = view.BeginLoading(LoadingType.Loading);
        var second = view.BeginLoading(LoadingType.LogingIn);
        var state = Assert.IsType<LoadingPresentationContext>(root.View.BusyContent!.BindingContext);
        Assert.Equal(LoadingType.LogingIn, state.LoadingType);
        first.Dispose(); first.Dispose();
        Assert.True(root.View.IsBusyPresented);
        Assert.Equal(LoadingType.LogingIn, state.LoadingType);
        second.Dispose(); second.Dispose();
        Assert.False(root.View.IsBusyPresented);

        first = view.BeginLoading(LoadingType.Loading);
        second = view.BeginLoading(LoadingType.LogingIn);
        second.Dispose();
        Assert.True(root.View.IsBusyPresented);
        Assert.Equal(LoadingType.Loading, state.LoadingType);
        view.EndLoading();
        Assert.False(root.View.IsBusyPresented);
        first.Dispose();
        Assert.False(root.View.IsBusyPresented);
    }

    [Fact]
    public async Task Navigation_hides_customer_loading_before_guard_work_and_restores_same_content_after_rejection()
    {
        var root = await Open<Tabs>();
        var tabs = Assert.IsType<Tabs>(root.Current!.ViewModel);
        var source = Assert.IsType<LeafView>(root.Deepest.Current!.View);
        root.Current.View.LoadingPresentationTemplate = new DataTemplate(() => new Label { Text = "Customer loading" });
        using var loading = source.BeginLoading(LoadingType.LogingIn);
        var content = Assert.IsType<Label>(root.View.BusyContent);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ViewModel.Guard = async () => { entered.TrySetResult(); return await release.Task; };

        var rejected = tabs.Navigation.Select("archive", cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(root.IsNavigating);
        Assert.False(root.View.IsBusyPresented);
        Assert.Same(content, root.View.BusyContent);
        Assert.NotNull(content.Parent);
        release.TrySetResult(false);
        Assert.Equal(NavigationStatus.GuardRejected, (await rejected).Status);
        Assert.False(root.IsNavigating);
        Assert.True(root.View.IsBusyPresented);
        Assert.Same(content, root.View.BusyContent);

        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ViewModel.Guard = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("guard failure");
        };
        var failed = tabs.Navigation.Select("archive", cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(root.View.IsBusyPresented); Assert.Same(content, root.View.BusyContent);
        release.TrySetResult(true);
        Assert.Equal(NavigationStatus.Failed, (await failed).Status);
        Assert.True(root.View.IsBusyPresented); Assert.Same(content, root.View.BusyContent);

        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ViewModel.Guard = async () => { entered.TrySetResult(); return await release.Task; };
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = tabs.Navigation.Select("archive", cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(root.View.IsBusyPresented); Assert.Same(content, root.View.BusyContent);
            cancellation.Cancel(); release.TrySetResult(true);
            Assert.Equal(NavigationStatus.Cancelled, (await cancelled).Status);
            Assert.True(root.View.IsBusyPresented); Assert.Same(content, root.View.BusyContent);
        }

        source.ViewModel.Guard = null;
        var succeeded = tabs.Navigation.Select("archive", cancellationToken: Token);
        Assert.False(root.View.IsBusyPresented);
        Success(await succeeded);
        Assert.False(root.View.IsBusyPresented);
        Assert.Equal("archive", root.Deepest.Current!.ViewModel is Leaf leaf ? leaf.Filter : null);
    }

    [Fact]
    public async Task Navigation_does_not_create_loading_presentation_without_customer_busy_state()
    {
        var root = await Open<Tabs>();
        var tabs = Assert.IsType<Tabs>(root.Current!.ViewModel);
        var source = Assert.IsType<LeafView>(root.Deepest.Current!.View);
        var creations = 0;
        root.Current.View.LoadingPresentationTemplate = new DataTemplate(() =>
        {
            creations++;
            return new Label { Text = "Customer loading" };
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ViewModel.Guard = async () => { entered.TrySetResult(); return await release.Task; };

        var navigation = tabs.Navigation.Select("archive", cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(root.IsNavigating);
        Assert.False(root.View.IsBusyPresented);
        Assert.Null(root.View.BusyContent);
        Assert.Equal(0, creations);

        release.TrySetResult(true);
        Success(await navigation);
        Assert.False(root.View.IsBusyPresented);
        Assert.Null(root.View.BusyContent);
        Assert.Equal(0, creations);
    }

    [Fact]
    public async Task Dismissal_clears_typed_context_and_managed_loading_tokens()
    {
        var root = await Open<Leaf>();
        var view = Assert.IsType<LeafView>(root.Current!.View);
        view.LoadingPresentationTemplate = new DataTemplate(() => new Label());
        var handle = view.BeginLoading(LoadingType.LogingIn);
        var content = root.View.BusyContent!;
        var state = Assert.IsType<LoadingPresentationContext>(content.BindingContext);
        Assert.Same(view.BindingContext, state.Owner);

        Success(await factory.SetRoot<Leaf>(root.Window, options: Enforced, cancellationToken: Token));
        Assert.Null(content.Parent);
        Assert.Null(content.BindingContext);
        Assert.Null(state.Owner);
        handle.Dispose();
        Assert.False(root.View.IsBusyPresented);
    }
}
