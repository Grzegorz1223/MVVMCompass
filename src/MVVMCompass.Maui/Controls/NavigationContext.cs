using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using MVVMCompass.Core;

namespace MVVMCompass;

/// <summary>The automatic action at the leading edge of a custom toolbar.</summary>
public enum NavigationLeadingAction
{
    /// <summary>No navigation action is available.</summary>
    None,
    /// <summary>Pop the active screen after its guard allows navigation.</summary>
    Back,
    /// <summary>Close the modal scope after its guards allow navigation.</summary>
    Close,
    /// <summary>Toggle the owning flyout.</summary>
    Menu
}

/// <summary>Observable destination metadata shared by tabs, rails, and flyout menus.</summary>
public sealed class NavigationItemContext : ObservableObject
{
    private bool selected;
    private bool busy;
    private bool visible = true;
    private bool enabled = true;
    internal NavigationItemContext(NavigationDestination destination, ICommand command) { Destination = destination; SelectCommand = command; visible = destination.Item?.IsVisible ?? true; enabled = destination.Item?.IsEnabled ?? true; }
    internal NavigationDestination Destination { get; set; }
    internal void RefreshMetadata() { OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(SelectedIcon)); OnPropertyChanged(nameof(UnselectedIcon)); OnPropertyChanged(nameof(Icon)); }
    /// <summary>Gets the stable destination ID.</summary>
    public string Id => Destination.Id;
    /// <summary>Gets the display title.</summary>
    public string Title => Destination.Title;
    /// <summary>Gets the optional image name.</summary>
    public string? Icon => IsSelected ? SelectedIcon : UnselectedIcon;
    /// <summary>Gets the selected destination icon.</summary>
    public string? SelectedIcon => Destination.Item?.SelectedIcon ?? Destination.Icon;
    /// <summary>Gets the unselected destination icon.</summary>
    public string? UnselectedIcon => Destination.Item?.UnselectedIcon ?? Destination.Icon;
    /// <summary>Gets the library-owned destination selection command.</summary>
    public ICommand SelectCommand { get; }
    /// <summary>Gets whether the destination is on the selected branch.</summary>
    public bool IsSelected { get => selected; internal set { if (SetProperty(ref selected, value)) OnPropertyChanged(nameof(Icon)); } }
    /// <summary>Gets or sets a busy indicator without changing navigation permission.</summary>
    public bool IsBusy { get => busy; set => SetProperty(ref busy, value); }
    /// <summary>Gets or sets selector visibility. Programmatic selection remains available.</summary>
    public bool IsVisible { get => visible; set { if (SetProperty(ref visible, value) && SelectCommand is Command command) command.ChangeCanExecute(); } }
    /// <summary>Gets or sets selector availability. This does not introduce a Back guard.</summary>
    public bool IsEnabled { get => enabled; set { if (SetProperty(ref enabled, value) && SelectCommand is Command command) command.ChangeCanExecute(); } }
}

/// <summary>One custom navigation scope, coordinated by its explicit window host.</summary>
internal sealed partial class NavigationContext : ObservableObject, INavigationInitializable<NavigationDefinition>, INavigationAware
{
    private readonly MauiNavigationHost owner;
    private readonly Dictionary<string, DestinationState> destinations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> selectedChildren = new(StringComparer.Ordinal);
    private readonly List<NavigationScreenEntry> ownedScreens = [];
    private readonly ObservableCollection<NavigationItemContext> tabs = [];
    private readonly ObservableCollection<NavigationItemContext> menu = [];
    private readonly Dictionary<string, NavigationItemContext> items = new(StringComparer.Ordinal);
    private DestinationState? active;
    private int pending;
    private bool closed;
    private bool covered;
    private NavigationEntry? scopeEntry;
    private NavigationFlyoutPage? flyout;
    private NavigationHostPage? page;
    private readonly HashSet<NavigationEntry> deactivating = [];

