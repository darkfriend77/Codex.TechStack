using System.Text;
using Microsoft.Extensions.Logging;
using OandaHistoryFetcher.Cli;
using OandaHistoryFetcher.Core.Interfaces;
using OandaHistoryFetcher.Core.Models;
using OandaHistoryFetcher.Core.Providers;
using OandaHistoryFetcher.Core.Utils;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        // Parse arguments
        var argDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                var key = args[i];
                var value = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[i + 1] : "true";
                argDict[key] = value;
                if (value != "true") i++; // Skip next arg if it was the value
            }
        }

        // Helper to get argument or default
        string GetArg(string key, string? defaultValue = null)
        {
            return argDict.TryGetValue(key, out var value) ? value : defaultValue ?? "";
        }

        bool HasArg(string key) => argDict.ContainsKey(key);

        // Extract arguments
        var fromCurrency = GetArg("--from-currency");
        var toCurrency = GetArg("--to-currency");
        var startDateStr = GetArg("--start-date");
        var endDateStr = GetArg("--end-date");
        var amount = decimal.Parse(GetArg("--amount", "1.0"));
        var output = GetArg("--output", "./rates.csv");
        var mode = GetArg("--mode", "auto");
        var apiKey = GetArg("--oanda-api-key");
        var delayMs = int.Parse(GetArg("--delay-ms", "1000"));
        var maxRetries = int.Parse(GetArg("--max-retries", "3"));
        var logLevel = GetArg("--log-level", "info");
        var acknowledgeScraping = HasArg("--acknowledge-scraping");

        // Validate required arguments
        if (string.IsNullOrWhiteSpace(fromCurrency))
        {
            Console.Error.WriteLine("Error: --from-currency is required");
            PrintUsage();
            return 1;
        }
        if (string.IsNullOrWhiteSpace(toCurrency))
        {
            Console.Error.WriteLine("Error: --to-currency is required");
            PrintUsage();
            return 1;
        }
        if (string.IsNullOrWhiteSpace(startDateStr))
        {
            Console.Error.WriteLine("Error: --start-date is required");
            PrintUsage();
            return 1;
        }
        if (string.IsNullOrWhiteSpace(endDateStr))
        {
            Console.Error.WriteLine("Error: --end-date is required");
            PrintUsage();
            return 1;
        }

        // Parse dates
        var startDate = DateUtils.ParseDateString(startDateStr);
        var endDate = DateUtils.ParseDateString(endDateStr);

        // Get API key from environment if not provided
        var effectiveApiKey = string.IsNullOrWhiteSpace(apiKey) 
            ? Environment.GetEnvironmentVariable("OANDA_API_KEY") 
            : apiKey;

        // Build configuration
        var config = new AppConfig
        {
            FromCurrency = fromCurrency.ToUpperInvariant(),
            ToCurrency = toCurrency.ToUpperInvariant(),
            StartDate = startDate,
            EndDate = endDate,
            Amount = amount,
            OutputPath = output,
            Mode = mode.ToLowerInvariant(),
            OandaApiKey = effectiveApiKey,
            DelayMs = delayMs,
            MaxRetries = maxRetries,
            LogLevel = logLevel.ToLowerInvariant(),
            AcknowledgeScraping = acknowledgeScraping
        };

        // Validate configuration
        config.Validate();

        // Setup logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(config.LogLevel switch
            {
                "debug" => LogLevel.Debug,
                "error" => LogLevel.Error,
                _ => LogLevel.Information
            });
        });

        var logger = loggerFactory.CreateLogger("OandaHistoryFetcher");

        // Display configuration
        logger.LogInformation("=== OANDA Historical Rate Fetcher ===");
        logger.LogInformation("Mode: {Mode}", config.ActiveMode);
        logger.LogInformation("Currency: {From} -> {To}", config.FromCurrency, config.ToCurrency);
        logger.LogInformation("Date range: {Start} to {End}", config.StartDate, config.EndDate);
        logger.LogInformation("Amount: {Amount}", config.Amount);
        logger.LogInformation("Output: {Output}", config.OutputPath);
        logger.LogInformation("Delay: {Delay}ms", config.DelayMs);
        logger.LogInformation("Max retries: {MaxRetries}", config.MaxRetries);
        logger.LogInformation("======================================");

        // Create rate provider
        IRateProvider rateProvider = config.ActiveMode switch
        {
            "api" => new OandaApiRateProvider(
                config.OandaApiKey!,
                config.Delay,
                config.MaxRetries,
                loggerFactory.CreateLogger<OandaApiRateProvider>()),
            
            "scrape" => new OandaPlaywrightRateProvider(
                config.Delay,
                config.MaxRetries,
                loggerFactory.CreateLogger<OandaPlaywrightRateProvider>(),
                headless: true),
            
            _ => throw new ArgumentException($"Invalid mode: {config.ActiveMode}")
        };

        await using (rateProvider)
        {
            // Open CSV file
            var directory = Path.GetDirectoryName(Path.GetFullPath(config.OutputPath));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var streamWriter = new StreamWriter(config.OutputPath, false, Encoding.UTF8);
            using var csvWriter = CsvUtils.CreateCsvWriter(streamWriter);

            // Write header
            CsvUtils.WriteHeader(csvWriter);

            // Iterate through dates
            var dates = DateUtils.EnumerateDates(config.StartDate, config.EndDate).ToList();
            var totalDates = dates.Count;
            var successCount = 0;
            var errorCount = 0;

            logger.LogInformation("Processing {Count} dates...", totalDates);

            foreach (var (date, index) in dates.Select((d, i) => (d, i)))
            {
                try
                {
                    logger.LogInformation("[{Current}/{Total}] Fetching rate for {Date}...", 
                        index + 1, totalDates, date);

                    var rate = await rateProvider.GetRateAsync(
                        config.FromCurrency,
                        config.ToCurrency,
                        config.Amount,
                        date,
                        CancellationToken.None);

                    var record = new RateRecord(
                        date,
                        config.FromCurrency,
                        config.ToCurrency,
                        config.Amount,
                        rate,
                        config.ActiveMode,
                        "ok",
                        "");

                    CsvUtils.WriteRecord(csvWriter, record);
                    successCount++;

                    logger.LogInformation("✓ Rate: {Rate}", rate);
                }
                catch (RateFetchError ex)
                {
                    logger.LogWarning("✗ Error: {Message}", ex.Message);

                    var record = new RateRecord(
                        date,
                        config.FromCurrency,
                        config.ToCurrency,
                        config.Amount,
                        null,
                        config.ActiveMode,
                        "error",
                        ex.Message);

                    CsvUtils.WriteRecord(csvWriter, record);
                    errorCount++;
                }

                // Delay between requests (except for last one)
                if (index < totalDates - 1)
                {
                    await Task.Delay(config.Delay);
                }
            }

            logger.LogInformation("======================================");
            logger.LogInformation("Completed processing {Total} dates", totalDates);
            logger.LogInformation("✓ Successful: {Success}", successCount);
            logger.LogInformation("✗ Errors: {Errors}", errorCount);
            logger.LogInformation("Output saved to: {Output}", config.OutputPath);
            logger.LogInformation("======================================");
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        if (ex.InnerException != null)
        {
            Console.Error.WriteLine($"Inner: {ex.InnerException.Message}");
        }
        return 1;
    }
}

