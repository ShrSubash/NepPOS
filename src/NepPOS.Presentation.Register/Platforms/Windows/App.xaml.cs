using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace NepPOS.Presentation.Register.WinUI;

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
		this.InitializeComponent();

		// Crashes inside the UI layer don't reach AppDomain.UnhandledException — log them here.
		this.UnhandledException += (_, e) =>
			Serilog.Log.Fatal(e.Exception, "Unhandled UI exception: {Message}", e.Message);
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

