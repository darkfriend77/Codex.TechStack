using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using OandaHistoryFetcher.Core.Interfaces;
using OandaHistoryFetcher.Core.Models;

namespace OandaHistoryFetcher.Core.Providers;

/// <summary>
/// Rate provider using Playwright browser automation to scrape OANDA converter page.
/// </summary>
public partial class OandaPlaywrightRateProvider : IRateProvider
{
    private readonly TimeSpan _delay;
    private readonly int _maxRetries;
    private readonly ILogger? _logger;
    private readonly bool _headless;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private bool _initialized;
    private bool _disposed;

    private string? _currentFromCurrency;
    private string? _currentToCurrency;
    private decimal _currentAmount;

    [GeneratedRegex(@"[^\d\-\.,]")]
    private static partial Regex NonNumericRegex();

    public OandaPlaywrightRateProvider(
        TimeSpan delay,
        int maxRetries,
        ILogger? logger = null,
        bool headless = true)
    {
        _delay = delay;
        _maxRetries = maxRetries;
        _logger = logger;
        _headless = headless;
    }

    private async Task InitializeAsync()
    {
        if (_initialized) return;

        _logger?.LogInformation("Initializing Playwright browser...");

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = _headless,
            Args = new[] { "--disable-blink-features=AutomationControlled" }
        });

        _page = await _browser.NewPageAsync(new()
        {
            UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            ViewportSize = new() { Width = 1920, Height = 1080 }
        });

        _initialized = true;
        _logger?.LogInformation("Playwright browser initialized successfully");
    }

    public async Task<decimal> GetRateAsync(
        string fromCurrency,
        string toCurrency,
        decimal amount,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync();

        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                _logger?.LogDebug(
                    "Scraping rate: {From} -> {To} for {Date} (attempt {Attempt})",
                    fromCurrency, toCurrency, date, attempt + 1);

                // Navigate to converter page if not already there or currencies changed
                if (_currentFromCurrency != fromCurrency ||
                    _currentToCurrency != toCurrency ||
                    _currentAmount != amount)
                {
                    await NavigateToConverterAsync(fromCurrency, toCurrency, amount, cancellationToken);
                }

                // Set the date
                await SetDateAsync(date, cancellationToken);

                // Wait for and extract the rate
                var rate = await ExtractRateAsync(amount, cancellationToken);

                _logger?.LogInformation(
                    "Successfully scraped rate: {From} -> {To} on {Date}: {Rate}",
                    fromCurrency, toCurrency, date, rate);

                return rate;
            }
            catch (Exception ex) when (ex is not RateFetchError && attempt < _maxRetries)
            {
                _logger?.LogWarning(
                    ex,
                    "Scraping error on attempt {Attempt} for {Date}",
                    attempt + 1, date);
                await Task.Delay(_delay * (attempt + 1), cancellationToken);
            }
            catch (Exception ex) when (ex is not RateFetchError)
            {
                _logger?.LogError(ex, "Failed to scrape rate for {Date}", date);
                throw new RateFetchError(
                    date,
                    "scrape",
                    $"Scraping failed: {ex.Message}",
                    false,
                    ex);
            }
        }

        throw new RateFetchError(
            date,
            "scrape",
            $"Failed to scrape rate after {_maxRetries + 1} attempts",
            false);
    }

    private async Task NavigateToConverterAsync(
        string fromCurrency,
        string toCurrency,
        decimal amount,
        CancellationToken cancellationToken)
    {
        if (_page == null) throw new InvalidOperationException("Page not initialized");

        var url = $"https://www.oanda.com/currency-converter/en/" +
                  $"?from={fromCurrency.ToUpperInvariant()}" +
                  $"&to={toCurrency.ToUpperInvariant()}" +
                  $"&amount={amount}";

        _logger?.LogDebug("Navigating to {Url}", url);

        try
        {
            await _page.GotoAsync(url, new()
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000
            });

            _currentFromCurrency = fromCurrency;
            _currentToCurrency = toCurrency;
            _currentAmount = amount;

            // Wait for the converter to be ready by waiting for key elements
            await _page.WaitForSelectorAsync("input", new() { Timeout = 10000 });

            // Give it extra time for any dynamic content
            await Task.Delay(3000, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to navigate to converter");
            throw;
        }
    }

    private async Task SetDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        if (_page == null) throw new InvalidOperationException("Page not initialized");

        // Format date as OANDA expects (e.g., "21 November 2024")
        var formattedDate = FormatDateForOanda(date);
        _logger?.LogDebug("Setting date to: {Date}", formattedDate);

        try
        {
            // Look for the date input inside the datepicker wrapper
            // OANDA uses a React datepicker component
            ILocator? dateInput = null;

            // Try to find input within the datepicker wrapper
            var datePickerSelectors = new[]
            {
                ".react-datepicker-wrapper input",
                "[class*='datepicker'] input",
                "input[placeholder*='date' i]",
                "input[type='text']"
            };

            foreach (var selector in datePickerSelectors)
            {
                var locator = _page.Locator(selector);
                if (await locator.CountAsync() > 0)
                {
                    // Find the first visible and enabled input
                    var count = await locator.CountAsync();
                    for (int i = 0; i < count; i++)
                    {
                        var element = locator.Nth(i);
                        if (await element.IsVisibleAsync() && await element.IsEnabledAsync())
                        {
                            dateInput = element;
                            _logger?.LogDebug("Found date input with selector: {Selector}", selector);
                            break;
                        }
                    }
                    if (dateInput != null) break;
                }
            }

            if (dateInput == null)
            {
                throw new Exception("Could not find date input field");
            }

            // Clear existing value and set new date
            await dateInput.ClickAsync();
            await Task.Delay(300, cancellationToken);

            // Select all text and delete
            await dateInput.PressAsync("Control+A");
            await dateInput.PressAsync("Backspace");
            await Task.Delay(200, cancellationToken);

            // Type the new date character by character
            await _page.Keyboard.TypeAsync(formattedDate, new() { Delay = 50 });
            await Task.Delay(300, cancellationToken);

            // Press Enter or Tab to confirm
            await dateInput.PressAsync("Enter");

            // Wait for the page to update with new date
            await Task.Delay(2000, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set date");
            throw new RateFetchError(
                date,
                "scrape",
                $"Failed to set date: {ex.Message}",
                true,
                ex);
        }
    }

    private async Task<decimal> ExtractRateAsync(decimal amount, CancellationToken cancellationToken)
    {
        if (_page == null) throw new InvalidOperationException("Page not initialized");

        try
        {
            // Wait a moment for the conversion to complete
            await Task.Delay(2000, cancellationToken);

            // Try to find the result using various strategies
            var resultSelectors = new[]
            {
                // Common output/result field selectors
                "input[class*='output']",
                "input[class*='result']",
                "input[class*='converted']"
            };

            ILocator? resultLocator = null;
            string? resultText = null;

            foreach (var selector in resultSelectors)
            {
                try
                {
                    var locator = _page.Locator(selector);
                    var count = await locator.CountAsync();

                    if (count > 0)
                    {
                        _logger?.LogDebug("Found {Count} elements with selector {Selector}", count, selector);

                        var element = locator.First;
                        if (await element.IsVisibleAsync())
                        {
                            resultText = await element.InputValueAsync();

                            if (!string.IsNullOrWhiteSpace(resultText))
                            {
                                var cleaned = new string(resultText.Where(c => char.IsDigit(c) || c == '.' || c == ',' || c == '-').ToArray());
                                if (cleaned.Length > 0)
                                {
                                    _logger?.LogDebug("Found result with selector {Selector}: {Text}", selector, resultText);
                                    resultLocator = element;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug("Selector {Selector} failed: {Error}", selector, ex.Message);
                    continue;
                }
            }

            // Fallback: Find all text inputs and skip the amount field
            if (string.IsNullOrWhiteSpace(resultText))
            {
                try
                {
                    var allInputs = _page.Locator("input[type='text']");
                    var count = await allInputs.CountAsync();
                    _logger?.LogDebug("Fallback: Found {Count} text inputs", count);

                    for (int i = 0; i < count; i++)
                    {
                        var element = allInputs.Nth(i);

                        if (!await element.IsVisibleAsync())
                            continue;

                        var value = await element.InputValueAsync();
                        _logger?.LogDebug("Input[{Index}] value: {Value}", i, value);

                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            var cleaned = new string(value.Where(c => char.IsDigit(c) || c == '.' || c == ',' || c == '-').ToArray());
                            if (cleaned.Length > 0 && decimal.TryParse(cleaned.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var testValue))
                            {
                                // Skip if this is the amount field
                                if (Math.Abs(testValue - amount) < 0.01m)
                                {
                                    _logger?.LogDebug("Skipping input[{Index}] - matches amount", i);
                                    continue;
                                }

                                // This should be the conversion result
                                _logger?.LogDebug("Using input[{Index}] as result: {Value}", i, value);
                                resultText = value;
                                resultLocator = element;
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Fallback input search failed");
                }
            }

            if (string.IsNullOrWhiteSpace(resultText))
            {
                // Debug: Take a screenshot and dump page content
                var screenshotPath = $"debug_screenshot_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png";
                await _page.ScreenshotAsync(new() { Path = screenshotPath });
                _logger?.LogWarning("Saved debug screenshot to {Path}", screenshotPath);

                var allText = await _page.TextContentAsync("body") ?? "";
                _logger?.LogDebug("Page content: {Content}",
                    allText.Length > 1000 ? allText.Substring(0, 1000) + "..." : allText);

                throw new Exception("Could not find result element with numeric value");
            }

            // Parse the numeric value
            var convertedAmount = ParseNumericValue(resultText);
            var rate = convertedAmount / amount;

            _logger?.LogDebug("Extracted converted amount: {Amount}, calculated rate: {Rate}", convertedAmount, rate);

            return rate;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to extract rate");
            throw new RateFetchError(
                DateOnly.FromDateTime(DateTime.Today),
                "scrape",
                $"Failed to extract rate: {ex.Message}",
                true,
                ex);
        }
    }

    private static string FormatDateForOanda(DateOnly date)
    {
        // Format: "21 November 2024"
        return date.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture);
    }

    private static decimal ParseNumericValue(string text)
    {
        // Remove all non-numeric characters except digits, decimal separators, and minus
        var cleaned = NonNumericRegex().Replace(text, "");

        // Determine decimal separator (last occurrence of . or ,)
        var lastDot = cleaned.LastIndexOf('.');
        var lastComma = cleaned.LastIndexOf(',');

        string normalized;
        if (lastDot > lastComma)
        {
            // Period is decimal separator
            normalized = cleaned.Replace(",", "").Replace(".", ".");
        }
        else if (lastComma > lastDot)
        {
            // Comma is decimal separator
            normalized = cleaned.Replace(".", "").Replace(",", ".");
        }
        else
        {
            // No decimal separator
            normalized = cleaned.Replace(".", "").Replace(",", "");
        }

        if (decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"Could not parse numeric value from: {text}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _logger?.LogInformation("Disposing Playwright resources...");

        if (_page != null)
        {
            await _page.CloseAsync();
            _page = null;
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;

        _disposed = true;
        _logger?.LogInformation("Playwright resources disposed");
    }
}
