namespace OandaHistoryFetcher.Core.Interfaces;

/// <summary>
/// Interface for retrieving currency exchange rates.
/// </summary>
public interface IRateProvider : IAsyncDisposable
{
    /// <summary>
    /// Gets the exchange rate for a specific date.
    /// </summary>
    /// <param name="fromCurrency">Source currency code (e.g., BTC)</param>
    /// <param name="toCurrency">Target currency code (e.g., CHF)</param>
    /// <param name="amount">Amount to convert</param>
    /// <param name="date">Date for the rate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Exchange rate</returns>
    Task<decimal> GetRateAsync(
        string fromCurrency,
        string toCurrency,
        decimal amount,
        DateOnly date,
        CancellationToken cancellationToken = default);
}