    internal NavigationContext(MauiNavigationHost owner, NavigationDefinition definition, bool modal)
    {
        this.owner = owner; Definition = definition; IsModal = modal;
        TabItems = new(tabs); MenuItems = new(menu);
        View = new NavigationView();
        foreach (var destination in NavigationDefinition.Flatten(definition.Destinations))
        {
            items.Add(destination.Id, new(destination, new Command(async () => await ReportAsync(SelectAsync(destination.Id,
                options: new() { Origin = Current?.Entry, RejectIfBusy = true })), () => IsActive && !IsNavigating &&
                    items.TryGetValue(destination.Id, out var item) && item.IsEnabled && item.IsVisible)));
            if (destination.Factory != null) destinations.Add(destination.Id, new(destination));
        }
        if (HasFlyout) foreach (var destination in definition.Destinations) menu.Add(items[destination.Id]);
        View.Bind(this);
    }

    /// <summary>Gets the immutable scope definition.</summary>
    public NavigationDefinition Definition { get; private set; }
    /// <summary>Gets the currently registered selector metadata, including nested tabs.</summary>
    public IReadOnlyCollection<NavigationItemContext> Destinations => items.Values;
    /// <summary>Gets the persistent custom layout.</summary>
    public NavigationView View { get; }
    /// <summary>Gets the explicit owning window.</summary>
    public Window Window => owner.Window;
    /// <summary>Gets the active screen, or null while preparing or after teardown.</summary>
    public NavigationScreenEntry? Current => active?.Stack.LastOrDefault();
    /// <summary>Gets the active destination ID.</summary>
    public string? SelectedDestinationId => active?.Definition.Id;
    /// <summary>Gets the selected top-level destination, including a containing tab group.</summary>
    public string? SelectedMenuId => active == null ? null : TopLevel(active.Definition.Id).Id;
    /// <summary>Gets the selected branch's tabs.</summary>
    public ReadOnlyObservableCollection<NavigationItemContext> TabItems { get; }
    /// <summary>Gets the flyout's declared destinations.</summary>
    public ReadOnlyObservableCollection<NavigationItemContext> MenuItems { get; }
    /// <summary>Gets whether the active stack has a previous screen. Does not invoke a guard.</summary>
    public bool CanGoBack => active?.Stack.Count > 1 || Current?.Children?.CanGoBack == true;
    private bool LocalCanGoBack => active?.Stack.Count > 1;
    /// <summary>Gets whether the scope is a modal.</summary>
    public bool IsModal { get; }
    /// <summary>Gets whether the scope has a flyout.</summary>
    public bool HasFlyout => Definition.Presentation == NavigationPresentation.Flyout;
    /// <summary>Gets whether a transient flyout is open.</summary>
    public bool IsFlyoutOpen => (flyout?.IsPresented == true || embeddedFlyoutOpen) && !IsFlyoutPinned;
    /// <summary>Gets whether the flyout is displayed beside the content.</summary>
    public bool IsFlyoutPinned => flyout is IFlyoutPageController controller && controller.ShouldShowSplitMode;
    /// <summary>Gets whether this scope is executing or waiting for navigation.</summary>
    public bool IsNavigating => pending != 0 || Current?.Children?.IsNavigating == true;
    /// <summary>Gets whether the scope has completed or begun terminal teardown.</summary>
    public bool IsClosed => closed || scopeEntry?.Lifetime.IsDismissed == true || owner.IsClosed;
    /// <summary>Gets whether this scope owns the currently visible page.</summary>
    public bool IsActive => !IsClosed && !covered && scopeEntry?.State == NavigationEntryState.Active &&
        (ParentContext == null ? ReferenceEquals(Window.Navigation.ModalStack.LastOrDefault() ?? Window.Page, PresentedPage)
            : ParentContext.IsActive && ReferenceEquals(ParentContext.Current, ContainerScreen));
    /// <summary>Gets the action selected from the active stack, modal, and flyout context.</summary>
    public NavigationLeadingAction LeadingAction => ActiveChain().Any(context => context.LocalCanGoBack) ? NavigationLeadingAction.Back : RootContext.IsModal
        ? NavigationLeadingAction.Close : ActiveChain().Any(context => context.HasFlyout && !context.IsFlyoutPinned) ? NavigationLeadingAction.Menu : NavigationLeadingAction.None;
    /// <summary>Gets completion of the latest platform Back request.</summary>
    public Task NativeBackCompletion { get; private set; } = Task.CompletedTask;

