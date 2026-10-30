namespace MVVMCompass.Sample;
public partial class TabsDemoView : TabbedViewBase<TabsDemoViewModel>
{
    public TabsDemoView(TabsDemoViewModel model) : base(model) => InitializeComponent();
}

public sealed class SharedTabsDemoView : TabsDemoView, IModalPresentationObserver
{
    private readonly ContentView compactHost = new();
    private readonly Label modalState = new() { Text = "Modal depth: 0" };
    private readonly Label viewState = new();
    private int modalDepth;
    internal int ModalDepth => modalDepth;
    public SharedTabsDemoView(SharedTabsDemoViewModel model) : base(model)
    {
        var frame = new ControlTemplate(() => new Border
        {
            StrokeThickness = 0, Background = new SolidColorBrush(Color.FromArgb("#E8EDF8")),
            Margin = new Thickness(8, 0, 8, 8),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = new ContentPresenter()
        });
        var toggle = new Button { Text = "Frame the tab body", Margin = new Thickness(8, 0) };
        toggle.Clicked += (_, _) =>
        {
            var framed = BodyTemplate == null;
            BodyTemplate = framed ? frame : null;
            DetailMargin = framed ? new Thickness(0, 0, 8, 8) : Thickness.Zero;
            toggle.Text = framed ? "Remove the body frame" : "Frame the tab body";
        };
        var compact = new Border
        {
            Padding = 12, Background = new SolidColorBrush(Color.FromArgb("#E8EDF8")),
            VerticalOptions = LayoutOptions.End, IsVisible = false,
            Content = new VerticalStackLayout { Children = { compactHost, modalState, viewState } }
        };
        var compactToggle = new Button { Text = "Show compact panel", Margin = new Thickness(8, 0) };
        compactToggle.Clicked += (_, _) =>
        { compact.IsVisible = !compact.IsVisible; compactToggle.Text = compact.IsVisible ? "Hide compact panel" : "Show compact panel"; };
        HeaderContent = new HorizontalStackLayout { Children = { toggle, compactToggle } };
        BodyRowOverlayContent = compact;
        CurrentTabChanged += (_, _) =>
        {
            compactHost.Content = (CurrentTab?.View as DocumentView)?.CompactPanel;
            if (compactHost.Content != null) compactHost.Content.BindingContext = CurrentTab!.ViewModel;
        };
        NavigatedTo += (_, args) => viewState.Text = "View: " + args.Kind;
        NavigatedFrom += (_, args) => viewState.Text = "View: " + args.Kind;
    }
    public void OnModalPresenting(ModalPresentation presentation) => modalState.Text = "Modal depth: " + ++modalDepth;
    public void OnModalDismissed(ModalPresentation presentation) => modalState.Text = "Modal depth: " + --modalDepth;
}
