namespace MVVMCompass;

/// <summary>Observable state supplied to a customer loading presentation.</summary>
public sealed class LoadingPresentationContext : BindableObject
{
    /// <summary>Identifies the active loading category.</summary>
    private static readonly BindablePropertyKey LoadingTypePropertyKey = BindableProperty.CreateReadOnly(nameof(LoadingType),
        typeof(LoadingType), typeof(LoadingPresentationContext), LoadingType.Loading);
    /// <summary>Identifies the active loading category.</summary>
    public static readonly BindableProperty LoadingTypeProperty = LoadingTypePropertyKey.BindableProperty;
    /// <summary>Identifies the binding context of the view that declared the loading template.</summary>
    private static readonly BindablePropertyKey OwnerPropertyKey = BindableProperty.CreateReadOnly(nameof(Owner),
        typeof(object), typeof(LoadingPresentationContext), null);
    /// <summary>Identifies the binding context of the view that declared the loading template.</summary>
    public static readonly BindableProperty OwnerProperty = OwnerPropertyKey.BindableProperty;

    /// <summary>Gets the most recent active loading category, or Loading for direct IsBusy state.</summary>
    public LoadingType LoadingType => (LoadingType)GetValue(LoadingTypeProperty);
    /// <summary>Gets the binding context of the view that declared the active loading template.</summary>
    public object? Owner => GetValue(OwnerProperty);

    internal void Update(LoadingType type, object? owner)
    {
        SetValue(LoadingTypePropertyKey, type);
        SetValue(OwnerPropertyKey, owner);
    }

    internal void Clear() => SetValue(OwnerPropertyKey, null);
}

public abstract partial class ViewBase
{
    private readonly object loadingGate = new();
    private readonly List<LoadingToken> loadingTokens = [];

    /// <summary>Identifies custom loading content inherited by the active screens inside this view or container.</summary>
    public static readonly BindableProperty BusyOverlayTemplateProperty = BindableProperty.Create(nameof(BusyOverlayTemplate),
        typeof(DataTemplate), typeof(ViewBase), null, propertyChanged: BusyPresentationChanged);
    /// <summary>Identifies typed customer loading content inherited by active screens inside this view or container.</summary>
    public static readonly BindableProperty LoadingPresentationTemplateProperty = BindableProperty.Create(nameof(LoadingPresentationTemplate),
        typeof(DataTemplate), typeof(ViewBase), null, propertyChanged: BusyPresentationChanged);
    /// <summary>Identifies the backdrop behind runtime loading content. Null inherits; a transparent brush explicitly removes the tint.</summary>
    public static readonly BindableProperty LoadingBackdropProperty = BindableProperty.Create(nameof(LoadingBackdrop),
        typeof(Brush), typeof(ViewBase), null, propertyChanged: (bindable, _, value) =>
        {
            var view = (ViewBase)bindable;
            if (value is Brush brush) SetInheritedBindingContext(brush, view.BindingContext);
            view.NotifyLoadingStateChanged();
        });
    /// <summary>Identifies whether the default indicator is used when no active custom template is supplied.</summary>
    public static readonly BindableProperty UseDefaultBusyIndicatorProperty = BindableProperty.Create(nameof(UseDefaultBusyIndicator),
        typeof(bool), typeof(ViewBase), true, propertyChanged: BusyPresentationChanged);

    /// <summary>Gets or sets a loading template. Null inherits the closest active ancestor's template.</summary>
    /// <remarks>The window/modal root renders one overlay. Content inherits the declaring view's binding context and resources,
    /// is reused while that view/template remains active, and is shown from the deepest active screen's IsBusy state.</remarks>
    public DataTemplate? BusyOverlayTemplate { get => (DataTemplate?)GetValue(BusyOverlayTemplateProperty); set => SetValue(BusyOverlayTemplateProperty, value); }
    /// <summary>Gets or sets a loading template whose binding context reports the loading category and declaring owner.</summary>
    /// <remarks>The closest active declaration wins. On the same view this template takes precedence over BusyOverlayTemplate.</remarks>
    public DataTemplate? LoadingPresentationTemplate
    { get => (DataTemplate?)GetValue(LoadingPresentationTemplateProperty); set => SetValue(LoadingPresentationTemplateProperty, value); }
    /// <summary>Gets or sets the loading backdrop independently of template selection. Null inherits the nearest active ancestor; the library fallback is #33FFFFFF.</summary>
    public Brush? LoadingBackdrop { get => (Brush?)GetValue(LoadingBackdropProperty); set => SetValue(LoadingBackdropProperty, value); }
    /// <summary>Gets or sets whether this view and its active descendants allow the default indicator. The default is true. Custom templates take precedence.</summary>
    /// <remarks>Any active ancestor can disable the fallback. This changes visual presentation only; it does not change IsBusy or navigation guards.</remarks>
    public bool UseDefaultBusyIndicator { get => (bool)GetValue(UseDefaultBusyIndicatorProperty); set => SetValue(UseDefaultBusyIndicatorProperty, value); }

    private static void BusyPresentationChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((ViewBase)bindable).Navigator?.Context.RootContext.View.RefreshBusy();

    internal (bool Requested, LoadingType Type) LoadingState
    {
        get
        {
            lock (loadingGate)
            {
                if (loadingTokens.Count != 0) return (true, loadingTokens[^1].Type);
                return (IsBusy, LoadingType.Loading);
            }
        }
    }

    private IDisposable AddLoadingToken(LoadingType type)
    {
        LoadingToken token;
        lock (loadingGate)
        {
            token = new(this, type);
            loadingTokens.Add(token);
        }
        NotifyLoadingStateChanged();
        return token;
    }

    private void ReleaseLoadingToken(LoadingToken token)
    {
        lock (loadingGate)
        {
            if (!loadingTokens.Remove(token)) return;
        }
        NotifyLoadingStateChanged();
    }

    private bool ClearLoadingTokens()
    {
        LoadingToken[] removed;
        lock (loadingGate)
        {
            if (loadingTokens.Count == 0) return false;
            removed = [.. loadingTokens];
            loadingTokens.Clear();
        }
        foreach (var token in removed) token.Detach();
        return true;
    }

    private void NotifyLoadingStateChanged()
    {
        if (Navigator is not { } navigator) return;
        var root = navigator.Context.RootContext;
        void Refresh()
        {
            if (!root.IsClosed) root.View.RefreshBusy();
        }
        if (root.Window.Dispatcher.IsDispatchRequired) root.Window.Dispatcher.Dispatch(Refresh);
        else Refresh();
    }

    private sealed class LoadingToken(ViewBase owner, LoadingType type) : IDisposable
    {
        private ViewBase? owner = owner;
        internal LoadingType Type { get; } = type;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseLoadingToken(this);
        internal void Detach() => Interlocked.Exchange(ref owner, null);
    }
}
