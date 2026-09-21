namespace MVVMCompass;

/// <summary>Determines scheduling and leave-guard policy for application-owned root changes.</summary>
public enum RootTransitionMode
{
    /// <summary>Respects outgoing leave guards and normal request ordering.</summary>
    Normal,
    /// <summary>Supersedes uncommitted normal requests and bypasses ordinary leave vetoes.</summary>
    Enforced
}

/// <summary>Options for replacing the root of an explicitly owned window.</summary>
public sealed record RootTransitionOptions
{
    /// <summary>Gets the transition policy. Enforcement preserves ownership and terminal cleanup.</summary>
    public RootTransitionMode Mode { get; init; } = RootTransitionMode.Normal;
    /// <summary>Gets an optional nonempty key that supersedes earlier uncommitted normal work with the same key.</summary>
    /// <remarks>Only Normal requests support coalescing. Enforced requests retain their relative order.</remarks>
    public string? CoalescingKey { get; init; }
}

/// <summary>An admitted root request whose result can be observed outside its submitting callback.</summary>
public sealed class RootTransitionHandle
{
    internal RootTransitionHandle(Task<NavigationResult> completion) => Completion = completion;
    /// <summary>Completes when the request and required cleanup settle, including rejection and cancellation.</summary>
    /// <remarks>Never await this task inside the navigation callback that submitted the request.</remarks>
    public Task<NavigationResult> Completion { get; }
}
