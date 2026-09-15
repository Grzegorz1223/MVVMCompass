namespace MVVMCompass.Sample;

internal partial class DetailPage : PlaygroundPage<DetailViewModel>
{
    public DetailPage(DetailViewModel vm) : base(vm)
    {
        InitializeComponent();
        foreach (var action in Ui.CommonActions(vm)) Actions.Children.Add(action);
        Actions.Children.Add(Ui.Trace(vm.Log));
    }
}
