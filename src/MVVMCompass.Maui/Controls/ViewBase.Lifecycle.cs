using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Interfaces;

namespace MVVMCompass;

public abstract partial class ViewBase
{
    /// <summary>Identifies the visual busy overlay. It does not veto navigation.</summary>
    public static readonly BindableProperty IsBusyProperty = BindableProperty.Create(nameof(IsBusy), typeof(bool), typeof(ViewBase), false,
        propertyChanged: (view, _, _) => ((ViewBase)view).NotifyLoadingStateChanged());
    /// <summary>Gets or sets the busy overlay independently of CanNavigate.</summary>
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

    private void AttachModelEvents()
    {
        SetBinding(IsBusyProperty, Binding.Create(static (ViewModelBase model) => model.IsBusy));
        ViewModel.DisplayToastEvent += DisplayToast;
        ViewModel.SendCustomActionEvent += SendCustomAction;
        ViewModel.NotifyLanguageChangeEvent += NotifyLanguageChange;
        ViewModel.ShowLoadingEvent += ShowLoading;
        ViewModel.HideLoadingEvent += HideLoading;
        ViewModel.CustomActionDispatcher = action => Dispatcher.DispatchAsync(action);
        Loaded += ViewLoaded; Unloaded += ViewUnloaded;
        ViewModel.RegisterSubscriptionCleanup(() =>
        {
            ViewModel.DisplayToastEvent -= DisplayToast; ViewModel.SendCustomActionEvent -= SendCustomAction;
            ViewModel.NotifyLanguageChangeEvent -= NotifyLanguageChange; ViewModel.ShowLoadingEvent -= ShowLoading;
            ViewModel.HideLoadingEvent -= HideLoading; Loaded -= ViewLoaded; Unloaded -= ViewUnloaded;
            ClearLoadingTokens();
        });
    }
    private async void ViewLoaded(object? sender, EventArgs args)
    {
        if (ViewModel.IsDismissed) return;
        try { await ViewModel.Loaded(); }
        catch (Exception error) { NavigationDiagnostics.Report(error, "View loaded"); }
    }
    private async void ViewUnloaded(object? sender, EventArgs args)
    {
        if (ViewModel.IsDismissed) return;
        try { await ViewModel.Unloaded(); }
        catch (Exception error) { NavigationDiagnostics.Report(error, "View unloaded"); }
    }
    /// <summary>Displays a Toolkit toast, or routes a notification category to the registered notification service.</summary>
    protected virtual Task DisplayToast(ToastEventArgs args)
    {
        if (args.ToastType is ToastType.Short or ToastType.Long)
            return Toast.Make(args.Message ?? args.Id, args.ToastType == ToastType.Long ? ToastDuration.Long : ToastDuration.Short).Show();
        GetService<INotificationService>().SendNotification(args.Message ?? args.Id, args.ToastType);
        return Task.CompletedTask;
    }
    /// <summary>Handles an application-defined visual action.</summary>
    protected virtual Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);
    /// <summary>Refreshes localized visuals.</summary>
    protected virtual bool NotifyLanguageChange() => false;
    /// <summary>Opens an independent visual loading scope and returns its idempotent cleanup handle.</summary>
    protected virtual IDisposable ShowLoading(LoadingType type) => AddLoadingToken(type);
    /// <summary>Clears all managed loading scopes and direct busy state for legacy force-hide behavior.</summary>
    protected virtual void HideLoading()
    {
        var removed = ClearLoadingTokens();
        void Hide()
        {
            var wasBusy = IsBusy;
            IsBusy = false;
            if (removed && !wasBusy) NotifyLoadingStateChanged();
        }
        if (Dispatcher.IsDispatchRequired) Dispatcher.Dispatch(Hide);
        else Hide();
    }
    /// <summary>Displays a confirmation on the handled page for this view's window.</summary>
    protected Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel) =>
        Navigator.DisplayAlertAsync(title, message, accept, cancel);

    /// <summary>Displays an informational alert on the handled page for this view's window.</summary>
    protected Task DisplayAlertAsync(string title, string message, string cancel) =>
        Navigator.DisplayAlertAsync(title, message, cancel);
}
