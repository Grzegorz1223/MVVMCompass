using System.Runtime.CompilerServices;

namespace MVVMCompass.Services;

/// <summary>Serializes root, content and popup installation on the same window.</summary>
internal sealed class RootOperationGate : IDisposable
{
    private static readonly ConditionalWeakTable<Window, SemaphoreSlim> gates = new();
    private readonly SemaphoreSlim gate;
    private RootOperationGate(SemaphoreSlim gate) => this.gate = gate;

    // Deferred presentations yield to roots and popup guards without reserving a waiter.
    internal static RootOperationGate? TryEnter(Window window)
    {
        var gate = gates.GetValue(window, _ => new(1));
        return gate.Wait(0) ? new(gate) : null;
    }

    internal static async Task<RootOperationGate> EnterAsync(Window window, CancellationToken token)
    {
        var gate = gates.GetValue(window, _ => new(1));
        await gate.WaitAsync(token);
        return new(gate);
    }

    public void Dispose() => gate.Release();
}
