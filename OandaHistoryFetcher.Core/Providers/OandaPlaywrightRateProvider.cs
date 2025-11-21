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

    private async Task EnsureInitializedAsync(string fromCurrency, string toCurrency, decimal amount)
    {
        if (_initialized) return;

        _logger?.LogInformation("Initializing Playwright browser...");
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = _headless });
        _page = await _browser.NewPageAsync();

        var url = $"https://www.oanda.com/currency-converter/en/?from={fromCurrency}&to={toCurrency}&amount={amount}";
        _logger?.LogInformation("Navigating to {Url}", url);
        
        // Go to the page. NetworkIdle can be flaky on heavy sites, so use DomContentLoaded and then wait for selector.
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

        _initialized = true;
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
        
        await dateInput.ClickAsync();
        // Select all text to overwrite
        await _page.Keyboard.PressAsync("Control+A"); 
        await _page.Keyboard.TypeAsync(formattedDate);
        await _page.Keyboard.PressAsync("Enter");
        
        // 2. Wait for result to update
        // We need to wait for the result to reflect the new date. 
        // The result input has name="numberformat" and is the second one (index 1).
        var quoteAmountInput = _page.Locator("input[name='numberformat']").Nth(1);
        
        // Wait a bit for calculation (network request)
        // We can check if the value changes, but we don't know the old value easily unless we read it before.
        // For now, just wait a bit.
        await Task.Delay(1000, cancellationToken); 

        var value = await quoteAmountInput.InputValueAsync();
        
        if (string.IsNullOrWhiteSpace(value))
        {
             // Try getting text if it's not an input
             value = await quoteAmountInput.InnerTextAsync();
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
