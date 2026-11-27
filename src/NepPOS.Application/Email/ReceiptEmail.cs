using System.Text;
using NepPOS.Application.Hardware;

namespace NepPOS.Application.Email;

/// <summary>
/// The customer's receipt as an email: the same lines as the printed one, as plain text, so it reads the same
/// in any mail app. Sent through the register's outbox, so it goes when the internet is back if it's down.
/// </summary>
public static class ReceiptEmail
{
    /// <summary>True when the email settings can send to a customer (a server and a from address; no Z recipients needed).</summary>
    public static bool CanSend(EmailSettings settings) =>
        settings is not null && !string.IsNullOrWhiteSpace(settings.SmtpHost) && EmailAddressList.IsValid(settings.EffectiveFrom);

    /// <summary>Why this address can't be used, or null when it's fine.</summary>
    public static string? AddressProblem(string? address) =>
        EmailAddressList.IsValid((address ?? "").Trim()) ? null : "Check the email address.";

    public static EmailMessage Build(ReceiptDocument receipt, string storeName, long invoiceNumber, string to)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var body = new StringBuilder();
        body.AppendLine("Thanks for shopping with us. Your receipt is below.");
        body.AppendLine();
        foreach (var line in receipt.Lines)
        {
            // The barcode line is for the scanner at the till; the email shows the receipt number instead.
            if (line.Barcode)
                body.AppendLine(Centre($"Receipt {line.Text}", receipt.LineWidth));
            else
                body.AppendLine(line.Align switch
                {
                    ReceiptAlign.Center => Centre(line.Text, receipt.LineWidth),
                    ReceiptAlign.Right => line.Text.PadLeft(receipt.LineWidth),
                    _ => line.Text
                });
        }

        var subject = string.IsNullOrWhiteSpace(storeName) ? $"Your receipt #{invoiceNumber}" : $"Your {storeName.Trim()} receipt #{invoiceNumber}";
        return new EmailMessage(new[] { to.Trim() }, subject, body.ToString(), Array.Empty<EmailAttachment>());
    }

    private static string Centre(string text, int width) =>
        text.Length >= width ? text : new string(' ', (width - text.Length) / 2) + text;
}
