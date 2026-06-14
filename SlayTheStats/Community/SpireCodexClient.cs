using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStats.Community;

/// <summary>Outcome of a single fetch — never throws into callers for network/HTTP failure.</summary>
public enum FetchStatus { Ok, Error }

/// <summary>
/// A fetch result carrying either a parsed value or an error. On a 429/503 the
/// <see cref="RetryAfter"/> is surfaced from the <c>Retry-After</c> header so the
/// cache backoff layer can honor the server's pacing.
/// </summary>
public sealed class FetchResult<T>
{
    public FetchStatus Status { get; init; }
    public T? Value { get; init; }
    public string? Error { get; init; }
    public TimeSpan? RetryAfter { get; init; }

    public bool IsOk => Status == FetchStatus.Ok;

    public static FetchResult<T> Ok(T value) => new() { Status = FetchStatus.Ok, Value = value };
    public static FetchResult<T> Err(string error, TimeSpan? retryAfter = null)
        => new() { Status = FetchStatus.Error, Error = error, RetryAfter = retryAfter };
}

/// <summary>
/// Thin client over the Spire Codex precomputed-stats endpoints. Wraps a single
/// long-lived <see cref="HttpClient"/> (never per-request). The transport handler
/// is injectable so the parsing/pagination/error logic is unit-testable offline.
/// </summary>
public sealed class SpireCodexClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // encounter-stats paginates; bound the page loop well above the real corpus
    // (106 rows / 50 per page ≈ 3 pages) so a server bug can never spin forever.
    private const int MaxEncounterPages = 50;

    private readonly HttpClient _http;

    /// <param name="baseUrl">API origin, e.g. "https://spire-codex.com".</param>
    /// <param name="userAgent">Descriptive UA; the site Cloudflare-403s empty/bot agents.</param>
    /// <param name="handler">Transport seam — defaults to a real handler with gzip auto-decompression.</param>
    public SpireCodexClient(string baseUrl, string userAgent, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(
            handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate },
            disposeHandler: handler == null)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/runs/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>GET metrics/{entityType}?cohort={cohort} — entityType is "cards"|"relics"|"potions".</summary>
    public Task<FetchResult<MetricsResponse>> GetMetricsAsync(string entityType, string cohort, CancellationToken ct)
        => GetJsonAsync<MetricsResponse>($"metrics/{entityType}?cohort={cohort}", ct);

    /// <summary>GET versions — the build_ids present in the corpus.</summary>
    public Task<FetchResult<VersionsResponse>> GetVersionsAsync(CancellationToken ct)
        => GetJsonAsync<VersionsResponse>("versions", ct);

    /// <summary>
    /// GET encounter-stats, paged to completion. The endpoint 500s on an <c>?act=</c>
    /// filter, so we pull every page (no act filter) and let the caller group by
    /// <see cref="EncounterRow.Act"/>.
    /// </summary>
    public async Task<FetchResult<List<EncounterRow>>> GetEncounterStatsAsync(CancellationToken ct)
    {
        var all = new List<EncounterRow>();
        for (int page = 1; page <= MaxEncounterPages; page++)
        {
            var r = await GetJsonAsync<EncounterStatsResponse>($"encounter-stats?page={page}", ct).ConfigureAwait(false);
            if (!r.IsOk || r.Value == null)
                return FetchResult<List<EncounterRow>>.Err(r.Error ?? "encounter-stats failed", r.RetryAfter);

            all.AddRange(r.Value.Encounters);
            if (!r.Value.HasNext || r.Value.Encounters.Count == 0)
                break;
        }
        return FetchResult<List<EncounterRow>>.Ok(all);
    }

    private async Task<FetchResult<T>> GetJsonAsync<T>(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            int code = (int)resp.StatusCode;
            if (code == 429 || code >= 500)
                return FetchResult<T>.Err($"HTTP {code}", ParseRetryAfter(resp));
            if (!resp.IsSuccessStatusCode)
                return FetchResult<T>.Err($"HTTP {code}");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct).ConfigureAwait(false);
            return value == null
                ? FetchResult<T>.Err("empty/null response body")
                : FetchResult<T>.Ok(value);
        }
        // Our caller's token cancelling means the whole refresh is being torn down — propagate.
        // A cancellation WITHOUT that token firing is the per-request HttpClient.Timeout (15s):
        // treat it as an ordinary per-unit failure so one slow endpoint can't abort the rest
        // of a partial refresh (it previously surfaced as a misleading "refresh timed out").
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return FetchResult<T>.Err("request timed out (15s)"); }
        catch (Exception e) { return FetchResult<T>.Err(e.Message); }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra == null) return null;
        if (ra.Delta is { } delta) return delta;
        if (ra.Date is { } date)
        {
            var d = date - DateTimeOffset.UtcNow;
            return d > TimeSpan.Zero ? d : null;
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
