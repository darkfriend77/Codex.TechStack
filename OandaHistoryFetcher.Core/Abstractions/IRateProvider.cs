using OandaHistoryFetcher.Core.Models;

namespace OandaHistoryFetcher.Core.Abstractions;

public interface IRateProvider : IAsyncDisposable
{
    Task<decimal> GetRateAsync(
        string fromCurrency,
        string toCurrency,
        decimal amount,
        DateOnly date,
        CancellationToken cancellationToken = default);
}
