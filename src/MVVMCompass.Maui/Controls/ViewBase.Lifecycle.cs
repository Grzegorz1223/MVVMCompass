using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using Microsoft.Maui.Dispatching;
using MVVMCompass.Interfaces;

namespace MVVMCompass;

public abstract partial class ViewBase
{
    /// <summary>Identifies the visual busy overlay. It does not veto navigation.</summary>
    public static readonly BindableProperty IsBusyProperty = BindableProperty.Create(nameof(IsBusy), typeof(bool), typeof(ViewBase), false,
        propertyChanged: (view, _, _) => ((ViewBase)view).Navigator?.Context.RootContext.RefreshState());
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
    /// <summary>Shows a visual busy indicator and returns a handle that hides it.</summary>
    protected virtual IDisposable ShowLoading(LoadingType type) { IsBusy = true; return new LoadingHandle(this); }
    /// <summary>Hides the visual busy indicator.</summary>
    protected virtual void HideLoading() => IsBusy = false;
    private sealed class LoadingHandle(ViewBase view) : IDisposable { public void Dispose() => view.IsBusy = false; }
    /// <summary>Displays a confirmation on the handled page for this view's window.</summary>
    protected Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel) =>
        Navigator.DisplayAlertAsync(title, message, accept, cancel);
}
