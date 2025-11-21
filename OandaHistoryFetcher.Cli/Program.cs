using Microsoft.Extensions.Logging;
using OandaHistoryFetcher.Core;
using OandaHistoryFetcher.Core.Models;
using OandaHistoryFetcher.Core.Providers;
using OandaHistoryFetcher.Core.Utilities;

namespace OandaHistoryFetcher.Cli;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!AppOptions.TryParse(args, out var parsedOptions, out var error))
        {
            if (error == "help")
            {
                PrintUsage();
                return 0;
            }

            Console.Error.WriteLine(error ?? "Invalid arguments.");
            PrintUsage();
            return 1;
        }

        var options = parsedOptions ?? throw new InvalidOperationException("Options should never be null after successful parse.");

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(options.LogLevel);
            builder.AddConsole();
        });

        var logger = loggerFactory.CreateLogger("OandaHistoryFetcher");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        logger.LogInformation("Starting fetch {From}/{To} {Start}..{End} mode={Mode} delay={DelayMs}ms", options.FromCurrency, options.ToCurrency, options.StartDate, options.EndDate, options.EffectiveMode, options.DelayMs);

        IRateProvider rateProvider = options.EffectiveMode switch
        {
            Mode.Api => new OandaApiRateProvider(options.ApiKey!, TimeSpan.FromMilliseconds(options.DelayMs), options.MaxRetries, loggerFactory.CreateLogger<OandaApiRateProvider>()),
            _ => new OandaPlaywrightRateProvider(TimeSpan.FromMilliseconds(options.DelayMs), options.MaxRetries, loggerFactory.CreateLogger<OandaPlaywrightRateProvider>())
        };

        var totalDays = 0;
        var totalErrors = 0;

        try
        {
            using var writer = CsvWriter.CreateWriter(options.OutputPath);
            CsvWriter.WriteHeader(writer);

            foreach (var date in DateUtils.Enumerate(options.StartDate, options.EndDate))
            {
                totalDays++;
                if (cts.IsCancellationRequested)
                {
                    logger.LogWarning("Cancellation requested. Stopping before date {Date}", date);
                    break;
                }

                logger.LogInformation("Fetching {Date}", date);

                try
                {
                    var rate = await rateProvider.GetRateAsync(options.FromCurrency, options.ToCurrency, options.Amount, date, cts.Token);
                    var record = new RateRecord(date, options.FromCurrency, options.ToCurrency, options.Amount, rate, options.SourceLabel, "ok", string.Empty);
                    CsvWriter.WriteRecord(writer, record);
                }
                catch (RateFetchError ex)
                {
                    totalErrors++;
                    var record = new RateRecord(date, options.FromCurrency, options.ToCurrency, options.Amount, null, options.SourceLabel, "error", ex.Message);
                    CsvWriter.WriteRecord(writer, record);
                    logger.LogWarning(ex, "Failed to fetch rate for {Date}", date);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Operation cancelled. Writing remaining dates as skipped.");
                    var record = new RateRecord(date, options.FromCurrency, options.ToCurrency, options.Amount, null, options.SourceLabel, "skipped", "Cancelled");
                    CsvWriter.WriteRecord(writer, record);
                    break;
                }
                catch (Exception ex)
                {
                    totalErrors++;
                    var record = new RateRecord(date, options.FromCurrency, options.ToCurrency, options.Amount, null, options.SourceLabel, "error", ex.Message);
                    CsvWriter.WriteRecord(writer, record);
                    logger.LogError(ex, "Unexpected failure on {Date}", date);
                }

                await writer.FlushAsync();
                await Task.Delay(options.DelayMs, cts.Token);
            }
        }
        finally
        {
            await rateProvider.DisposeAsync();
        }

        logger.LogInformation("Finished. Days processed: {Days}, errors: {Errors}. Output: {Output}", totalDays, totalErrors, Path.GetFullPath(options.OutputPath));
        return totalErrors > 0 ? 2 : 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project OandaHistoryFetcher.Cli -- --from-currency BTC --to-currency CHF --start-date 2024-01-01 --end-date 2024-01-05 [options]");
        Console.WriteLine();
        Console.WriteLine("Required:");
        Console.WriteLine("  --from-currency <code>   ISO currency/asset code (e.g., BTC)");
        Console.WriteLine("  --to-currency <code>     ISO currency/asset code (e.g., CHF)");
        Console.WriteLine("  --start-date YYYY-MM-DD  Start date (inclusive)");
        Console.WriteLine("  --end-date YYYY-MM-DD    End date (inclusive)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --amount <decimal>             Amount to convert (default 1)");
        Console.WriteLine("  --output <path>                Output CSV path (default ./rates.csv)");
        Console.WriteLine("  --mode api|scrape|auto         Preferred mode (default auto)");
        Console.WriteLine("  --oanda-api-key <key>          API key (or set OANDA_API_KEY env)");
        Console.WriteLine("  --delay-ms <int>               Delay between days in ms (default 1000)");
        Console.WriteLine("  --max-retries <int>            Retries per day (default 3)");
        Console.WriteLine("  --log-level info|debug|error   Logging level (default info)");
        Console.WriteLine("  --acknowledge-scraping         Confirm Terms of Use reviewed for scraping mode");
    }
}
