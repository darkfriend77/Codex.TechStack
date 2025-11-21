using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using OandaHistoryFetcher.Core.Utilities;

namespace OandaHistoryFetcher.Core.Providers;

public sealed class OandaPlaywrightRateProvider : IRateProvider
{
    private const string SourceName = "scrape";
    private static readonly string[] DateInputSelectors =
    {
        "input.cc36",
        "input[aria-label='Date']",
        "input[data-testid='date-input']",
        "input[type='date']",
        "input[placeholder*='Date' i]",
        "input[name*='date' i]"
    };
    private static readonly string[] ResultSelectors =
    {
        "input[name='numberformat'][tabindex='4']",
        "[data-testid='converter-result']",
        "[data-qa='converter-result']",
        ".converter-result",
        ".converted-amount",
        "[data-testid*='result' i]"
    };

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
    private ExceptionDispatchInfo? _initializationFailure;
    private ILocator? _dateInput;
    private ILocator? _resultLocator;

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
                var resultLocator = await EnsureResultLocatorAsync(date, cancellationToken);
                var previousText = await TryReadResultTextAsync(resultLocator, cancellationToken);

                await SetDateAsync(date, resultLocator, previousText, cancellationToken);
                var convertedAmount = await ReadConvertedAmountAsync(amount, date, resultLocator, cancellationToken);
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
        if (_initializationFailure != null)
        {
            _initializationFailure.Throw();
        }

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
            _browser = await LaunchBrowserAsync(_playwright, cancellationToken);

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
            try
            {
                // Oanda keeps background connections open; DOMContentLoaded is enough for us.
                await _page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60000
                });
            }
            catch (TimeoutException ex)
            {
                _logger?.LogWarning(ex, "Initial navigation timed out. Continuing with the partially loaded page.");
            }

            try
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 15000 });
            }
            catch (TimeoutException ex)
            {
                _logger?.LogWarning(ex, "Load state wait timed out during initialization.");
            }

            await DismissConsentAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _initializationFailure = ExceptionDispatchInfo.Capture(ex);
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright, CancellationToken cancellationToken)
    {
        var launchArgs = new[]
        {
            "--no-sandbox",
            "--disable-setuid-sandbox",
            "--disable-dev-shm-usage",
            "--single-process"
        };

        if (!UseFirefoxOnly())
        {
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = _headless,
                ChromiumSandbox = false,
                Args = launchArgs
            };

            try
            {
                _logger?.LogDebug("Launching Playwright Chromium headless={Headless} sandbox={Sandbox} args={Args}", _headless, launchOptions.ChromiumSandbox, string.Join(' ', launchArgs));
                return await playwright.Chromium.LaunchAsync(launchOptions);
            }
            catch (PlaywrightException ex) when (IsSandboxFailure(ex))
            {
                _logger?.LogWarning(ex, "Chromium launch failed (likely sandbox restrictions). Falling back to Firefox.");
            }
        }
        else
        {
            _logger?.LogInformation("Skipping Chromium launch (PLAYWRIGHT_USE_FIREFOX_ONLY=true).");
        }

        _logger?.LogInformation("Launching Playwright Firefox headless={Headless}", _headless);
        try
        {
            var firefoxOptions = new BrowserTypeLaunchOptions
            {
                Headless = _headless,
                Timeout = 45000,
                Args = new[] { "-headless", "-no-remote" },
                Env = new Dictionary<string, string>
                {
                    ["MOZ_DISABLE_CONTENT_SANDBOX"] = "1",
                    ["MOZ_DISABLE_GMP_SANDBOX"] = "1",
                    ["MOZ_DISABLE_RDD_SANDBOX"] = "1",
                    ["MOZ_SANDBOX_LOGGING"] = "1",
                    ["MOZ_HEADLESS"] = "1"
                },
                FirefoxUserPrefs = new Dictionary<string, object>
                {
                    ["security.sandbox.content.level"] = 0,
                    ["security.sandbox.gpu.level"] = 0,
                    ["security.sandbox.rdd.level"] = 0,
                    ["layers.acceleration.disabled"] = true
                }
            };

            return await playwright.Firefox.LaunchAsync(firefoxOptions);
        }
        catch (PlaywrightException ex) when (IsMissingBrowserExecutable(ex))
        {
            _logger?.LogWarning(ex, "Playwright browsers not found locally. Attempting automatic install of Firefox.");
            await EnsureBrowserInstalledAsync("firefox", cancellationToken);
            return await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _headless,
                Timeout = 45000,
                Args = new[] { "-headless", "-no-remote" },
                Env = new Dictionary<string, string>
                {
                    ["MOZ_DISABLE_CONTENT_SANDBOX"] = "1",
                    ["MOZ_DISABLE_GMP_SANDBOX"] = "1",
                    ["MOZ_DISABLE_RDD_SANDBOX"] = "1",
                    ["MOZ_SANDBOX_LOGGING"] = "1",
                    ["MOZ_HEADLESS"] = "1"
                },
                FirefoxUserPrefs = new Dictionary<string, object>
                {
                    ["security.sandbox.content.level"] = 0,
                    ["security.sandbox.gpu.level"] = 0,
                    ["security.sandbox.rdd.level"] = 0,
                    ["layers.acceleration.disabled"] = true
                }
            });
        }
    }

    private static bool IsSandboxFailure(PlaywrightException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("sandbox", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMissingBrowserExecutable(PlaywrightException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnsureBrowserInstalledAsync(string browserName, CancellationToken cancellationToken)
    {
        try
        {
            var installTask = Task.Run(() => Program.Main(new[] { "install", browserName }), cancellationToken);
            var completed = await Task.WhenAny(installTask, Task.Delay(TimeSpan.FromSeconds(45), cancellationToken));
            if (completed != installTask)
            {
                throw new PlaywrightException($"Timed out installing Playwright browser '{browserName}'.");
            }

            if (await installTask != 0)
            {
                throw new PlaywrightException($"Playwright CLI exited with a non-zero status while installing '{browserName}'.");
            }
        }
        catch (Exception ex) when (ex is not PlaywrightException)
        {
            throw new PlaywrightException($"Failed to install Playwright browser '{browserName}': {ex.Message}", ex);
        }
    }

    private static bool UseFirefoxOnly()
    {
        var env = Environment.GetEnvironmentVariable("PLAYWRIGHT_USE_FIREFOX_ONLY");
        return string.Equals(env, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(env, "1", StringComparison.OrdinalIgnoreCase);
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

    private async Task SetDateAsync(DateOnly date, ILocator resultLocator, string? previousResultText, CancellationToken cancellationToken)
    {
        var formattedDate = FormatDateForOanda(date);
        var dateInput = await EnsureDateInputAsync(date, cancellationToken);

        _logger?.LogDebug("Setting date to {Date} (formatted '{Formatted}')", date, formattedDate);

        try
        {
            await dateInput.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
            await dateInput.FillAsync(formattedDate, new LocatorFillOptions { Timeout = 5000 });
            await dateInput.PressAsync("Enter", new LocatorPressOptions { Timeout = 5000 });
            await WaitForResultRefreshAsync(date, resultLocator, previousResultText, cancellationToken);
        }
        catch (PlaywrightException ex)
        {
            throw new RateFetchError(date, SourceName, $"Failed to set date on converter page: {ex.Message}", isRetryable: true, ex);
        }
    }

    private async Task<decimal> ReadConvertedAmountAsync(decimal amount, DateOnly date, ILocator resultLocator, CancellationToken cancellationToken)
    {
        try
        {
            await resultLocator.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            var rawText = await ReadLocatorTextOrValueAsync(resultLocator, cancellationToken);

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
        var amountText = amount.ToString(CultureInfo.InvariantCulture);
        return $"https://www.oanda.com/currency-converter/en/?from={Uri.EscapeDataString(fromCurrency)}&to={Uri.EscapeDataString(toCurrency)}&amount={amountText}";
    }

    private static string FormatDateForOanda(DateOnly date)
    {
        // OANDA's date input currently expects a long-form date (e.g., "01 January 2024").
        return date.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture);
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

    private async Task<ILocator> EnsureDateInputAsync(DateOnly date, CancellationToken cancellationToken)
    {
        if (_page == null)
        {
            throw new InvalidOperationException("Playwright page not initialized.");
        }

        if (_dateInput != null)
        {
            return _dateInput;
        }

        foreach (var selector in DateInputSelectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = _page.Locator(selector).First;
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions { Timeout = 4000 });
                _logger?.LogDebug("Using date input selector '{Selector}'", selector);
                _dateInput = candidate;
                return _dateInput;
            }
            catch (PlaywrightException ex)
            {
                _logger?.LogDebug(ex, "Date input selector '{Selector}' did not match in time.", selector);
            }
        }

        throw new RateFetchError(date, SourceName, "Could not locate date input on converter page.", isRetryable: true);
    }

    private async Task<ILocator> EnsureResultLocatorAsync(DateOnly date, CancellationToken cancellationToken)
    {
        if (_page == null)
        {
            throw new InvalidOperationException("Playwright page not initialized.");
        }

        if (_resultLocator != null)
        {
            return _resultLocator;
        }

        // The converter currently renders the converted amount as the second input[name="numberformat"].
        var numberFormatInputs = _page.Locator("input[name='numberformat']");
        try
        {
            var secondInput = numberFormatInputs.Nth(1);
            await secondInput.WaitForAsync(new LocatorWaitForOptions { Timeout = 8000 });
            _logger?.LogDebug("Using result selector 'input[name=\"numberformat\"] (index 1)'");
            _resultLocator = secondInput;
            return _resultLocator;
        }
        catch (PlaywrightException ex)
        {
            _logger?.LogDebug(ex, "Result selector input[name='numberformat'] (index 1) did not match in time.");
        }

        foreach (var selector in ResultSelectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = _page.Locator(selector).First;
            try
            {
                await candidate.WaitForAsync(new LocatorWaitForOptions { Timeout = 8000 });
                _logger?.LogDebug("Using result selector '{Selector}'", selector);
                _resultLocator = candidate;
                return _resultLocator;
            }
            catch (PlaywrightException ex)
            {
                _logger?.LogDebug(ex, "Result selector '{Selector}' did not match in time.", selector);
            }
        }

        throw new RateFetchError(date, SourceName, "Could not locate converter result on the page.", isRetryable: true);
    }

    private async Task<string?> TryReadResultTextAsync(ILocator locator, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ReadLocatorTextOrValueAsync(locator, cancellationToken);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _logger?.LogDebug(ex, "Result text not readable yet.");
            return null;
        }
    }

    private async Task WaitForResultRefreshAsync(DateOnly date, ILocator resultLocator, string? previousText, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow - start < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await TryReadResultTextAsync(resultLocator, cancellationToken);
            if (!string.IsNullOrWhiteSpace(current) &&
                (previousText == null || !string.Equals(previousText.Trim(), current.Trim(), StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(300, cancellationToken);
        }

        throw new RateFetchError(date, SourceName, "Rate did not refresh after changing the date.", isRetryable: true);
    }

    private static async Task<string> ReadLocatorTextOrValueAsync(ILocator locator, CancellationToken cancellationToken)
    {
        // Inputs don't expose InnerText; fall back to value when applicable.
        var element = await locator.ElementHandleAsync(new LocatorElementHandleOptions { Timeout = 4000 });
        if (element == null)
        {
            throw new InvalidOperationException("Locator no longer resolves to an element.");
        }

        var tagName = await element.EvaluateAsync<string>("el => el.tagName");
        if (string.Equals(tagName, "input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tagName, "textarea", StringComparison.OrdinalIgnoreCase))
        {
            var value = await locator.InputValueAsync(new LocatorInputValueOptions { Timeout = 4000 });
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return await locator.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 4000 });
    }

    private async Task DismissConsentAsync(CancellationToken cancellationToken)
    {
        if (_page == null)
        {
            return;
        }

        var selectors = new[]
        {
            "button:has-text('Accept All')",
            "button:has-text('Accept all')",
            "button:has-text('Accept')",
            "#onetrust-accept-btn-handler"
        };

        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var button = _page.Locator(selector).First;
                if (await button.IsVisibleAsync())
                {
                    _logger?.LogDebug("Clicking consent button selector '{Selector}'", selector);
                    await button.ClickAsync(new LocatorClickOptions { Timeout = 4000 });
                    await Task.Delay(500, cancellationToken);
                    return;
                }
            }
            catch (PlaywrightException ex)
            {
                _logger?.LogDebug(ex, "Consent selector '{Selector}' not clickable.", selector);
            }
        }
    }
}
