using System;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OandaHistoryFetcher.Core.Interfaces;
using OandaHistoryFetcher.Core.Models;
using OandaHistoryFetcher.Core.Providers;

namespace OandaHistoryFetcher.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("OANDA Historical Currency Rate Exporter");

        var fromOption = new Option<string>("--from-currency", "Source currency code (e.g. BTC)") { IsRequired = true };
        var toOption = new Option<string>("--to-currency", "Target currency code (e.g. CHF)") { IsRequired = true };
        var startDateOption = new Option<string?>("--start-date", "Start date (YYYY-MM-DD)");
        var endDateOption = new Option<string?>("--end-date", "End date (YYYY-MM-DD)");
        var datesOption = new Option<string?>("--dates", "Comma-separated list of dates (e.g. 21.02.2025, 22.02.2025)");
        var amountOption = new Option<decimal>("--amount", () => 1.0m, "Amount to convert");
        var outputOption = new Option<string>("--output", () => "./rates.csv", "Output CSV file path");
        var modeOption = new Option<string>("--mode", () => "auto", "Mode: api, scrape, auto");
        var apiKeyOption = new Option<string>("--oanda-api-key", "OANDA API Key (or set OANDA_API_KEY env var)");
        var delayOption = new Option<int>("--delay-ms", () => 1000, "Delay between requests in ms");
        var maxRetriesOption = new Option<int>("--max-retries", () => 3, "Max retries per request");
        var logLevelOption = new Option<string>("--log-level", () => "info", "Log level: info, debug, error");
        var ackScrapingOption = new Option<bool>("--acknowledge-scraping", "Acknowledge scraping terms");

        rootCommand.AddOption(fromOption);
        rootCommand.AddOption(toOption);
        rootCommand.AddOption(startDateOption);
        rootCommand.AddOption(endDateOption);
        rootCommand.AddOption(datesOption);
        rootCommand.AddOption(amountOption);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(modeOption);
        rootCommand.AddOption(apiKeyOption);
        rootCommand.AddOption(delayOption);
        rootCommand.AddOption(maxRetriesOption);
        rootCommand.AddOption(logLevelOption);
        rootCommand.AddOption(ackScrapingOption);

        rootCommand.SetHandler(async (context) =>
        {
            var from = context.ParseResult.GetValueForOption(fromOption)!;
            var to = context.ParseResult.GetValueForOption(toOption)!;
            var startStr = context.ParseResult.GetValueForOption(startDateOption);
            var endStr = context.ParseResult.GetValueForOption(endDateOption);
            var datesStr = context.ParseResult.GetValueForOption(datesOption);
            var amount = context.ParseResult.GetValueForOption(amountOption);
            var output = context.ParseResult.GetValueForOption(outputOption)!;
            var mode = context.ParseResult.GetValueForOption(modeOption)!;
            var apiKey = context.ParseResult.GetValueForOption(apiKeyOption) ?? Environment.GetEnvironmentVariable("OANDA_API_KEY");
            var delayMs = context.ParseResult.GetValueForOption(delayOption);
            var maxRetries = context.ParseResult.GetValueForOption(maxRetriesOption);
            var logLevelStr = context.ParseResult.GetValueForOption(logLevelOption)!;
            var ackScraping = context.ParseResult.GetValueForOption(ackScrapingOption);

            // Setup Logging
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = false;
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                });
                
                var level = logLevelStr.ToLower() switch
                {
                    "debug" => LogLevel.Debug,
                    "error" => LogLevel.Error,
                    _ => LogLevel.Information
                };
                builder.SetMinimumLevel(level);
            });
            var logger = loggerFactory.CreateLogger<Program>();

            // Validation & Date Parsing
            var targetDates = new List<DateOnly>();

            if (!string.IsNullOrWhiteSpace(datesStr))
            {
                var parts = datesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var part in parts)
                {
                    if (DateOnly.TryParseExact(part, new[] { "yyyy-MM-dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        targetDates.Add(date);
                    }
                    else
                    {
                        logger.LogError("Invalid date format in list: {Date}. Use YYYY-MM-DD or DD.MM.YYYY", part);
                        context.ExitCode = 1;
                        return;
                    }
                }
                targetDates.Sort();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(startStr) || string.IsNullOrWhiteSpace(endStr))
                {
                    logger.LogError("Either --dates OR (--start-date AND --end-date) must be provided.");
                    context.ExitCode = 1;
                    return;
                }

                if (!DateOnly.TryParse(startStr, out var startDate))
                {
                    logger.LogError("Invalid start date format.");
                    context.ExitCode = 1;
                    return;
                }
                if (!DateOnly.TryParse(endStr, out var endDate))
                {
                    logger.LogError("Invalid end date format.");
                    context.ExitCode = 1;
                    return;
                }
                if (startDate > endDate)
                {
                    logger.LogError("Start date must be before or equal to end date.");
                    context.ExitCode = 1;
                    return;
                }

                for (var date = startDate; date <= endDate; date = date.AddDays(1))
                {
                    targetDates.Add(date);
                }
            }

            if (targetDates.Count == 0)
            {
                 logger.LogError("No valid dates to process.");
                 context.ExitCode = 1;
                 return;
            }

            // Mode Selection
            IRateProvider? provider = null;
            string activeMode = mode;

            try
            {
                if (mode == "api")
                {
                    if (string.IsNullOrEmpty(apiKey))
                    {
                        logger.LogError("API mode requires an API key.");
                        context.ExitCode = 1;
                        return;
                    }
                    provider = new OandaApiRateProvider(apiKey, TimeSpan.FromMilliseconds(delayMs), maxRetries, logger);
                }
                else if (mode == "scrape")
                {
                    if (!ackScraping)
                    {
                        logger.LogWarning("Scraping mode selected. Please ensure you comply with OANDA's Terms of Use.");
                        // Not strictly failing, but warning.
                    }
                    provider = new OandaPlaywrightRateProvider(TimeSpan.FromMilliseconds(delayMs), maxRetries, headless: true, logger: logger);
                }
                else // auto
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        activeMode = "api";
                        logger.LogInformation("Auto mode: API key found, using API.");
                        provider = new OandaApiRateProvider(apiKey, TimeSpan.FromMilliseconds(delayMs), maxRetries, logger);
                    }
                    else
                    {
                        activeMode = "scrape";
                        logger.LogInformation("Auto mode: No API key, falling back to scraping.");
                        provider = new OandaPlaywrightRateProvider(TimeSpan.FromMilliseconds(delayMs), maxRetries, headless: true, logger: logger);
                    }
                }

                // Execution Loop
                logger.LogInformation("Starting fetch: {From}->{To} for {Count} dates using {Mode}", from, to, targetDates.Count, activeMode);

                // Prepare CSV
                var csvHeader = "date,from_currency,to_currency,amount,rate,source,status,error_message";
                bool fileExists = File.Exists(output);
                
                // If overwriting or new, write header. If appending, maybe check? Requirement implies new file or overwrite default.
                // Let's overwrite for simplicity as per typical CLI behavior unless specified otherwise.
                // But let's just write to a new file stream.
                
                using var writer = new StreamWriter(output, false, Encoding.UTF8);
                await writer.WriteLineAsync(csvHeader);

                int totalDays = 0;
                int successCount = 0;
                int errorCount = 0;

                foreach (var date in targetDates)
                {
                    totalDays++;
                    RateRecord record;
                    try
                    {
                        decimal rate = await provider.GetRateAsync(from, to, amount, date);
                        record = new RateRecord(date, from, to, amount, rate, activeMode, "ok", "");
                        successCount++;
                        logger.LogInformation("{Date}: Rate {Rate}", date, rate);
                    }
                    catch (RateFetchError ex)
                    {
                        record = new RateRecord(date, from, to, amount, null, activeMode, "error", ex.Message);
                        errorCount++;
                        logger.LogError("{Date}: Failed - {Message}", date, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        record = new RateRecord(date, from, to, amount, null, activeMode, "error", ex.Message);
                        errorCount++;
                        logger.LogError("{Date}: Unexpected Error - {Message}", date, ex.Message);
                    }

                    // Write CSV Row
                    var line = $"{record.Date:yyyy-MM-dd},{record.FromCurrency},{record.ToCurrency},{record.Amount},{record.Rate?.ToString(CultureInfo.InvariantCulture)},{record.Source},{record.Status},\"{record.ErrorMessage?.Replace("\"", "\"\"")}\"";
                    await writer.WriteLineAsync(line);
                    await writer.FlushAsync(); // Flush often to save progress

                    // Delay
                    if (date != targetDates[targetDates.Count - 1]) // Don't delay after last
                        await Task.Delay(delayMs);
                }

                logger.LogInformation("Done. Total: {Total}, Success: {Success}, Errors: {Errors}. Output: {Output}", totalDays, successCount, errorCount, output);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Fatal error during execution.");
                context.ExitCode = 1;
            }
            finally
            {
                if (provider != null)
                    await provider.DisposeAsync();
            }
        });

        return await rootCommand.InvokeAsync(args);
    }
}
