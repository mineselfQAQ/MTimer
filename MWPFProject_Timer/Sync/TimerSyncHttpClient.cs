using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MTimer.Sync.Contracts;

namespace MWPFProject_Timer.Sync;

internal sealed class TimerSyncHttpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    internal TimerSyncHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    internal async Task<SyncPushResponse> PushAsync(
        SyncPushRequest request,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient.PostAsync(
            "/sync/push",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncPushResponse>(
                   JsonOptions,
                   cancellationToken) ??
               new SyncPushResponse();
    }

    internal async Task<SyncPullResponse> PullAsync(long after, CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<SyncPullResponse>(
                   $"/sync/pull?after={Math.Max(0, after)}&protocolVersion={SyncProtocol.CurrentVersion}",
                   JsonOptions,
                   cancellationToken) ??
               new SyncPullResponse();
    }

    internal static HttpClient CreateClient(Uri endpoint, TimeSpan timeout)
    {
        return new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = endpoint,
            Timeout = timeout
        };
    }

    internal static async Task<Uri?> ResolveHealthyEndpointAsync(
        IEnumerable<string?> candidates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var checkedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) ||
                !Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out Uri? endpoint) ||
                endpoint.Scheme is not ("http" or "https"))
            {
                continue;
            }

            var normalized = new Uri(endpoint.GetLeftPart(UriPartial.Authority));
            if (!checkedEndpoints.Add(normalized.AbsoluteUri))
            {
                continue;
            }

            try
            {
                using HttpClient client = CreateClient(normalized, timeout);
                using HttpResponseMessage response = await client.GetAsync("/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return normalized;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return null;
    }
}
