using System;

namespace OandaHistoryFetcher.Core.Models;

public sealed record RateRecord(
    DateOnly Date,
    string FromCurrency,
    string ToCurrency,
    decimal Amount,
    decimal? Rate,
    string Source,
    string Status,
    string ErrorMessage
);
