using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NepPOS.Application.Sync;
using NepPOS.Contracts.Sync;

namespace NepPOS.Infrastructure.Integration.HeadOffice;

/// <summary>
/// Calls the Head Office API over HTTPS. Every failure becomes a <see cref="HeadOfficeException"/>
/// that says whether to retry later (unreachable), connect again (unauthorized) or fix something (refused).
/// </summary>
public sealed class HeadOfficeClient : IHeadOfficeClient
{
    private readonly HttpClient _http;

    /// <param name="http">One long-lived client (tests pass one that talks to Head Office in memory).</param>
    public HeadOfficeClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>The client the register uses: 30-second timeout, connections refreshed every 5 minutes.</summary>
    public static HttpClient CreateDefaultHttpClient() =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

    public async Task<ActivateResponse> ActivateAsync(string baseUrl, ActivateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var message = new HttpRequestMessage(HttpMethod.Post, Url(baseUrl, "api/v1/activate"))
        {
            Content = JsonContent.Create(request, options: SyncJson.Options)
        };
        return await SendAsync<ActivateResponse>(message, cancellationToken);
    }

    public async Task<SyncEventsResponse> SendEventsAsync(HeadOfficeConnection connection, IReadOnlyList<SyncEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var message = Authorised(connection, HttpMethod.Post, "api/v1/sync/events");
        message.Content = JsonContent.Create(new SyncEventsRequest(events), options: SyncJson.Options);
        return await SendAsync<SyncEventsResponse>(message, cancellationToken);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(HeadOfficeConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return await SendAsync<HeartbeatResponse>(Authorised(connection, HttpMethod.Post, "api/v1/heartbeat"), cancellationToken);
    }

    public async Task<SharedEventsResponse> GetSharedAsync(HeadOfficeConnection connection, long afterId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return await SendAsync<SharedEventsResponse>(
            Authorised(connection, HttpMethod.Get, $"api/v1/sync/shared?after={Math.Max(0, afterId)}"), cancellationToken);
    }

    private static HttpRequestMessage Authorised(HeadOfficeConnection connection, HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, Url(connection.BaseUrl, path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        return message;
    }

    /// <summary>"https://ho.example.com" or "https://ho.example.com/" + "api/v1/…".</summary>
    private static Uri Url(string baseUrl, string path)
    {
        if (!Uri.TryCreate((baseUrl ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root) ||
            (root.Scheme != Uri.UriSchemeHttps && root.Scheme != Uri.UriSchemeHttp))
            throw new HeadOfficeException(HeadOfficeProblem.Refused, "The Head Office address isn't a valid web address (it starts with https://).");
        return new Uri(root, path);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new HeadOfficeException(HeadOfficeProblem.Unreachable, "Can't reach Head Office. Check the internet connection and the address.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HeadOfficeException(HeadOfficeProblem.Unreachable, "Head Office didn't answer within 30 seconds.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new HeadOfficeException(HeadOfficeProblem.Unauthorized, "Head Office no longer accepts this register's key. Ask for a new activation code and connect again.");

            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                throw new HeadOfficeException(HeadOfficeProblem.Unreachable, $"Head Office is having a problem ({(int)response.StatusCode}). It will be tried again.");

            if (!response.IsSuccessStatusCode)
                throw new HeadOfficeException(HeadOfficeProblem.Refused, await ProblemDetailAsync(response, cancellationToken));

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(SyncJson.Options, cancellationToken)
                       ?? throw new JsonException("Empty answer.");
            }
            catch (JsonException ex)
            {
                throw new HeadOfficeException(HeadOfficeProblem.Unreachable, "Head Office sent an answer the register doesn't understand. Is the address right?", ex);
            }
        }
    }

    /// <summary>Head Office explains a refusal in a "detail" field (RFC 9457 problem details).</summary>
    private static async Task<string> ProblemDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text)
                return text;
            if (doc.RootElement.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } heading)
                return heading;
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the status code.
        }
        return $"Head Office refused the request ({(int)response.StatusCode} {response.ReasonPhrase}).";
    }
}
