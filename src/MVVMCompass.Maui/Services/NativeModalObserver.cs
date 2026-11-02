using System.Runtime.CompilerServices;
using MVVMCompass.Core;

namespace MVVMCompass.Services;

/// <summary>Releases models on unowned native modal pages; coordinated scopes and popup sessions clean up their own presentations.</summary>
internal static class NativeModalObserver
{
    private static readonly ConditionalWeakTable<Page, object> owned = new();

    internal static void OwnModal(Page page) => owned.GetValue(page, _ => new());
    internal static void ReleaseModal(Page page) => owned.Remove(page);

    internal static void AttachApplication()
    {
        if (Application.Current is not { } application) return;
        application.ModalPopped -= ModalPopped;
        application.ModalPopped += ModalPopped;
    }

    private static async void ModalPopped(object? sender, ModalPoppedEventArgs args)
    {
        if (owned.TryGetValue(args.Modal, out _)) return;
        try
        {
            await ViewModelCleanup.DismissAsync(ViewModelTree.Collect(args.Modal)
                .Where(model => model.Ownership.Parent == null && !MauiNavigationHost.IsClaimedModel(model)
                    && !NavigationEntryScope.IsOwned(model) && !PopupOwnership.IsOwnedModel(model)), DismissalReason.Back);
        }
        catch (Exception error) { NavigationDiagnostics.Report(error, "Native modal removal"); }
    }
}
