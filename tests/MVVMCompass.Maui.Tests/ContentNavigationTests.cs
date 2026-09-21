using System.Collections.ObjectModel;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;
using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Maui.Tests;

[Collection("Application roots")]
public sealed class ContentNavigationTests : IAsyncDisposable
{
    private readonly Application? previous = Application.Current;
    private readonly TestApplication application = new();
    private readonly List<MauiNavigationHost> hosts = [];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        try { foreach (var host in hosts) await host.DisposeAsync(); }
        finally { Application.Current = previous; }
    }

    [Fact]
    public async Task Body_push_and_back_preserve_toolbar_and_restore_retained_root()
    {
        var root = new Model();
        var (host, context) = await CreateAsync(Plain(root));
        var toolbar = context.View.Toolbar;
        var body = context.Current!.View;
        var detail = new Model();
        var pushed = await context.PushAsync(Factory(detail), cancellationToken: Token);
        Success(pushed);
        Assert.Same(toolbar, context.View.Toolbar);
        Assert.Same(pushed.Value!.View, context.View.Presenter.CurrentContent);
        Assert.Same(pushed.Value.Entry, host.CurrentEntry);
        Assert.Equal(1, root.Deactivations);
        Assert.False(root.IsDismissed);
        Assert.Equal(NavigationLeadingAction.Back, context.LeadingAction);
        var returned = await detail.Navigator!.BackAsync(Token);
        Success(returned);
        Assert.Same(toolbar, context.View.Toolbar);
        Assert.Same(body, context.View.Presenter.CurrentContent);
        Assert.Equal(2, root.Activations);
        Assert.Equal(1, detail.Dismissals);
        Assert.True(detail.IsDismissed);
        Assert.Equal(NavigationLeadingAction.None, context.LeadingAction);
        Assert.Equal(1, detail.GuardCalls);
        Assert.Equal(0, detail.OtherGuardCalls);
    }

    [Fact]
    public async Task CanNavigate_is_authoritative_and_rejection_preserves_body_and_actions()
    {
        var (_, context) = await CreateAsync(Plain(new Model()));
        var detail = new Model { Allowed = false };
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        var current = context.Current;
        var body = context.View.Presenter.CurrentContent;
        var result = await context.BackAsync(cancellationToken: Token);
        Assert.Equal(NavigationStatus.GuardRejected, result.Status);
        Assert.False(result.HasCommitted);
        Assert.Same(current, context.Current);
        Assert.Same(body, context.View.Presenter.CurrentContent);
        Assert.Equal(0, detail.Deactivations);
        Assert.Equal(0, detail.Dismissals);
        Assert.Equal(1, detail.GuardCalls);
        Assert.Equal(0, detail.OtherGuardCalls);
        Assert.False(context.IsNavigating);
        detail.Allowed = true;
        Success(await context.BackAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task Pending_guard_can_be_cancelled_without_changing_the_screen()
    {
        var (_, context) = await CreateAsync(Plain(new Model()));
        var started = Signal(); var release = Signal();
        var detail = new Model { Guard = async () => { started.TrySetResult(); await release.Task; return true; } };
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var back = context.BackAsync(cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(context.IsNavigating);
        cancellation.Cancel(); release.SetResult();
        var result = await back.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(NavigationStatus.Cancelled, result.Status);
        Assert.Same(detail, context.Current!.ViewModel);
        Assert.False(detail.IsDismissed);
        Assert.False(context.IsNavigating);
    }

    [Fact]
    public async Task Guard_failure_and_reentrancy_leave_scope_usable()
    {
        var (_, context) = await CreateAsync(Plain(new Model()));
        var detail = new Model { Guard = () => throw new InvalidOperationException("guard failed") };
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        var failed = await context.BackAsync(cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, failed.Status);
        Assert.Same(detail, context.Current!.ViewModel);
        detail.Guard = async () =>
        {
            Assert.Equal(NavigationStatus.Reentrant, (await detail.Navigator!.BackAsync(Token)).Status);
            return false;
        };
        Assert.Equal(NavigationStatus.GuardRejected, (await context.BackAsync(cancellationToken: Token)).Status);
        detail.Guard = null;
        Success(await context.BackAsync(cancellationToken: Token));
    }

    [Theory]
    [InlineData(TabBarPosition.Top)]
    [InlineData(TabBarPosition.Bottom)]
    [InlineData(TabBarPosition.Left)]
    [InlineData(TabBarPosition.Right)]
    public async Task Edge_selectors_reserve_space_before_measuring_the_screen_body(TabBarPosition position)
    {
        var (_, context) = await CreateAsync(new([new("a", "A", Factory(new Model())),
            new("b", "B", Factory(new Model()))], NavigationPresentation.Tabs));
        context.View.TabBarPosition = position;
        var horizontal = position is TabBarPosition.Top or TabBarPosition.Bottom;
        var selector = horizontal ? context.View.TabSelector : context.View.RailSelector;
        var grid = Assert.IsType<Grid>(selector.Parent);
        var row = Grid.GetRow(selector); var column = Grid.GetColumn(selector);
        // Use the navigation grid's actual tracks with a body that fills its measure
        // constraint, as a native scrollable screen does. No native window is needed.
        var body = new MeasureProbe((width, height) => new(width, height));
        var edge = new MeasureProbe((width, height) => horizontal ? new(width, 52) : new(100, height));
        grid.Children.Clear();
        grid.Add(body, 1, 1); grid.Add(edge, column, row);
        var manager = new GridLayoutManager(grid);
        var measured = manager.Measure(752, 423);
        manager.ArrangeChildren(new(0, 0, 752, 423));

        Assert.True(measured.Width <= 752 && measured.Height <= 423, $"Grid exceeds its viewport: {measured}");
        Assert.True(edge.Arranged.Right <= 752 && edge.Arranged.Bottom <= 423, $"Selector is outside its viewport: {edge.Arranged}");
        Assert.Equal(horizontal ? 371 : 423, body.Arranged.Height);
        Assert.Equal(horizontal ? 752 : 652, body.Arranged.Width);
    }

    [Theory]
    [InlineData(NavigationPresentation.Tabs)]
    [InlineData(NavigationPresentation.Rail)]
    [InlineData(NavigationPresentation.Flyout)]
    public async Task Selectors_preserve_separate_histories_and_duplicate_model_types(NavigationPresentation presentation)
    {
        var first = new Model(); var second = new Model();
        var (_, context) = await CreateAsync(new([new("first", "First", Factory(first)), new("second", "Second", Factory(second))], presentation));
        var detail = new Model();
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        var firstDetail = context.Current;
        Success(await context.SelectAsync("second", cancellationToken: Token));
        Assert.Same(second, context.Current!.ViewModel);
        Assert.False(context.CanGoBack);
        Success(await context.SelectAsync("first", cancellationToken: Token));
        Assert.Same(firstDetail, context.Current);
        Assert.True(context.CanGoBack);
        Assert.False(detail.IsDismissed);
        Assert.Equal(2, detail.Activations);
        detail.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await context.SelectAsync("second", cancellationToken: Token)).Status);
        Assert.Equal("first", context.SelectedDestinationId);
        Assert.Same(firstDetail, context.Current);
    }

    [Fact]
    public async Task Nested_flyout_tabs_keep_the_same_detail_page_and_automatic_context()
    {
        var definition = new NavigationDefinition([
            new("group", "Workspace", new NavigationDestination[] { new("a", "A", Factory(new Model())), new("b", "B", Factory(new Model())) }),
            new("other", "Other", Factory(new Model()))], NavigationPresentation.Flyout);
        var (host, context) = await CreateAsync(definition);
        var flyout = Assert.IsType<NavigationFlyoutPage>(host.Window.Page);
        var detailPage = flyout.Detail;
        Assert.Equal(2, context.TabItems.Count);
        Assert.Equal(NavigationLeadingAction.Menu, context.LeadingAction);
        Success(await context.SelectAsync("b", cancellationToken: Token));
        Success(await context.PushAsync(Factory(new Model()), cancellationToken: Token));
        var detail = context.Current;
        Assert.Equal(NavigationLeadingAction.Back, context.LeadingAction);
        Success(await context.SelectAsync("other", cancellationToken: Token));
        Assert.Empty(context.TabItems);
        Success(await context.SelectAsync("group", cancellationToken: Token));
        Assert.Equal("b", context.SelectedDestinationId);
        Assert.Same(detail, context.Current);
        Assert.Same(detailPage, flyout.Detail);
    }

    [Fact]
    public async Task Hidden_and_foreign_screen_origins_cannot_navigate_the_visible_scope()
    {
        var (_, first) = await CreateAsync(Plain(new Model()));
        var stale = first.Current!.View.Navigator;
        Success(await first.PushAsync(Factory(new Model()), cancellationToken: Token));
        Assert.Equal(NavigationStatus.InvalidOrigin, (await stale.BackAsync(Token)).Status);
        var (_, second) = await CreateAsync(Plain(new Model()));
        Assert.Equal(NavigationStatus.InvalidOrigin, (await second.BackAsync(new() { Origin = first.Current!.Entry }, Token)).Status);
        Assert.Same(second.Current!.View, second.View.Presenter.CurrentContent);
    }

    [Fact]
    public async Task Failed_view_construction_cleans_candidate_and_preserves_root()
    {
        var root = new Model(); var candidate = new Model();
        var (_, context) = await CreateAsync(Plain(root));
        var original = context.Current;
        var factory = ScreenFactory.Create(() => candidate, _ => throw new InvalidOperationException("view failed"));
        var result = await context.PushAsync(factory, cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.Same(original, context.Current);
        Assert.False(root.IsDismissed);
        Assert.Equal(0, root.Deactivations);
        Assert.True(candidate.IsDismissed);
        Assert.Equal(1, candidate.Dismissals);
    }

    [Fact]
    public async Task Root_replacement_disposes_every_retained_destination_and_clears_toolbar_commands()
    {
        var a = new Model(); var b = new Model(); var child = new Model();
        var (host, context) = await CreateAsync(new([new("a", "A", Factory(a)), new("b", "B", Factory(b))], NavigationPresentation.Tabs));
        Success(await context.PushAsync(Factory(child), cancellationToken: Token));
        Success(await context.SelectAsync("b", cancellationToken: Token));
        Success(await host.ReplaceContentRootAsync(new(Plain(new Model())), view => view.Presenter.AnimateNavigation = false, Token));
        Assert.True(context.IsClosed);
        Assert.Null(context.View.Presenter.CurrentContent);
        foreach (var model in new[] { a, b, child }) { Assert.True(model.IsDismissed); Assert.Equal(1, model.Dismissals); Assert.Null(model.Navigator); }
    }

    [Fact]
    public async Task Toolbar_center_defaults_overrides_and_empty_actions_restore_on_back()
    {
        var defaultCenter = new Label { Text = "Host" };
        var (host, context) = await CreateAsync(Plain(new Model()), view => view.ToolbarDefaults = new()
        { CenterContent = defaultCenter, RightItems = new() { new() { Text = "Host action", Command = new Command(() => { }) } } });
        var toolbar = context.View.Toolbar;
        Assert.Contains(defaultCenter, Descendants(toolbar));
        var customCenter = new Entry { Text = "Screen" };
        var custom = new NavigationToolbarDefinition { CenterContent = customCenter, RightItems = new() };
        Success(await context.PushAsync(Factory(new Model(), custom), cancellationToken: Token));
        Assert.Contains(customCenter, Descendants(toolbar));
        Assert.DoesNotContain(defaultCenter, Descendants(toolbar));
        Assert.DoesNotContain(Descendants(toolbar).OfType<Button>(), button => button.Text == "Host action");
        Success(await context.BackAsync(cancellationToken: Token));
        Assert.Contains(defaultCenter, Descendants(toolbar));
        Assert.Contains(Descendants(toolbar).OfType<Button>(), button => button.Text == "Host action");
        Assert.Same(context.Current!.Entry, host.CurrentEntry);
    }

    [Fact]
    public async Task Detached_toolbar_actions_are_disabled_and_live_actions_follow_CanExecute()
    {
        var executions = 0; var enabled = true;
        var command = new Command(() => executions++, () => enabled);
        var definition = new NavigationToolbarDefinition { RightItems = new() { new() { Text = "Action", Command = command } } };
        var (_, context) = await CreateAsync(Plain(new Model()), view => view.ToolbarDefaults = definition);
        var rendered = Descendants(context.View.Toolbar).OfType<Button>().Single(button => button.Text == "Action");
        Assert.True(rendered.Command!.CanExecute(null));
        enabled = false; command.ChangeCanExecute();
        Assert.False(rendered.Command.CanExecute(null));
        enabled = true; command.ChangeCanExecute();
        var oldCommand = rendered.Command;
        Success(await context.PushAsync(Factory(new Model(), new() { RightItems = new() }), cancellationToken: Token));
        Assert.False(oldCommand.CanExecute(null));
        oldCommand.Execute(null);
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task Platform_back_awaits_the_same_CanNavigate_guard()
    {
        var (host, context) = await CreateAsync(Plain(new Model()));
        var detail = new Model { Allowed = false };
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        Assert.True(host.Window.Page!.SendBackButtonPressed());
        await context.NativeBackCompletion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Same(detail, context.Current!.ViewModel);
        Assert.Equal(1, detail.GuardCalls);
        detail.Allowed = true;
        Assert.True(host.Window.Page.SendBackButtonPressed());
        await context.NativeBackCompletion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(detail.IsDismissed);
        Assert.False(context.CanGoBack);
    }

    [Fact]
    public void Standalone_toolbar_uses_bindable_actions_without_a_navigation_scope()
    {
        var executions = 0;
        var toolbar = new NavigationToolbar { Definition = new() { Title = "Standalone", RightItems = new()
        { new() { Text = "Action", Command = new Command(() => executions++) } } } };
        var button = Descendants(toolbar).OfType<Button>().Single(button => button.Text == "Action");
        Assert.True(button.Command!.CanExecute(null));
        button.Command.Execute(null);
        Assert.Equal(1, executions);
        Assert.False(toolbar.LeadingCommand.CanExecute(null));
    }

    [Fact]
    public async Task Window_host_back_and_modal_close_use_content_history_and_CanNavigate()
    {
        var (host, context) = await CreateAsync(Plain(new Model()));
        var detail = new Model { Allowed = false };
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        Assert.Equal(NavigationStatus.GuardRejected, (await host.BackAsync(cancellationToken: Token)).Status);
        Assert.Same(detail, context.Current!.ViewModel);
        detail.Allowed = true;
        Success(await host.BackAsync(new(null) { Origin = context.Current.Entry }, cancellationToken: Token));
        Assert.True(detail.IsDismissed);
        var modalModel = new Model { Allowed = false };
        Success(await context.OpenModalAsync(Plain(modalModel), view => view.Presenter.AnimateNavigation = false, cancellationToken: Token));
        Assert.Equal(NavigationStatus.GuardRejected, (await host.CloseModalAsync(cancellationToken: Token)).Status);
        Assert.Single(host.Window.Navigation.ModalStack);
        modalModel.Allowed = true;
        Success(await host.CloseModalAsync(cancellationToken: Token));
        Assert.True(context.IsActive);
    }

    [Fact]
    public async Task Native_modal_pop_is_cancelled_then_reissued_only_after_CanNavigate_allows_it()
    {
        var (host, context) = await CreateAsync(Plain(new Model()));
        var model = new Model { Allowed = false };
        var result = await context.OpenModalAsync(Plain(model), view => view.Presenter.AnimateNavigation = false, cancellationToken: Token);
        Success(result);
        var modal = result.Value!;
        await host.Window.Navigation.PopModalAsync(false);
        await modal.NativeBackCompletion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Single(host.Window.Navigation.ModalStack);
        Assert.Same(modal, host.CurrentContentNavigation);
        Assert.Equal(1, model.GuardCalls);
        Assert.False(model.IsDismissed);
        model.Allowed = true;
        await host.Window.Navigation.PopModalAsync(false);
        await modal.NativeBackCompletion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Empty(host.Window.Navigation.ModalStack);
        Assert.True(model.IsDismissed);
        Assert.True(context.IsActive);
        Assert.Equal(2, model.GuardCalls);
    }

    [Fact]
    public async Task Modal_back_and_close_restore_the_covered_custom_scope()
    {
        var (host, context) = await CreateAsync(Plain(new Model()));
        var root = context.Current;
        var model = new Model();
        var result = await context.OpenModalAsync(Plain(model), view => view.Presenter.AnimateNavigation = false, cancellationToken: Token)
            .WaitAsync(TimeSpan.FromSeconds(10), Token);
        Success(result);
        var modal = result.Value!;
        Assert.False(context.IsActive);
        Assert.True(modal.IsActive);
        Assert.Same(modal, host.CurrentContentNavigation);
        Assert.Equal(NavigationLeadingAction.Close, modal.LeadingAction);
        Assert.True(model.IsModal);
        Assert.False(root!.ViewModel.IsModal);
        var detail = new Model();
        Success(await modal.PushAsync(Factory(detail), cancellationToken: Token));
        Assert.True(detail.IsModal);
        Assert.Equal(NavigationLeadingAction.Back, modal.LeadingAction);
        Success(await modal.BackAsync(cancellationToken: Token));
        model.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await modal.BackAsync(cancellationToken: Token)).Status);
        Assert.Single(host.Window.Navigation.ModalStack);
        model.Allowed = true;
        Success(await modal.CloseModalAsync(cancellationToken: Token));
        Assert.Empty(host.Window.Navigation.ModalStack);
        Assert.True(model.IsDismissed);
        Assert.True(context.IsActive);
        Assert.Same(root, context.Current);
        Assert.Same(context, host.CurrentContentNavigation);
    }

    [Fact]
    public async Task Failed_deactivation_cleans_uninstalled_candidate_before_reactivating_root()
    {
        var root = new Model(); var candidate = new Model();
        var (_, context) = await CreateAsync(Plain(root));
        root.Deactivate = () => throw new InvalidOperationException("deactivation failed");
        var result = await context.PushAsync(Factory(candidate), cancellationToken: Token);
        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.True(candidate.IsDismissed);
        Assert.Same(root, context.Current!.ViewModel);
        Assert.Equal(NavigationEntryState.Active, context.Current.Entry.State);
        Assert.False(root.IsDismissed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_modal_deactivation_recovers_even_when_candidate_cleanup_throws(bool failCleanup)
    {
        var root = new Model(); var candidate = new Model();
        var (_, context) = await CreateAsync(Plain(root));
        var deactivationError = new InvalidOperationException("Deactivation failed");
        var cleanupError = new InvalidOperationException("Cleanup failed");
        root.Deactivate = () => throw deactivationError;
        if (failCleanup) candidate.Dismiss = () => throw cleanupError;

        var result = await context.OpenModalAsync(Plain(candidate), cancellationToken: Token);

        Assert.Equal(NavigationStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        var errors = result.Error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [result.Error];
        Assert.Contains(deactivationError, errors);
        if (failCleanup) Assert.Contains(cleanupError, errors);
        Assert.True(candidate.IsDismissed);
        Assert.Equal(1, candidate.Dismissals);
        Assert.True(context.IsActive);
        Assert.Empty(context.Window.Navigation.ModalStack);
        root.Deactivate = null;
        Success(await context.PushAsync(Factory(new Model()), cancellationToken: Token));
    }

    [Fact]
    public async Task Reselect_is_a_no_op_and_flyout_back_closes_only_the_drawer()
    {
        var model = new Model();
        var (host, context) = await CreateAsync(new([new("root", "Root", Factory(model))], NavigationPresentation.Flyout));
        context.ToggleFlyout();
        Assert.True(context.IsFlyoutOpen);
        Assert.True(host.Window.Page!.SendBackButtonPressed());
        await context.NativeBackCompletion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(context.IsFlyoutOpen);
        Assert.Equal(0, model.GuardCalls);
        Success(await context.SelectAsync("root", cancellationToken: Token));
        Assert.Equal(1, model.Activations);
        Assert.Equal(0, model.GuardCalls);
    }

    [Fact]
    public async Task Removing_an_active_group_guards_all_its_screens_before_changing_selection()
    {
        var a = new Model(); var b = new Model(); var detail = new Model(); var c = new Model();
        var (_, context) = await CreateAsync(new([
            new("group", "Group", new NavigationDestination[] { new("a", "A", Factory(a)), new("b", "B", Factory(b)) }),
            new("c", "C", Factory(c))], NavigationPresentation.Flyout));
        Success(await context.SelectAsync("b", cancellationToken: Token));
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        List<string> guards = [];
        a.Guard = () => { guards.Add("a"); return Task.FromResult(a.Allowed); };
        b.Guard = () => { guards.Add("b"); return Task.FromResult(true); };
        detail.Guard = () => { guards.Add("detail"); return Task.FromResult(true); };
        a.Allowed = false;
        var toolbar = context.View.Toolbar;
        var group = context.MenuItems[0];
        var denied = await context.RemoveDestinationAsync("group", cancellationToken: Token);
        Assert.Equal(NavigationStatus.GuardRejected, denied.Status);
        Assert.Equal(new[] { "detail", "b", "a" }, guards);
        Assert.Same(detail, context.Current!.ViewModel);
        Assert.False(detail.IsDismissed);
        Assert.Equal(2, context.MenuItems.Count);
        a.Allowed = true; guards.Clear();
        Success(await context.RemoveDestinationAsync("group", cancellationToken: Token));
        Assert.Equal(new[] { "detail", "b", "a" }, guards);
        Assert.Same(c, context.Current!.ViewModel);
        Assert.Same(toolbar, context.View.Toolbar);
        Assert.Single(context.Destinations);
        Assert.True(a.IsDismissed && b.IsDismissed && detail.IsDismissed);
        Assert.False(group.SelectCommand.CanExecute(null));
        Success(await context.SelectDefaultAsync(cancellationToken: Token));
        Assert.Equal(NavigationStatus.Failed, (await context.RemoveDestinationAsync("c", cancellationToken: Token)).Status);
        Assert.False(c.IsDismissed);
    }

    [Fact]
    public async Task Destination_reset_guards_and_recreates_only_the_requested_history()
    {
        List<Model> roots = [];
        var factory = ScreenFactory.Create(() => { var model = new Model(); roots.Add(model); return model; }, model => new ViewBase<Model>(model));
        var b = new Model();
        var (_, context) = await CreateAsync(new([new("a", "A", factory), new("b", "B", Factory(b))], NavigationPresentation.Tabs));
        var first = roots.Single(); var detail = new Model();
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        first.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await context.ResetDestinationAsync("a", cancellationToken: Token)).Status);
        Assert.False(detail.IsDismissed);
        first.Allowed = true;
        Success(await context.ResetDestinationAsync("a", cancellationToken: Token));
        Assert.True(first.IsDismissed && detail.IsDismissed);
        Assert.Same(roots[1], context.Current!.ViewModel);
        Success(await context.SelectAsync("b", cancellationToken: Token));
        var bGuardCalls = b.GuardCalls;
        Success(await context.ResetDestinationAsync("a", cancellationToken: Token));
        Assert.True(roots[1].IsDismissed);
        Assert.Equal(bGuardCalls, b.GuardCalls);
        Assert.Same(b, context.Current!.ViewModel);
        Assert.Equal(2, roots.Count);
        Success(await context.SelectDefaultAsync(cancellationToken: Token));
        Assert.Equal(3, roots.Count);
        Assert.Same(roots[2], context.Current!.ViewModel);
    }

    [Fact]
    public async Task Removing_last_tab_removes_empty_group_and_recovers_the_default()
    {
        var (_, context) = await CreateAsync(new([
            new("group", "Group", new NavigationDestination[] { new("a", "A", Factory(new Model())) }),
            new("b", "B", Factory(new Model()))], NavigationPresentation.Flyout, "a"));
        Success(await context.RemoveDestinationAsync("a", cancellationToken: Token));
        Assert.Equal("b", context.SelectedMenuId);
        Assert.Empty(context.TabItems);
        Assert.Single(context.MenuItems);
        Success(await context.SelectDefaultAsync(cancellationToken: Token));
        Assert.Equal("b", context.SelectedDestinationId);
        var item = context.MenuItems[0];
        item.IsVisible = false;
        Assert.False(item.SelectCommand.CanExecute(null));
        item.IsVisible = true; item.IsEnabled = false;
        Assert.False(item.SelectCommand.CanExecute(null));
        item.IsEnabled = true;
        Assert.True(item.SelectCommand.CanExecute(null));
    }

    [Fact]
    public async Task Host_and_screen_content_keep_distinct_binding_owners_when_host_context_changes()
    {
        var shared = new Model { Caption = "Host" };
        var root = new Model { Caption = "Root" };
        var header = BoundLabel(); var footer = BoundLabel(); var defaultCenter = BoundLabel();
        var (_, context) = await CreateAsync(Plain(root), view =>
        {
            view.BindingContext = shared;
            view.HeaderContent = header; view.FooterContent = footer;
            view.ToolbarDefaults.CenterContent = defaultCenter;
        });
        Assert.Equal("Host", defaultCenter.Text);
        Assert.Equal("Host", header.Text);
        var detail = new Model { Caption = "Detail" }; var center = BoundLabel();
        Success(await context.PushAsync(Factory(detail, new() { CenterContent = center }), cancellationToken: Token));
        Assert.Same(detail, center.BindingContext);
        Assert.Equal("Detail", center.Text);
        context.View.BindingContext = new Model { Caption = "Replacement host" };
        Assert.Equal("Replacement host", header.Text);
        Assert.Equal("Replacement host", footer.Text);
        Assert.Equal("Detail", center.Text);
        detail.Caption = "Edited detail";
        Assert.Equal("Edited detail", center.Text);
        Success(await context.BackAsync(cancellationToken: Token));
        Assert.Equal("Replacement host", defaultCenter.Text);
    }

    [Fact]
    public async Task Center_templates_retain_input_and_recreate_only_when_the_template_changes()
    {
        var root = new Model { Caption = "Root input" }; var creations = 0;
        var definition = new NavigationToolbarDefinition { CenterContentTemplate = new DataTemplate(() =>
        {
            creations++;
            var entry = new Entry(); entry.SetBinding(Entry.TextProperty, nameof(Model.Caption), BindingMode.TwoWay);
            return entry;
        }) };
        var (_, context) = await CreateAsync(new([new("root", "Root", Factory(root, definition))]));
        var center = Assert.Single(Descendants(context.View.Toolbar).OfType<Entry>());
        Assert.Equal("Root input", center.Text);
        center.Text = "Retained edit";
        Success(await context.PushAsync(Factory(new Model(), new() { Title = "Detail" }), cancellationToken: Token));
        Success(await context.BackAsync(cancellationToken: Token));
        Assert.Same(center, Assert.Single(Descendants(context.View.Toolbar).OfType<Entry>()));
        Assert.Equal("Retained edit", root.Caption);
        Assert.Equal(1, creations);
        definition.CenterContentTemplate = new DataTemplate(() => BoundLabel());
        Assert.DoesNotContain(center, Descendants(context.View.Toolbar));
        Assert.Contains(Descendants(context.View.Toolbar).OfType<Label>(), label => label.Text == "Retained edit");
    }

    [Fact]
    public async Task Explicit_center_binding_context_is_preserved()
    {
        var independent = new Model { Caption = "Independent" };
        var center = BoundLabel(); center.BindingContext = independent;
        var (_, context) = await CreateAsync(new([new("root", "Root", Factory(new Model(), new() { CenterContent = center }))]));
        context.View.BindingContext = new Model { Caption = "Host" };
        independent.Caption = "Changed independently";
        Assert.Same(independent, center.BindingContext);
        Assert.Equal("Changed independently", center.Text);
    }

    [Fact]
    public async Task Dismissed_toolbar_releases_the_screen_binding_owner()
    {
        var model = new Model { Caption = "Screen center" };
        var (host, context) = await CreateAsync(new([new("root", "Root", Factory(model,
            new() { CenterContentTemplate = new DataTemplate(() => BoundLabel()) }))]));
        Assert.Contains(Descendants(context.View.Toolbar), element => ReferenceEquals(element.BindingContext, model));
        Success(await host.ReplaceContentRootAsync(new(Plain(new Model())), cancellationToken: Token));
        Assert.True(model.IsDismissed);
        Assert.DoesNotContain(Descendants(context.View.Toolbar), element => ReferenceEquals(element.BindingContext, model));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dynamic_actions_update_text_state_parameter_and_accessible_label(bool templated)
    {
        var model = new Model { Caption = "Action", ActionLabel = "Perform action" };
        object? received = null;
        model.Action = new Command<object>(value => received = value);
        model.ActionParameter = new object();
        var item = BoundAction();
        var definition = new NavigationToolbarDefinition { RightItems = new() { item },
            RightItemTemplate = templated ? new DataTemplate(ActionButton) : null };
        var (_, context) = await CreateAsync(new([new("root", "Root", Factory(model, definition))]));
        var button = Action(context.View.Toolbar, "Action");
        button.Command!.Execute(button.CommandParameter);
        Assert.Same(model.ActionParameter, received);
        model.Caption = "Renamed"; model.ActionLabel = "Updated spoken label";
        Assert.Equal("Renamed", button.Text);
        Assert.Equal("Updated spoken label", SemanticProperties.GetDescription(button));
        var icon = new FontImageSource { Glyph = "+" };
        item.Icon = icon;
        Assert.Same(icon, templated ? button.ImageSource : Assert.Single(Descendants(button.Parent).OfType<Image>()).Source);
        model.ActionEnabled = false;
        Assert.False(button.IsEnabled); Assert.False(button.Command.CanExecute(button.CommandParameter));
        received = null; button.Command.Execute(button.CommandParameter); Assert.Null(received);
        model.ActionEnabled = true; model.ActionVisible = false;
        Assert.False(button.IsVisible); Assert.False(button.Command.CanExecute(button.CommandParameter));
        model.ActionVisible = true; model.ActionParameter = new object();
        Assert.True(button.IsVisible); Assert.True(button.IsEnabled);
        button.Command.Execute(button.CommandParameter); Assert.Same(model.ActionParameter, received);
        var replacementExecuted = false;
        model.Action = new Command(() => replacementExecuted = true);
        button.Command.Execute(button.CommandParameter); Assert.True(replacementExecuted);
    }

    [Fact]
    public async Task Replacing_action_collections_detaches_removed_commands_and_binds_new_items()
    {
        var model = new Model { Caption = "Bound addition", Action = new Command(() => { }) };
        var oldItems = new ObservableCollection<ToolbarButton> { new() { Text = "Old", Command = model.Action } };
        var definition = new NavigationToolbarDefinition { RightItems = oldItems };
        var (_, context) = await CreateAsync(new([new("root", "Root", Factory(model, definition))]));
        var stale = Action(context.View.Toolbar, "Old").Command!;
        definition.RightItems = new() { BoundAction() };
        Assert.False(stale.CanExecute(null));
        Assert.Same(model, definition.RightItems[0].BindingContext);
        Assert.Equal("Bound addition", Action(context.View.Toolbar, "Bound addition").Text);
        oldItems.Add(new() { Text = "Detached collection" });
        Assert.DoesNotContain(Descendants(context.View.Toolbar).OfType<Button>(), button => button.Text == "Detached collection");
        var newCommand = Action(context.View.Toolbar, "Bound addition").Command!;
        definition.RightItems.Clear();
        Assert.False(newCommand.CanExecute(null));
        Assert.DoesNotContain(Descendants(context.View.Toolbar).OfType<Button>(), button => button.Text == "Bound addition");
        definition.RightItems.Add(BoundAction());
        Assert.Equal("Bound addition", Action(context.View.Toolbar, "Bound addition").Text);
    }

    [Fact]
    public async Task Inherited_actions_keep_the_host_model_and_rebind_when_that_owner_changes()
    {
        var firstCalls = 0; var secondCalls = 0;
        var shared = new Model { Caption = "First host", Action = new Command(() => firstCalls++) };
        var (_, context) = await CreateAsync(Plain(new Model()), view =>
        { view.BindingContext = shared; view.ToolbarDefaults.RightItems = new() { BoundAction() }; });
        Success(await context.PushAsync(Factory(new Model { Caption = "Detail" }), cancellationToken: Token));
        var button = Action(context.View.Toolbar, "First host");
        button.Command!.Execute(null); Assert.Equal(1, firstCalls);
        context.View.BindingContext = new Model { Caption = "Second host", Action = new Command(() => secondCalls++) };
        Assert.Equal("Second host", button.Text);
        button.Command.Execute(null); Assert.Equal(1, secondCalls); Assert.Equal(1, firstCalls);
        Success(await context.BackAsync(cancellationToken: Token));
        Action(context.View.Toolbar, "Second host").Command!.Execute(null);
        Assert.Equal(2, secondCalls);
    }

    [Fact]
    public async Task Replacing_a_right_item_template_disables_the_old_rendered_command()
    {
        var model = new Model { Caption = "Action", Action = new Command(() => { }) };
        var definition = new NavigationToolbarDefinition { RightItems = new() { BoundAction() } };
        var (_, context) = await CreateAsync(new([new("root", "Root", Factory(model, definition))]));
        var original = Action(context.View.Toolbar, "Action");
        definition.RightItemTemplate = new DataTemplate(ActionButton);
        var replacement = Action(context.View.Toolbar, "Action");
        Assert.NotSame(original, replacement);
        Assert.False(original.Command!.CanExecute(null)); Assert.True(replacement.Command!.CanExecute(null));
        model.ActionEnabled = false; Assert.False(replacement.IsEnabled);
        definition.RightItemTemplate = null;
        Assert.False(replacement.Command.CanExecute(null));
        Assert.False(Action(context.View.Toolbar, "Action").IsEnabled);
    }

    [Fact]
    public async Task Leading_template_tracks_context_and_cannot_duplicate_a_pending_guard()
    {
        var (_, context) = await CreateAsync(Plain(new Model()), view => view.Toolbar.LeadingButtonTemplate = new DataTemplate(() =>
        {
            var button = new Button { AutomationId = "Leading template" };
            button.SetBinding(Button.TextProperty, nameof(NavigationToolbar.LeadingText));
            button.SetBinding(Button.CommandProperty, nameof(NavigationToolbar.LeadingCommand));
            return button;
        }));
        var detail = new Model();
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        var leading = Descendants(context.View.Toolbar).OfType<Button>().Single(button => button.AutomationId == "Leading template");
        Assert.Equal("Back", leading.Text);
        var entered = Signal(); var release = Signal();
        detail.Guard = async () => { entered.TrySetResult(); await release.Task; return false; };
        var pending = context.BackAsync(cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        try
        {
            Assert.False(leading.Command!.CanExecute(null));
            Assert.False(((VisualElement)leading.Parent).IsEnabled);
            Assert.Equal(1, detail.GuardCalls);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(NavigationStatus.GuardRejected, (await pending).Status);
        Assert.True(leading.Command!.CanExecute(null));
        detail.Guard = null;
        Success(await context.BackAsync(cancellationToken: Token));
        Assert.False(leading.Command.CanExecute(null));
    }

    [Fact]
    public async Task Covered_and_dismissed_toolbar_commands_cannot_execute()
    {
        var count = 0;
        var definition = new NavigationToolbarDefinition { RightItems = new() { new() { Text = "Parent", Command = new Command(() => count++) } } };
        var (host, context) = await CreateAsync(Plain(new Model()), view => view.ToolbarDefaults = definition);
        var command = Action(context.View.Toolbar, "Parent").Command!;
        var result = await context.OpenModalAsync(Plain(new Model()), view => view.Presenter.AnimateNavigation = false, cancellationToken: Token);
        Success(result);
        Assert.False(command.CanExecute(null)); command.Execute(null); Assert.Equal(0, count);
        Success(await result.Value!.CloseModalAsync(cancellationToken: Token));
        Assert.True(command.CanExecute(null)); command.Execute(null); Assert.Equal(1, count);
        Success(await host.ReplaceContentRootAsync(new(Plain(new Model())), cancellationToken: Token));
        Assert.False(command.CanExecute(null)); command.Execute(null); Assert.Equal(1, count);
    }

    [Fact]
    public async Task Toolbar_visibility_inherits_and_restores_without_becoming_a_Back_guard()
    {
        var (_, context) = await CreateAsync(Plain(new Model()), view => view.ToolbarDefaults.IsVisible = false);
        Assert.False(context.View.Toolbar.IsVisible);
        var definition = new NavigationToolbarDefinition { IsVisible = true };
        Success(await context.PushAsync(Factory(new Model(), definition), cancellationToken: Token));
        Assert.True(context.View.Toolbar.IsVisible);
        definition.IsVisible = null; Assert.False(context.View.Toolbar.IsVisible);
        Success(await context.BackAsync(cancellationToken: Token));
        Assert.False(context.View.Toolbar.IsVisible);
        context.View.ToolbarDefaults.IsVisible = true; Assert.True(context.View.Toolbar.IsVisible);
    }

    [Fact]
    public async Task Shared_panels_overlays_and_busy_state_survive_navigation_and_release_on_teardown()
    {
        var hostModel = new Model { Caption = "Shared" };
        var header = BoundLabel(); var footer = BoundLabel(); var bodyOverlay = BoundLabel(); var overlay = BoundLabel();
        var (host, context) = await CreateAsync(Plain(new Model()), view =>
        {
            view.BindingContext = hostModel; view.HeaderContent = header; view.FooterContent = footer;
            view.BodyOverlayContent = bodyOverlay; view.OverlayContent = overlay; view.IsBusy = true;
        });
        var detail = new Model { Allowed = false };
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        foreach (var content in new[] { header, footer, bodyOverlay, overlay })
        { Assert.Equal("Shared", content.Text); Assert.Contains(content, Descendants(context.View)); }
        Assert.False(((ContentView)bodyOverlay.Parent).InputTransparent);
        Assert.False(((ContentView)overlay.Parent).InputTransparent);
        var indicator = Descendants(context.View).OfType<ActivityIndicator>().Single();
        Assert.True(((VisualElement)indicator.Parent).InputTransparent);
        Assert.True(((VisualElement)indicator.Parent).IsVisible);
        Assert.Equal(NavigationStatus.GuardRejected, (await context.BackAsync(cancellationToken: Token)).Status);
        detail.Allowed = true; Success(await context.BackAsync(cancellationToken: Token));
        Assert.True(context.View.IsBusy);
        context.View.IsBusy = false; Assert.False(((VisualElement)indicator.Parent).IsVisible);
        var bodySurface = (ContentView)bodyOverlay.Parent;
        context.View.BodyOverlayContent = null;
        Assert.True(bodySurface.InputTransparent); Assert.False(bodySurface.IsVisible);
        Success(await host.ReplaceContentRootAsync(new(Plain(new Model())), cancellationToken: Token));
        foreach (var content in new[] { header, footer, bodyOverlay, overlay }) Assert.Null(content.Parent);
    }

    [Fact]
    public async Task Swipe_registration_is_opt_in_and_uses_the_same_guard()
    {
        var (_, context) = await CreateAsync(Plain(new Model()));
        Assert.Empty(context.View.Presenter.GestureRecognizers);
        var detail = new Model { Allowed = false };
        Success(await context.PushAsync(Factory(detail), cancellationToken: Token));
        context.View.IsBackSwipeEnabled = true;
        var swipe = Assert.IsType<SwipeGestureRecognizer>(Assert.Single(context.View.Presenter.GestureRecognizers));
        swipe.SendSwiped(context.View.Presenter, SwipeDirection.Right);
        await context.NativeBackCompletion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, detail.GuardCalls); Assert.Same(detail, context.Current!.ViewModel);
        context.View.IsBackSwipeEnabled = false;
        swipe.SendSwiped(context.View.Presenter, SwipeDirection.Right);
        Assert.Equal(1, detail.GuardCalls); Assert.Empty(context.View.Presenter.GestureRecognizers);
        context.View.IsBackSwipeEnabled = true; detail.Allowed = true;
        ((SwipeGestureRecognizer)Assert.Single(context.View.Presenter.GestureRecognizers)).SendSwiped(context.View.Presenter, SwipeDirection.Right);
        await context.NativeBackCompletion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(detail.IsDismissed);
    }

    [Fact]
    public async Task Selector_templates_follow_metadata_and_release_removed_items()
    {
        var (_, context) = await CreateAsync(new([new("a", "A", Factory(new Model())), new("b", "B", Factory(new Model()))], NavigationPresentation.Tabs));
        context.View.TabSelector.ItemTemplate = new DataTemplate(() =>
        {
            var button = new Button(); button.SetBinding(Button.TextProperty, nameof(NavigationItemContext.Title));
            button.SetBinding(Button.CommandProperty, nameof(NavigationItemContext.SelectCommand)); return button;
        });
        var item = context.TabItems[1];
        var button = Action(context.View.TabSelector, "B");
        Assert.Same(item, button.BindingContext);
        item.IsVisible = false; Assert.False(button.IsVisible); Assert.False(button.Command!.CanExecute(null));
        item.IsVisible = true; item.IsEnabled = false; Assert.False(button.IsEnabled);
        item.IsEnabled = true; Success(await context.SelectAsync("b", cancellationToken: Token));
        Assert.True(item.IsSelected);
        var stale = button.Command;
        Success(await context.RemoveDestinationAsync("b", cancellationToken: Token));
        Assert.False(stale.CanExecute(null)); Assert.DoesNotContain(button, Descendants(context.View.TabSelector));
    }

    [Fact]
    public async Task Pop_to_root_guards_all_details_atomically_and_cleans_each_once()
    {
        var root = new Model(); var first = new Model(); var second = new Model();
        var (_, context) = await CreateAsync(Plain(root));
        Success(await context.PushAsync(Factory(first), cancellationToken: Token));
        Success(await context.PushAsync(Factory(second), cancellationToken: Token));
        List<string> calls = [];
        second.Guard = () => { calls.Add("second"); return Task.FromResult(true); };
        first.Guard = () => { calls.Add("first"); return Task.FromResult(first.Allowed); }; first.Allowed = false;
        Assert.Equal(NavigationStatus.GuardRejected, (await context.PopToRootAsync(cancellationToken: Token)).Status);
        Assert.Equal(new[] { "second", "first" }, calls);
        Assert.Same(second, context.Current!.ViewModel); Assert.False(first.IsDismissed || second.IsDismissed);
        first.Allowed = true; calls.Clear(); var rootCalls = root.GuardCalls;
        Success(await context.PopToRootAsync(cancellationToken: Token));
        Assert.Equal(new[] { "second", "first" }, calls); Assert.Equal(rootCalls, root.GuardCalls);
        Assert.Same(root, context.Current!.ViewModel); Assert.Equal(1, first.Dismissals); Assert.Equal(1, second.Dismissals);
    }

    [Fact]
    public async Task Independent_windows_keep_commands_modals_and_teardown_scoped_to_their_owners()
    {
        var (firstHost, first) = await CreateAsync(Plain(new Model()));
        var (secondHost, second) = await CreateAsync(Plain(new Model()));
        var firstDetail = new Model(); var secondDetail = new Model();
        Success(await first.PushAsync(Factory(firstDetail), cancellationToken: Token));
        Success(await second.PushAsync(Factory(secondDetail), cancellationToken: Token));
        var secondEntry = second.Current;
        var modal = await first.OpenModalAsync(Plain(new Model()), view => view.Presenter.AnimateNavigation = false, cancellationToken: Token);
        Success(modal);
        Assert.Empty(secondHost.Window.Navigation.ModalStack);
        Assert.True(second.View.Toolbar.LeadingCommand.CanExecute(null));
        Success(await secondHost.BackAsync(cancellationToken: Token));
        Assert.True(secondDetail.IsDismissed); Assert.False(firstDetail.IsDismissed);
        Assert.Same(modal.Value, firstHost.CurrentContentNavigation);
        Success(await firstHost.ReplaceContentRootAsync(new(Plain(new Model())), cancellationToken: Token));
        Assert.True(first.IsClosed); Assert.True(modal.Value!.IsClosed);
        Assert.True(second.IsActive); Assert.NotSame(secondEntry, second.Current);
        Success(await second.PushAsync(Factory(new Model()), cancellationToken: Token));
        Assert.True(second.CanGoBack);
    }

    private static Label BoundLabel()
    { var label = new Label(); label.SetBinding(Label.TextProperty, nameof(Model.Caption)); return label; }

    private static ToolbarButton BoundAction()
    {
        var item = new ToolbarButton();
        item.SetBinding(ToolbarButton.TextProperty, nameof(Model.Caption));
        item.SetBinding(ToolbarButton.CommandProperty, nameof(Model.Action));
        item.SetBinding(ToolbarButton.CommandParameterProperty, nameof(Model.ActionParameter));
        item.SetBinding(ToolbarButton.IsEnabledProperty, nameof(Model.ActionEnabled));
        item.SetBinding(ToolbarButton.IsVisibleProperty, nameof(Model.ActionVisible));
        item.SetBinding(ToolbarButton.AccessibilityLabelProperty, nameof(Model.ActionLabel));
        return item;
    }

    private static object ActionButton()
    {
        var button = new Button();
        button.SetBinding(Button.TextProperty, nameof(ToolbarButton.Text));
        button.SetBinding(Button.ImageSourceProperty, nameof(ToolbarButton.Icon));
        button.SetBinding(Button.CommandProperty, nameof(ToolbarButton.Command));
        button.SetBinding(Button.CommandParameterProperty, nameof(ToolbarButton.CommandParameter));
        button.SetBinding(VisualElement.IsEnabledProperty, nameof(ToolbarButton.IsEnabled));
        button.SetBinding(SemanticProperties.DescriptionProperty, nameof(ToolbarButton.AccessibilityLabel));
        return button;
    }

    private static Button Action(Element element, string text) => Descendants(element).OfType<Button>().Single(button => button.Text == text);

    private async Task<(MauiNavigationHost, NavigationContext)> CreateAsync(NavigationDefinition definition, Action<NavigationView>? configure = null)
    {
        var host = new MauiNavigationHostFactory(new Locator(), new()).ForWindow(application.Add());
        hosts.Add(host);
        var result = await host.ReplaceContentRootAsync(new(definition), view =>
        { view.Presenter.AnimateNavigation = false; configure?.Invoke(view); }, Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Success(result);
        Assert.True(result.Value!.Entry.ViewModel.IsActive);
        return (host, result.Value.Entry.ViewModel);
    }
    private static NavigationDefinition Plain(Model root) => new([new("root", "Root", Factory(root))]);
    private static ScreenFactory Factory(Model model, NavigationToolbarDefinition? toolbar = null) =>
        ScreenFactory.Create(() => model, value => new ViewBase<Model>(value) { Content = new Entry { Text = "Retained input" }, Toolbar = toolbar });
    private static void Success<T>(NavigationOutcome<T> result) => Assert.True(result.IsSuccess, $"{result.Status}: {result.Error}");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IEnumerable<Element> Descendants(Element element)
    {
        yield return element;
        foreach (var child in ((IElementController)element).LogicalChildren)
            foreach (var nested in Descendants(child)) yield return nested;
    }
    private sealed class MeasureProbe(Func<double, double, Size> measure) : View
    {
        internal Rect Arranged;
        protected override Size MeasureOverride(double widthConstraint, double heightConstraint) => measure(widthConstraint, heightConstraint);
        protected override Size ArrangeOverride(Rect bounds) { Arranged = bounds; return bounds.Size; }
    }

    private sealed class Model : ViewModelBase, INavigationGuard
    {
        private string caption = string.Empty, actionLabel = string.Empty;
        private bool actionEnabled = true, actionVisible = true;
        private object? actionParameter;
        private System.Windows.Input.ICommand? action;
        public string Caption { get => caption; set => SetProperty(ref caption, value); }
        public string ActionLabel { get => actionLabel; set => SetProperty(ref actionLabel, value); }
        public bool ActionEnabled { get => actionEnabled; set => SetProperty(ref actionEnabled, value); }
        public bool ActionVisible { get => actionVisible; set => SetProperty(ref actionVisible, value); }
        public object? ActionParameter { get => actionParameter; set => SetProperty(ref actionParameter, value); }
        public System.Windows.Input.ICommand? Action { get => action; set => SetProperty(ref action, value); }
        internal bool Allowed = true;
        internal Func<Task<bool>>? Guard;
        internal Func<Task>? Deactivate;
        internal Func<Task>? Dismiss;
        internal int GuardCalls, OtherGuardCalls, Activations, Deactivations, Dismissals;
        public override Task<bool> CanNavigate() { GuardCalls++; return Guard?.Invoke() ?? Task.FromResult(Allowed); }
        public Task<bool> CanNavigateAsync(CancellationToken token) { OtherGuardCalls++; return Task.FromResult(!Allowed); }
        public override Task Appearing() { Activations++; return Task.CompletedTask; }
        public override Task Deactivated() { Deactivations++; return Deactivate?.Invoke() ?? Task.CompletedTask; }
        public override Task AfterDismissed() { Dismissals++; return Dismiss?.Invoke() ?? Task.CompletedTask; }
    }
    private sealed class TestApplication : Application
    {
        internal Window Add() => (Window)((IApplication)this).CreateWindow(null);
        protected override Window CreateWindow(IActivationState? state) => new(new ContentPage());
    }
    private sealed class Locator : IViewLocator
    {
        public void Initialize(Dictionary<Type, Type> pairs) { }
        public VisualElement CreateAndBindVEFor<T>() where T : ViewModelBase => throw new NotSupportedException();
        public VisualElement CreateAndBindVEFor(Type type) => throw new NotSupportedException();
        public Type FindVEForViewModel(Type type) => throw new NotSupportedException();
        public Type FindViewModelForVE(Type type) => throw new NotSupportedException();
    }
}
