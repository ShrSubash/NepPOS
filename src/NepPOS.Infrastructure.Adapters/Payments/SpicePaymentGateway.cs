using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using NepPOS.Application.Payments;

namespace NepPOS.Infrastructure.Adapters.Payments;

/// <summary>Where Spice runs. Defaults match the Spice install (same PC, port 8282).</summary>
/// <param name="PosId">Paired terminal id. Recommended by mx51 when Spice has more than one terminal; blank = Spice decides.</param>
public sealed record SpiceOptions(string Host = "localhost", int Port = 8282, string PosId = "");

/// <summary>
/// mx51 Spice EFTPOS adapter — a port of the certified legacy SpiceEFTPOS (Mypayment.vb).
/// Spice runs on the register PC and shows its own window for card prompts, signature and cancel,
/// so the POS only sends the purchase and waits for the final answer.
///
/// Money safety rules (same as legacy):
///  - Ping first; if Spice isn't paired and connected, decline without sending anything.
///  - A NEW posRefId for every attempt — Spice rejects a reused one.
///  - Once a purchase is sent it is NOT cancelled from the POS: only the terminal / Spice window can
///    cancel, and Spice then answers with a decline. Abandoning the HTTP call could leave a charged card.
///  - If the connection drops after sending, look the posRefId up before saying anything to the cashier.
///  - Every request, response and receipt is written to disk for disputes.
/// </summary>
public sealed class SpicePaymentGateway : IPaymentGateway, IDisposable
{
    private const string PosName = "NepPOS";
    private const int MaxLogBytes = 5_000_000;

    private readonly SpiceOptions _options;
    private readonly HttpClient _http;
    private readonly string _dataFolder;
    private readonly object _logLock = new();

    public SpicePaymentGateway(SpiceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Infinite timeout: a purchase waits for the customer (PIN, signature…). Ping and lookups set their own.
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://{options.Host}:{options.Port}/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Add("POS-Name", PosName);
        _http.DefaultRequestHeaders.Add("POS-Version",
            typeof(SpicePaymentGateway).Assembly.GetName().Version?.ToString() ?? "1.0.0");

        _dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NepPOS", "Spice");
        Directory.CreateDirectory(_dataFolder);
    }

    public string Name => "mx51 Spice";

    public async Task<PaymentResult> PurchaseAsync(PaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Purchase amount must be positive.");

        // 1. Is Spice running and paired? If not, nothing is sent and nobody is charged.
        if (!await IsReadyAsync(cancellationToken))
            return Declined("NR", "EFTPOS NOT AVAILABLE - check Spice is running and the terminal is paired.");

        // 2. Unique reference per attempt (sale id prefix ties it back to the sale in our logs).
        var posRefId = $"{request.TransactionId:N}"[..12] + "-" +
                       DateTime.Now.ToString("HHmmssfff", CultureInfo.InvariantCulture);

        var payload = new Dictionary<string, object>
        {
            ["posRefId"] = posRefId,
            ["purchaseAmount"] = ToCents(request.Amount),
            ["tipAmount"] = 0,
            ["surchargeAmount"] = 0,
            ["cashoutAmount"] = 0,
            ["promptForCashout"] = false
        };
        if (!string.IsNullOrWhiteSpace(_options.PosId))
            payload["posId"] = _options.PosId;

        var json = JsonSerializer.Serialize(payload);
        Log($"Purchase request: {json}");

        string body;
        try
        {
            // Deliberately NOT cancellable (CancellationToken.None) — see class remarks.
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("v1/purchase", content, CancellationToken.None);
            body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError)
        {
            // Never reached Spice: the purchase was not sent, the customer was not charged.
            Log($"Purchase {posRefId} not sent - Spice not reachable: {ex.Message}");
            return Declined("NC", "EFTPOS (Spice) is not running.");
        }
        catch (Exception ex)
        {
            // Sent, but no answer. Find out what actually happened before telling the cashier anything.
            Log($"Purchase {posRefId} sent but no response ({ex.Message}) - checking status");
            return await RecoverAsync(posRefId);
        }

        Log($"Purchase response: {body}");

        var node = ParseResponseNode(body);
        if (node is null || !node.Value.TryGetProperty("success", out _))
        {
            // mx51: no Response object means the result is unknown — recover, don't guess.
            Log($"Purchase {posRefId} returned no Response - checking status");
            return await RecoverAsync(posRefId);
        }

        return MapResult(node.Value);
    }

