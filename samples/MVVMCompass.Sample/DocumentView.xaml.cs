namespace MVVMCompass.Sample;
public partial class DocumentView : ViewBase<DocumentViewModel>
{
    // A separate property value: the container can host it without moving this screen's body.
    public View CompactPanel { get; }
    public DocumentView(DocumentViewModel model) : base(model)
    {
        InitializeComponent();
        var title = new Label(); title.SetBinding(Label.TextProperty, nameof(model.Caption));
        var popupButton = new Button { Text = "Popup from this tab" };
        popupButton.Clicked += (_, _) =>
        {
            var close = new Button { Text = "Close popup" };
            var popup = new CommunityToolkit.Maui.Views.Popup { Content = close };
            close.Clicked += async (_, _) => await popup.CloseAsync();
            ShowPopup(popup);
        };
        CompactPanel = new HorizontalStackLayout { Spacing = 12, Children = { title, popupButton } };
    }
}
