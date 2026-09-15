using Microsoft.Maui.Controls.Shapes;

namespace MVVMCompass;

/// <summary>Transitions screen bodies inside a clipped region, leaving surrounding controls mounted.</summary>
public sealed class NavigationContentPresenter : ContentView
{
    private readonly Grid bodies = new();
    private readonly RectangleGeometry clip = new();

    /// <summary>Creates an empty content presenter.</summary>
    public NavigationContentPresenter()
    {
        Content = bodies;
        bodies.Clip = clip;
        SizeChanged += (_, _) => clip.Rect = new Rect(0, 0, Math.Max(0, Width), Math.Max(0, Height));
    }

    /// <summary>Identifies whether body transitions animate.</summary>
    public static readonly BindableProperty AnimateNavigationProperty = BindableProperty.Create(nameof(AnimateNavigation), typeof(bool), typeof(NavigationContentPresenter), true);
    /// <summary>Identifies transition duration in milliseconds.</summary>
    public static readonly BindableProperty TransitionDurationProperty = BindableProperty.Create(nameof(TransitionDuration), typeof(uint), typeof(NavigationContentPresenter), (uint)180);
    /// <summary>Gets or sets whether body navigation animates.</summary>
    public bool AnimateNavigation { get => (bool)GetValue(AnimateNavigationProperty); set => SetValue(AnimateNavigationProperty, value); }
    /// <summary>Gets or sets transition duration in milliseconds.</summary>
    public uint TransitionDuration { get => (uint)GetValue(TransitionDurationProperty); set => SetValue(TransitionDurationProperty, value); }
    /// <summary>Gets the currently presented body.</summary>
    public View? CurrentContent { get; private set; }

    internal void Install(View? content)
    {
        if (CurrentContent != content && CurrentContent != null) UnfocusBody(CurrentContent);
        bodies.Children.Clear();
        CurrentContent = content;
        if (content != null) { content.TranslationX = 0; content.Opacity = 1; bodies.Add(content); }
    }

    internal async Task PresentAsync(View content, bool backwards)
    {
        if (ReferenceEquals(CurrentContent, content)) return;
        var old = CurrentContent;
        if (!AnimateNavigation || Handler == null || Width <= 0 || old == null || TransitionDuration == 0)
        { Install(content); return; }
        UnfocusBody(old);
        CurrentContent = content;
        content.TranslationX = backwards ? -Width : Width;
        bodies.Add(content);
        try
        {
            await Task.WhenAll(content.TranslateToAsync(0, 0, TransitionDuration, Easing.CubicOut),
                old.TranslateToAsync(backwards ? Width : -Width, 0, TransitionDuration, Easing.CubicOut));
        }
        finally
        {
            bodies.Remove(old);
            old.TranslationX = 0;
            content.TranslationX = 0;
        }
    }

    private static void UnfocusBody(VisualElement element)
    {
        if (element.IsFocused) element.Unfocus();
        foreach (var child in ((IElementController)element).LogicalChildren.OfType<VisualElement>()) UnfocusBody(child);
    }
}
