#if ANDROID
using System.ComponentModel;
using AndroidX.Activity;

namespace MVVMCompass;

internal sealed partial class NavigationContext
{
    private AndroidBackRegistration? androidBack;
    partial void ConnectPlatformBack() => androidBack = new(this);
    partial void DisconnectPlatformBack() { androidBack?.Dispose(); androidBack = null; }

    // MAUI's predictive-back registration recognizes native page stacks only. Custom body history
    // needs its own AndroidX callback, enabled before the system decides to animate back-to-home.
    private sealed class AndroidBackRegistration : IDisposable
    {
        private readonly NavigationContext context;
        private ComponentActivity? activity;
        private BackCallback? callback;

        internal AndroidBackRegistration(NavigationContext context)
        {
            this.context = context;
            context.PropertyChanged += Changed;
            context.Window.HandlerChanged += HandlerChanged;
            context.Window.ModalPushed += ModalPushed;
            context.Window.ModalPopped += ModalPopped;
            Attach();
        }

        private void HandlerChanged(object? sender, EventArgs args) => Attach();
        private void ModalPushed(object? sender, ModalPushedEventArgs args) => Update();
        private void ModalPopped(object? sender, ModalPoppedEventArgs args) => Update();
        private void Changed(object? sender, PropertyChangedEventArgs args) => Update();
        private void Attach()
        {
            var next = context.Window.Handler?.PlatformView as ComponentActivity;
            if (activity == next) return;
            ReleaseCallback();
            activity = next;
            if (activity != null)
            {
                callback = new(context, activity);
                activity.OnBackPressedDispatcher.AddCallback(activity, callback);
                Update();
            }
        }
        private void Update()
        {
            if (callback != null) callback.Enabled = context.ParentContext == null && Services.PopupOwnership.HasGuardedPopup(context.Window)
                || context.IsActive && (context.CanGoBack || context.IsModal || context.ActiveChain().Any(item => item.IsFlyoutOpen));
        }
        private void ReleaseCallback()
        {
            callback?.Remove(); callback?.Dispose(); callback = null; activity = null;
        }
        public void Dispose()
        {
            context.PropertyChanged -= Changed;
            context.Window.HandlerChanged -= HandlerChanged;
            context.Window.ModalPushed -= ModalPushed;
            context.Window.ModalPopped -= ModalPopped;
            ReleaseCallback();
        }

        private sealed class BackCallback(NavigationContext context, ComponentActivity activity) : OnBackPressedCallback(false)
        {
            public override void HandleOnBackPressed()
            {
                if (context.RequestPlatformBack()) return;
                Enabled = false;
                activity.OnBackPressedDispatcher.OnBackPressed();
            }
        }
    }
}
#endif
