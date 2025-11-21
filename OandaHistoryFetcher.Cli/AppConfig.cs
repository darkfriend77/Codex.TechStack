using Microsoft.Extensions.Logging;

namespace OandaHistoryFetcher.Cli;

internal enum ExecutionMode
{
    Api,
    Scrape,
    Auto
}

internal sealed class AppConfig
{
    public string FromCurrency { get; init; } = null!;
    public string ToCurrency { get; init; } = null!;
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public decimal Amount { get; init; } = 1m;
    public string OutputPath { get; init; } = "./rates.csv";
    public ExecutionMode Mode { get; init; } = ExecutionMode.Auto;
    public string? ApiKey { get; init; }
    public int DelayMs { get; init; } = 1000;
    public int MaxRetries { get; init; } = 3;
    public LogLevel LogLevel { get; init; } = LogLevel.Information;
    public bool AcknowledgeScraping { get; init; }

    public bool ShouldUseApi => Mode == ExecutionMode.Api || (Mode == ExecutionMode.Auto && !string.IsNullOrWhiteSpace(ApiKey));
    public TimeSpan Delay => TimeSpan.FromMilliseconds(DelayMs);

    public static AppConfig Parse(string[] args)
    {
        var parser = new ArgParser(args);
        var missing = new List<string>();
        var fromCurrency = parser.GetValue("from-currency");
        var toCurrency = parser.GetValue("to-currency");
        if (string.IsNullOrWhiteSpace(fromCurrency)) missing.Add("--from-currency");
        if (string.IsNullOrWhiteSpace(toCurrency)) missing.Add("--to-currency");

        var startDateText = parser.GetValue("start-date");
        var endDateText = parser.GetValue("end-date");
        if (string.IsNullOrWhiteSpace(startDateText)) missing.Add("--start-date");
        if (string.IsNullOrWhiteSpace(endDateText)) missing.Add("--end-date");

        if (missing.Count > 0)
        {
            throw new ArgumentException($"Missing required arguments: {string.Join(", ", missing)}");
        }

        if (!DateOnly.TryParse(startDateText, out var startDate))
        {
            throw new ArgumentException("Invalid start date format. Use YYYY-MM-DD.");
        }

        if (!DateOnly.TryParse(endDateText, out var endDate))
        {
            throw new ArgumentException("Invalid end date format. Use YYYY-MM-DD.");
        }

        if (endDate < startDate)
        {
            throw new ArgumentException("End date must be on or after start date.");
        }

        var amount = 1m;
        var amountText = parser.GetValue("amount");
        if (!string.IsNullOrWhiteSpace(amountText) && !decimal.TryParse(amountText, out amount))
        {
            throw new ArgumentException("Amount must be numeric.");
        }

        var output = parser.GetValue("output") ?? "./rates.csv";
        var delayMs = parser.GetInt("delay-ms", 1000);
        var maxRetries = parser.GetInt("max-retries", 3);
        var logLevel = ParseLogLevel(parser.GetValue("log-level") ?? "info");
        var mode = ParseMode(parser.GetValue("mode") ?? "auto");
        var apiKey = parser.GetValue("oanda-api-key") ?? Environment.GetEnvironmentVariable("OANDA_API_KEY");
        var acknowledge = parser.HasFlag("acknowledge-scraping");

        if (mode == ExecutionMode.Api && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API mode selected but no OANDA API key provided.");
        }

        if ((mode == ExecutionMode.Scrape || (mode == ExecutionMode.Auto && string.IsNullOrWhiteSpace(apiKey))) && !acknowledge)
        {
            throw new ArgumentException("Scraping mode requires --acknowledge-scraping to confirm compliance checks.");
        }

        if (amount <= 0)
        {
            throw new ArgumentException("Amount must be greater than zero.");
        }

        return new AppConfig
        {
            FromCurrency = fromCurrency!,
            ToCurrency = toCurrency!,
            StartDate = startDate,
            EndDate = endDate,
            Amount = amount,
            OutputPath = output,
            Mode = mode,
            ApiKey = apiKey,
            DelayMs = delayMs,
            MaxRetries = maxRetries,
            LogLevel = logLevel,
            AcknowledgeScraping = acknowledge
        };
    }

    private static LogLevel ParseLogLevel(string value) => value.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "error" => LogLevel.Error,
        _ => LogLevel.Information
    };

    private static ExecutionMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "api" => ExecutionMode.Api,
        "scrape" => ExecutionMode.Scrape,
        _ => ExecutionMode.Auto
    };
}

internal sealed class ArgParser
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public ArgParser(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = current[2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            else
            {
                value = "true";
            }

            _values[key] = value;
        }
    }

    public string? GetValue(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public int GetInt(string key, int defaultValue)
    {
        var text = GetValue(key);
        return int.TryParse(text, out var parsed) ? parsed : defaultValue;
    }

    public bool HasFlag(string key) => string.Equals(GetValue(key), "true", StringComparison.OrdinalIgnoreCase);
}
