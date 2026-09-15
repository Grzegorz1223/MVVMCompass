namespace MVVMCompass.Interfaces
{
    /// <summary>Application-provided notification delivery for the preserved notification categories.</summary>
    public interface INotificationService
    {
        /// <summary>Delivers a notification through the application-provided notification service.</summary>
        void SendNotification(string text, ToastType toastType);
    }
}