    internal Page PresentedPage => ParentContext?.PresentedPage ?? flyout as Page ?? page ?? throw new InvalidOperationException("The navigation scope has no page.");
    internal Page HandledPage => ParentContext?.HandledPage ?? page ?? throw new InvalidOperationException("The navigation scope has no page.");
    internal NavigationEntry ScopeEntry => scopeEntry ?? throw new InvalidOperationException("The scope is not owned.");
    internal IEnumerable<NavigationScreenEntry> Screens => ownedScreens;
    internal bool OwnsPage(Page candidate) => ReferenceEquals(candidate, PresentedPage) || ReferenceEquals(candidate, page);

    internal Page CreatePage()
    {
        page = new NavigationHostPage(this);
        if (HasFlyout) flyout = new NavigationFlyoutPage(this, page);
        ConnectPlatformBack();
        return PresentedPage;
    }

    internal void Attach(NavigationEntry entry) => scopeEntry = entry;
    internal NavigationEntry<ViewModelBase> ClaimScreen(ViewModelBase model)
    {
        var entry = owner.ClaimContentScreen(model);
        ScopeEntry.Ownership.Adopt(entry.Ownership);
        return entry;
    }

    async Task INavigationInitializable<NavigationDefinition>.InitializeAsync(NavigationDefinition definition, CancellationToken cancellationToken)
    {
        var target = Resolve(definition.InitialDestinationId ?? definition.Destinations[0].Id);
        var screen = await PrepareAsync(target.Definition.Factory!, null, _ => { }, cancellationToken);
        target.Stack.Add(screen);
        active = target;
        RememberSelection();
        View.Presenter.Install(screen.View);
        Publish();
    }

    async Task INavigationAware.ActivateAsync(CancellationToken lifetimeToken)
    {
        covered = false;
        if (Current != null) await ActivateAsync(Current);
        await NotifySelectionAsync();
        Publish();
    }
    async Task INavigationAware.DeactivateAsync()
    {
        covered = true;
        if (Current != null) await DeactivateAsync(Current);
        Publish();
    }
    async Task INavigationAware.DismissAsync(DismissalReason reason)
    {
        closed = true;
        try { await NavigationLifetimeGroup.DismissAsync(ownedScreens.AsEnumerable().Reverse().Select(item => item.Entry.Lifetime), reason); }
        finally
        {
            DisconnectPlatformBack();
            View.Unbind();
            flyout?.Disconnect();
            foreach (var state in destinations.Values) state.Stack.Clear();
            ownedScreens.Clear(); active = null;
            tabs.Clear(); menu.Clear(); Publish();
        }
    }

