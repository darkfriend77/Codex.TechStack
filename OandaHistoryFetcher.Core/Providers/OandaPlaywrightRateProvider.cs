using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using OandaHistoryFetcher.Core.Utilities;

namespace OandaHistoryFetcher.Core.Providers;

public sealed class OandaPlaywrightRateProvider : IRateProvider
{
    private const string SourceName = "scrape";

    private readonly TimeSpan _delay;
    private readonly int _maxRetries;
    private readonly ILogger? _logger;
    private readonly bool _headless;
    private readonly string _userAgent;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private string? _fromCurrency;
    private string? _toCurrency;
    private decimal _amount;
    private bool _disposed;

    public OandaPlaywrightRateProvider(TimeSpan delay, int maxRetries, ILogger? logger = null, bool headless = true, string? userAgent = null)
    {
        _delay = delay;
        _maxRetries = Math.Max(1, maxRetries);
        _logger = logger;
        _headless = headless;
        _userAgent = userAgent ?? "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0 PlaywrightFetcher/1.0";
    }

    public async Task<decimal> GetRateAsync(string fromCurrency, string toCurrency, decimal amount, DateOnly date, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(fromCurrency, toCurrency, amount, cancellationToken);

        var attempt = 0;
        Exception? lastError = null;

        while (attempt < _maxRetries)
        {
            attempt++;
            try
            {
                await SetDateAsync(date, cancellationToken);
                var convertedAmount = await ReadConvertedAmountAsync(amount, date, cancellationToken);
                _logger?.LogInformation("Fetched rate for {Date}: {Rate}", date, convertedAmount / amount);
                return convertedAmount / amount;
            }
            catch (RateFetchError ex) when (ex.IsRetryable && attempt < _maxRetries)
            {
                lastError = ex;
                var delay = TimeSpan.FromMilliseconds(_delay.TotalMilliseconds * attempt);
                _logger?.LogWarning(ex, "Retryable error on attempt {Attempt} for {Date}. Backing off {Delay}ms", attempt, date, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (PlaywrightException ex) when (attempt < _maxRetries)
            {
                lastError = ex;
                var delay = TimeSpan.FromMilliseconds(_delay.TotalMilliseconds * attempt);
                _logger?.LogWarning(ex, "Playwright error on attempt {Attempt} for {Date}. Backing off {Delay}ms", attempt, date, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex) when (attempt < _maxRetries)
            {
                lastError = ex;
                var delay = TimeSpan.FromMilliseconds(_delay.TotalMilliseconds * attempt);
                _logger?.LogWarning(ex, "Unexpected error on attempt {Attempt} for {Date}. Backing off {Delay}ms", attempt, date, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new RateFetchError(
            date,
            SourceName,
            $"Failed to fetch rate after {_maxRetries} attempts: {lastError?.Message ?? "Unknown error"}",
            isRetryable: false,
            lastError);
    }

    private async Task EnsureInitializedAsync(string fromCurrency, string toCurrency, decimal amount, CancellationToken cancellationToken)
    {
        if (_page != null)
        {
            ValidateCurrencies(fromCurrency, toCurrency, amount);
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_page != null)
            {
                ValidateCurrencies(fromCurrency, toCurrency, amount);
                return;
            }

            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _headless
            });

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = _userAgent,
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
            });

            _page = await _context.NewPageAsync();
            _fromCurrency = fromCurrency;
            _toCurrency = toCurrency;
            _amount = amount;

            var url = BuildConverterUrl(fromCurrency, toCurrency, amount);
            _logger?.LogInformation("Navigating once to converter page: {Url}", url);
            await _page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000
            });
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 });
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void ValidateCurrencies(string fromCurrency, string toCurrency, decimal amount)
    {
        if (!string.Equals(_fromCurrency, fromCurrency, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_toCurrency, toCurrency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Provider instance is limited to a single currency pair. Create a new provider for another pair.");
        }

        if (_amount != amount)
        {
            _logger?.LogDebug("Amount changed from {Old} to {New}. Re-using page but results will be scaled.", _amount, amount);
            _amount = amount;
        }
    }

    private async Task SetDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        if (_page == null)
        {
            throw new InvalidOperationException("Playwright page not initialized.");
        }

        var formattedDate = FormatDateForOanda(date);
        var dateInput = _page.Locator("input[aria-label='Date'], input[type='date'], input[data-testid='date-input']").First;

        _logger?.LogDebug("Setting date to {Date} (formatted '{Formatted}')", date, formattedDate);

        try
        {
            await dateInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 5000 });
            await dateInput.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
            await dateInput.FillAsync(formattedDate, new LocatorFillOptions { Timeout = 5000 });
            await dateInput.PressAsync("Enter", new LocatorPressOptions { Timeout = 5000 });
        }
        catch (PlaywrightException ex)
        {
            throw new RateFetchError(date, SourceName, $"Failed to set date on converter page: {ex.Message}", isRetryable: true, ex);
        }

        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 10000 });
    }

    private async Task<decimal> ReadConvertedAmountAsync(decimal amount, DateOnly date, CancellationToken cancellationToken)
    {
        if (_page == null)
        {
            throw new InvalidOperationException("Playwright page not initialized.");
        }

        var resultLocator = _page.Locator("[data-testid='converter-result'], [data-qa='converter-result'], .converter-result, .converted-amount").First;

        try
        {
            await resultLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
            var rawText = await resultLocator.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 5000 });

            _logger?.LogDebug("Raw result text for {Date}: {Text}", date, rawText);

            var convertedAmount = NumberParser.ParseDecimalFlexible(rawText);
            if (amount == 0)
            {
                throw new RateFetchError(date, SourceName, "Amount cannot be zero when calculating rate.", isRetryable: false);
            }

            return convertedAmount;
        }
        catch (FormatException ex)
        {
            throw new RateFetchError(date, SourceName, $"Could not parse rate from page text: {ex.Message}", isRetryable: true, ex);
        }
        catch (PlaywrightException ex)
        {
            throw new RateFetchError(date, SourceName, $"Playwright failed reading result: {ex.Message}", isRetryable: true, ex);
        }
    }

    private static string BuildConverterUrl(string fromCurrency, string toCurrency, decimal amount)
    {
        return $"https://www.oanda.com/currency-converter/en/?from={Uri.EscapeDataString(fromCurrency)}&to={Uri.EscapeDataString(toCurrency)}&amount={amount}";
    }

    private static string FormatDateForOanda(DateOnly date)
    {
        // The converter accepts typed dates; ISO format is usually accepted in the input field.
        return date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_page != null)
        {
            await _page.CloseAsync();
        }

        if (_context != null)
        {
            await _context.CloseAsync();
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _initLock.Dispose();
        _disposed = true;
    }
}
