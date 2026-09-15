using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MVVMCompass.Sample.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		if (Environment.GetEnvironmentVariable("MVVMCOMPASS_SMOKE_OUTPUT") is { Length: > 0 } report)
		{
			AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
				File.AppendAllText(Path.ChangeExtension(report, ".exceptions.txt"), args.Exception + Environment.NewLine);
		}
		UnhandledException += (_, args) =>
		{
			if (Environment.GetEnvironmentVariable("MVVMCOMPASS_SMOKE_OUTPUT") is { Length: > 0 } output)
				File.WriteAllText(Path.ChangeExtension(output, ".error.txt"), args.Exception.ToString());
		};
		this.InitializeComponent();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
