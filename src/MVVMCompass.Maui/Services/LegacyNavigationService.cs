using CommunityToolkit.Maui.Extensions;
using MVVMCompass.Interfaces;
using MVVMCompass.Core;
using System.Collections.ObjectModel;
using Microsoft.Maui.Dispatching;

namespace MVVMCompass.Services
{
    /// <summary>Implements the preserved navigation helpers with owned cleanup and optional entry scopes.</summary>
    internal class LegacyNavigationService : ILegacyNavigationService
    {
        /// <inheritdoc />
        public Action<Window>? NewWindowOpened { get; set; }


        private readonly Window? scopedWindow;
        internal Func<ViewModelBase, VisualElement?>? ResolveOwnedView { get; set; }
        internal Func<ViewModelBase, Func<Task>, Task>? ComposeRetained { get; set; }
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TabbedPage, SelectionSubscription> standardSelections = new();
        private sealed class SelectionSubscription(EventHandler handler) { internal EventHandler Handler = handler; }

        private Task ComposeAsync(ViewModelBase owner, Func<Task> build) => owner.PendingRootPreparation != null
            ? build() : ComposeRetained != null ? ComposeRetained(owner, build) : ComposeLegacyAsync(owner, build);

        private async Task ComposeLegacyAsync(ViewModelBase owner, Func<Task> build)
        {
            if (owner.IsDismissed) throw new InvalidOperationException("The composition owner has been dismissed.");
            var view = GetVEFromStackForViewModel(owner);
            var oldTabs = (view as TabbedPage)?.Children.ToArray();
            var oldSelection = (view as TabbedPage)?.CurrentPage;
            var oldCount = (view as ICustomTabbedViewBase)?.ChildCount ?? 0;
            var oldMenu = (view as FlyoutPage)?.Flyout;
            var oldDetail = (view as FlyoutPage)?.Detail;
            var oldModels = ViewModelTree.Collect(view).Where(model => !ReferenceEquals(model, owner)).ToArray();
            using var preparation = new RootPreparation(Application.Current?.Windows.SelectMany(window => ViewModelTree.Collect(window.Page, true)) ?? []);
            owner.PendingRootPreparation = preparation;
            var activating = false;
            try
            {
                await build();
                activating = true;
                var removedModels = oldModels.Except(ViewModelTree.Collect(view)).ToArray();
                try { await DismissViewModelsAsync(removedModels, DismissalReason.Removed); }
                catch (Exception error) { NavigationDiagnostics.Report(error, "Legacy retained composition cleanup"); }
                finally { UnwireScopedSubscriptions(removedModels); }
                preparation.FinishPreparation();
                await preparation.ActivateAsync();
            }
            catch
            {
                if (!activating)
                {
                    if (view is TabbedPage tabs && oldTabs != null)
                    {
                        foreach (var added in tabs.Children.Except(oldTabs).ToArray()) tabs.Children.Remove(added);
                        tabs.CurrentPage = oldSelection;
                    }
                    if (view is IRetainedTabMaintenance maintenance) maintenance.RemoveChildrenAfter(oldCount);
                    if (view is FlyoutPage flyout) { flyout.Flyout = oldMenu; flyout.Detail = oldDetail; }
                    await preparation.AbandonAsync();
                }
                throw;
            }
            finally { owner.PendingRootPreparation = null; }
        }

        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ViewModelBase, List<Action>> scopedSubscriptions = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Page, PageSubscription> pageSubscriptions = new();
        private sealed class PageSubscription(Action release) { internal Action Release = release; }

        private void TrackScopedSubscription(ViewModelBase model, Action unsubscribe)
        {
            scopedSubscriptions.GetOrCreateValue(model).Add(model.RegisterSubscriptionCleanup(unsubscribe));
        }

        internal void UnwireScopedSubscriptions(IEnumerable<ViewModelBase> models)
        {
            foreach (var model in models)
                if (scopedSubscriptions.TryGetValue(model, out var subscriptions))
                {
                    foreach (var unsubscribe in subscriptions) unsubscribe();
                    scopedSubscriptions.Remove(model);
                }
        }

        private Page? PresentationRoot
        {
            get
            {
                if (scopedWindow != null) return scopedWindow.Page;
                if (CurrentApplication.Windows.Any())
                {
                    return CurrentApplication.Windows[0].Page;
                }
                else
                {
                    return null;
                }
            }
            set
            {
                var currentWindow = scopedWindow ?? CurrentApplication.Windows.FirstOrDefault();
                if (currentWindow is { })
                {
                    currentWindow.Page = value;
                }
            }
        }

        private readonly IViewLocator _viewLocator;
        private INavigation Navigator => PresentationRoot?.Navigation ?? throw new Exception("Need to call LegacyNavigationService.PresentAsNavigatableMainPage");

        private static Application CurrentApplication => Application.Current ?? throw new Exception("LegacyNavigationService get PresentationRoot Application.Current null, Need to call LegacyNavigationService.PresentAsNavigatableMainPage");

        //We need this for FlayoutPage where all has to be set up before adding it to the PresentationRoot
        private Page? CurrentMainPage;

        //We need this for FlayoutPage if inside we have a TabbedPage and we need to add the inner views of a TabbedPage before adding the FlyoutPage to the PresentationRoot
        private List<VisualElement> TemporalFlyoutStack { get; set; } = new List<VisualElement>();

        private readonly NavigationOptions _options;

        /// <summary>Creates the preserved navigation service using the supplied locator and optional lifecycle policy.</summary>
        public LegacyNavigationService(IViewLocator viewLocator) : this(viewLocator, new NavigationOptions()) { }

        /// <summary>Creates the preserved navigation service using the supplied locator and optional lifecycle policy.</summary>
        public LegacyNavigationService(IViewLocator viewLocator, NavigationOptions options)
        {
            _viewLocator = viewLocator;
            _options = options;
        }

        internal LegacyNavigationService(IViewLocator viewLocator, NavigationOptions options, Window window) : this(viewLocator, options)
            => scopedWindow = window;

        internal void WireRoot<T>(Page page, T model) where T : ViewModelBase
        {
            model.RetainedViewLifecycleBehavior = _options.RetainedViewLifecycleBehavior;
            AddEvents<T>(page, model);
        }

        internal void SetCurrentRoot(Page? page) => CurrentMainPage = page;

        /// <inheritdoc />
        public void Initialize(Dictionary<Type, Type> registerPairs)
        {
            _viewLocator.Initialize(registerPairs);
            NativeNavigationObserver.AttachApplication();
        }

        /// <inheritdoc />
        public Page GetPresentationRoot()
        {
            if (PresentationRoot is { })
            {
                return PresentationRoot;
            }
            else
            {
                throw new Exception("LegacyNavigationService GetPresentationRoot PresentationRoot null");
            }
        }

        /// <inheritdoc />
        public VisualElement GetViewFromVM<T>() where T : ViewModelBase
        {
            return _viewLocator.CreateAndBindVEFor<T>();
        }

        /// <inheritdoc />
        public async Task<Page> CreateMainPage<T>() where T : ViewModelBase
        {
            return await CreateMainPage<T>(null);
        }

