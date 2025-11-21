using System.Globalization;
using Microsoft.Extensions.Logging;

namespace OandaHistoryFetcher.Cli;

internal enum Mode
{
    Api,
    Scrape,
    Auto
}

internal sealed class AppOptions
{
    private AppOptions(
        string fromCurrency,
        string toCurrency,
        DateOnly startDate,
        DateOnly endDate,
        decimal amount,
        string outputPath,
        Mode requestedMode,
        Mode effectiveMode,
        string? apiKey,
        int delayMs,
        int maxRetries,
        LogLevel logLevel,
        bool acknowledgeScraping)
    {
        FromCurrency = fromCurrency;
        ToCurrency = toCurrency;
        StartDate = startDate;
        EndDate = endDate;
        Amount = amount;
        OutputPath = outputPath;
        RequestedMode = requestedMode;
        EffectiveMode = effectiveMode;
        ApiKey = apiKey;
        DelayMs = delayMs;
        MaxRetries = maxRetries;
        LogLevel = logLevel;
        AcknowledgeScraping = acknowledgeScraping;
    }

    public string FromCurrency { get; }
    public string ToCurrency { get; }
    public DateOnly StartDate { get; }
    public DateOnly EndDate { get; }
    public decimal Amount { get; }
    public string OutputPath { get; }
    public Mode RequestedMode { get; }
    public Mode EffectiveMode { get; }
    public string? ApiKey { get; }
    public int DelayMs { get; }
    public int MaxRetries { get; }
    public LogLevel LogLevel { get; }
    public bool AcknowledgeScraping { get; }
    public string SourceLabel => EffectiveMode == Mode.Api ? "api" : "scrape";

    public static bool TryParse(string[] args, out AppOptions? options, out string? error)
    {
        options = null;
        error = null;

        var parsed = ParseArgs(args, out error);
        if (parsed is null)
        {
            return false;
        }

        if (parsed.ContainsKey("help") || parsed.ContainsKey("?"))
        {
            error = "help";
            return false;
        }

        if (!parsed.TryGetValue("from-currency", out var fromCurrency) || string.IsNullOrWhiteSpace(fromCurrency))
        {
            error = "Missing required --from-currency.";
            return false;
        }

        if (!parsed.TryGetValue("to-currency", out var toCurrency) || string.IsNullOrWhiteSpace(toCurrency))
        {
            error = "Missing required --to-currency.";
            return false;
        }

        if (!parsed.TryGetValue("start-date", out var startDateRaw) || !DateOnly.TryParseExact(startDateRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate))
        {
            error = "Missing or invalid --start-date (expected YYYY-MM-DD).";
            return false;
        }

        if (!parsed.TryGetValue("end-date", out var endDateRaw) || !DateOnly.TryParseExact(endDateRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate))
        {
            error = "Missing or invalid --end-date (expected YYYY-MM-DD).";
            return false;
        }

        if (endDate < startDate)
        {
            error = "end-date must be on or after start-date.";
            return false;
        }

        var requestedMode = ParseMode(parsed.GetValueOrDefault("mode"));
        var logLevel = ParseLogLevel(parsed.GetValueOrDefault("log-level"));

        var amount = 1.0m;
        if (parsed.TryGetValue("amount", out var amountRaw))
        {
            if (!decimal.TryParse(amountRaw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
            {
                error = "Invalid --amount (use decimal, e.g., 1.5).";
                return false;
            }
        }

        var outputPath = parsed.GetValueOrDefault("output") ?? "./rates.csv";
        var delayMs = ParseInt(parsed.GetValueOrDefault("delay-ms"), 1000, 0, int.MaxValue, "delay-ms", ref error);
        if (error is not null)
        {
            return false;
        }

        var maxRetries = ParseInt(parsed.GetValueOrDefault("max-retries"), 3, 1, 10, "max-retries", ref error);
        if (error is not null)
        {
            return false;
        }

        var apiKey = parsed.GetValueOrDefault("oanda-api-key") ?? Environment.GetEnvironmentVariable("OANDA_API_KEY");
        var acknowledgeScraping = parsed.ContainsKey("acknowledge-scraping");

        var effectiveMode = requestedMode switch
        {
            Mode.Api => Mode.Api,
            Mode.Scrape => Mode.Scrape,
            Mode.Auto => string.IsNullOrWhiteSpace(apiKey) ? Mode.Scrape : Mode.Api,
            _ => Mode.Auto
        };

        if (effectiveMode == Mode.Api && string.IsNullOrWhiteSpace(apiKey))
        {
            error = "API mode selected but no API key supplied (--oanda-api-key or OANDA_API_KEY env).";
            return false;
        }

        if (effectiveMode == Mode.Scrape && !acknowledgeScraping)
        {
            error = "Scraping mode requires --acknowledge-scraping to confirm Terms of Use have been reviewed.";
            return false;
        }

        options = new AppOptions(
            fromCurrency.Trim(),
            toCurrency.Trim(),
            startDate,
            endDate,
            amount,
            outputPath,
            requestedMode,
            effectiveMode,
            apiKey,
            delayMs,
            maxRetries,
            logLevel,
            acknowledgeScraping);
        return true;
    }

    private static Mode ParseMode(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "api" => Mode.Api,
            "scrape" => Mode.Scrape,
            "auto" => Mode.Auto,
            _ => Mode.Auto
        };
    }

    private static LogLevel ParseLogLevel(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "debug" => LogLevel.Debug,
            "error" => LogLevel.Error,
            _ => LogLevel.Information
        };
    }

    private static int ParseInt(string? value, int @default, int min, int max, string name, ref string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return @default;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"Invalid --{name}.";
            return @default;
        }

        if (parsed < min || parsed > max)
        {
            error = $"--{name} must be between {min} and {max}.";
            return @default;
        }

        return parsed;
    }

    private static Dictionary<string, string?>? ParseArgs(string[] args, out string? error)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unrecognized argument '{arg}'.";
                return null;
            }

            var key = arg[2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[i + 1];
                i++;
            }

            result[key] = value ?? "true";
        }

        return result;
    }
}
