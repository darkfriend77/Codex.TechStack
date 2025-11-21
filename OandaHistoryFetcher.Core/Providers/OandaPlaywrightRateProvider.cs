using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using OandaHistoryFetcher.Core.Abstractions;
using OandaHistoryFetcher.Core.Models;
using OandaHistoryFetcher.Core.Utilities;

namespace OandaHistoryFetcher.Core.Providers;

public sealed class OandaPlaywrightRateProvider : IRateProvider
{
    private readonly TimeSpan _delay;
    private readonly int _maxRetries;
    private readonly ILogger? _logger;
    private readonly bool _headless;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private string? _currentFrom;
    private string? _currentTo;
    private decimal _currentAmount;

    public OandaPlaywrightRateProvider(TimeSpan delay, int maxRetries, ILogger? logger = null, bool headless = true)
    {
        _delay = delay;
        _maxRetries = Math.Max(1, maxRetries);
        _logger = logger;
        _headless = headless;
    }

    public async Task<decimal> GetRateAsync(
        string fromCurrency,
        string toCurrency,
        decimal amount,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        await EnsurePageAsync(fromCurrency, toCurrency, amount, cancellationToken).ConfigureAwait(false);
        if (_page is null)
        {
            throw new RateFetchError(date, "scrape", "Playwright page was not initialized.", false);
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                await SetDateAsync(_page, date, cancellationToken).ConfigureAwait(false);
                var text = await ReadResultTextAsync(_page, date, cancellationToken).ConfigureAwait(false);
                var parsed = RateParsing.ParseNormalizedNumber(text);
                if (amount == 0)
                {
                    throw new RateFetchError(date, "scrape", "Amount cannot be zero when computing rate.", false);
                }

                return parsed / amount;
            }
            catch (RateFetchError ex) when (ex.IsRetryable && attempt < _maxRetries)
            {
                lastError = ex;
                _logger?.LogWarning(ex, "Retrying scraping for {Date} (attempt {Attempt})", date, attempt);
                await Task.Delay(GetBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex) when (attempt < _maxRetries)
            {
                lastError = ex;
                _logger?.LogWarning(ex, "Timeout when scraping for {Date}, retrying (attempt {Attempt})", date, attempt);
                await Task.Delay(GetBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new RateFetchError(date, "scrape", $"Scraping failed: {ex.Message}", attempt < _maxRetries, ex);
            }
        }

        throw new RateFetchError(date, "scrape", lastError?.Message ?? "Unknown scraping failure", false, lastError);
    }

    private async Task EnsurePageAsync(string fromCurrency, string toCurrency, decimal amount, CancellationToken token)
    {
        if (_page is not null && _currentFrom == fromCurrency && _currentTo == toCurrency && _currentAmount == amount)
        {
            return;
        }

        if (_playwright is null)
        {
            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        }

        _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _headless
        });

        _page ??= await _browser.NewPageAsync(new BrowserNewPageOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119 Safari/537.36"
        });

        var url = BuildBaseUrl(fromCurrency, toCurrency, amount);
        _logger?.LogDebug("Navigating to {Url}", url);
        await _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
        _currentFrom = fromCurrency;
        _currentTo = toCurrency;
        _currentAmount = amount;
    }

    private static string BuildBaseUrl(string from, string to, decimal amount)
    {
        var query = $"from={WebUtility.UrlEncode(from)}&to={WebUtility.UrlEncode(to)}&amount={WebUtility.UrlEncode(amount.ToString(CultureInfo.InvariantCulture))}";
        return $"https://www.oanda.com/currency-converter/en/?{query}";
    }

    private async Task SetDateAsync(IPage page, DateOnly date, CancellationToken token)
    {
        var formattedDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var locator = page.Locator("input[aria-label*='Date'], input[name='date'], input[type='date']");
        if (await locator.CountAsync().ConfigureAwait(false) == 0)
        {
            throw new RateFetchError(date, "scrape", "Date input not found on converter page.", true);
        }

        var dateInput = locator.First;
        token.ThrowIfCancellationRequested();
        await dateInput.ClickAsync(new LocatorClickOptions { Timeout = 10000, Force = true }).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await dateInput.FillAsync(formattedDate, new LocatorFillOptions { Timeout = 10000 }).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await dateInput.PressAsync("Enter", new LocatorPressOptions { Timeout = 10000 }).ConfigureAwait(false);
        await Task.Delay(_delay, token).ConfigureAwait(false);
    }

    private async Task<string> ReadResultTextAsync(IPage page, DateOnly date, CancellationToken token)
    {
        var selectors = new[]
        {
            "[data-testid='converter-result']",
            "data-testid=converter-result",
            "[data-testid='converted-amount']",
            ".converter-result",
            "text=/Converted amount/i"
        };

        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync().ConfigureAwait(false) == 0)
            {
                continue;
            }

            var handle = locator.First;
            await handle.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000, State = WaitForSelectorState.Visible }).ConfigureAwait(false);
            var text = await handle.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 15000 }).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _logger?.LogDebug("Raw scraped text: {Text}", text);
                return text;
            }
        }

        throw new RateFetchError(date, "scrape", "Could not read converter result.", true);
    }

    private TimeSpan GetBackoff(int attempt) => TimeSpan.FromMilliseconds(_delay.TotalMilliseconds * Math.Max(1, attempt));

    public async ValueTask DisposeAsync()
    {
        if (_page is not null)
        {
            await _page.CloseAsync();
        }

        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
    }
}