        /// <inheritdoc />
        public Task<Page> CreateMainPage<T>(Dictionary<string, object>? parameters) where T : ViewModelBase =>
            ReplaceRootAsync<T>(parameters, navigable: false, present: false);

        private int rootOperationInProgress;

        private async Task<Page> ReplaceRootAsync<T>(Dictionary<string, object>? parameters, bool navigable, bool present)
            where T : ViewModelBase
        {
            // Fail promptly, including calls made by initialization/cleanup callbacks: queuing a
            // reentrant operation would deadlock while the outer call awaits that callback.
            if (Interlocked.CompareExchange(ref rootOperationInProgress, 1, 0) != 0)
                throw new InvalidOperationException("A root operation is already in progress. Await it before starting another root operation.");

            RootPreparation? preparation = null;
            Page? replacement = null;
            Window? window = null;
            RootOperationGate? windowGate = null;
            var teardownStarted = false;
            var stage = RootReplacementStage.Commitment;
            try
            {
                var application = CurrentApplication;
                window = application.Windows.FirstOrDefault();
                if (window != null) windowGate = RootOperationGate.Enter(window);
                if (present && window == null)
                    throw new InvalidOperationException("Presenting a root requires an application window. Use CreateMainPage or CreateNavigableMainPage during window creation.");
                var oldRoot = window?.Page;
                preparation = new RootPreparation(application.Windows.SelectMany(w => FindViewModelsToDismiss(w.Page)));
                NativeNavigationObserver.AttachApplication();

                var visualElement = await NavigationViewFactory.CreateAsync<T>(_viewLocator);
                preparation.Track(visualElement);
                if (visualElement is not Page newMainPage)
                    throw new InvalidOperationException("CreateMainPage visual element is not a page");
                var viewModel = GetViewModelForVE(newMainPage);
                if (parameters != null) await viewModel.GetParameters(parameters);
                AddEvents<T>(newMainPage, viewModel);
                await viewModel.BeforeFirstShown();

                replacement = navigable ? new NavigationPage(newMainPage) : newMainPage;
                if (replacement is NavigationPage navigation)
                {
                    NativeNavigationObserver.Attach(navigation);
                    TrackScopedSubscription(viewModel, () => NativeNavigationObserver.Detach(navigation));
                }

                // Capture once, then validate after asynchronous preparation. Never redirect the
                // operation into a different first window or dismiss an externally replaced root.
                if (!ReferenceEquals(Application.Current, application) ||
                    (window != null && (!application.Windows.Contains(window) || !ReferenceEquals(window.Page, oldRoot))) ||
                    (window == null && application.Windows.Count != 0))
                    throw new InvalidOperationException("The root window changed during preparation. Retry against the current application state.");

                var outgoing = FindViewModelsToDismiss(oldRoot);
                preparation.Validate(outgoing);
                teardownStarted = true;
                if (oldRoot is NavigationPage oldNavigation) NativeNavigationObserver.Detach(oldNavigation);
                foreach (var model in outgoing) model.Lifetime.MarkDismissed(DismissalReason.RootReplaced);
                if (window != null && oldRoot != null)
                    try { await PopupOwnership.EndForRootAsync(window, oldRoot, DismissalReason.RootReplaced); }
                    catch (Exception error) { NavigationDiagnostics.Report(error, "Root popup cleanup"); }
                await DismissAllAsync(outgoing);
                if (!ReferenceEquals(Application.Current, application) ||
                    (window != null && (!application.Windows.Contains(window) || !ReferenceEquals(window.Page, oldRoot))) ||
                    (window == null && application.Windows.Count != 0))
                    throw new InvalidOperationException("The root window changed during outgoing cleanup. Present a fresh root to recover.");
                preparation.FinishPreparation();
                CurrentMainPage = newMainPage;

                // Installation can trigger native lifecycle events synchronously. They are held
                // by the preparation context until old shared-integration owners have shut down.
                if (present) window!.Page = replacement;
                stage = RootReplacementStage.Activation;
                await preparation.ActivateAsync();
                return replacement;
            }
            catch (Exception error)
            {
                var installed = present && replacement != null && ReferenceEquals(window?.Page, replacement);
                if (!installed && preparation != null)
                {
                    if (replacement is NavigationPage navigation) NativeNavigationObserver.Detach(navigation);
                    await preparation.AbandonAsync();
                    if (teardownStarted) CurrentMainPage = null;
                }
                if (teardownStarted)
                    throw new RootReplacementException(stage, installed, error);
                throw;
            }
            finally
            {
                preparation?.Dispose();
                windowGate?.Dispose();
                Volatile.Write(ref rootOperationInProgress, 0);
            }
        }

        // Host replacement marks the entire tree before any asynchronous cleanup can yield.
        internal static Task DismissAllAsync(IEnumerable<ViewModelBase> viewModels) =>
            DismissViewModelsAsync(viewModels, DismissalReason.RootReplaced);

        internal static async Task DismissViewModelsAsync(IEnumerable<ViewModelBase> viewModels, DismissalReason reason)
        {
            var outgoing = viewModels.Distinct<ViewModelBase>(ReferenceEqualityComparer.Instance).ToArray();
            if (reason == DismissalReason.RootReplaced)
                foreach (var model in outgoing) model.MarkHostReplaced();
            try
            {
                await NavigationLifetimeGroup.DismissAsync(outgoing.Select(model => model.Lifetime), reason);
            }
            catch (Exception exception)
            {
                NavigationDiagnostics.Report(exception, $"Dismiss ({reason})");
            }
            foreach (var model in outgoing)
                foreach (var exception in model.Lifetime.CancellationErrors)
                    NavigationDiagnostics.Report(exception, "Lifetime cancellation");
        }

        /// <inheritdoc />
        public async Task<NavigationPage> CreateNavigableMainPage<T>() where T : ViewModelBase
        {
            return await CreateNavigableMainPage<T>(null);
        }

        /// <inheritdoc />
        public async Task<NavigationPage> CreateNavigableMainPage<T>(Dictionary<string, object>? parameters) where T : ViewModelBase =>
            (NavigationPage)await ReplaceRootAsync<T>(parameters, navigable: true, present: false);

        /// <inheritdoc />
        public Task PresentAsMainPage<T>() where T : ViewModelBase => PresentAsMainPage<T>(null);

        /// <inheritdoc />
        public async Task PresentAsMainPage<T>(Dictionary<string, object>? parameters) where T : ViewModelBase =>
            await ReplaceRootAsync<T>(parameters, navigable: false, present: true);

        /// <inheritdoc />
        public Task PresentAsNavigableMainPage<T>() where T : ViewModelBase => PresentAsNavigableMainPage<T>(null);

        /// <inheritdoc />
        public async Task PresentAsNavigableMainPage<T>(Dictionary<string, object>? parameters) where T : ViewModelBase =>
            await ReplaceRootAsync<T>(parameters, navigable: true, present: true);

        /// <inheritdoc />
        public async Task NavigateTo<T>() where T : ViewModelBase
        {
            await NavigateTo<T>(null);
        }