    // ---------------- Spice calls ----------------

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var body = await _http.GetStringAsync("v1/ping", timeout.Token);
            Log($"Ping: {body}");
            return body.Contains("PairedConnected", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log($"Ping failed - Spice not running? {ex.Message}");
            return false;
        }
    }

    /// <summary>Looks a sent purchase up by posRefId (legacy SendLast). Three tries before giving up.</summary>
    private async Task<PaymentResult> RecoverAsync(string posRefId)
    {
        var url = "v1/purchase?posRefId=" + Uri.EscapeDataString(posRefId);
        if (!string.IsNullOrWhiteSpace(_options.PosId))
            url += "&posId=" + Uri.EscapeDataString(_options.PosId);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = await _http.GetAsync(url, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                Log($"Status lookup {posRefId} (try {attempt}): {body}");

                var node = ParseResponseNode(body);
                if (node is not null)
                {
                    if (GetString(node.Value, "error_reason") == "POS_REF_ID_NOT_FOUND")
                        return Declined("NF", "Transaction not found - customer was not charged.");

                    if (node.Value.TryGetProperty("success", out _))
                        return MapResult(node.Value);
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log($"Status lookup {posRefId} (try {attempt}) failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new PaymentOutcomeUnknownException(posRefId, lastError);
    }

    // ---------------- mapping ----------------

    private PaymentResult MapResult(JsonElement node)
    {
        SaveReceipts(node);

        var approved = node.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;

        var text = GetString(node, "host_response_text") ?? GetString(node, "error_detail") ?? (approved ? "APPROVED" : "DECLINED");
        var code = GetString(node, "host_response_code") ?? GetString(node, "error_reason") ?? (approved ? "00" : "XX");

        // Optional extras: kept if Spice sends them, ignored if not.
        return new PaymentResult(
            approved,
            code,
            text,
            AuthCode: GetString(node, "auth_code"),
            CardType: GetString(node, "scheme_name"),
            MaskedPan: GetString(node, "masked_pan"),
            CustomerReceipt: SlipToPrint(node, "customer_receipt"),
            MerchantReceipt: SlipToPrint(node, "merchant_receipt"));
    }

    /// <summary>mx51: only print a slip when "&lt;name&gt;_printed" is false or absent (the terminal didn't print it).</summary>
    private static string? SlipToPrint(JsonElement node, string name)
    {
        var alreadyPrinted = node.TryGetProperty(name + "_printed", out var printed) && printed.ValueKind == JsonValueKind.True;
        var text = GetString(node, name);
        return alreadyPrinted || string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static JsonElement? ParseResponseNode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Response", out var r) ? r : root;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static int ToCents(decimal amount) =>
        (int)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    private static PaymentResult Declined(string code, string text) => new(false, code, text);

    // ---------------- disk records (disputes / certification) ----------------

    /// <summary>Spice\Receipts\yyyy-MM-dd\{posRef}_{rrn}_{HHmmss}_merchant.txt / _customer.txt — kept permanently.</summary>
    private void SaveReceipts(JsonElement node)
    {
        try
        {
            var merchant = GetString(node, "merchant_receipt");
            var customer = GetString(node, "customer_receipt");
            if (merchant is null && customer is null) return;

            var folder = Path.Combine(_dataFolder, "Receipts", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);

            var baseName = SafeFileName(
                $"{GetString(node, "pos_ref_id")}_{GetString(node, "rrn")}_{DateTime.Now:HHmmss}");

            if (merchant is not null)
                File.WriteAllText(Path.Combine(folder, baseName + "_merchant.txt"), merchant.ReplaceLineEndings());
            if (customer is not null)
                File.WriteAllText(Path.Combine(folder, baseName + "_customer.txt"), customer.ReplaceLineEndings());
        }
        catch (Exception ex)
        {
            Log($"Could not save receipt files: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        try
        {
            lock (_logLock)
            {
                var path = Path.Combine(_dataFolder, "EFTPOS.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                    File.Delete(path);

                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break a payment.
        }
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name;
    }

    public void Dispose() => _http.Dispose();
}
