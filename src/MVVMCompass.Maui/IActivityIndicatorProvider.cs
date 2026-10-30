namespace MVVMCompass;

/// <summary>Provides an observable activity indicator for a retained destination, independently of the model's command busy state.</summary>
public interface IActivityIndicatorProvider
{
    /// <summary>Gets whether the destination's selector should show an activity indicator.</summary>
    bool IsTabBusy { get; }
}