        /// <inheritdoc />
        public async Task NavigateTo<T>(Dictionary<string, object>? parameters) where T : ViewModelBase
        {
            using var preparation = new RootPreparation(Application.Current?.Windows.SelectMany(window => ViewModelTree.Collect(window.Page, true)) ?? []);
            Page? candidate = null;
            var navigator = Navigator;
            try
            {
                var visualElement = await NavigationViewFactory.CreateAsync<T>(_viewLocator);
                preparation.Track(visualElement);
                var viewModel = GetViewModelForVE(visualElement);
                candidate = visualElement as Page ?? throw new InvalidOperationException("NavigateTo visual element is not a page");
                if (parameters != null) await viewModel.GetParameters(parameters);
                AddEvents<T>(candidate, viewModel);
                await viewModel.BeforeFirstShown();
                preparation.Validate(Application.Current?.Windows.SelectMany(window => ViewModelTree.Collect(window.Page, true)) ?? []);
                preparation.FinishPreparation();
                if (candidate is IModal { IsModal: true }) await navigator.PushModalAsync(candidate);
                else await navigator.PushAsync(candidate);
                if (!navigator.NavigationStack.Contains(candidate) && !navigator.ModalStack.Contains(candidate))
                    throw new InvalidOperationException("The destination was not installed.");
                await preparation.ActivateAsync();
            }
            finally
            {
                if (candidate == null || (!navigator.NavigationStack.Contains(candidate) && !navigator.ModalStack.Contains(candidate)))
                    await preparation.AbandonAsync();
            }
        }

        /// <inheritdoc />
        public async Task DisplayPopup<T>(ViewModelBase currentVM) where T : ViewModelBase
        {
            await DisplayPopup<T>(currentVM, null);
        }

        /// <inheritdoc />
        public async Task DisplayPopup<T>(ViewModelBase currentVM, Dictionary<string, object>? parameters) where T : ViewModelBase
        {
            _ = await DisplayPopupResultAsync<T, object?>(currentVM, parameters);
        }

        /// <inheritdoc />
        public Task<TResult?> DisplayPopupWithResult<T, TResult>(ViewModelBase currentVM) where T : ViewModelBase =>
            DisplayPopupWithResult<T, TResult>(currentVM, null);

        /// <inheritdoc />
        public async Task<TResult?> DisplayPopupWithResult<T, TResult>(ViewModelBase currentVM, Dictionary<string, object>? parameters) where T : ViewModelBase =>
            (await DisplayPopupResultAsync<T, TResult>(currentVM, parameters)).Result;

        internal Task<PopupNavigationResult<TResult>> DisplayPopupResultAsync<T, TResult>(ViewModelBase? currentVM,
            Dictionary<string, object>? parameters, CancellationToken cancellationToken = default) where T : ViewModelBase =>
            DisplayPopupResultAsync<T, TResult>(currentVM, parameters, cancellationToken, default, () => { });

        internal async Task<PopupNavigationResult<TResult>> DisplayPopupResultAsync<T, TResult>(ViewModelBase? currentVM,
            Dictionary<string, object>? parameters, CancellationToken cancellationToken,
            CancellationToken ownerCancellationToken, Action preparationCompleted) where T : ViewModelBase
        {
            var window = scopedWindow ?? PresentationRoot?.Window
                ?? throw new InvalidOperationException("A popup requires an existing window.");
            return await window.Dispatcher.DispatchAsync(async () =>
            {
                Task<PopupNavigationResult<TResult>> showing;
                try
                {
                    using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownerCancellationToken);
                    preparation.Token.ThrowIfCancellationRequested();
                    if (currentVM?.IsDismissed == true) throw new InvalidOperationException("A dismissed view model cannot present a popup.");
                    var root = window.Page;
                    var view = await GetView<T>(parameters, preparation.Token);
                    if (preparation.IsCancellationRequested || window.Page != root || currentVM?.IsDismissed == true)
                    {
                        await DismissViewModelsAsync(ViewModelTree.Collect(view), DismissalReason.PreparationFailed);
                        preparation.Token.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("The popup owner changed during preparation.");
                    }
                    // ShowAsync registers session ownership before its first asynchronous wait.
                    // Only caller cancellation applies to the result wait; host closure ends
                    // the session and awaits cleanup instead of cancelling that wait early.
                    showing = PopupOwnership.ShowAsync<TResult>(window, view, currentVM, cancellationToken);
                }
                finally { preparationCompleted(); }
                return await showing;
            });
        }

        private Task<View> GetView<T>(Dictionary<string, object>? parameters, CancellationToken cancellationToken) where T : ViewModelBase =>
            PreparePopupAsync(() => NavigationViewFactory.CreateAsync<T>(_viewLocator, cancellationToken), parameters, cancellationToken: cancellationToken);

        private async Task<View> PreparePopupAsync(Func<Task<VisualElement>> create, Dictionary<string, object>? parameters,
            ViewModelBase? parent = null, CancellationToken cancellationToken = default)
        {
            using var preparation = new RootPreparation(Application.Current?.Windows.SelectMany(window => ViewModelTree.Collect(window.Page, true)) ?? [], cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                VisualElement visualElement;
                using (parent == null ? null : ViewModelBase.SetPendingParent(parent)) visualElement = await create();
                preparation.Track(visualElement);
                cancellationToken.ThrowIfCancellationRequested();
                var model = GetViewModelForVE(visualElement);
                model.ParentViewModel = parent;
                parent?.Ownership.Adopt(model.Ownership);
                if (visualElement is not View view) throw new InvalidOperationException("GetView visual element is not a view");
                if (parameters != null) await model.GetParameters(parameters);
                AddEvents<ViewModelBase>(view, model);
                await model.BeforeFirstShown();
                preparation.Validate(Application.Current?.Windows.SelectMany(window => ViewModelTree.Collect(window.Page, true)) ?? []);
                preparation.FinishPreparation();
                return view;
            }
            catch { await preparation.AbandonAsync(); throw; }
        }

        /// <inheritdoc />
        public async Task NavigateToFlyoutItem(string id)
        {
            await NavigateToFlyoutItem(id, null);
        }

        /// <inheritdoc />
        public async Task NavigateToFlyoutItem(string id, Dictionary<string, object>? parameters)
        {
            var parentPage = CurrentMainPage as FlyoutPage;

            if (parentPage == null)
            {
                throw new Exception("NavigateToFlyoutItem parentPage null");
            }

            var flyout = parentPage.Flyout as IFlyoutMenuSelectItem;

            if (flyout != null)
            {
                await flyout.SelectMenuItemById(id, parameters);
            }
            else
            {
                throw new Exception("NavigateToFlyoutItem flyout null");
            }
        }

        /// <inheritdoc />
        public async Task OpenNewWindow<T>(Action? windowClosed = null, bool isResizable = false) where T : ViewModelBase
        {
            await OpenNewWindow<T>(null, windowClosed, isResizable);
        }

