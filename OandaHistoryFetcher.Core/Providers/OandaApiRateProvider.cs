using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using OandaHistoryFetcher.Core.Utilities;

namespace OandaHistoryFetcher.Core.Providers;

/// <summary>
/// Placeholder implementation for the official OANDA API.
/// </summary>
public sealed class OandaApiRateProvider : IRateProvider
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _delay;
    private readonly int _maxRetries;
    private readonly ILogger? _logger;
    private bool _disposed;

    public OandaApiRateProvider(string apiKey, TimeSpan delay, int maxRetries, ILogger? logger = null, HttpClient? httpClient = null)
    {
        _delay = delay;
        _maxRetries = Math.Max(1, maxRetries);
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OandaHistoryFetcher/1.0 (+https://example.com)");
    }

    public async Task<decimal> GetRateAsync(string fromCurrency, string toCurrency, decimal amount, DateOnly date, CancellationToken cancellationToken = default)
    {
        // This is intentionally a scaffold. Replace with the documented OANDA historical endpoint.
        var message = "OANDA API integration not wired yet. Please implement the HTTP call to the official endpoint.";
        _logger?.LogWarning("API mode hit placeholder for {Date} {From}/{To}", date, fromCurrency, toCurrency);
        throw new RateFetchError(date, "api", message, isRetryable: false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _httpClient.Dispose();
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
