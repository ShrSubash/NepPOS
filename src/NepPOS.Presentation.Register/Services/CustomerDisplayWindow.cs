using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using NepPOS.Application.Hardware;
using Windows.Graphics;

namespace NepPOS.Presentation.Register.Services;

/// <summary>
/// Opens the customer display full screen on the second monitor (the one the till isn't on). Does nothing when it's
/// switched off on Manage → Till options or only one monitor is connected, so a one-screen till is unaffected.
/// </summary>
public static class CustomerDisplayWindow
{
    private static Window? _window;

    /// <summary>True while the display window is being created, so the till's full-screen setting skips it.</summary>
    internal static bool Opening { get; private set; }

    public static bool IsOpen => _window is not null;

    /// <summary>At start-up and after the setting is saved.</summary>
    public static void Refresh()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var services = IPlatformApplication.Current?.Services;
                var enabled = services?.GetService<IRegisterSettingsStore>()?.Load().CustomerDisplay.Enabled == true;
                if (enabled) Open(); else Close();
            }
            catch (Exception ex)
            {
                // The display is extra; it must never stop the till.
                IPlatformApplication.Current?.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(CustomerDisplayWindow))
                    .LogWarning(ex, "Customer display couldn't open");
                Opening = false;
            }
        });
    }

    /// <summary>How many monitors are connected (the display needs two).</summary>
    public static int MonitorCount()
    {
        try { return DisplayArea.FindAll().Count; }
        catch { return 1; }
    }

    private static void Open()
    {
        if (_window is not null || MonitorCount() < 2 || Microsoft.Maui.Controls.Application.Current is not { } app)
            return;

        Opening = true;
        var window = new Window(new CustomerDisplayPage()) { Title = "NepPOS customer display" };
        window.Created += (_, _) => MoveToSecondMonitor(window);
        window.Destroying += (_, _) => _window = null;
        _window = window;
        app.OpenWindow(window);
    }

    private static void Close()
    {
        if (_window is { } window)
            Microsoft.Maui.Controls.Application.Current?.CloseWindow(window);
        _window = null;
    }

    private static void MoveToSecondMonitor(Window window)
    {
        try
        {
            if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window display)
                return;

            var till = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            var tillDisplay = till is null ? null : DisplayArea.GetFromWindowId(till.AppWindow.Id, DisplayAreaFallback.Primary);

            var areas = DisplayArea.FindAll();
            DisplayArea? target = null;
            for (var i = 0; i < areas.Count; i++)
            {
                if (tillDisplay is null || areas[i].DisplayId.Value != tillDisplay.DisplayId.Value)
                {
                    target = areas[i];
                    break;
                }
            }
            if (target is null)
                return;

            var bounds = target.OuterBounds;
            display.AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            display.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        finally
        {
            Opening = false;
        }
    }
}