        /// <inheritdoc />
        public async Task OpenNewWindow<T>(Dictionary<string, object>? parameters, Action? windowClosed = null, bool isResizable = false) where T : ViewModelBase
        {
            using var preparation = new RootPreparation(Application.Current?.Windows.SelectMany(window => ViewModelTree.Collect(window.Page, true)) ?? []);
            Window? createdWindow = null;
            try
            {
                var visualElement = await NavigationViewFactory.CreateAsync<T>(_viewLocator);
                preparation.Track(visualElement);

                var viewModel = GetViewModelForVE(visualElement);

                if (viewModel != null)
                {
                    if (parameters != null)
                    {
                        await viewModel.GetParameters(parameters);
                    }

                    if (visualElement is Page page)
                    {

                        AddEvents<T>(page, viewModel);

                        await viewModel.BeforeFirstShown();

                        var windowView = (page as IWindowView);

                        double scale;
                        if (DeviceDisplay.Current.MainDisplayInfo.Density >= 100)
                        {
                            scale = DeviceDisplay.Current.MainDisplayInfo.Density / 100;
                        }
                        else
                        {
                            scale = DeviceDisplay.Current.MainDisplayInfo.Density;
                        }

                        if (windowView is { })
                        {
                            var window = createdWindow = new Window
                            {
                                Page = page,
                                Height = windowView.WindowHeight,
                                Width = windowView.WindowWidth,
                                MinimumHeight = windowView.WindowHeight,
                                MinimumWidth = windowView.WindowWidth,
                                Title = windowView.WindowTitle,
                                // center window
                                X = (DeviceDisplay.Current.MainDisplayInfo.Width / scale - windowView.WindowWidth) / 2,
                                Y = (DeviceDisplay.Current.MainDisplayInfo.Height / scale - windowView.WindowHeight) / 2
                            };

                            if (!isResizable)
                            {
                                window.MaximumWidth = windowView.WindowWidth;
                                window.MaximumHeight = windowView.WindowHeight;
                            }

                            window.Destroying += async (_, _) =>
                            {
                                try
                                {
                                    // Preserve the immediate host notification before asynchronous cleanup.
                                    try { windowClosed?.Invoke(); }
                                    finally
                                    {
                                        await DismissViewModelsAsync(ViewModelTree.Collect(window.Page, includeModals: true),
                                            DismissalReason.WindowClosed);
                                    }
                                }
                                catch (Exception exception) { NavigationDiagnostics.Report(exception, "Window destruction"); }
                            };

                            var mainWindow = CurrentApplication.Windows[0];

                            var avalibleHeight = DeviceDisplay.Current.MainDisplayInfo.Height - mainWindow.Height - window.Y - 10;
                            var avalibleWidth = DeviceDisplay.Current.MainDisplayInfo.Width - mainWindow.Width - window.X - 20;

                            if (avalibleHeight > window.Height)
                            {
                                while (window.Y + window.Height >= mainWindow.Y)
                                {
                                    window.Y -= 10;
                                }

                                if (window.Y <= 0)
                                {
                                    window.Y = 10;
                                }
                            }
                            else if (avalibleWidth >= window.Width && avalibleHeight <= window.Height)
                            {
                                if (window.Y > avalibleHeight - window.Height)
                                {
                                    if (avalibleHeight - window.Height <= 0)
                                    {
                                        window.Y = 10;
                                    }
                                    else
                                    {
                                        window.Y -= avalibleHeight - window.Height;

                                        while (window.Y + window.Height >= mainWindow.Y)
                                        {
                                            window.Y -= 10;
                                        }

                                        if (window.Y <= 0)
                                        {
                                            window.Y = 10;
                                        }
                                    }
                                }
                            }

                            if (avalibleWidth > window.Width)
                            {
                                while (window.X + window.Width >= mainWindow.X)
                                {
                                    window.X -= 10;
                                }

                                if (window.X <= 0)
                                {
                                    window.X = 10;
                                }
                            }
                            else if (avalibleWidth <= window.Width)
                            {
                                if (window.X > avalibleWidth - window.Width)
                                {
                                    window.X -= avalibleWidth - window.Width;

                                    if (avalibleWidth - window.Width <= 0)
                                    {
                                        window.X = 10;
                                    }
                                    else
                                    {
                                        while (window.X + window.Width >= mainWindow.X)
                                        {
                                            window.X -= 10;
                                        }

                                        if (window.X <= 0)
                                        {
                                            window.X = 10;
                                        }
                                    }
                                }
                            }

                            preparation.FinishPreparation();
                            CurrentApplication.OpenWindow(window);
                            await preparation.ActivateAsync();
                            CurrentApplication.ActivateWindow(window);
                            NewWindowOpened?.Invoke(window);
                        }
                        else
                        {
                            throw new Exception("OpenNewWindow visual element is not a page");
                        }
                    }
                    else
                    {
                        throw new Exception("OpenNewWindow View needs to implement IWindowView");
                    }
                }
                else
                {
                    throw new Exception("OpenNewWindow null ViewModel");
                }
            }
            finally
            {
                if (createdWindow == null || Application.Current?.Windows.Contains(createdWindow) != true)
                    await preparation.AbandonAsync();
            }
        }

        /// <inheritdoc />
        public async Task ClosePopup()
        {
            var window = scopedWindow ?? PresentationRoot?.Window
                ?? throw new InvalidOperationException("A popup requires an existing window.");
            await window.Dispatcher.DispatchAsync(() => PopupOwnership.CloseTopAsync(window));
        }

        /// <inheritdoc />
        public async Task NavigateBack(ViewModelBase currentVM)
        {
            if (currentVM == null) return;
            var removed = currentVM.IsModal ? await Navigator.PopModalAsync() : await Navigator.PopAsync();
            if (removed != null)
                await DismissViewModelsAsync(ViewModelTree.Collect(removed), DismissalReason.Back);
        }

        /// <inheritdoc />
        public async Task NavigateBackToRoot()
        {
            var outgoing = Navigator.NavigationStack.Skip(1).Reverse()
                .SelectMany(page => ViewModelTree.Collect(page)).ToArray();
            await Navigator.PopToRootAsync();
            await DismissViewModelsAsync(outgoing, DismissalReason.Back);
        }

        /// <inheritdoc />
        public Type? GetActiveViewModelType()
        {
            var topPage = GetTopPage();
            if (topPage is null)
                return null;

            // If the top page is a custom tabbed view, return the active tab's ViewModel type
            if (topPage is ICustomTabbedViewBase customTabbed && customTabbed.CurrentTab is { } activeTab)
                return activeTab.ViewModel.GetType();

            // For normal pages, return the page's own ViewModel type
            if (topPage is IHasVM hasVM)
                return hasVM.ViewModel.GetType();

            return null;
        }

        private Page? GetTopPage()
        {
            if (PresentationRoot is null)
                return null;

            // Check modal stack first (modals are on top of everything)
            if (Navigator.ModalStack.Count > 0)
                return Navigator.ModalStack[^1];

            // Then the navigation stack (top of stack = currently visible)
            if (Navigator.NavigationStack.Count > 0)
                return Navigator.NavigationStack[^1];

            // Fallback to the root page itself
            if (PresentationRoot is NavigationPage navPage)
                return navPage.CurrentPage;

            return PresentationRoot;
        }

