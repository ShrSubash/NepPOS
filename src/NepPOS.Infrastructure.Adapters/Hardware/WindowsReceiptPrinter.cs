using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;
using NepPOS.Application.Hardware;

namespace NepPOS.Infrastructure.Adapters.Hardware;

/// <summary>
/// Prints a receipt through a normal Windows printer driver (A4 laser, label, or thermal with a driver),
/// in Consolas sized so a full receipt line fits the printable width.
/// "Microsoft Print to PDF" / XPS writers save straight to %LOCALAPPDATA%\NepPOS\Receipts — no dialog.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsReceiptPrinter
{
    private const float MaxFontSize = 10f;
    private const float MinFontSize = 6f;

    /// <returns>The file written for PDF/XPS printers, otherwise null.</returns>
    public static string? Print(string printerName, ReceiptDocument receipt, string documentName)
    {
        using var document = new PrintDocument
        {
            DocumentName = documentName,
            PrintController = new StandardPrintController() // no "Printing…" dialog
        };

        document.PrinterSettings.PrinterName = printerName;
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Printer '{printerName}' was not found.");

        string? outputFile = null;
        var extension = printerName.Contains("PDF", StringComparison.OrdinalIgnoreCase) ? ".pdf"
                      : printerName.Contains("XPS", StringComparison.OrdinalIgnoreCase) ? ".oxps"
                      : null;
        if (extension is not null)
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NepPOS", "Receipts");
            Directory.CreateDirectory(folder);
            outputFile = Path.Combine(folder, $"{documentName} {DateTime.Now:yyyyMMdd-HHmmss}{extension}");

            document.PrinterSettings.PrintToFile = true;
            document.PrinterSettings.PrintFileName = outputFile;
        }

        var nextLine = 0;
        document.PrintPage += (_, e) =>
        {
            var graphics = e.Graphics!;

            // Use the whole printable area (narrow on receipt printers), not the 1-inch default margins.
            var printable = e.PageSettings.PrintableArea;
            var area = new RectangleF(4, 4, printable.Width - 8, printable.Height - 8);

            // Fit LineWidth characters across the printable width (Consolas is ~0.55 em wide).
            var widthPoints = area.Width / 100f * 72f;
            var size = Math.Clamp(widthPoints / (receipt.LineWidth * 0.55f), MinFontSize, MaxFontSize);

            using var regular = new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point);
            using var bold = new Font("Consolas", size, FontStyle.Bold, GraphicsUnit.Point);
            var lineHeight = regular.GetHeight(graphics);

            float y = area.Top;
            while (nextLine < receipt.Lines.Count)
            {
                if (y + lineHeight > area.Bottom)
                {
                    e.HasMorePages = true;
                    return;
                }

                var line = receipt.Lines[nextLine++];
                var text = line.Align switch
                {
                    ReceiptAlign.Center => line.Text.PadLeft((receipt.LineWidth + line.Text.Length) / 2),
                    ReceiptAlign.Right => line.Text.PadLeft(receipt.LineWidth),
                    _ => line.Text
                };

                graphics.DrawString(text, line.Bold || line.Large ? bold : regular, Brushes.Black, area.Left, y);
                y += lineHeight;
            }

            e.HasMorePages = false;
        };

        document.Print();
        return outputFile;
    }
}
