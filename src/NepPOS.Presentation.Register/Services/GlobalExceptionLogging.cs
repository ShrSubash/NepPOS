using Serilog;

namespace NepPOS.Presentation.Register.Services;

/// <summary>
/// Writes crashes that nothing else caught to the log file, so "the register just closed"
/// can always be explained afterwards. The Windows UI thread is hooked separately in
/// Platforms/Windows/App.xaml.cs.
/// </summary>
public static class GlobalExceptionLogging
{
    public static void Register()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception - app terminating: {IsTerminating}", e.IsTerminating);
            Log.CloseAndFlush();
        };

        // A background task failed and nobody awaited it: log it instead of losing it silently.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved background task exception");
            e.SetObserved();
        };
    }
}
