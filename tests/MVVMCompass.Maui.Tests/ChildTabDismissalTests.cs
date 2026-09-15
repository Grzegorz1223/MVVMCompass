using Microsoft.Maui.Controls;
using MVVMCompass;
using MVVMCompass.Interfaces;

// Regression coverage for child ownership during root replacement.
using LegacyNavigationService = MVVMCompass.Services.LegacyNavigationService;

namespace MVVMCompass.Maui.Tests;

/// <summary>
/// Who gets told they are being dismissed when the root page is replaced (logout, forced logout, re-login,
/// update).
/// <para>
/// This is the contract the duplicate-recording bug broke. Feature views are CHILD TABS: their views are
/// extracted into the host's content area, so they are not pages on the navigation stack. Collecting only the
/// stack's <c>BindingContext</c>s dismissed the shell and left every child alive — still subscribed to the
/// singleton <c>IKeywordListener</c>. One live child per past login meant one spoken "start" opened that many
/// recordings against the same encounter, and the backend transcribed the conversation once per recording.
/// </para>
/// <para>
/// Asserted on <c>AddPageViewModels</c> rather than through <c>CreateMainPage</c>, which would need a live
/// Application and Window. This is the unit that regressed; the recording-coordinator and keyword-listener
/// tests only cover the backstops, so a return to BindingContext-only walking would not fail any of them.
/// </para>
/// </summary>
public sealed class ChildTabDismissalTests
{
    [Fact]
    public void ReplacingAHostPageDismissesEveryChildTabBeforeTheHostItself()
    {
        var shell = new StubViewModel();
        var activeChild = new StubViewModel();
        var hiddenChild = new StubViewModel();

        // hiddenChild is added but never shown — the leak did not care whether a tab had ever appeared, and neither
        // does the fix. CurrentTab is deliberately left null here to prove that.
        var host = new FakeTabHost { BindingContext = shell };
        host.AddTab(activeChild);
        host.AddTab(hiddenChild);

        var collected = new List<ViewModelBase>();
        LegacyNavigationService.AddPageViewModels(host, collected);

        // Children first, then the host: each feature tears its own state down before the shell's stop-everything
        // sweep, so that sweep stays a backstop rather than the primary path.
        Assert.Equal(new ViewModelBase[] { activeChild, hiddenChild, shell }, collected);
    }

    [Fact]
    public void APlainPageStillYieldsItsOwnViewModel()
    {
        // The non-tabbed branch — Login, Loading, Update, History. Regressing this would silently stop
        // dismissing every ordinary page.
        var login = new StubViewModel();
        var page = new ContentPage { BindingContext = login };

        var collected = new List<ViewModelBase>();
        LegacyNavigationService.AddPageViewModels(page, collected);

        Assert.Same(login, Assert.Single(collected));
    }

    [Fact]
    public void AHostWithNoViewModelOfItsOwnStillDismissesItsChildren()
    {
        // Order matters more than presence: children are appended before the host's BindingContext is even
        // examined, so a host without one must not cost the children their teardown.
        var activeChild = new StubViewModel();

        var host = new FakeTabHost();
        host.AddTab(activeChild);

        var collected = new List<ViewModelBase>();
        LegacyNavigationService.AddPageViewModels(host, collected);

        Assert.Same(activeChild, Assert.Single(collected));
    }

    [Fact]
    public void AViewModelDoesNotConsiderItsHostReplacedUntilTheNavigationLayerSaysSo()
    {
        // The flag exists because AfterDismissed cannot tell a tab switch from a host replacement, and inferring
        // it from flags the outgoing tab left on the shared parent is what produced the stale-NoDismiss bug. So it
        // must default to false: any view model that has not been told otherwise is mid-tab-switch, and a default
        // of true would make every tab switch tear the encounter down.
        var viewModel = new StubViewModel();

        Assert.False(viewModel.IsHostReplaced);

        // One-way. Replacing the host is the end of the instance, so there is no path back — nothing may reset it
        // and hand a torn-down view model to a keep-the-encounter branch.
        viewModel.MarkHostReplaced();
        Assert.True(viewModel.IsHostReplaced);

        viewModel.MarkHostReplaced();
        Assert.True(viewModel.IsHostReplaced);
    }

    [Fact]
    public void CollectingViewModelsDoesNotByItselfMarkTheirHostReplaced()
    {
        // Collection and marking are separate steps: DismissAllAsync marks the whole tree in its own first pass.
        // Marking during collection instead would leak the flag to callers that only enumerate — GetActiveViewModel
        // style lookups — and a view model wrongly marked skips the keep-the-encounter branch on a plain tab
        // switch, silently discarding the loaded note.
        var shell = new StubViewModel();
        var activeChild = new StubViewModel();

        var host = new FakeTabHost { BindingContext = shell };
        host.AddTab(activeChild);

        var collected = new List<ViewModelBase>();
        LegacyNavigationService.AddPageViewModels(host, collected);

        Assert.All(collected, vm => Assert.False(vm.IsHostReplaced));
    }

