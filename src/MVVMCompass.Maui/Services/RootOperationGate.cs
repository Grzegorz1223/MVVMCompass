using System.Runtime.CompilerServices;
using MVVMCompass.Core;

namespace MVVMCompass.Services;

/// <summary>Serializes explicit hosts with compatible legacy root calls on the same window.</summary>
internal sealed class RootOperationGate : IDisposable
{
    private static readonly ConditionalWeakTable<Window, SemaphoreSlim> gates = new();
    private static readonly AsyncLocal<LegacyExecution?> execution = new();
    private readonly SemaphoreSlim gate;
    private LegacyExecution? frame;
    private NavigationCallbackScope? callbacks;
    private RootOperationGate(SemaphoreSlim gate) => this.gate = gate;

    // Deferred presentations yield to roots and popup guards; they never reserve a waiter ahead
    // of navigation or block cancellation processing while another operation owns the window.
    internal static RootOperationGate? TryEnter(Window window)
    {
        var gate = gates.GetValue(window, _ => new(1));
        return gate.Wait(0) ? new(gate) : null;
    }

    internal static RootOperationGate Enter(Window window)
    {
        var gate = gates.GetValue(window, _ => new(1));
        if (!gate.Wait(0)) throw new InvalidOperationException("A root operation is already in progress for this window.");
        var lease = new RootOperationGate(gate) { frame = new(window, execution.Value), callbacks = NavigationCallbackScope.Enter(window) };
        execution.Value = lease.frame;
        return lease;
    }

    internal static async Task<RootOperationGate> EnterAsync(Window window, CancellationToken cancellationToken)
    {
        var gate = gates.GetValue(window, _ => new(1));
        await gate.WaitAsync(cancellationToken);
        return new(gate);
    }

    internal static bool IsLegacyReentrant(Window window)
    {
        if (NavigationCallbackScope.IsActive(window)) return true;
        for (var frame = execution.Value; frame != null; frame = frame.Parent)
            if (frame.Executing && ReferenceEquals(frame.Window, window)) return true;
        return false;
    }

    public void Dispose()
    {
        if (frame != null) frame.Executing = false;
        callbacks?.Dispose();
        gate.Release();
    }

    private sealed class LegacyExecution(Window window, LegacyExecution? parent)
    {
        internal Window Window { get; } = window;
        internal LegacyExecution? Parent { get; } = parent;
        internal volatile bool Executing = true;
    }
}
