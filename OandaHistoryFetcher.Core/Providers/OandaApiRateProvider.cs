using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OandaHistoryFetcher.Core.Abstractions;
using OandaHistoryFetcher.Core.Models;
using OandaHistoryFetcher.Core.Utilities;

namespace OandaHistoryFetcher.Core.Providers;

public sealed class OandaApiRateProvider : IRateProvider
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _delay;
    private readonly int _maxRetries;
    private readonly ILogger? _logger;

    public OandaApiRateProvider(
        string apiKey,
        TimeSpan delay,
        int maxRetries,
        ILogger? logger = null,
        HttpMessageHandler? handler = null,
        Uri? baseUri = null)
    {
        _delay = delay;
        _maxRetries = Math.Max(1, maxRetries);
        _logger = logger;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.BaseAddress = baseUri ?? new Uri("https://api-fxtrade.oanda.com/");
    }

    public async Task<decimal> GetRateAsync(
        string fromCurrency,
        string toCurrency,
        decimal amount,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var instrument = $"{fromCurrency.Trim().ToUpperInvariant()}_{toCurrency.Trim().ToUpperInvariant()}";
        var from = date.ToUtcStart();
        var to = date.AddDays(1).ToUtcStart();
        var url = $"v3/instruments/{instrument}/candles?price=M&granularity=D&from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}&alignmentTimezone=UTC";

        Exception? lastError = null;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == (HttpStatusCode)429 && attempt < _maxRetries)
                {
                    await Task.Delay(GetBackoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger?.LogDebug("API response for {Date}: {Response}", date, content.Length > 512 ? content[..512] + "..." : content);

                using var json = JsonDocument.Parse(content);
                if (!json.RootElement.TryGetProperty("candles", out var candles))
                {
                    throw new RateFetchError(date, "api", "Response missing candles collection.", false);
                }

                var candle = candles.EnumerateArray().LastOrDefault();
                if (candle.ValueKind == JsonValueKind.Undefined)
                {
                    throw new RateFetchError(date, "api", "No candles returned for requested date.", false);
                }

                var mid = candle.GetProperty("mid");
                var closeText = mid.TryGetProperty("c", out var close) ? close.GetString() : null;
                if (string.IsNullOrWhiteSpace(closeText))
                {
                    throw new RateFetchError(date, "api", "Mid close price missing in response.", false);
                }

                var rate = decimal.Parse(closeText, System.Globalization.CultureInfo.InvariantCulture);
                if (amount != 0)
                {
                    rate /= amount;
                }

                return rate;
            }
            catch (RateFetchError)
            {
                throw;
            }
            catch (Exception ex) when (attempt < _maxRetries)
            {
                lastError = ex;
                _logger?.LogWarning(ex, "Retrying API fetch for {Date} (attempt {Attempt})", date, attempt);
                await Task.Delay(GetBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new RateFetchError(date, "api", $"API fetch failed: {ex.Message}", false, ex);
            }
        }

        throw new RateFetchError(date, "api", lastError?.Message ?? "Unknown API failure", false, lastError);
    }

    private TimeSpan GetBackoff(int attempt) => TimeSpan.FromMilliseconds(_delay.TotalMilliseconds * Math.Max(1, attempt));

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
