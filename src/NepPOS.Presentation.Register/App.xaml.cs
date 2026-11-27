namespace NepPOS.Presentation.Register;

public partial class App : Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "NepPOS" };
        // The customer display (Manage → Till options) opens once the till's own window is up.
        window.Created += (_, _) => NepPOS.Presentation.Register.Services.CustomerDisplayWindow.Refresh();
        return window;
    }
}