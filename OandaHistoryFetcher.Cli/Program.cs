using System.Text;
using Microsoft.Extensions.Logging;
using OandaHistoryFetcher.Core.Abstractions;
using OandaHistoryFetcher.Core.Models;
using OandaHistoryFetcher.Core.Providers;
using OandaHistoryFetcher.Core.Utilities;

namespace OandaHistoryFetcher.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AppConfig config;
        try
        {
            config = AppConfig.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");
            return 1;
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(config.LogLevel);
            builder.AddConsole();
        });

        var logger = loggerFactory.CreateLogger("OandaHistoryFetcher");
        logger.LogInformation("Starting run {From}->{To} from {Start} to {End} using mode {Mode}", config.FromCurrency, config.ToCurrency, config.StartDate, config.EndDate, config.Mode);

        IRateProvider provider = config.ShouldUseApi
            ? new OandaApiRateProvider(config.ApiKey!, config.Delay, config.MaxRetries, loggerFactory.CreateLogger<OandaApiRateProvider>())
            : new OandaPlaywrightRateProvider(config.Delay, config.MaxRetries, loggerFactory.CreateLogger<OandaPlaywrightRateProvider>());

        var sourceLabel = config.ShouldUseApi ? "api" : "scrape";

        await using var disposableProvider = provider;
        await using var stream = new FileStream(config.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await CsvWriter.WriteHeaderAsync(writer);

        var successCount = 0;
        var failureCount = 0;

        foreach (var date in DateUtils.EnumerateDates(config.StartDate, config.EndDate))
        {
            RateRecord record;
            try
            {
                var rate = await FetchWithRetriesAsync(provider, config, date, sourceLabel, logger);
                record = new RateRecord(date, config.FromCurrency, config.ToCurrency, config.Amount, rate, sourceLabel, "ok", string.Empty);
                successCount++;
                logger.LogInformation("Fetched rate for {Date}: {Rate}", date, rate);
            }
            catch (RateFetchError ex)
            {
                record = new RateRecord(date, config.FromCurrency, config.ToCurrency, config.Amount, null, sourceLabel, "error", ex.Message);
                failureCount++;
                logger.LogError(ex, "Failed to fetch rate for {Date}", date);
            }
            catch (Exception ex)
            {
                record = new RateRecord(date, config.FromCurrency, config.ToCurrency, config.Amount, null, sourceLabel, "error", ex.Message);
                failureCount++;
                logger.LogError(ex, "Unexpected error when fetching rate for {Date}", date);
            }

            await CsvWriter.WriteRecordAsync(writer, record);
            await writer.FlushAsync();
            await Task.Delay(config.Delay);
        }

        logger.LogInformation("Completed run. Successes: {Successes}, Failures: {Failures}", successCount, failureCount);
        return failureCount > 0 ? 2 : 0;
    }

    private static async Task<decimal> FetchWithRetriesAsync(IRateProvider provider, AppConfig config, DateOnly date, string source, ILogger logger)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= config.MaxRetries; attempt++)
        {
            try
            {
                return await provider.GetRateAsync(config.FromCurrency, config.ToCurrency, config.Amount, date);
            }
            catch (RateFetchError ex) when (ex.IsRetryable && attempt < config.MaxRetries)
            {
                lastError = ex;
                logger.LogWarning(ex, "Retryable error when fetching {Date} (attempt {Attempt})", date, attempt);
                await Task.Delay(config.Delay * attempt);
            }
            catch (RateFetchError ex)
            {
                throw ex;
            }
            catch (Exception ex) when (attempt < config.MaxRetries)
            {
                lastError = ex;
                logger.LogWarning(ex, "Unexpected error when fetching {Date} (attempt {Attempt})", date, attempt);
                await Task.Delay(config.Delay * attempt);
            }
        }

        throw lastError switch
        {
            RateFetchError ex => ex,
            Exception ex => new RateFetchError(date, source, ex.Message, false, ex),
            _ => new RateFetchError(date, source, "Unknown error", false)
        };
    }
}
