using System.ComponentModel;

namespace MVVMCompass;

// Keep the native button's text, command, focus, and accessibility behavior, but
// measure the image separately. Button.ImageSource uses the asset's intrinsic
// size on native platforms and cannot enforce a cross-platform icon box.
internal sealed class ToolbarActionView : Grid
{
    private readonly Button button = NavigationButton.Create();
    private readonly Image icon = new()
    {
        WidthRequest = 24, HeightRequest = 24, Aspect = Aspect.AspectFit,
        VerticalOptions = LayoutOptions.Center, InputTransparent = true
    };

    internal ToolbarActionView(Color foreground, Style? style)
    {
        button.TextColor = foreground;
        button.Style = style;
        button.SetBinding(Button.TextProperty, Binding.Create(static (ToolbarButton item) => item.Text));
        button.SetBinding(Button.CommandProperty, Binding.Create(static (ToolbarButton item) => item.Command));
        button.SetBinding(Button.CommandParameterProperty, Binding.Create(static (ToolbarButton item) => item.CommandParameter));
        button.SetBinding(IsEnabledProperty, Binding.Create(static (ToolbarButton item) => item.IsEnabled));
        button.SetBinding(IsVisibleProperty, Binding.Create(static (ToolbarButton item) => item.IsVisible));
        button.SetBinding(SemanticProperties.DescriptionProperty, Binding.Create(static (ToolbarButton item) => item.AccessibilityLabel));
        icon.SetBinding(Image.SourceProperty, Binding.Create(static (ToolbarButton item) => item.Icon));
        AutomationProperties.SetIsInAccessibleTree(icon, false);
        button.PropertyChanged += ContentChanged;
        icon.PropertyChanged += ContentChanged;
        Add(button); Add(icon);
        UpdateIcon();
    }

    internal void SetForeground(Color foreground) => button.TextColor = foreground;

    private void ContentChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Button.Text) or nameof(Image.Source) or nameof(FlowDirection)) UpdateIcon();
    }

    private void UpdateIcon()
    {
        var combined = icon.Source != null && !string.IsNullOrEmpty(button.Text);
        icon.IsVisible = icon.Source != null;
        icon.HorizontalOptions = combined ? LayoutOptions.Start : LayoutOptions.Center;
        icon.Margin = combined ? new Thickness(10, 0) : 0;
        button.Padding = !combined ? new Thickness(10, 4)
            : (((IVisualElementController)button).EffectiveFlowDirection & EffectiveFlowDirection.RightToLeft) != 0
                ? new Thickness(10, 4, 38, 4) : new Thickness(38, 4, 10, 4);
    }
}
