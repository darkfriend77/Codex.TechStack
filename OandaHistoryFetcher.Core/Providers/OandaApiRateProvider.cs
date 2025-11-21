using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OandaHistoryFetcher.Core.Interfaces;
using OandaHistoryFetcher.Core.Models;

namespace OandaHistoryFetcher.Core.Providers;

/// <summary>
/// Rate provider using OANDA's official API.
/// Note: This is a placeholder implementation. Actual OANDA API endpoints
/// and authentication mechanisms should be verified against official documentation.
/// </summary>
public class OandaApiRateProvider : IRateProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly TimeSpan _delay;
    private readonly int _maxRetries;
    private readonly ILogger? _logger;
    private bool _disposed;

    public OandaApiRateProvider(
        string apiKey,
        TimeSpan delay,
        int maxRetries,
        ILogger? logger = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _delay = delay;
        _maxRetries = maxRetries;
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.oanda.com/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "OandaHistoryFetcher/1.0");
    }

    public async Task<decimal> GetRateAsync(
        string fromCurrency,
        string toCurrency,
        decimal amount,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                _logger?.LogDebug(
                    "Fetching rate via API: {From} -> {To} for {Date} (attempt {Attempt})",
                    fromCurrency, toCurrency, date, attempt + 1);

                // Note: This endpoint is a placeholder. The actual OANDA API endpoint for
                // historical rates should be used (e.g., /v3/instruments/{instrument}/candles
                // or similar, depending on account type and API version).
                var requestDate = date.ToString("yyyy-MM-dd");
                var endpoint = $"v3/instruments/{fromCurrency}_{toCurrency}/candles" +
                               $"?granularity=D&from={requestDate}T00:00:00Z&to={requestDate}T23:59:59Z";

                var response = await _httpClient.GetAsync(endpoint, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger?.LogWarning("Rate limit hit, retrying after delay");
                    await Task.Delay(_delay * (attempt + 1), cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                
                _logger?.LogDebug("API response: {Response}", content);

                // Parse the response and extract the rate
                // This is a simplified example; actual parsing depends on OANDA's response format
                using var doc = JsonDocument.Parse(content);
                var candles = doc.RootElement.GetProperty("candles");
                
                if (candles.GetArrayLength() == 0)
                {
                    throw new RateFetchError(
                        date,
                        "api",
                        $"No rate data available for {date}",
                        false);
                }

                var candle = candles[0];
                var mid = candle.GetProperty("mid");
                var closePrice = mid.GetProperty("c").GetDecimal();

                _logger?.LogInformation(
                    "Successfully fetched rate: {From} -> {To} on {Date}: {Rate}",
                    fromCurrency, toCurrency, date, closePrice);

                return closePrice;
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries)
            {
                _logger?.LogWarning(
                    ex,
                    "HTTP error on attempt {Attempt} for {Date}",
                    attempt + 1, date);
                await Task.Delay(_delay * (attempt + 1), cancellationToken);
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to parse API response for {Date}", date);
                throw new RateFetchError(
                    date,
                    "api",
                    $"Failed to parse response: {ex.Message}",
                    false,
                    ex);
            }
            catch (Exception ex) when (ex is not RateFetchError)
            {
                _logger?.LogError(ex, "Unexpected error fetching rate for {Date}", date);
                throw new RateFetchError(
                    date,
                    "api",
                    $"Unexpected error: {ex.Message}",
                    attempt < _maxRetries,
                    ex);
            }
        }

        throw new RateFetchError(
            date,
            "api",
            $"Failed to fetch rate after {_maxRetries + 1} attempts",
            false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _httpClient?.Dispose();
        _disposed = true;
        
        await Task.CompletedTask;
    }
}
