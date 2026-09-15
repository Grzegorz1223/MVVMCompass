using MVVMCompass.ApiContracts;
using MVVMCompass.Core;

namespace MVVMCompass.Compatibility.Tests;

public sealed class CurrentApiTests
{
    [Theory]
    [InlineData(true, "CurrentCoreApi.txt")]
    [InlineData(false, "CurrentMauiApi.txt")]
    public void Current_contract_includes_constraints_inheritance_defaults_nullability_and_AOT_annotations(bool core, string snapshot)
    {
        var actual = ApiSurface.Capture(core ? typeof(NavigationEntry).Assembly : typeof(ViewModelBase).Assembly);
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, snapshot));
        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
    }
}
