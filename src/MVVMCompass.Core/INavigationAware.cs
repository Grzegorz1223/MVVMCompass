namespace MVVMCompass.Core;

/// <summary>Optional lifecycle capabilities for ordinary view models without a framework base class.</summary>
public interface INavigationAware
{
    /// <summary>Acquires active-view resources. The token ends only on permanent dismissal.</summary>
    Task ActivateAsync(CancellationToken lifetimeToken) => Task.CompletedTask;

    /// <summary>Releases active-view resources while retaining the entry and its lifetime.</summary>
    Task DeactivateAsync() => Task.CompletedTask;

    /// <summary>Releases owned resources permanently, including after partial initialization.</summary>
    Task DismissAsync(DismissalReason reason) => Task.CompletedTask;
}

/// <summary>Optional typed initialization, performed before activation and presentation.</summary>
public interface INavigationInitializable<in TParameter>
{
    /// <summary>Initializes a prepared entry; cancellation must leave it safe to dismiss.</summary>
    Task InitializeAsync(TParameter parameter, CancellationToken cancellationToken);
}

/// <summary>An optional asynchronous veto before a coordinated page is covered or removed.</summary>
public interface INavigationGuard
{
    /// <summary>Returns false to retain the current presentation without committing navigation.</summary>
    Task<bool> CanNavigateAsync(CancellationToken cancellationToken);
}
