using Microsoft.Maui.Dispatching;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Views;
using MVVMCompass.Interfaces;

namespace MVVMCompass;

/// <summary>Typed Toolkit popup whose result completion includes navigation-owned model cleanup.</summary>
public abstract class PopupViewBase<T, TResult> : Popup<TResult>, IHasVM, Services.INativePopupClose where T : ViewModelBase
{
    /// <summary>Gets the ViewModel that owns this popup.</summary>
    public T ViewModel { get; }

    ViewModelBase IHasVM.ViewModel => ViewModel;

    /// <summary>Resolves a required service from the entry provider, or the application provider when no entry scope is owned. Throws if unavailable.</summary>
    public TService GetService<TService>() => Current.GetService<TService>() ?? throw new InvalidOperationException("Cannot resolve TService");

    /// <summary>Gets the service provider for this entry, falling back to the initialized application provider.</summary>
    public IServiceProvider Current
    {
        get
        {
            if (Services.NavigationEntryScope.For(this) is { } entryServices) return entryServices;
            IPlatformApplication? app = IPlatformApplication.Current;
            if (app == null)
                throw new InvalidOperationException("Cannot resolve current application. Services should be accessed after MauiProgram initialization.");
            return app.Services;
        }
    }

    /// <summary>Typed Toolkit popup whose result completion includes navigation-owned model cleanup.</summary>
    public PopupViewBase(T viewModel)
    {
        ViewModel = viewModel;
        BindingContext = viewModel;

        CanBeDismissedByTappingOutsideOfPopup = false;
        Services.PopupOwnership.Observe(this);

        ViewModel.DisplayToastEvent += DisplayToast;
        ViewModel.SendCustomActionEvent += SendCustomAction;
        ViewModel.CustomActionDispatcher = action => Dispatcher.DispatchAsync(action);
        ViewModel.NotifyLanguageChangeEvent += NotifyLanguageChange;
        ViewModel.ShowLoadingEvent += ShowLoading;
        ViewModel.HideLoadingEvent += HideLoading;
        ViewModel.RegisterSubscriptionCleanup(() =>
        {
            ViewModel.DisplayToastEvent -= DisplayToast; ViewModel.SendCustomActionEvent -= SendCustomAction;
            ViewModel.NotifyLanguageChangeEvent -= NotifyLanguageChange;
            ViewModel.ShowLoadingEvent -= ShowLoading; ViewModel.HideLoadingEvent -= HideLoading;
        });
    }

    /// <summary>Closes this popup and awaits owned cleanup, returning the supplied result or its default value.</summary>
    public override async Task CloseAsync(CancellationToken token = new CancellationToken())
    {
        await Services.PopupOwnership.CloseAsync(this, () => base.CloseAsync(token), token);
    }

    Task Services.INativePopupClose.CloseNativeAsync(CancellationToken cancellationToken) => base.CloseAsync(cancellationToken);

    /// <summary>Closes this popup and awaits owned cleanup, returning the supplied result or its default value.</summary>
    public override async Task CloseAsync(TResult result, CancellationToken token = new CancellationToken())
    {
        await Services.PopupOwnership.CloseAsync(this, () => base.CloseAsync(result, token), token, hasResult: true);
    }

    /// <summary>Delivers a notification through the application-provided notification service.</summary>
    public void SendNotification(string text, ToastType toastType)
    {
        GetService<INotificationService>().SendNotification(text, toastType);
    }

    /// <summary>Creates a short or long Toolkit toast; notification categories require the notification service.</summary>
    public IToast GetToast(string text, ToastType toastType)
    {
        switch (toastType)
        {
            case ToastType.Long:
                return Toast.Make(text, ToastDuration.Long);
            case ToastType.Short:
                return Toast.Make(text, ToastDuration.Short);
            default:
                throw new InvalidOperationException("Wrong type of Toast");
        }
    }

    /// <summary>Handles or dispatches a toast request and awaits its presentation callback.</summary>
    protected virtual Task DisplayToast(ToastEventArgs args) => Task.CompletedTask;

    /// <summary>Handles or dispatches an application-defined action and returns its result, which may be null.</summary>
    protected virtual Task<object?> SendCustomAction(CustomActionEventArgs args) => Task.FromResult<object?>(null);

    /// <summary>Requests a localization refresh and returns whether the view handled it.</summary>
    protected virtual bool NotifyLanguageChange() => false;

    /// <summary>Shows the requested loading presentation and returns its cleanup handle; an unhandled model request returns null.</summary>
    protected virtual IDisposable ShowLoading(LoadingType loadingType) => new EmptyLoading();
    private sealed class EmptyLoading : IDisposable { public void Dispose() { } }

    /// <summary>Hides the active loading presentation.</summary>
    protected virtual void HideLoading() { }
}
