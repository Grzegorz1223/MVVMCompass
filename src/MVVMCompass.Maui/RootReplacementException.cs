namespace MVVMCompass;

/// <summary>A root operation failed after outgoing lifetimes ended; the old tree cannot be resumed.</summary>
public sealed class RootReplacementException : Exception
{
    internal RootReplacementException(RootReplacementStage stage, bool isReplacementPresented, Exception innerException)
        : base($"Root replacement failed during {stage}. Outgoing lifetimes cannot be resumed; present a fresh root to recover.", innerException)
    {
        Stage = stage;
        IsReplacementPresented = isReplacementPresented;
    }

    /// <summary>The stage that failed after the irreversible teardown boundary.</summary>
    public RootReplacementStage Stage { get; }

    /// <summary>
    /// Whether the replacement is installed in the captured window. An installed candidate
    /// retains its lifetime and is cleaned up by the next root replacement. An uninstalled
    /// candidate has already been dismissed. This does not certify successful native rendering.
    /// </summary>
    public bool IsReplacementPresented { get; }
}

/// <summary>Stages after successful preparation and outgoing-tree dismissal.</summary>
public enum RootReplacementStage
{
    /// <summary>Assigning the prepared page to the captured window.</summary>
    Commitment,
    /// <summary>Running deferred tab selection and lifecycle callbacks.</summary>
    Activation
}
