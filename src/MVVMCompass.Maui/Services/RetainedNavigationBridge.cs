using System.Runtime.CompilerServices;

namespace MVVMCompass.Services;

/// <summary>Routes existing control commands to their explicit window owner.</summary>
internal sealed class RetainedNavigationBridge
{
    private static readonly ConditionalWeakTable<VisualElement, RetainedNavigationBridge> bindings = new();
    private static readonly ConditionalWeakTable<ViewModelBase, Lifecycle> lifecycles = new();
    internal static void OwnLifecycle(ViewModelBase model, bool custom)
    { lifecycles.Remove(model); lifecycles.Add(model, new(custom)); }
    internal static void ReleaseLifecycle(ViewModelBase model) => lifecycles.Remove(model);
    internal static bool Handles(ViewModelBase model, string operation) => lifecycles.TryGetValue(model, out var value)
        && (operation == "Appearing" || operation == "NavigatedTo" && value.Custom);
    private sealed record Lifecycle(bool Custom);
    internal static RetainedNavigationBridge? Find(VisualElement view) => bindings.TryGetValue(view, out var binding) ? binding : null;
    internal static void Bind(VisualElement view, RetainedNavigationBridge binding)
    { bindings.Remove(view); bindings.Add(view, binding); }
    internal static void Release(VisualElement view) => bindings.Remove(view);
    internal Func<int, Task<bool>>? SelectTab { get; init; }
    internal Func<FlyoutMenuItem, Dictionary<string, object>?, Task>? SelectFlyout { get; init; }
    internal bool Applying { get; set; }
    internal int? PreparedTabIndex { get; set; }
    internal bool Composing { get; set; }
}

internal interface IRetainedTabMaintenance
{
    void RemoveChildrenAfter(int count);
    void ReleaseSubscriptions();
}