        private ViewModelBase GetViewModelForVE(VisualElement? visualElement)
        {
            if (visualElement is IHasVM pageWithVM)
            {
                pageWithVM.ViewModel.RetainedViewLifecycleBehavior = _options.RetainedViewLifecycleBehavior;
                return pageWithVM.ViewModel;
            }

            throw new Exception("LegacyNavigationService GetViewModelForPage unknown type");
        }

        private VisualElement GetVEFromStackForViewModel(ViewModelBase viewModel)
        {
            if (viewModel.PendingRootPreparation?.Find(viewModel) is { } preparingView) return preparingView;
            if (ResolveOwnedView?.Invoke(viewModel) is { } ownedView) return ownedView;
            VisualElement? visualElement = null;

            if (viewModel.IsModal)
            {
                visualElement = Navigator.ModalStack.First(x => ((IHasVM)x).ViewModel == viewModel);
            }
            else
            {
                if (PresentationRoot != null)
                {
                    visualElement = Navigator.NavigationStack.FirstOrDefault(x => x is IHasVM hasVm && hasVm.ViewModel == viewModel);
                }

                if (visualElement == null && TemporalFlyoutStack.Any())
                {
                    visualElement = TemporalFlyoutStack.FirstOrDefault(x => x is IHasVM hasVm && hasVm.ViewModel == viewModel);
                }

                if (visualElement == null && CurrentMainPage is IHasVM currentMainPageWithVM && currentMainPageWithVM.ViewModel == viewModel)
                {
                    visualElement = CurrentMainPage;
                }

                if (visualElement == null)
                {
                    var navigationStackCount = PresentationRoot != null ? Navigator.NavigationStack.Count : 0;
                    throw new Exception($"LegacyNavigationService GetVEFromStackForViewModel page not found for ViewModel {viewModel.GetType().FullName}. PresentationRoot:{PresentationRoot?.GetType().FullName ?? "null"}, CurrentMainPage:{CurrentMainPage?.GetType().FullName ?? "null"}, NavigationStackCount:{navigationStackCount}, TemporalFlyoutStackCount:{TemporalFlyoutStack.Count}");
                }
            }

            return visualElement;
        }

        private static List<ViewModelBase> FindViewModelsToDismiss(Page? page) =>
            ViewModelTree.Collect(page, includeModals: true);

        internal static void AddPageViewModels(Page? page, List<ViewModelBase> viewModels)
        {
            foreach (var model in ViewModelTree.Collect(page))
                if (!viewModels.Any(existing => ReferenceEquals(existing, model))) viewModels.Add(model);
        }

        private Task AddedTabbedViewModels<T>(TabbedViewModelsEventArgs e) where T : ViewModelBase =>
            ComposeAsync(e.ParentViewModel, () => BuildTabsAsync<T>(e));

        private async Task BuildTabsAsync<T>(TabbedViewModelsEventArgs e) where T : ViewModelBase
        {
            var parentPage = GetVEFromStackForViewModel(e.ParentViewModel);

            if (parentPage is ICustomTabbedViewBase customTabbed)
            {
                foreach (var tabModel in e.TabbedViewModels)
                {
                    // Set the ambient parent BEFORE DI creates the child View + ViewModel.
                    // This lets the child VM's constructor call GetParentViewModel<T>().
                    {
                        var visualElement = await NavigationViewFactory.CreateWithCancellationAsync(_viewLocator, tabModel.TabViewModelType, e.ParentViewModel,
                            e.ParentViewModel.PendingRootPreparation?.CancellationToken ?? default);
                        e.ParentViewModel.PendingRootPreparation?.Track(visualElement);
                        var innerViewModel = GetViewModelForVE(visualElement);

                        // Explicit assignment is still kept as the canonical source of truth.
                        innerViewModel.ParentViewModel = e.ParentViewModel;
                        e.ParentViewModel.Ownership.Adopt(innerViewModel.Ownership);

                        // Set logical parent so the child Page can traverse up to the Window.
                        // The child Page is not in the visual tree (its content is extracted),
                        // but it still needs GetParentWindow(), DisplayAlertAsync(), etc. to work.
                        // BindingContext won't be overridden because LegacyViewBase sets it explicitly.
                        if (visualElement is Element childElement)
                            childElement.Parent = parentPage as Element;

                        // Wire framework events (Loaded/Unloaded on the view,
                        // plus AddedTabbedViewModels/AddedFlyoutViewModels on the VM)
                        if (visualElement is Page page)
                        {
                            AddEvents<T>(page, innerViewModel, skipAppearing: true);
                        }
                        else
                        {
                            AddEvents<T>((View)visualElement, innerViewModel);
                        }

                        if (tabModel.Parameters != null)
                        {
                            await innerViewModel.GetParameters(tabModel.Parameters);
                        }

                        await innerViewModel.BeforeFirstShown();

                        // Extract inner content for the tab host.
                        // IViewContentProvider gives us the content without toolbar/chrome.
                        // Fallback: use ContentPage.Content or the element itself.
                        View content;
                        if (visualElement is IViewContentProvider contentProvider
                            && contentProvider.ViewContent is View extractedContent)
                        {
                            content = extractedContent;

                            // Clear ViewContent so the internal contentView binding in BaseView
                            // doesn't re-evaluate and reclaim the extracted content from _tabContentHost.
                            contentProvider.ViewContent = null;
                        }
                        else if (visualElement is ContentPage contentPage
                                 && contentPage.Content is View pageContent)
                        {
                            content = pageContent;
                        }
                        else if (visualElement is View directView)
                        {
                            content = directView;
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"Cannot extract tab content from {visualElement.GetType().Name}. " +
                                "Implement IViewContentProvider or use a View-based element.");
                        }

                        // Explicitly set BindingContext so it survives reparenting.
                        // Without this, the content inherits the parent CustomTabbedViewBase's
                        // BindingContext (the MainViewModel) instead of the child's ViewModel.
                        content.BindingContext = innerViewModel;

                        customTabbed.AddChildInternal(new ChildTabInfo
                        {
                            View = visualElement,
                            ViewModel = innerViewModel,
                            Content = content,
                            HideInTabBar = tabModel.HideInTabBar
                        });
                    }
                }

                // Selection invokes lifecycle callbacks that can claim shared singleton resources.
                // During root preparation only construct children; select after outgoing cleanup.
                if (customTabbed.ChildCount > 0)
                {
                    var declarations = e.TabbedViewModels.ToList();
                    var customDefaultIndex = declarations.FindIndex(tab => tab.ShouldBeSelectedByDefault);
                    var selectedIndex = customDefaultIndex >= 0 ? customTabbed.ChildCount - declarations.Count + customDefaultIndex : 0;
                    if (RetainedNavigationBridge.Find(parentPage) is { Composing: true } bridge)
                        bridge.PreparedTabIndex = selectedIndex;
                    else if (e.ParentViewModel.PendingRootPreparation is { IsPreparing: true } preparation)
                        preparation.Defer(() => customTabbed.SwitchToAsync(selectedIndex));
                    else
                        await customTabbed.SwitchToAsync(selectedIndex);
                }

                return;
            }

