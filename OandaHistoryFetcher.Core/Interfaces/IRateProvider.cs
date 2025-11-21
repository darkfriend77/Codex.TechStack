using System;
using System.Threading;
using System.Threading.Tasks;

namespace OandaHistoryFetcher.Core.Interfaces;

public interface IRateProvider : IAsyncDisposable
{
    Task<decimal> GetRateAsync(
        string fromCurrency,
        string toCurrency,
        decimal amount,
        DateOnly date,
        CancellationToken cancellationToken = default);
}
