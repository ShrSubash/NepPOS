using System.Runtime.Versioning;
using NepPOS.Application.Hardware;

namespace NepPOS.Infrastructure.Adapters.Hardware;

/// <summary>Lists the printers installed in Windows (Settings → Printers &amp; scanners).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrinterDirectory : IPrinterDirectory
{
    public IReadOnlyList<string> GetInstalledPrinters()
    {
        try
        {
            return System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().OrderBy(name => name).ToList();
        }
        catch
        {
            // Print spooler stopped etc. — the screen shows an empty list rather than crashing.
            return Array.Empty<string>();
        }
    }
}
