using System.Globalization;
using System.Net.Mail;
using System.Text;
using NepPOS.Application.Hardware;
using NepPOS.Application.Reports;

namespace NepPOS.Application.Email;

/// <summary>
/// How this register sends email (Manage → Email). Works with Gmail, Outlook/Microsoft 365
/// or the store's own mail server; for Gmail and Microsoft 365 use an app password.
/// </summary>
public sealed record EmailSettings
{
    public string SmtpHost { get; init; } = "";

    /// <summary>587 (STARTTLS) for Gmail and Microsoft 365.</summary>
    public int Port { get; init; } = 587;

    /// <summary>Encrypt the connection (STARTTLS). Leave on unless the mail server says otherwise.</summary>
    public bool UseTls { get; init; } = true;

    public string UserName { get; init; } = "";

    /// <summary>Kept encrypted on disk (Windows DPAPI); plain only in memory.</summary>
    public string Password { get; init; } = "";

    /// <summary>The address the email comes from. Empty = the user name.</summary>
    public string FromAddress { get; init; } = "";

    /// <summary>Who gets the reports: one or more addresses separated by commas or semicolons.</summary>
    public string ToAddresses { get; init; } = "";

    /// <summary>Email the Z (PDF, plus CSV when "Save CSV" is ticked) when the day is closed.</summary>
    public bool EmailZ { get; init; }

    public string EffectiveFrom => string.IsNullOrWhiteSpace(FromAddress) ? UserName.Trim() : FromAddress.Trim();

    /// <summary>Enough filled in to try sending.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(SmtpHost) && Port is > 0 and < 65536 &&
        EmailAddressList.IsValid(EffectiveFrom) && EmailAddressList.Parse(ToAddresses).Valid.Count > 0;
}

/// <summary>Loads and saves the email settings for this register.</summary>
public interface IEmailSettingsStore
{
    EmailSettings Load();
    void Save(EmailSettings settings);
}

public sealed record EmailAttachment(string FileName, byte[] Content);

public sealed record EmailMessage(
    IReadOnlyList<string> To,
    string Subject,
    string Body,
    IReadOnlyList<EmailAttachment> Attachments);

/// <summary>What is waiting to be sent, for the Email screen.</summary>
public sealed record OutboxStatus(int Waiting, int Failing, string? LastError, DateTime? LastSentUtc);

/// <summary>
/// Emails go into an outbox on disk first and are sent in the background, so closing the day
/// never waits for (or fails because of) the internet. Unsent emails survive a restart.
/// </summary>
public interface IEmailOutbox
{
    /// <summary>Adds the email to the outbox and tries to send it straight away in the background.</summary>
    Task QueueAsync(EmailMessage message, CancellationToken cancellationToken = default);

    /// <summary>Tries every email that is due now. Returns how many were sent.</summary>
    Task<int> SendDueAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes every waiting email due now (the "Send now" button) and tries them.</summary>
    Task<int> RetryAllNowAsync(CancellationToken cancellationToken = default);

    OutboxStatus GetStatus();

    /// <summary>Sends a short test email straight away (not through the outbox), with these settings.</summary>
    Task<HardwareResult> SendTestAsync(EmailSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Turns "a@x.com; b@y.com, wrong" into valid and invalid addresses.</summary>
public static class EmailAddressList
{
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) Parse(string? text)
    {
        var valid = new List<string>();
        var invalid = new List<string>();

        foreach (var part in (text ?? "").Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsValid(part)) valid.Add(part.Trim());
            else invalid.Add(part.Trim());
        }

        return (valid.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), invalid);
    }

    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        try
        {
            // MailAddress accepts "Name <a@b.com>"; only a bare address is wanted here.
            var parsed = new MailAddress(address.Trim());
            return parsed.Address == address.Trim() && parsed.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>The email that goes with a Z (or any report): a short summary in the body, the files attached.</summary>
public static class ReportEmail
{
    private static readonly CultureInfo Au = CultureInfo.GetCultureInfo("en-AU");

    public static string Subject(EndOfDayReport report, StoreProfile store)
    {
        var name = string.IsNullOrWhiteSpace(store.BusinessName) ? "NepPOS" : store.BusinessName.Trim();
        var day = report.IsZ
            ? ReportPeriod.TradingDay(report.PeriodEndUtc).ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)
            : ReportTitles.Period(report);
        return $"{name} - {ReportTitles.Title(report)} - {day}";
    }

    public static string Body(EndOfDayReport report, StoreProfile store)
    {
        string Money(decimal amount) => amount.ToString("C", Au);
        var eftpos = report.Tenders.Where(t => t.Tender == Domain.Enums.TenderType.Eftpos).Sum(t => t.Amount);
        var problems = report.VoidCount + report.NoSales + report.CardOutcomeUnknown + report.SaveFailures + report.LinesRemoved;

        var body = new StringBuilder();
        body.AppendLine($"{ReportTitles.Title(report)} for {(string.IsNullOrWhiteSpace(store.BusinessName) ? "your store" : store.BusinessName.Trim())}, register {report.RegisterId}.");
        body.AppendLine(ReportTitles.Period(report));
        body.AppendLine();
        body.AppendLine($"Sales (inc GST):  {Money(report.GrossSales)}   ({report.SaleCount} transactions)");
        body.AppendLine($"GST:              {Money(report.Gst)}");
        body.AppendLine($"Cash in drawer:   {Money(report.CashExpected)}   (plus your opening float)");
        body.AppendLine($"EFTPOS:           {Money(eftpos)}");
        if (report.Departments.Count > 0)
            body.AppendLine($"Top department:   {report.Departments[0].Department} {Money(report.Departments[0].Amount)}");
        if (problems > 0)
            body.AppendLine($"Needs a look:     {problems} (voids, no sales, card issues) - see the attached report");
        body.AppendLine();
        body.AppendLine("The full report is attached.");
        body.AppendLine();
        body.AppendLine("Sent by NepPOS");
        return body.ToString();
    }
}

/// <summary>When to try a failed email again: 1, 2, 4, 8… minutes, never more than an hour apart.</summary>
public static class EmailRetry
{
    public static DateTime NextAttemptUtc(int failedAttempts, DateTime nowUtc)
    {
        var minutes = failedAttempts <= 0 ? 0 : Math.Min(60, Math.Pow(2, Math.Min(failedAttempts - 1, 10)));
        return nowUtc.AddMinutes(minutes);
    }
}
