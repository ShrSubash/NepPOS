using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NepPOS.Application.Email;
using NepPOS.Application.Hardware;

namespace NepPOS.Infrastructure.Adapters.Email;

/// <summary>
/// The email outbox: one JSON file per email in %LOCALAPPDATA%\NepPOS\Outbox, its attachments in a
/// folder of the same name. A background loop tries every minute; a failed email waits 1, 2, 4…
/// minutes (never more than an hour) and is tried again, until it goes or someone deletes it.
/// </summary>
public sealed class FileEmailOutbox : IEmailOutbox, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly IEmailSettingsStore _settings;
    private readonly ILogger<FileEmailOutbox> _logger;
    private readonly string _folder;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    private string? _lastError;
    private DateTime? _lastSentUtc;

    public FileEmailOutbox(IEmailSettingsStore settings, ILogger<FileEmailOutbox> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NepPOS", "Outbox");
    }

    /// <summary>Starts the once-a-minute background sending. Called once at app start.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                try
                {
                    await SendDueAsync(_stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Email outbox loop failed; it will try again in a minute");
                }
            }
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        });
    }

    public async Task QueueAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.To.Count == 0)
            throw new ArgumentException("An email needs at least one recipient.", nameof(message));

        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var attachmentFolder = Path.Combine(_folder, id);
        Directory.CreateDirectory(attachmentFolder);

        var attachments = new List<string>();
        foreach (var attachment in message.Attachments)
        {
            var name = string.Concat(attachment.FileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
            await File.WriteAllBytesAsync(Path.Combine(attachmentFolder, name), attachment.Content, cancellationToken);
            attachments.Add(name);
        }

        var job = new OutboxJob(id, DateTime.UtcNow, message.To.ToList(), message.Subject, message.Body,
            attachments, 0, DateTime.UtcNow, null);
        await WriteJobAsync(job, cancellationToken);
        _logger.LogInformation("Email queued: {Subject} to {To}", message.Subject, string.Join(", ", message.To));

        // Try straight away, without making the caller wait for the mail server.
        _ = Task.Run(async () =>
        {
            try { await SendDueAsync(_stop.Token); }
            catch (Exception ex) { _logger.LogError(ex, "Sending queued email failed; the outbox will retry"); }
        });
    }

    public async Task<int> SendDueAsync(CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var due = LoadJobs().Where(j => j.NextAttemptUtc <= DateTime.UtcNow).OrderBy(j => j.CreatedUtc).ToList();
            if (due.Count == 0)
                return 0;

            var settings = _settings.Load();
            if (!settings.IsComplete)
            {
                _lastError = "Email isn't set up yet (Manage → Email).";
                return 0;
            }

            var sent = 0;
            foreach (var job in due)
            {
                try
                {
                    using var mail = BuildMail(settings, job.To, job.Subject, job.Body,
                        job.Attachments.Select(a => Path.Combine(_folder, job.Id, a)));
                    await SendAsync(settings, mail, cancellationToken);

                    Delete(job);
                    sent++;
                    _lastSentUtc = DateTime.UtcNow;
                    _lastError = null;
                    _logger.LogInformation("Email sent: {Subject}", job.Subject);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var attempts = job.Attempts + 1;
                    _lastError = Friendly(ex);
                    await WriteJobAsync(job with
                    {
                        Attempts = attempts,
                        NextAttemptUtc = EmailRetry.NextAttemptUtc(attempts, DateTime.UtcNow),
                        LastError = _lastError
                    }, cancellationToken);
                    _logger.LogWarning(ex, "Email not sent (attempt {Attempt}): {Subject}", attempts, job.Subject);

                    // The server is unreachable or refusing us: no point trying the rest now.
                    break;
                }
            }

            return sent;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<int> RetryAllNowAsync(CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var job in LoadJobs().Where(j => j.NextAttemptUtc > DateTime.UtcNow))
                await WriteJobAsync(job with { NextAttemptUtc = DateTime.UtcNow }, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }

        return await SendDueAsync(cancellationToken);
    }

    public OutboxStatus GetStatus()
    {
        var jobs = LoadJobs();
        return new OutboxStatus(
            jobs.Count,
            jobs.Count(j => j.Attempts > 0),
            jobs.Where(j => j.LastError is not null).OrderByDescending(j => j.CreatedUtc).Select(j => j.LastError).FirstOrDefault() ?? _lastError,
            _lastSentUtc);
    }

    public async Task<HardwareResult> SendTestAsync(EmailSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsComplete)
            return HardwareResult.Failed("Fill in the mail server, a From address and at least one valid To address.");

        var to = EmailAddressList.Parse(settings.ToAddresses).Valid;
        try
        {
            using var mail = BuildMail(settings, to, "NepPOS test email",
                $"This is a test from NepPOS on {Environment.MachineName}, sent {DateTime.Now:dd/MM/yyyy HH:mm}.\r\n\r\nEmail is working: end-of-day reports will arrive like this.",
                Array.Empty<string>());
            await SendAsync(settings, mail, cancellationToken);
            return new HardwareResult(true, $"Test email sent to {string.Join(", ", to)}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test email failed");
            return HardwareResult.Failed(Friendly(ex));
        }
    }

    private static MailMessage BuildMail(EmailSettings settings, IEnumerable<string> to, string subject, string body,
                                         IEnumerable<string> attachmentPaths)
    {
        var mail = new MailMessage
        {
            From = new MailAddress(settings.EffectiveFrom, "NepPOS"),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        foreach (var address in to)
            mail.To.Add(address);
        foreach (var path in attachmentPaths.Where(File.Exists))
            mail.Attachments.Add(new Attachment(path));
        return mail;
    }

    private static async Task SendAsync(EmailSettings settings, MailMessage mail, CancellationToken cancellationToken)
    {
        // System.Net.Mail: STARTTLS on 587, which Gmail and Microsoft 365 use. No extra package needed.
        using var client = new SmtpClient(settings.SmtpHost.Trim(), settings.Port)
        {
            EnableSsl = settings.UseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = (int)SendTimeout.TotalMilliseconds,
            Credentials = string.IsNullOrWhiteSpace(settings.UserName)
                ? null
                : new NetworkCredential(settings.UserName.Trim(), settings.Password)
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        await client.SendMailAsync(mail, timeout.Token);
    }

    /// <summary>A message the owner can act on, instead of the raw SMTP error.</summary>
    private static string Friendly(Exception ex) => ex switch
    {
        SmtpException { StatusCode: SmtpStatusCode.MustIssueStartTlsFirst } =>
            "The mail server wants an encrypted connection: tick \"Encrypt (TLS)\".",
        SmtpException smtp when smtp.Message.Contains("5.7", StringComparison.Ordinal) || smtp.StatusCode == SmtpStatusCode.ClientNotPermitted =>
            "The mail server refused the user name or password. For Gmail or Microsoft 365, use an app password.",
        SmtpException { InnerException: System.Net.Sockets.SocketException } or System.Net.Sockets.SocketException =>
            "Can't reach the mail server. Check the internet connection, the server name and the port.",
        OperationCanceledException => "The mail server didn't answer within 30 seconds.",
        _ => ex.GetBaseException().Message
    };

    private List<OutboxJob> LoadJobs()
    {
        if (!Directory.Exists(_folder))
            return new List<OutboxJob>();

        var jobs = new List<OutboxJob>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<OutboxJob>(File.ReadAllText(file), JsonOptions) is { } job)
                    jobs.Add(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox file {File} is damaged and was skipped", file);
            }
        }
        return jobs;
    }

    private async Task WriteJobAsync(OutboxJob job, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, job.Id + ".json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(job, JsonOptions), cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    private void Delete(OutboxJob job)
    {
        File.Delete(Path.Combine(_folder, job.Id + ".json"));
        var attachments = Path.Combine(_folder, job.Id);
        if (Directory.Exists(attachments))
            Directory.Delete(attachments, recursive: true);
    }

    // Stops the background loop when the app closes. The token source and lock are left for the
    // garbage collector, so a send that is still finishing never hits a disposed object.
    public void Dispose() => _stop.Cancel();

    /// <summary>One email waiting in the outbox.</summary>
    private sealed record OutboxJob(
        string Id,
        DateTime CreatedUtc,
        List<string> To,
        string Subject,
        string Body,
        List<string> Attachments,
        int Attempts,
        DateTime NextAttemptUtc,
        string? LastError);
}
