namespace NepPOS.Presentation.Register;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();

		// The app scales itself to the screen (index.html); stop Ctrl+wheel / pinch zoom from fighting it.
		blazorWebView.BlazorWebViewInitialized += (_, e) =>
			e.WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
	}
}
