using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using OandaHistoryFetcher.Core.Interfaces;
using OandaHistoryFetcher.Core.Models;

namespace OandaHistoryFetcher.Core.Providers;

public class OandaPlaywrightRateProvider : IRateProvider
{
    private readonly TimeSpan _delay;
    private readonly int _maxRetries;
    private readonly ILogger? _logger;
    private readonly bool _headless;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private bool _initialized;

    public OandaPlaywrightRateProvider(TimeSpan delay, int maxRetries, bool headless = true, ILogger? logger = null)
    {
        _delay = delay;
        _maxRetries = maxRetries;
        _headless = headless;
        _logger = logger;
    }

    private string? _lastUrl;

    private async Task EnsureInitializedAsync(string fromCurrency, string toCurrency, decimal amount)
    {
        if (!_initialized)
        {
            _logger?.LogInformation("Initializing Playwright browser...");
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = _headless });
            _page = await _browser.NewPageAsync();
            _initialized = true;
        }

        var url = $"https://www.oanda.com/currency-converter/en/?from={fromCurrency}&to={toCurrency}&amount={amount}";

        if (_lastUrl == url) return;

        _logger?.LogInformation("Navigating to {Url}", url);

        // Go to the page. NetworkIdle can be flaky on heavy sites, so use DomContentLoaded and then wait for selector.
        if (_page == null) throw new InvalidOperationException("Page not initialized");
        await _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });

        // Wait for the converter widget to be visible
        try
        {
            await _page.Locator(".react-datepicker-wrapper input").WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        }
        catch
        {
            // If specific input not found, wait for body
            await _page.Locator("body").WaitForAsync();
        }

        // Handle cookie consent if it appears (common on these sites)
        // This is speculative but good practice.
        try
        {
            var cookieButton = _page.Locator("button#onetrust-accept-btn-handler");
            if (await cookieButton.IsVisibleAsync())
            {
                await cookieButton.ClickAsync();
            }
        }
        catch { /* Ignore if not found */ }

        _lastUrl = url;
    }

    public async Task<decimal> GetRateAsync(string fromCurrency, string toCurrency, decimal amount, DateOnly date, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(fromCurrency, toCurrency, amount);

        int attempts = 0;
        while (attempts <= _maxRetries)
        {
            try
            {
                return await FetchRateInternalAsync(date, amount, cancellationToken);
            }
            catch (Exception ex)
            {
                attempts++;
                if (attempts > _maxRetries)
                {
                    throw new RateFetchError(date, "scrape", $"Failed to fetch rate after {_maxRetries} retries: {ex.Message}", false, ex);
                }

                _logger?.LogWarning("Scraping attempt {Attempt} failed for {Date}: {Message}. Retrying...", attempts, date, ex.Message);
                await Task.Delay(_delay * attempts, cancellationToken);
            }
        }

        throw new RateFetchError(date, "scrape", "Unexpected unreachable code", false);
    }

    private async Task<decimal> FetchRateInternalAsync(DateOnly date, decimal amount, CancellationToken cancellationToken)
    {
        if (_page == null) throw new InvalidOperationException("Browser not initialized");

        // 1. Set Date
        // Selector for date input. Found via dump: inside .react-datepicker-wrapper
        var dateInput = _page.Locator(".react-datepicker-wrapper input");

        // Wait for input to be ready
        await dateInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        // Format: dd MMMM yyyy seems to be the display format (e.g. "21 November 2025")
        string formattedDate = date.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture);

        // Create a task to wait for the network response initiated by the date change
        // We wait for LoadState.NetworkIdle which is generally reliable for these SPAs
        // Actually, pressing Enter might not trigger navigation but XHR. 
        // Let's use WaitForResponseAsync if we can identify strict pattern, but NetworkIdle is easier for now.
        // Better: Wait for the inputs to update? No, difficult.
        // Let's go with NetworkIdle.

        // Create a task to wait for the *specific* API response.
        // OANDA typically uses an API like https://www.oanda.com/api/v1/rates/...
        // A generic check for "rates" in URL and JSON content type is robust.

        var waitTask = _page.WaitForResponseAsync(resp =>
            resp.Url.Contains("rates") &&
            resp.Status == 200 &&
            resp.Request.Method == "GET");

        // Force clearing the input first to ensure change event triggers
        await dateInput.ClickAsync();
        await _page.Keyboard.PressAsync("Control+A");
        await _page.Keyboard.PressAsync("Backspace");

        // Type new date
        await _page.Keyboard.TypeAsync(formattedDate);
        await _page.Keyboard.PressAsync("Enter");

        // Click somewhere else to force blur/commit if Enter isn't enough
        await _page.Locator("body").ClickAsync();

        // Wait for API response
        try
        {
            await waitTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger?.LogWarning("Timeout waiting for API response after date change. Proceeding...");
        }

        // 2. Wait for result to update logic (keep retry logic)
        var quoteAmountInput = _page.Locator("input[name='numberformat']").Nth(1);

        string value = "";
        for (int i = 0; i < 10; i++) // Increased retries
        {
            await Task.Delay(200, cancellationToken);
            value = await quoteAmountInput.InputValueAsync();
            if (string.IsNullOrWhiteSpace(value))
                value = await quoteAmountInput.InnerTextAsync();

            // Basic validation: shouldn't be empty, and ideally shouldn't be the suspicious 1.5902 if we know it's wrong?
            // But we can't hardcode magic values.
            if (!string.IsNullOrWhiteSpace(value)) break;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new Exception("Could not find rate value in page.");
        }

        // 3. Parse
        // Remove commas, keep decimal point.
        // Clean string:
        value = value.Replace(",", ""); // Assume comma is thousands separator

        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
        {
            // The value in the box is the Total Amount (Rate * Amount).
            // We need the rate per 1 unit if amount was not 1.
            // But requirements say we input amount. 
            // If we want the rate, we calculate: Rate = Total / Amount

            if (amount == 0) return 0;
            return rate / amount;
        }

        throw new Exception($"Failed to parse rate from value: '{value}'");
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
        {
            await _browser.CloseAsync();
            await _browser.DisposeAsync();
        }
        _playwright?.Dispose();
    }
}
