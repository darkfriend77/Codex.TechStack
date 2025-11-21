namespace OandaHistoryFetcher.Core;

public interface IRateProvider : IAsyncDisposable
{
    Task<decimal> GetRateAsync(
        string fromCurrency,
        string toCurrency,
        decimal amount,
        DateOnly date,
        CancellationToken cancellationToken = default);
}
