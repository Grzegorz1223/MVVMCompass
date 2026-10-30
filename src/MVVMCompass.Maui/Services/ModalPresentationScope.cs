namespace MVVMCompass.Services;

internal sealed class ModalPresentationScope : IDisposable
{
    private readonly ModalPresentation presentation;
    private readonly Page? root;
    private readonly List<IModalPresentationObserver> entered = [];
    private bool finished;
    internal IReadOnlyList<IModalPresentationObserver> Observers => entered;

    private ModalPresentationScope(ModalPresentation presentation)
    {
        this.presentation = presentation;
        root = presentation.Window.Page;
        presentation.Window.Destroying += WindowDestroying;
        presentation.Window.PropertyChanged += WindowChanged;
    }

    internal static ModalPresentationScope? Begin(ViewModelBase? model, Window window, ModalPresentationKind kind)
    {
        var origin = (VisualElement?)model?.Navigator?.Screen.View ?? model?.NavigationBinding?.PopupView;
        if (origin == null) return null;
        var context = model?.Navigator?.Context ?? model?.NavigationBinding?.PopupContext;
        return Begin(origin, window, kind, context);
    }

    internal static ModalPresentationScope BeginWindow(NavigationContext active, NavigationContext root, ModalPresentationKind kind) =>
        Begin(active.Deepest.Current!.View, active.Window, kind, active,
            active.ActiveChain().Select(context => (Element?)context.Current?.View).Append(root.StackRoot?.View));

    internal static ModalPresentationScope Begin(VisualElement origin, Window window, ModalPresentationKind kind, NavigationContext? context,
        IEnumerable<Element?>? additionalObservers = null)
    {
        var observers = new List<IModalPresentationObserver>();
        void Include(Element? element)
        {
            for (; element != null; element = element.Parent)
                if (element is IModalPresentationObserver observer && !observers.Contains(observer, ReferenceEqualityComparer.Instance))
                    observers.Add(observer);
        }
        Include(origin);
        foreach (var observer in PopupOwnership.ObserversFor(origin))
            if (!observers.Contains(observer, ReferenceEqualityComparer.Instance)) observers.Add(observer);
        Include(context?.Current?.View);
        for (; context != null; context = context.ParentContext) Include(context.ContainerScreen?.View);
        if (additionalObservers != null) foreach (var element in additionalObservers) Include(element);
        var scope = new ModalPresentationScope(new(kind, origin, window));
        try
        {
            foreach (var observer in observers)
            {
                scope.entered.Add(observer);
                observer.OnModalPresenting(scope.presentation);
                if (scope.finished) throw new InvalidOperationException("The window closed during modal presentation.");
            }
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    private void WindowDestroying(object? sender, EventArgs args) => Dispose();
    private void WindowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Window.Page) && !ReferenceEquals(root, presentation.Window.Page)) Dispose();
    }
    public void Dispose()
    {
        if (finished) return;
        finished = true;
        presentation.Window.Destroying -= WindowDestroying;
        presentation.Window.PropertyChanged -= WindowChanged;
        foreach (var observer in entered)
            try { observer.OnModalDismissed(presentation); }
            catch (Exception error) { NavigationDiagnostics.Report(error, "Modal observer dismissal"); }
        entered.Clear();
    }
}
