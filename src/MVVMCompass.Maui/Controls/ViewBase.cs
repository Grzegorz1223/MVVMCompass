using MVVMCompass.Core;
using MVVMCompass.Interfaces;

namespace MVVMCompass;

/// <summary>A screen body hosted beneath a persistent navigation toolbar.</summary>
public abstract partial class ViewBase : ContentView, IHasVM
{
    /// <summary>Creates a screen and binds it to its model.</summary>
    protected ViewBase(ViewModelBase viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        BindingContext = viewModel;
        AttachModelEvents();
    }

    /// <summary>Identifies the screen's toolbar configuration.</summary>
    public static readonly BindableProperty ToolbarProperty = BindableProperty.Create(nameof(Toolbar),
        typeof(NavigationToolbarDefinition), typeof(ViewBase), propertyChanged: (view, _, _) => ((ViewBase)view).BindToolbar());
    /// <summary>Gets the screen's model and authoritative CanNavigate override.</summary>
    public ViewModelBase ViewModel { get; }
    /// <summary>Gets or sets per-screen toolbar overrides; null inherits host defaults.</summary>
    public NavigationToolbarDefinition? Toolbar
    { get => (NavigationToolbarDefinition?)GetValue(ToolbarProperty); set => SetValue(ToolbarProperty, value); }
    /// <summary>Gets navigation bound to this entry's origin. Available during initialization and activation.</summary>
    internal ScreenNavigator Navigator { get; set; } = null!;

    /// <summary>Binds screen-specific toolbar content to the screen's model.</summary>
    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        BindToolbar();
    }

    private void BindToolbar()
    {
        if (Toolbar != null) SetInheritedBindingContext(Toolbar, BindingContext);
    }
}

/// <summary>A strongly typed custom screen body.</summary>
public class ViewBase<TViewModel> : ViewBase where TViewModel : ViewModelBase
{
    /// <summary>Creates a screen for the supplied model.</summary>
    public ViewBase(TViewModel viewModel) : base(viewModel) { }
    /// <summary>Gets the strongly typed model.</summary>
    public new TViewModel ViewModel => (TViewModel)base.ViewModel;
}

/// <summary>A factory that owns a model before constructing or initializing its screen.</summary>
internal abstract class ScreenFactory
{
    private protected ScreenFactory() { }
    internal abstract Task<NavigationScreenEntry> PrepareAsync(NavigationContext owner,
        Action<NavigationEntry<ViewModelBase>> own, Dictionary<string, object>? parameters, CancellationToken cancellationToken);

    /// <summary>Creates an AOT-safe factory with explicit model and view construction.</summary>
    public static ScreenFactory Create<TViewModel>(Func<TViewModel> createModel,
        Func<TViewModel, ViewBase<TViewModel>> createView, Dictionary<string, object>? parameters = null)
        where TViewModel : ViewModelBase => new TypedFactory<TViewModel>(createModel, createView, parameters);

    private sealed class TypedFactory<T>(Func<T> createModel, Func<T, ViewBase<T>> createView,
        Dictionary<string, object>? initialParameters) : ScreenFactory where T : ViewModelBase
    {
        internal override async Task<NavigationScreenEntry> PrepareAsync(NavigationContext owner,
            Action<NavigationEntry<ViewModelBase>> own, Dictionary<string, object>? parameters, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(createModel);
            ArgumentNullException.ThrowIfNull(createView);
            cancellationToken.ThrowIfCancellationRequested();
            var model = createModel() ?? throw new InvalidOperationException("The model factory returned null.");
            var entry = owner.ClaimScreen(model);
            own(entry);
            model.IsModal = owner.IsModal;
            var view = createView(model) ?? throw new InvalidOperationException("The screen factory returned null.");
            if (!ReferenceEquals(view.ViewModel, model) || view.Parent != null)
                throw new InvalidOperationException("The factory must return a detached screen bound to its supplied model.");
            var screen = new NavigationScreenEntry(view, entry);
            view.Navigator = new(owner, screen);
            model.Navigator = view.Navigator;
            var data = new Dictionary<string, object>(initialParameters ?? []);
            if (parameters != null) foreach (var item in parameters) data[item.Key] = item.Value;
            if (data.Count != 0) await model.GetParameters(data);
            cancellationToken.ThrowIfCancellationRequested();
            await model.BeforeFirstShown();
            cancellationToken.ThrowIfCancellationRequested();
            return screen;
        }
    }
}

/// <summary>An owned screen with a stable entry and retained body.</summary>
internal sealed class NavigationScreenEntry
{
    internal NavigationScreenEntry(ViewBase view, NavigationEntry<ViewModelBase> entry) { View = view; Entry = entry; }
    /// <summary>Gets the screen body.</summary>
    public ViewBase View { get; }
    /// <summary>Gets the portable identity, state, and lifetime.</summary>
    public NavigationEntry<ViewModelBase> Entry { get; }
    /// <summary>Gets the model.</summary>
    public ViewModelBase ViewModel => Entry.ViewModel;
    internal NavigationContext? Children { get; set; }
}
