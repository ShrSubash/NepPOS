using Microsoft.JSInterop;
using Microsoft.UI.Windowing;

namespace NepPOS.Presentation.Register.Services;

/// <summary>
/// Full screen for the register window. It starts full screen (the till fills the screen and the
/// taskbar is hidden); F11 switches it on and off and the choice is remembered on this PC.
/// Alt+Tab and Alt+F4 still work.
/// </summary>
public static class ScreenMode
{
    private const string PreferenceKey = "NepPOS.FullScreen";

    /// <summary>Called once when Windows creates the app window.</summary>
    public static void Apply(Microsoft.UI.Xaml.Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Set(window, Preferences.Default.Get(PreferenceKey, true));
    }

    /// <summary>F11 on any screen (see index.html).</summary>
    [JSInvokable("ToggleFullScreen")]
    public static void Toggle()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView
                is not Microsoft.UI.Xaml.Window window)
                return;

            var fullScreen = window.AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
            Set(window, fullScreen);
            Preferences.Default.Set(PreferenceKey, fullScreen);
        });
    }

    private static void Set(Microsoft.UI.Xaml.Window window, bool fullScreen)
    {
        var appWindow = window.AppWindow;
        if (fullScreen)
        {
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            return;
        }

        // Back to a normal window, maximised so it still uses the whole screen.
        appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (appWindow.Presenter is OverlappedPresenter overlapped)
            overlapped.Maximize();
    }
}