            var tabbedCurrentPage = (TabbedPage)parentPage;
            var oldSelected = tabbedCurrentPage.CurrentPage;
            if (standardSelections.TryGetValue(tabbedCurrentPage, out var oldSubscription))
            {
                tabbedCurrentPage.CurrentPageChanged -= oldSubscription.Handler;
                standardSelections.Remove(tabbedCurrentPage);
            }

            foreach (var tabModel in e.TabbedViewModels)
            {
                var visualElement = await NavigationViewFactory.CreateWithCancellationAsync(_viewLocator, tabModel.TabViewModelType, e.ParentViewModel,
                            e.ParentViewModel.PendingRootPreparation?.CancellationToken ?? default);
                e.ParentViewModel.PendingRootPreparation?.Track(visualElement);
                var innerViewModel = GetViewModelForVE(visualElement);

                innerViewModel.ParentViewModel = e.ParentViewModel;
                e.ParentViewModel.Ownership.Adopt(innerViewModel.Ownership);

                if (visualElement is Page page)
                {
                    AddEvents<T>(page, innerViewModel, true);

                    if (tabModel.Parameters != null)
                    {
                        await innerViewModel.GetParameters(tabModel.Parameters);
                    }

                    await innerViewModel.BeforeFirstShown();

                    tabbedCurrentPage.Children.Add(page);
                }
                else
                {
                    throw new Exception("AddedTabbedViewModels visual element is not a Page");
                }
            }

            var declaredTabs = e.TabbedViewModels.ToList();
            var defaultIndex = declaredTabs.FindIndex(tab => tab.ShouldBeSelectedByDefault);
            if (defaultIndex >= 0)
                tabbedCurrentPage.CurrentPage = tabbedCurrentPage.Children[tabbedCurrentPage.Children.Count - declaredTabs.Count + defaultIndex];

            Page? previousPage = tabbedCurrentPage.CurrentPage;
            bool isHandlingNavigation = false;

            // Call Appearing for the initially selected tab
            if (previousPage != null && (RetainedNavigationBridge.Find(tabbedCurrentPage) == null || previousPage != oldSelected))
            {
                var initialViewModel = GetViewModelForVE(previousPage);
                if (e.ParentViewModel.PendingRootPreparation is { IsPreparing: true } preparation)
                    preparation.Defer(() => tabbedCurrentPage.CurrentPage is { } selected
                        ? GetViewModelForVE(selected).Appearing() : Task.CompletedTask);
                else
                    await initialViewModel.Appearing();
            }

            standardSelections.Add(tabbedCurrentPage, new(TabbedCurrentPageOnCurrentPageChanged));
            tabbedCurrentPage.CurrentPageChanged += TabbedCurrentPageOnCurrentPageChanged;
            TrackScopedSubscription(e.ParentViewModel, () => tabbedCurrentPage.CurrentPageChanged -= TabbedCurrentPageOnCurrentPageChanged);

            async void TabbedCurrentPageOnCurrentPageChanged(object? sender, EventArgs eventArgs)
            {
                if (isHandlingNavigation || e.ParentViewModel.IsDismissed || RetainedNavigationBridge.Find(tabbedCurrentPage) != null)
                    return;
                if (e.ParentViewModel.PendingRootPreparation is { IsPreparing: true })
                {
                    previousPage = tabbedCurrentPage.CurrentPage;
                    return;
                }

                await DispatchLifecycleAsync(e.ParentViewModel, ApplySelectionAsync, "Standard tab selection");
            }

