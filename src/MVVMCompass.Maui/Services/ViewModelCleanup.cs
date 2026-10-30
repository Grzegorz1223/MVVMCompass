using MVVMCompass.Core;

namespace MVVMCompass.Services;

/// <summary>Ends a group of models while reporting cleanup and cancellation failures.</summary>
internal static class ViewModelCleanup
{
    internal static async Task DismissAsync(IEnumerable<ViewModelBase> models, DismissalReason reason)
    {
        var outgoing = models.Distinct<ViewModelBase>(ReferenceEqualityComparer.Instance).ToArray();
        if (reason == DismissalReason.RootReplaced)
            foreach (var model in outgoing) model.MarkHostReplaced();
        try { await NavigationLifetimeGroup.DismissAsync(outgoing.Select(model => model.Lifetime), reason); }
        catch (Exception error) { NavigationDiagnostics.Report(error, $"Dismiss ({reason})"); }
        foreach (var error in outgoing.SelectMany(model => model.Lifetime.CancellationErrors))
            NavigationDiagnostics.Report(error, "Lifetime cancellation");
    }
}
