namespace MVVMCompass.Sample;
public partial class TabsDemoView : TabbedViewBase<TabsDemoViewModel>
{
    public TabsDemoView(TabsDemoViewModel model) : base(model) => InitializeComponent();
}