    [Fact]
    public void NothingIsCollectedForAMissingPage()
    {
        var collected = new List<ViewModelBase>();

        LegacyNavigationService.AddPageViewModels(null, collected);

        Assert.Empty(collected);
    }

    [Fact]
    public async Task DismissingMarksEveryViewModelAndKeepsGoingWhenOneFails()
    {
        // A feature whose teardown throws must not stop the ones behind it — a half-dismissed tree with live
        // singleton handlers is the leak this whole change exists to remove, and on a forced logout there is no
        // second chance to finish the job.
        var first = new StubViewModel();
        var throws = new ThrowingViewModel();
        var last = new StubViewModel();

        await LegacyNavigationService.DismissAllAsync(new ViewModelBase[] { first, throws, last });

        Assert.True(first.DismissedCount > 0);
        Assert.True(throws.WasCalled);
        Assert.True(last.DismissedCount > 0, "a throw from an earlier view model swallowed the rest of the teardown");

        // Marked regardless of whether their own teardown succeeded — the mark is what tells them their host is
        // going away, and it happens before AfterDismissed precisely so a throw cannot cost them that information.
        Assert.All(new ViewModelBase[] { first, throws, last }, vm => Assert.True(vm.IsHostReplaced));
    }

    [Fact]
    public async Task EveryViewModelIsMarkedBeforeAnyTeardownStarts()
    {
        // Marking is synchronous; teardown awaits network work and UI actions. Interleaving them left the later
        // children unmarked while the first child's AfterDismissed was suspended — and the UI thread is free during
        // that suspension, so one of THEIR queued keyword handlers or start continuations could run, read
        // IsHostReplaced as false, and proceed into work that assumes a live host. That is the whole reason the
        // feature view models gate on the flag, so it has to be set for the entire tree before anything yields.
        var later = new StubViewModel();
        var first = new MarkObservingViewModel { Others = new ViewModelBase[] { later } };

        await LegacyNavigationService.DismissAllAsync(new ViewModelBase[] { first, later });

        Assert.True(
            first.OthersWereAlreadyMarked,
            "a later view model was still unmarked while an earlier one was tearing down");
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private class StubViewModel : ViewModelBase
    {
        public int DismissedCount { get; private set; }

        public override Task AfterDismissed()
        {
            DismissedCount++;
            return base.AfterDismissed();
        }
    }

    /// <summary>
    /// Records, from inside its own teardown, whether the other outgoing view models had already been marked —
    /// and yields, the way real teardown does when it awaits a stop or a UI action.
    /// </summary>
    private sealed class MarkObservingViewModel : StubViewModel
    {
        public IReadOnlyList<ViewModelBase> Others { get; init; } = Array.Empty<ViewModelBase>();

        public bool OthersWereAlreadyMarked { get; private set; }

        public override async Task AfterDismissed()
        {
            OthersWereAlreadyMarked = Others.All(vm => vm.IsHostReplaced);

            await Task.Yield();

            await base.AfterDismissed();
        }
    }

    private sealed class ThrowingViewModel : StubViewModel
    {
        public bool WasCalled { get; private set; }

        public override Task AfterDismissed()
        {
            WasCalled = true;
            throw new InvalidOperationException("teardown failed");
        }
    }

    /// <summary>
    /// A minimal stand-in for <c>CustomTabbedViewBase</c>: a Page that owns child tab view models. Hand-rolled
    /// rather than using the real base class so the test does not depend on MAUI tab-bar construction.
    /// </summary>
    private sealed class FakeTabHost : ContentPage, ICustomTabbedViewBase
    {
        private readonly List<ChildTabInfo> _children = new();

        public void AddTab(ViewModelBase viewModel)
        {
            var content = new ContentView();
            _children.Add(new ChildTabInfo
            {
                View = content,
                ViewModel = viewModel,
                Content = content,
            });
        }

        public IReadOnlyList<ChildTabInfo> Children => _children;

        public ChildTabInfo? CurrentTab => null;

        public int ChildCount => _children.Count;

        public bool IsTabBarEnabled { get; set; } = true;

        public void AddChildInternal(ChildTabInfo child) => _children.Add(child);

        public Task<bool> SwitchToAsync(int index) => throw new NotSupportedException();

        public Task<bool> SwitchToAsync(Type viewModelType) => throw new NotSupportedException();
    }
}
