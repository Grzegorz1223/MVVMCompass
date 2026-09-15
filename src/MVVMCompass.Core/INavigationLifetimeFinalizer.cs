namespace MVVMCompass.Core;

// Adapter-owned view connections survive the complete outgoing batch's callbacks.
// Resource ownership and per-entry cleanup retain their existing ordering.
internal interface INavigationLifetimeFinalizer
{
    void DetachView();
    Task ViewDetachment { get; }
}
