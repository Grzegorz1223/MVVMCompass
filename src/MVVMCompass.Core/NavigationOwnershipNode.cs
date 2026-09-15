namespace MVVMCompass.Core;

/// <summary>An explicit owner of child lifetimes and terminal resource cleanup.</summary>
public sealed class NavigationOwnershipNode
{
    internal static readonly object Gate = new();
    private List<NavigationOwnershipNode>? children;
    private List<Func<Task>>? cleanup;
    private List<Func<Task>>? finalCleanup;
    private NavigationOwnershipNode? parent;

    internal NavigationOwnershipNode(NavigationLifetime lifetime, object? owner)
    { Lifetime = lifetime; Owner = owner; }

    private Guid id;
    /// <summary>Gets a stable identity for inspecting the ownership tree.</summary>
    public Guid Id
    {
        get { lock (Gate) { if (id == Guid.Empty) id = Guid.NewGuid(); return id; } }
    }
    /// <summary>Gets the object represented by this node, when supplied by its adapter.</summary>
    public object? Owner { get; internal set; }
    /// <summary>Gets the permanent lifetime of this owner.</summary>
    public NavigationLifetime Lifetime { get; }
    /// <summary>Gets the explicit parent. A detached node must be cleaned by its caller.</summary>
    public NavigationOwnershipNode? Parent { get { lock (Gate) return parent; } }
    /// <summary>Gets children in adoption order. Terminal cleanup runs in this order before the owner.</summary>
    public IReadOnlyList<NavigationOwnershipNode> Children { get { lock (Gate) return children?.ToArray() ?? []; } }

    internal bool HasChildren { get { lock (Gate) return children is { Count: > 0 }; } }

    /// <summary>Adopts a detached child. Duplicate adoption is a no-op; cycles and multiple owners are rejected.</summary>
    public void Adopt(NavigationOwnershipNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        lock (Gate)
        {
            ValidateLive(); child.ValidateLive();
            if (child.parent == this) return;
            if (child.parent != null) throw new InvalidOperationException("The child already has an owner. Detach it before transferring ownership.");
            for (NavigationOwnershipNode? node = this; node != null; node = node.parent)
                if (node == child) throw new InvalidOperationException("Navigation ownership cannot contain a cycle.");
            (children ??= []).Add(child); child.parent = this;
        }
    }

    /// <summary>Transfers cleanup responsibility back to the caller without ending a live child's lifetime.</summary>
    public void Detach(NavigationOwnershipNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        lock (Gate)
        {
            ValidateLive(); child.ValidateLive();
            if (child.parent != this) throw new InvalidOperationException("The child does not belong to this owner.");
            children!.Remove(child); child.parent = null;
        }
    }

    /// <summary>Registers explicitly owned resource cleanup after child and owner callbacks, in registration order.</summary>
    /// <remarks>Register each resource once. Injected services are not inferred or disposed by this API.</remarks>
    /// <param name="release">The asynchronous release operation.</param>
    /// <param name="runLast">Runs after ordinary resource callbacks; intended for their enclosing service scope.</param>
    public void RegisterCleanup(Func<Task> release, bool runLast = false)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (Gate) { ValidateLive(); (runLast ? finalCleanup ??= [] : cleanup ??= []).Add(release); }
    }

    /// <summary>Gets this node and all explicit descendants in child-before-parent order.</summary>
    public IReadOnlyList<NavigationOwnershipNode> Snapshot()
    {
        lock (Gate)
        {
            List<NavigationOwnershipNode> result = [];
            Visit(this);
            return result;
            void Visit(NavigationOwnershipNode node)
            { if (node.children != null) foreach (var child in node.children) Visit(child); result.Add(node); }
        }
    }

    internal void Mark(DismissalReason reason)
    {
        lock (Gate)
            Visit(this);
        void Visit(NavigationOwnershipNode node)
        {
            if (node.Lifetime.IsDismissed) return;
            if (node.children != null) foreach (var child in node.children) Visit(child);
            node.Lifetime.MarkOnly(reason);
        }
    }

    internal bool Contains(NavigationLifetime lifetime)
    {
        lock (Gate)
            for (var node = lifetime.Ownership; node != null; node = node.parent)
                if (node == this) return true;
        return false;
    }

    internal Func<Task>[] CleanupCallbacks
    {
        get
        {
            lock (Gate)
            {
                if (cleanup == null || cleanup.Count == 0) return finalCleanup?.ToArray() ?? [];
                if (finalCleanup == null || finalCleanup.Count == 0) return cleanup.ToArray();
                var result = new Func<Task>[cleanup.Count + finalCleanup.Count];
                cleanup.CopyTo(result); finalCleanup.CopyTo(result, cleanup.Count);
                return result;
            }
        }
    }
    internal void Complete()
    {
        lock (Gate)
        {
            parent?.children?.Remove(this); parent = null;
            cleanup?.Clear();
            finalCleanup?.Clear();
        }
    }
    private void ValidateLive()
    { if (Lifetime.IsDismissed) throw new InvalidOperationException("Terminal ownership cannot acquire or transfer resources."); }
}