static void PrintUsage()
{
    Console.WriteLine();
    Console.WriteLine("OANDA Historical Currency Rate Exporter");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project OandaHistoryFetcher.Cli -- [options]");
    Console.WriteLine();
    Console.WriteLine("Required Options:");
    Console.WriteLine("  --from-currency <CODE>     Source currency code (e.g., BTC)");
    Console.WriteLine("  --to-currency <CODE>       Target currency code (e.g., CHF)");
    Console.WriteLine("  --start-date <YYYY-MM-DD>  Start date");
    Console.WriteLine("  --end-date <YYYY-MM-DD>    End date (inclusive)");
    Console.WriteLine();
    Console.WriteLine("Optional:");
    Console.WriteLine("  --amount <DECIMAL>         Amount to convert (default: 1.0)");
    Console.WriteLine("  --output <PATH>            Output CSV file (default: ./rates.csv)");
    Console.WriteLine("  --mode <MODE>              Mode: api, scrape, auto (default: auto)");
    Console.WriteLine("  --oanda-api-key <KEY>      OANDA API key (or set OANDA_API_KEY env var)");
    Console.WriteLine("  --delay-ms <MS>            Delay between requests in ms (default: 1000)");
    Console.WriteLine("  --max-retries <N>          Max retries per request (default: 3)");
    Console.WriteLine("  --log-level <LEVEL>        Log level: info, debug, error (default: info)");
    Console.WriteLine("  --acknowledge-scraping     Acknowledge scraping mode usage");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  dotnet run --project OandaHistoryFetcher.Cli -- \\");
    Console.WriteLine("    --from-currency BTC --to-currency CHF \\");
    Console.WriteLine("    --start-date 2024-01-01 --end-date 2024-01-10 \\");
    Console.WriteLine("    --mode scrape --acknowledge-scraping");
    Console.WriteLine();
}