    /// <summary>Pushes a fresh screen on the active destination's stack.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> PushAsync(ScreenFactory factory,
        Dictionary<string, object>? parameters = null, NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) => RunAsync<NavigationScreenEntry?>(options, async operation =>
    {
        ArgumentNullException.ThrowIfNull(factory);
        var previous = Current!;
        await GuardAsync([previous], operation);
        var candidate = await PrepareAsync(factory, parameters, entry => operation.Own(entry), operation.CancellationToken);
        if (candidate.ViewModel.IsModal && candidate.ViewModel.NavigationBinding != null)
            return await PresentPreparedModalAsync(candidate, factory, operation);
        operation.BeginCommit();
        await DeactivateAsync(previous);
        active!.Stack.Add(candidate);
        Publish();
        await View.Presenter.PresentAsync(candidate.View, false);
        await ActivateAsync(candidate);
        return candidate;
    }, cancellationToken);

    /// <summary>Pops the active screen or closes a modal root, after CanNavigate allows the operation.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> BackAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) => RunAsync(options, async operation =>
    {
        if (IsFlyoutOpen) { operation.BeginCommit(); SetFlyout(false); return Current; }
        if (!LocalCanGoBack)
        {
            if (IsModal) return await CloseModalCoreAsync(operation);
            return Current;
        }
        var previous = Current!;
        await GuardAsync([previous], operation);
        operation.BeginCommit();
        active!.Stack.RemoveAt(active.Stack.Count - 1);
        previous.Entry.Lifetime.MarkDismissed(DismissalReason.Back);
        Publish();
        try { await View.Presenter.PresentAsync(Current!.View, true); }
        finally
        {
            try { await DismissAsync([previous], DismissalReason.Back); }
            finally { await ActivateAsync(Current!); }
        }
        return Current;
    }, cancellationToken);

    /// <summary>Removes the active destination's details while preserving its root.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> PopToRootAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) => RunAsync(options, async operation =>
    {
        if (!LocalCanGoBack) return Current;
        var removed = active!.Stack.Skip(1).Reverse().ToArray();
        await GuardAsync(removed, operation);
        operation.BeginCommit();
        active.Stack.RemoveRange(1, active.Stack.Count - 1);
        foreach (var screen in removed) screen.Entry.Lifetime.MarkDismissed(DismissalReason.Back);
        Publish();
        try { await View.Presenter.PresentAsync(Current!.View, true); }
        finally
        {
            try { await DismissAsync(removed, DismissalReason.Back); }
            finally { await ActivateAsync(Current!); }
        }
        return Current;
    }, cancellationToken);

    /// <summary>Selects a retained destination. Different destinations retain independent histories.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> SelectAsync(string destinationId,
        Dictionary<string, object>? parameters = null, NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) => SelectAsync(() => destinationId, parameters, options, cancellationToken);

    private Task<NavigationOutcome<NavigationScreenEntry?>> SelectAsync(Func<string> destinationId,
        Dictionary<string, object>? parameters = null, NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) => RunAsync(options, async operation =>
    {
        var target = Resolve(destinationId());
        if (target == active && parameters == null) { SetFlyout(false); return Current; }
        var previous = Current!;
        await GuardAsync([previous], operation);
        NavigationScreenEntry? candidate = null;
        if (target.Stack.Count == 0)
            candidate = await PrepareAsync(target.Definition.Factory!, parameters, entry => operation.Own(entry), operation.CancellationToken);
        operation.BeginCommit();
        if (candidate == null && parameters != null) await target.Stack[^1].ViewModel.GetParameters(parameters);
        await DeactivateAsync(previous);
        if (candidate != null) target.Stack.Add(candidate);
        active = target;
        RememberSelection();
        SetFlyout(false);
        Publish();
        await View.Presenter.PresentAsync(Current!.View, false);
        await ActivateAsync(Current);
        await NotifySelectionAsync();
        return Current;
    }, cancellationToken);

    /// <summary>Opens a modal with its own custom toolbar, selected destinations, and history.</summary>
    public Task<NavigationOutcome<NavigationContext?>> OpenModalAsync(NavigationDefinition definition,
        Action<NavigationView>? configure = null, NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) => RunAsync<NavigationContext?>(options, async operation =>
    {
        ArgumentNullException.ThrowIfNull(definition);
        await GuardAsync([Current!], operation);
        var modal = new NavigationContext(owner, definition, true);
        var entry = owner.CreateContentScope(modal);
        operation.Own(entry);
        modal.Attach(entry);
        modal.CreatePage();
        configure?.Invoke(modal.View);
        await ((INavigationInitializable<NavigationDefinition>)modal).InitializeAsync(definition, operation.CancellationToken);
        operation.BeginCommit();
        try
        {
            await RootContext.ScopeEntry.DeactivateAsync();
            await owner.PresentContentModalAsync(modal, entry);
            await entry.ActivateAsync();
            modal.RefreshState();
            return modal;
        }
        catch (Exception error)
        {
            await RecoverUnpresentedModalAsync(modal, entry, error);
            throw;
        }
    }, cancellationToken);

    /// <summary>Closes this modal and its retained screens after their guards allow dismissal.</summary>
    public Task<NavigationOutcome<NavigationScreenEntry?>> CloseModalAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) => RunAsync(options, CloseModalCoreAsync, cancellationToken);

    private async Task<NavigationScreenEntry?> CloseModalCoreAsync(NavigationOperationContext<object?> operation)
    {
        if (!IsModal) return Current;
        await GuardAsync(CurrentFirst(AllScreens()), operation);
        operation.BeginCommit();
        await owner.CloseContentModalAsync(this);
        return owner.CurrentContentNavigation?.Current;
    }

    /// <summary>Opens or closes the flyout without leaving the active screen.</summary>
    public void ToggleFlyout()
    {
        if (IsActive && !IsNavigating && HasFlyout && !IsFlyoutPinned) SetFlyout(!IsFlyoutOpen);
    }

    internal void SetFlyout(bool open)
    {
        if (flyout != null && !IsFlyoutPinned) flyout.IsPresented = open;
        else if (HasFlyout) { embeddedFlyoutOpen = open; View.Refresh(); }
        FlyoutChanged();
    }
    internal void FlyoutChanged()
    { OnPropertyChanged(nameof(IsFlyoutOpen)); OnPropertyChanged(nameof(IsFlyoutPinned)); OnPropertyChanged(nameof(LeadingAction)); }

    internal bool RequestPlatformBack()
    {
        if (ParentContext == null && Services.PopupOwnership.RequestPlatformBack(Window, out var popupClose))
        { NativeBackCompletion = popupClose; return true; }
        if (!IsActive || (!CanGoBack && !IsModal && !ActiveChain().Any(context => context.IsFlyoutOpen))) return false;
        var target = BackContext();
        NativeBackCompletion = ReportAsync(target.BackAsync(new() { Origin = Deepest.Current?.Entry, RejectIfBusy = true }));
        return true;
    }
    internal void RequestPlatformModalClose()
    {
        var origin = Current?.Entry;
        NativeBackCompletion = CloseAfterNativeEventAsync();
        async Task CloseAfterNativeEventAsync()
        {
            // Return from ModalPopping before starting the replacement, guarded request.
            await Task.Yield();
            await ReportAsync(CloseModalAsync(new() { Origin = origin, RejectIfBusy = true }));
        }
    }
    internal async Task ExecuteLeadingAsync()
    {
        if (LeadingAction == NavigationLeadingAction.Menu) ActiveChain().Last(context => context.HasFlyout).ToggleFlyout();
        else await ReportAsync(BackContext().BackAsync(new() { Origin = Deepest.Current?.Entry, RejectIfBusy = true }));
    }
    internal static async Task ReportAsync<T>(Task<NavigationOutcome<T>> task)
    {
        var result = await task;
        if (result.Error != null) NavigationDiagnostics.Report(result.Error, "Custom content navigation");
    }

    private async Task<NavigationOutcome<T>> RunAsync<T>(NavigationRequestOptions? options,
        Func<NavigationOperationContext<object?>, Task<T>> callback, CancellationToken token)
    {
        if (owner.IsContentCallback) return new(NavigationStatus.Reentrant, default, false);
        await owner.DispatchContentAsync(() => { pending++; AvailabilityChanged(); });
        try
        {
            return await owner.RunContentOperationAsync(this, options, async operation =>
            {
                if (!IsActive || Current == null || options?.Origin is { } origin && !RootContext.ContainsActiveOrigin(origin))
                    operation.Reject(NavigationStatus.InvalidOrigin);
                try { return await callback(operation); }
                finally
                {
                    var abandoned = ownedScreens.Where(screen => !destinations.Values.Any(destination => destination.Stack.Contains(screen))).ToArray();
                    if (abandoned.Length != 0) await DismissAsync(abandoned, DismissalReason.PreparationFailed);
                    ownedScreens.RemoveAll(screen => screen.Entry.Lifetime.IsDismissed);
                    if (!IsClosed && IsActive && Current?.Entry.State == NavigationEntryState.Inactive)
                        await ActivateAsync(Current);
                    Publish();
                }
            }, token);
        }
        finally { await owner.DispatchContentAsync(() => { pending--; AvailabilityChanged(); }); }
    }

    private async Task<NavigationScreenEntry> PrepareAsync(ScreenFactory factory, Dictionary<string, object>? parameters,
        Action<NavigationEntry<ViewModelBase>> own, CancellationToken token)
    {
        var result = await factory.PrepareAsync(this, own, parameters, token);
        ownedScreens.Add(result);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ViewModelBase.IsBusy))
                _ = owner.DispatchContentAsync(() => { if (!IsClosed) Publish(); });
        }
        result.ViewModel.PropertyChanged += Changed;
        result.ViewModel.RegisterSubscriptionCleanup(() => result.ViewModel.PropertyChanged -= Changed);
        return result;
    }

    private async Task GuardAsync(IEnumerable<NavigationScreenEntry> screens, NavigationOperationContext<object?> operation)
    {
        foreach (var screen in screens.SelectMany(GuardBranch).Distinct())
        {
            operation.CancellationToken.ThrowIfCancellationRequested();
            if (!await screen.ViewModel.CanNavigate()) operation.Reject(NavigationStatus.GuardRejected);
            operation.CancellationToken.ThrowIfCancellationRequested();
            if (!IsActive) operation.Reject(NavigationStatus.InvalidOrigin);
        }
    }

    private static async Task ActivateAsync(NavigationScreenEntry screen)
    {
        if (screen.Entry.State != NavigationEntryState.Active)
        {
            await screen.Entry.ActivateAsync();
            await screen.ViewModel.NavigatedTo();
            await screen.ViewModel.Appearing();
        }
        // A child's deactivation can fail before its parent becomes inactive.
        // Restore that child even when the parent's entry is already active.
        if (screen.Children != null) await ((INavigationAware)screen.Children).ActivateAsync(screen.Entry.Lifetime.Token);
    }
    private async Task DeactivateAsync(NavigationScreenEntry screen)
    {
        if (screen.Entry.State != NavigationEntryState.Active || !deactivating.Add(screen.Entry)) return;
        try { if (screen.Children != null) await ((INavigationAware)screen.Children).DeactivateAsync();
            await screen.Entry.DeactivateAsync(); await screen.ViewModel.Deactivated(); }
        finally { deactivating.Remove(screen.Entry); }
    }
    private async Task DismissAsync(IEnumerable<NavigationScreenEntry> screens, DismissalReason reason)
    {
        var removed = screens.ToArray();
        try { await NavigationLifetimeGroup.DismissAsync(removed.Select(screen => screen.Entry.Lifetime), reason); }
        finally { foreach (var screen in removed) ownedScreens.Remove(screen); }
    }

    private DestinationState Resolve(string id)
    {
        if (!items.TryGetValue(id, out var item)) throw new ArgumentException("The destination is not registered.", nameof(id));
        if (!item.IsEnabled) throw new InvalidOperationException("The destination is disabled.");
        if (item.Destination.Children.Count != 0)
            id = selectedChildren.GetValueOrDefault(id) ?? item.Destination.Children.First(child => items.ContainsKey(child.Id)).Id;
        return destinations[id];
    }
    private NavigationDestination TopLevel(string id) => Definition.Destinations.First(destination =>
        destination.Id == id || destination.Children.Any(child => child.Id == id));
    private void RememberSelection()
    {
        var top = TopLevel(active!.Definition.Id);
        if (top.Children.Count != 0) selectedChildren[top.Id] = active.Definition.Id;
    }
    private void Publish()
    {
        foreach (var item in items.Values)
        {
            item.IsSelected = item.Id == SelectedDestinationId || item.Id == SelectedMenuId;
            if (destinations.TryGetValue(item.Id, out var state) && state.Stack.Count != 0 && state.Stack[0].ViewModel.NavigationBinding != null)
                item.IsBusy = state.Stack[0].ViewModel.IsBusy;
        }
        var top = active == null ? null : TopLevel(active.Definition.Id);
        var visibleTabs = top?.Children.Count > 0 ? top.Children :
            Definition.Presentation is NavigationPresentation.Tabs or NavigationPresentation.Rail ? Definition.Destinations : [];
        var next = visibleTabs.Where(destination => items.ContainsKey(destination.Id)).Select(destination => items[destination.Id]).ToArray();
        if (!tabs.SequenceEqual(next)) { tabs.Clear(); foreach (var item in next) tabs.Add(item); }
        OnPropertyChanged(nameof(Current)); OnPropertyChanged(nameof(SelectedDestinationId)); OnPropertyChanged(nameof(SelectedMenuId));
        OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(LeadingAction)); OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(IsClosed));
        View.Refresh();
        AvailabilityChanged();
        ParentContext?.Publish();
    }
    private void AvailabilityChanged()
    {
        OnPropertyChanged(nameof(IsNavigating));
        foreach (var item in items.Values) ((Command)item.SelectCommand).ChangeCanExecute();
        ParentContext?.AvailabilityChanged();
    }
    internal void RefreshState()
    {
        // Root entry activation completes after its callback. Refresh descendants once
        // that state is committed so native buttons receive the final CanExecute change.
        foreach (var context in ActiveChain().Reverse().ToArray()) context.Publish();
    }
    partial void ConnectPlatformBack();
    partial void DisconnectPlatformBack();
    private sealed class DestinationState(NavigationDestination definition)
    {
        internal NavigationDestination Definition { get; set; } = definition;
        internal List<NavigationScreenEntry> Stack { get; } = [];
    }
}
