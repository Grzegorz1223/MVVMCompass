using System.Windows.Input;
using MVVMCompass.Interfaces;

namespace MVVMCompass.Sample;

internal abstract class ProbeViewModel : ViewModelBase
{
    protected ProbeViewModel(ILegacyNavigationService navigation, ScenarioLog log)
    {
        Navigation = navigation;
        Log = log;
        ParentDuringConstruction = ParentViewModel;
        Log.Write($"{Name} constructed; parent={ParentViewModel?.GetType().Name ?? "none"}");
    }
    public ILegacyNavigationService Navigation { get; }
    public ScenarioLog Log { get; }
    public Guid Id { get; } = Guid.NewGuid();
    public string Name => $"{GetType().Name}/{Id.ToString()[..6]}";
    public string Caption { get; private set; } = "Navigation instance";
    public string ParameterSummary { get; private set; } = "No parameters";
    public ViewModelBase? ParentDuringConstruction { get; }
    public bool AllowNavigation { get; set; } = true;
    public int Appearances { get; private set; }
    public int Deactivations { get; private set; }
    public int DismissalCallbacks { get; private set; }
    public bool IsLifetimeCancelled => LifetimeToken.IsCancellationRequested;

    public override Task GetParameters(Dictionary<string, object> parameters)
    {
        if (parameters.TryGetValue("caption", out var caption)) Caption = caption.ToString()!;
        ParameterSummary = string.Join(", ", parameters.Select(pair => $"{pair.Key}={pair.Value}"));
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(ParameterSummary));
        return Record($"parameters: {ParameterSummary}");
    }
    public override Task BeforeFirstShown() => Record("BeforeFirstShown");
    public override Task Appearing() { Appearances++; return Record("Appearing"); }
    public override Task Disappearing() => Record("Disappearing");
    public override Task NavigatedTo() => Record("NavigatedTo");
    public override Task Loaded() => Record("Loaded");
    public override Task Unloaded() => Record("Unloaded");
    public override Task Deactivated() { Deactivations++; return Record("Deactivated (retained)"); }
    public override Task AfterDismissed()
    {
        DismissalCallbacks++;
        return Record($"AfterDismissed terminal={IsDismissed}, replaced={IsHostReplaced}, cancelled={IsLifetimeCancelled}");
    }
    public override async Task<bool> CanNavigate()
    {
        await Task.Delay(100);
        await Record($"CanNavigate={AllowNavigation}");
        return AllowNavigation;
    }
    public override Task OnActiveTabChanged(ViewModelBase child) => Record($"active child={((ProbeViewModel)child).Name}");
    protected Task Record(string action) { Log.Write($"{Name} {action}"); return Task.CompletedTask; }
    public Task<object?> RequestActionAsync() => SendCustomAction(new CustomActionEventArgs("echo", "Returned from the owning dispatcher"));
    public Task ToastAsync(ToastType duration) => DisplayToast(new ToastEventArgs("toast", duration, "Navigation helper"));
    public void RefreshLanguage() => NotifyLanguageChange();
    public async Task LoadingAsync()
    {
        using (ShowLoading(LoadingType.Loading)) await Task.Delay(600);
        HideLoading();
    }
    public ICommand FailingCommand => CreateCommand((Func<Task>)(() => Task.FromException(new InvalidOperationException("Expected command failure"))));
}

internal sealed class CatalogViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log);
internal sealed class DetailViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log);
internal sealed class ModalViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log);
internal sealed class PopupViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log)
{
    public Func<Task>? Cleanup { get; set; }
    public override async Task AfterDismissed() { await base.AfterDismissed(); if (Cleanup != null) await Cleanup(); }
}
internal sealed class WindowViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log);
internal sealed class FirstViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log);
internal sealed class SecondViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log);
internal sealed class MenuViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log);
internal sealed class LateViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log);

internal sealed class FailingViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log)
{
    public override Task BeforeFirstShown() => throw new InvalidOperationException("Deliberate root preparation failure");
}

internal sealed class TabViewModel(ILegacyNavigationService navigation, ScenarioLog log, SharedSession session)
    : ProbeViewModel(navigation, log), IActivityIndicatorProvider
{
    private bool tabBusy;
    private bool failActivation;
    public bool IsTabBusy { get => tabBusy; set => SetProperty(ref tabBusy, value); }
    public override Task GetParameters(Dictionary<string, object> parameters)
    {
        failActivation = parameters.TryGetValue("failActivation", out var fail) && fail is true;
        return base.GetParameters(parameters);
    }
    public override async Task BeforeFirstShown()
    {
        session.Subscribe(Id);
        await base.BeforeFirstShown();
    }
    public override async Task Appearing()
    {
        session.Claim(Id);
        await base.Appearing();
        if (failActivation) throw new InvalidOperationException("Deliberate root activation failure");
    }
    public override async Task Deactivated() { session.Release(Id); await base.Deactivated(); }
    public override async Task AfterDismissed()
    {
        session.Release(Id);
        if (IsDismissed) session.Unsubscribe(Id);
        await base.AfterDismissed();
    }
}

internal sealed class CustomTabsViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log)
{
    private bool failPreparation;
    private bool failActivation;
    public override Task GetParameters(Dictionary<string, object> parameters)
    {
        failPreparation = parameters.TryGetValue("failPreparation", out var prepare) && prepare is true;
        failActivation = parameters.TryGetValue("failActivation", out var activate) && activate is true;
        return base.GetParameters(parameters);
    }
    public override async Task BeforeFirstShown()
    {
        await base.BeforeFirstShown();
        await AddTabbedViewModels(new TabbedViewModelsEventArgs(new[]
        {
            new TabModel(typeof(TabViewModel), parameters: new() { ["caption"] = "First" }),
            new TabModel(typeof(TabViewModel), shouldBeSelectedByDefault: true, parameters: new() { ["caption"] = "Second", ["failActivation"] = failActivation }),
            new TabModel(typeof(TabViewModel), parameters: new() { ["caption"] = "Hidden" }, hideInTabBar: true)
        }, this));
        if (failPreparation) throw new InvalidOperationException("Deliberate composed root preparation failure");
    }
}

internal sealed class StandardTabsViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log)
{
    public override async Task BeforeFirstShown()
    {
        await base.BeforeFirstShown();
        // The type-only declaration remains executable.
        await AddTabbedViewModels(new TabbedViewModelsEventArgs(new[] { typeof(FirstViewModel), typeof(SecondViewModel) }, this));
    }
}

internal sealed class FlyoutViewModel(ILegacyNavigationService navigation, ScenarioLog log) : ProbeViewModel(navigation, log)
{
    public override async Task BeforeFirstShown()
    {
        await base.BeforeFirstShown();
        await AddFlyoutViewModels(new FlyoutViewModelsEventArgs(new[]
        {
            new FlyoutModel(typeof(FirstViewModel), new() { ["caption"] = "First retained page" }),
            new FlyoutModel(typeof(SecondViewModel), new() { ["caption"] = "Second retained page" }),
            new FlyoutModel(typeof(PopupViewModel))
        }, this, typeof(MenuViewModel)));
    }
}
