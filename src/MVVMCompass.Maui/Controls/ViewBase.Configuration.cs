using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using MVVMCompass.Services;

namespace MVVMCompass;

public abstract partial class ViewBase
{
    /// <summary>Identifies the optional right-swipe Back gesture for this screen or container's active body.</summary>
    public static readonly BindableProperty IsBackSwipeEnabledProperty = BindableProperty.Create(nameof(IsBackSwipeEnabled), typeof(bool), typeof(ViewBase), false,
        propertyChanged: (view, _, _) => ((ViewBase)view).Navigator?.Context.RootContext.RefreshState());
    /// <summary>Enables a right swipe on the active body. CanNavigate guards it just like toolbar and platform Back.</summary>
    public bool IsBackSwipeEnabled { get => (bool)GetValue(IsBackSwipeEnabledProperty); set => SetValue(IsBackSwipeEnabledProperty, value); }
    /// <summary>Identifies Title.</summary>
    public static readonly BindableProperty TitleProperty = BindableProperty.Create(nameof(Title), typeof(string), typeof(ViewBase), string.Empty, propertyChanged: VisualChanged);
    /// <summary>The default toolbar title.</summary>
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>Identifies ToolbarCenterContent.</summary>
    public static readonly BindableProperty ToolbarCenterContentProperty = BindableProperty.Create(nameof(ToolbarCenterContent), typeof(View), typeof(ViewBase), null, propertyChanged: VisualChanged);
    /// <summary>Persistent custom content in the toolbar center.</summary>
    public View? ToolbarCenterContent { get => (View?)GetValue(ToolbarCenterContentProperty); set => SetValue(ToolbarCenterContentProperty, value); }
    /// <summary>Identifies ToolbarCenterTemplate.</summary>
    public static readonly BindableProperty ToolbarCenterTemplateProperty = BindableProperty.Create(nameof(ToolbarCenterTemplate), typeof(DataTemplate), typeof(ViewBase), null, propertyChanged: VisualChanged);
    /// <summary>The toolbar center template.</summary>
    public DataTemplate? ToolbarCenterTemplate { get => (DataTemplate?)GetValue(ToolbarCenterTemplateProperty); set => SetValue(ToolbarCenterTemplateProperty, value); }
    /// <summary>Identifies ToolbarItemTemplate.</summary>
    public static readonly BindableProperty ToolbarItemTemplateProperty = BindableProperty.Create(nameof(ToolbarItemTemplate), typeof(DataTemplate), typeof(ViewBase), null, propertyChanged: VisualChanged);
    /// <summary>The template for each right-side toolbar button.</summary>
    public DataTemplate? ToolbarItemTemplate { get => (DataTemplate?)GetValue(ToolbarItemTemplateProperty); set => SetValue(ToolbarItemTemplateProperty, value); }
    /// <summary>Identifies ToolbarLeadingTemplate.</summary>
    public static readonly BindableProperty ToolbarLeadingTemplateProperty = BindableProperty.Create(nameof(ToolbarLeadingTemplate), typeof(DataTemplate), typeof(ViewBase), null, propertyChanged: VisualChanged);
    /// <summary>The template for the automatic Back, Menu, or Close button.</summary>
    public DataTemplate? ToolbarLeadingTemplate { get => (DataTemplate?)GetValue(ToolbarLeadingTemplateProperty); set => SetValue(ToolbarLeadingTemplateProperty, value); }
    /// <summary>Identifies IsToolbarVisible.</summary>
    public static readonly BindableProperty IsToolbarVisibleProperty = BindableProperty.Create(nameof(IsToolbarVisible), typeof(bool?), typeof(ViewBase), null, propertyChanged: VisualChanged);
    /// <summary>Toolbar visibility, inherited when unset.</summary>
    public bool? IsToolbarVisible { get => (bool?)GetValue(IsToolbarVisibleProperty); set => SetValue(IsToolbarVisibleProperty, value); }
    /// <summary>Gets right-side toolbar actions. An empty, explicitly assigned collection clears inherited actions.</summary>
    public ObservableCollection<ToolbarButton> ToolbarItems
    {
        get { EnsureToolbar(); return Toolbar!.RightItems ??= new(); }
        set { EnsureToolbar(); Toolbar!.RightItems = value; }
    }
    /// <summary>Resolves a service from this destination's owned scope.</summary>
    public TService GetService<TService>() where TService : notnull =>
        (NavigationEntryScope.For(this) ?? throw new InvalidOperationException("This view is not owned by navigation.")).GetRequiredService<TService>();

    private void EnsureToolbar() => Toolbar ??= new NavigationToolbarDefinition();
    private static void VisualChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (ViewBase)bindable;
        view.EnsureToolbar();
        if (view.IsSet(TitleProperty)) view.Toolbar!.Title = view.Title;
        if (view.IsSet(ToolbarCenterContentProperty)) view.Toolbar!.CenterContent = view.ToolbarCenterContent;
        if (view.IsSet(ToolbarCenterTemplateProperty)) view.Toolbar!.CenterContentTemplate = view.ToolbarCenterTemplate;
        if (view.IsSet(ToolbarItemTemplateProperty)) view.Toolbar!.RightItemTemplate = view.ToolbarItemTemplate;
        if (view.IsSet(IsToolbarVisibleProperty)) view.Toolbar!.IsVisible = view.IsToolbarVisible;
        view.Navigator?.Context.RootContext.RefreshState();
    }
}
