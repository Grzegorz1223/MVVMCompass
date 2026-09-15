namespace MVVMCompass.Sample;
public partial class DemoPopupView : PopupViewBase<DemoPopupViewModel, string>
{
    public DemoPopupView(DemoPopupViewModel model) : base(model) => InitializeComponent();
}