            async Task ApplySelectionAsync()
            {
                var newPage = tabbedCurrentPage.CurrentPage;

                if (previousPage != null && previousPage != newPage)
                {
                    var previousViewModel = GetViewModelForVE(previousPage);
                    var canNavigate = await previousViewModel.CanNavigate();

                    if (!canNavigate)
                    {
                        isHandlingNavigation = true;
                        tabbedCurrentPage.CurrentPage = previousPage;
                        isHandlingNavigation = false;
                        return;
                    }

                    await previousViewModel.DeactivateRetainedAsync();
                }

                if (newPage != null)
                {
                    var newViewModel = GetViewModelForVE(newPage);
                    await newViewModel.Appearing();
                }

                previousPage = newPage;
            }
        }

        private Task AddedFlyoutViewModels<T>(FlyoutViewModelsEventArgs e) where T : ViewModelBase =>
            ComposeAsync(e.ParentViewModel, () => BuildFlyoutAsync<T>(e));

        private async Task BuildFlyoutAsync<T>(FlyoutViewModelsEventArgs e) where T : ViewModelBase
        {
            var parentPage = GetVEFromStackForViewModel(e.ParentViewModel) as FlyoutPage;

            if (parentPage == null)
            {
                throw new Exception("AddedFlyoutViewModels parentPage null");
            }

            var previousModels = ViewModelTree.Collect(parentPage).Where(model => !ReferenceEquals(model, e.ParentViewModel)).ToArray();
            var menuItems = new ObservableCollection<FlyoutMenuItem>();

            var previousFlyoutStack = TemporalFlyoutStack;
            TemporalFlyoutStack = [];
            try
            {
                foreach (var flyoutModel in e.FlyoutModels)
                {
                    var visualElement = await NavigationViewFactory.CreateWithCancellationAsync(_viewLocator, flyoutModel.FlyoutViewModelType, e.ParentViewModel,
                        e.ParentViewModel.PendingRootPreparation?.CancellationToken ?? default);
                    e.ParentViewModel.PendingRootPreparation?.Track(visualElement);

                    var innerViewModel = GetViewModelForVE(visualElement);
                    innerViewModel.ParentViewModel = e.ParentViewModel;
                    e.ParentViewModel.Ownership.Adopt(innerViewModel.Ownership);

                    if (flyoutModel.Parameters != null)
                    {
                        await innerViewModel.GetParameters(flyoutModel.Parameters);
                    }

                    if (visualElement is Page page)
                    {
                        AddEvents<T>(page, innerViewModel);
                    }
                    else if (visualElement is View view)
                    {
                        AddEvents<T>(view, innerViewModel);
                    }

                    TemporalFlyoutStack.Add(visualElement);

                    await innerViewModel.BeforeFirstShown();

                    var idProvider = visualElement as IIdProvider;
                    var id = "No selected id provider"; ;

                    if (idProvider != null)
                    {
                        id = idProvider.GetId();
                    }

                    var titleProvider = visualElement as ITitleProvider;
                    var title = () => "No selected title provider"; ;

                    if (titleProvider != null)
                    {
                        title = () => titleProvider.GetTitle();
                    }

                    var selectedIconProvider = visualElement as ISelectedIconProvider;
                    var selectedIcon = () => "No selected icon provider";

                    if (selectedIconProvider != null)
                    {
                        selectedIcon = () => selectedIconProvider.GetSelectedIcon();
                    }

                    var unselectedIconProvider = visualElement as IUnselectedIconProvider;
                    var unselectedIcon = () => "No unselected icon provider";

                    if (unselectedIconProvider != null)
                    {
                        unselectedIcon = () => unselectedIconProvider.GetUnselectedIcon();
                    }

                    var flyoutItem = new FlyoutMenuItem(id, title, selectedIcon, unselectedIcon, visualElement, pageParam =>
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            parentPage.Detail = pageParam;
                            // A split pane remains visible; MAUI rejects attempts to close it.
                            if (!((IFlyoutPageController)parentPage).ShouldShowSplitMode)
                                parentPage.IsPresented = false;
                        });
                        return true;
                    });

                    if (visualElement is View)
                        flyoutItem.RecreatePopupAsync = () => PreparePopupAsync(
                            () => NavigationViewFactory.CreateAsync(_viewLocator, flyoutModel.FlyoutViewModelType),
                            flyoutModel.Parameters, e.ParentViewModel);

                    menuItems.Add(flyoutItem);
                }

                if (menuItems != null && menuItems.Any())
                {
                    menuItems.First().IsSelected = true;
                }
                else
                {
                    throw new Exception("AddedFlyoutViewModels no menu items defined");
                }

                var flyoutPage = await NavigationViewFactory.CreateWithCancellationAsync(_viewLocator, e.FlyoutViewFlyoutViewModel, null, cancellationToken:
                    e.ParentViewModel.PendingRootPreparation?.CancellationToken ?? default);
                e.ParentViewModel.PendingRootPreparation?.Track(flyoutPage);
                e.ParentViewModel.Ownership.Adopt(GetViewModelForVE(flyoutPage).Ownership);
                var flyoutMenuItems = flyoutPage as IFlyoutMenuItems;

                if (flyoutMenuItems != null)
                {
                    flyoutMenuItems.MenuItems = menuItems;
                }
                else
                {
                    throw new Exception("AddedFlyoutViewModels flyoutPage doesn't implement IFlyoutMenuItems");
                }

                var flyoutMenuClosedEvent = flyoutPage as IFlyoutMenuClosedEvent;

                if (flyoutMenuClosedEvent != null)
                {
                    EventHandler<EventArgs?> closed = (_, args) => FlyoutPage_ClosedFlyout(parentPage, args);
                    flyoutMenuClosedEvent.ClosedFlyout += closed;
                    TrackScopedSubscription(GetViewModelForVE(flyoutPage), () => flyoutMenuClosedEvent.ClosedFlyout -= closed);
                }
                else
                {
                    throw new Exception("AddedFlyoutViewModels flyoutPage doesn't implement IFlyoutMenuClosedEvent");
                }

                var firstItemContent = menuItems.FirstOrDefault()?.Content as Page;

                // var newHasVM = firstItemContent as IHasVM;
                // {
                //     if (newHasVM != null)
                //     {
                //         await newHasVM.ViewModel.Appearing();
                //     }
                // }

                parentPage.Flyout = flyoutPage as Page;
                parentPage.Detail = firstItemContent;
                if (e.ParentViewModel.PendingRootPreparation == null && previousModels.Length != 0)
                {
                    await DismissViewModelsAsync(previousModels, DismissalReason.Removed);
                    UnwireScopedSubscriptions(previousModels);
                }
            }
            finally { TemporalFlyoutStack = previousFlyoutStack; }
        }

        private void FlyoutPage_ClosedFlyout(object? sender, EventArgs? e)
        {
            var parentPage = sender as FlyoutPage ?? CurrentMainPage as FlyoutPage;

            if (parentPage == null)
            {
                throw new Exception("FlyoutPage_ClosedFlyout parentPage null");
            }

            parentPage.Dispatcher.Dispatch(() =>
            {
                if (!((IFlyoutPageController)parentPage).ShouldShowSplitMode)
                    parentPage.IsPresented = false;
            });
        }

        private void ToggledFlyoutVisibility<T>(object? sender, object? e) where T : ViewModelBase
        {
            var preparation = (sender as ViewModelBase)?.PendingRootPreparation;
            var parentPage = (preparation != null ? preparation.Root : CurrentMainPage) as FlyoutPage;

            if (parentPage == null)
            {
                throw new Exception("ToggledFlyoutVisibility parentPage null");
            }

            parentPage.Dispatcher.Dispatch(() =>
            {
                if (!((IFlyoutPageController)parentPage).ShouldShowSplitMode)
                    parentPage.IsPresented = !parentPage.IsPresented;
            });
        }

        private void AddEvents<T>(Page? page, ViewModelBase? viewModel, bool skipAppearing = false) where T : ViewModelBase
        {
            if (page != null && viewModel != null)
            {
                if (pageSubscriptions.TryGetValue(page, out var previous)) previous.Release();
                var release = viewModel.RegisterSubscriptionCleanup(() =>
                {
                    page.Appearing -= Page_Appearing;
                    page.Disappearing -= Page_Disappearing;
                    page.NavigatedTo -= Page_NavigatedTo;
                    page.NavigatingFrom -= Page_NavigatingFrom;
                    page.Loaded -= Page_Loaded;
                    page.Unloaded -= Page_Unloaded;
                    viewModel.AddedTabbedViewModels -= AddedTabbedViewModels<T>;
                    viewModel.AddedFlyoutViewModels -= AddedFlyoutViewModels<T>;
                    viewModel.ToggledFlyoutVisibility -= ToggledFlyoutVisibility<T>;
                    if (page is NavigationPage navigation) NativeNavigationObserver.Detach(navigation);
                    pageSubscriptions.Remove(page);
                });
                scopedSubscriptions.GetOrCreateValue(viewModel).Add(release);
                pageSubscriptions.Add(page, new(release));
            }
            if (page != null)
            {
                if (page is NavigationPage navigation) NativeNavigationObserver.Attach(navigation);
                if (!skipAppearing)
                {
                    page.Appearing -= Page_Appearing;
                    page.Appearing += Page_Appearing;

                    page.Disappearing -= Page_Disappearing;
                    page.Disappearing += Page_Disappearing;
                }

                page.NavigatedTo -= Page_NavigatedTo;
                page.NavigatedTo += Page_NavigatedTo;

                page.NavigatingFrom -= Page_NavigatingFrom;
                page.NavigatingFrom += Page_NavigatingFrom;

                page.Loaded -= Page_Loaded;
                page.Loaded += Page_Loaded;

                page.Unloaded -= Page_Unloaded;
                page.Unloaded += Page_Unloaded;
            }

            if (viewModel != null)
            {
                viewModel.AddedTabbedViewModels -= AddedTabbedViewModels<T>;
                viewModel.AddedTabbedViewModels += AddedTabbedViewModels<T>;

                viewModel.AddedFlyoutViewModels -= AddedFlyoutViewModels<T>;
                viewModel.AddedFlyoutViewModels += AddedFlyoutViewModels<T>;

                viewModel.ToggledFlyoutVisibility -= ToggledFlyoutVisibility<T>;
                viewModel.ToggledFlyoutVisibility += ToggledFlyoutVisibility<T>;
            }
        }

        private void AddEvents<T>(View view, ViewModelBase viewModel) where T : ViewModelBase
        {
            TrackScopedSubscription(viewModel, () =>
            {
                view.Loaded -= View_Loaded;
                view.Unloaded -= View_Unloaded;
            });
            view.Loaded -= View_Loaded;
            view.Loaded += View_Loaded;

            view.Unloaded -= View_Unloaded;
            view.Unloaded += View_Unloaded;
        }

        private static async Task DispatchLifecycleAsync(ViewModelBase model, Func<Task> callback, string operation)
        {
            if (model.IsDismissed) return;
            if (PopupOwnership.DeferLifecycle(model, callback, operation)) return;
            if (RetainedNavigationBridge.Handles(model, operation)) return;
            var nativeCallback = callback;
            callback = () => RetainedNavigationBridge.Handles(model, operation) ? Task.CompletedTask : nativeCallback();
            if (model.PendingRootPreparation is { IsPreparing: true } preparation)
            {
                preparation.Defer(callback);
                return;
            }
            if (model.PendingNavigationCallbacks is { } callbacks)
            {
                callbacks.Defer(model, callback, operation);
                return;
            }
            if (model.PendingRootPreparation is { } activating)
            {
                activating.Defer(callback);
                return;
            }
            if (model.NativeLifecycleObserver?.Invoke(callback, operation) == true) return;
            try { await callback(); }
            catch (Exception error) { NavigationDiagnostics.Report(error, operation); }
        }

        private async void View_Loaded(object? sender, EventArgs e)
        {
            View view;

            Type vmType;

            if (sender is View currentView)
            {
                view = currentView;
            }
            else
            {
                throw new Exception("LegacyNavigationService View_Loaded view null");
            }

            vmType = _viewLocator.FindViewModelForVE(view.GetType());

            if (vmType != null)
            {
                var viewModel = GetViewModelForVE(view);

                await DispatchLifecycleAsync(viewModel, viewModel.Loaded, "Loaded");
            }
        }

        private async void View_Unloaded(object? sender, EventArgs e)
        {
            View view;

            Type vmType;

            if (sender is View currentView)
            {
                view = currentView;
            }
            else
            {
                throw new Exception("LegacyNavigationService Page_Loaded page null");
            }

            vmType = _viewLocator.FindViewModelForVE(view.GetType());

            if (vmType != null)
            {
                var viewModel = GetViewModelForVE(view);

                await DispatchLifecycleAsync(viewModel, viewModel.Unloaded, "Unloaded");
            }
        }

        private async void Page_Appearing(object? sender, EventArgs e)
        {
            Page page;

            Type vmType;

            if (sender is NavigationPage navPage)
            {
                page = navPage.CurrentPage;
            }
            else if (sender is Page currentPage)
            {
                page = currentPage;
            }
            else
            {
                throw new Exception("LegacyNavigationService Page_Appearing page null");
            }

            vmType = _viewLocator.FindViewModelForVE(page.GetType());

            if (vmType != null)
            {
                var viewModel = GetViewModelForVE(page);

                if (!viewModel.IsComingFromPopup)
                {
                    await DispatchLifecycleAsync(viewModel, viewModel.Appearing, "Appearing");
                }
                else
                {
                    viewModel.IsComingFromPopup = false;
                }
            }
        }

        private async void Page_Disappearing(object? sender, EventArgs e)
        {
            // The complete async-void boundary includes sender validation and view-model lookup.
            // Native events cannot return these failures to an awaiting application caller.
            try
            {
                var page = sender switch
                {
                    NavigationPage navigation => navigation.CurrentPage,
                    Page current => current,
                    _ => throw new InvalidOperationException("The disappearing sender is not a page.")
                };
                if (_viewLocator.FindViewModelForVE(page.GetType()) == null) return;
                var viewModel = GetViewModelForVE(page);
                if (!viewModel.IsComingFromPopup)
                    await DispatchLifecycleAsync(viewModel, viewModel.Disappearing, "Disappearing");
            }
            catch (Exception error)
            {
                NavigationDiagnostics.Report(error, "Disappearing");
            }
        }

        private async void Page_NavigatedTo(object? sender, NavigatedToEventArgs e)
        {
            Page page;

            Type vmType;

            if (sender is NavigationPage navPage)
            {
                page = navPage.CurrentPage;
            }
            else if (sender is Page currentPage)
            {
                page = currentPage;
            }
            else
            {
                throw new Exception("LegacyNavigationService Page_NavigatedTo page null");
            }

            vmType = _viewLocator.FindViewModelForVE(page.GetType());

            if (vmType != null)
            {
                var viewModel = GetViewModelForVE(page);

                viewModel.IsComingFromPopup = e.WasPreviousPageACommunityToolkitPopupPage();

                // We have arrived at a real page, so nothing is layered over it any more. Cleared
                // unconditionally rather than only when returning from a popup, so the flag can
                // never leak true and silently suppress behaviour for the rest of the session.
                viewModel.IsPopupOpen = false;

                await DispatchLifecycleAsync(viewModel, viewModel.NavigatedTo, "NavigatedTo");
            }
        }

        /// <summary>
        /// Raised before this page is navigated away from. Used to detect that a CommunityToolkit
        /// popup is being displayed over the page — the toolkit hosts popups in an internal
        /// PopupPage pushed on the modal stack, so showing one looks like navigation.
        /// </summary>
        private void Page_NavigatingFrom(object? sender, NavigatingFromEventArgs e)
        {
            Page? page = null;

            if (sender is NavigationPage navPage)
            {
                // CurrentPage is null when the navigation stack is empty.
                page = navPage.CurrentPage;
            }
            else if (sender is Page currentPage)
            {
                page = currentPage;
            }

            if (page == null)
            {
                return;
            }

            var vmType = _viewLocator.FindViewModelForVE(page.GetType());

            if (vmType != null)
            {
                GetViewModelForVE(page).IsPopupOpen = e.IsDestinationPageACommunityToolkitPopupPage();
            }
        }

        private async void Page_Loaded(object? sender, EventArgs e)
        {
            Page page;

            Type vmType;

            if (sender is NavigationPage navPage)
            {
                page = navPage.CurrentPage;
            }
            else if (sender is Page currentPage)
            {
                page = currentPage;
            }
            else
            {
                throw new Exception("LegacyNavigationService Page_Loaded page null");
            }

            vmType = _viewLocator.FindViewModelForVE(page.GetType());

            if (vmType != null)
            {
                var viewModel = GetViewModelForVE(page);

                await DispatchLifecycleAsync(viewModel, viewModel.Loaded, "Loaded");
            }
        }

        private async void Page_Unloaded(object? sender, EventArgs e)
        {
            Page page;

            Type vmType;

            if (sender is NavigationPage navPage)
            {
                page = navPage.CurrentPage;
            }
            else if (sender is Page currentPage)
            {
                page = currentPage;
            }
            else
            {
                throw new Exception("LegacyNavigationService Page_Loaded page null");
            }

            vmType = _viewLocator.FindViewModelForVE(page.GetType());

            if (vmType != null)
            {
                var viewModel = GetViewModelForVE(page);

                await DispatchLifecycleAsync(viewModel, viewModel.Unloaded, "Unloaded");
            }
        }
    }
}
