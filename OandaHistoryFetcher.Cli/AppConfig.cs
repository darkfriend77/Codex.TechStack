namespace OandaHistoryFetcher.Cli;

/// <summary>
/// Configuration for the application.
/// </summary>
public class AppConfig
{
    public required string FromCurrency { get; init; }
    public required string ToCurrency { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public decimal Amount { get; init; } = 1.0m;
    public string OutputPath { get; init; } = "./rates.csv";
    public string Mode { get; init; } = "auto";
    public string? OandaApiKey { get; init; }
    public int DelayMs { get; init; } = 1000;
    public int MaxRetries { get; init; } = 3;
    public string LogLevel { get; init; } = "info";
    public bool AcknowledgeScraping { get; init; }

    public TimeSpan Delay => TimeSpan.FromMilliseconds(DelayMs);
    
    public bool HasApiKey => !string.IsNullOrWhiteSpace(OandaApiKey);

    public string ActiveMode => Mode.ToLowerInvariant() switch
    {
        "api" => "api",
        "scrape" => "scrape",
        "auto" => HasApiKey ? "api" : "scrape",
        _ => throw new ArgumentException($"Invalid mode: {Mode}")
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FromCurrency))
            throw new ArgumentException("From currency is required");
        
        if (string.IsNullOrWhiteSpace(ToCurrency))
            throw new ArgumentException("To currency is required");
        
        if (StartDate > EndDate)
            throw new ArgumentException("Start date must be before or equal to end date");
        
        if (Amount <= 0)
            throw new ArgumentException("Amount must be greater than 0");
        
        if (DelayMs < 0)
            throw new ArgumentException("Delay must be non-negative");
        
        if (MaxRetries < 0)
            throw new ArgumentException("Max retries must be non-negative");

        if (Mode.ToLowerInvariant() == "api" && !HasApiKey)
            throw new ArgumentException("API mode requires an API key");

        if (ActiveMode == "scrape" && !AcknowledgeScraping)
        {
            Console.WriteLine();
            Console.WriteLine("WARNING: Scraping mode is being used.");
            Console.WriteLine("Before using this in production, please review OANDA's Terms of Use and robots.txt");
            Console.WriteLine("to ensure compliance. If scraping is not allowed, use API mode instead.");
            Console.WriteLine();
            Console.WriteLine("To acknowledge this warning, add the --acknowledge-scraping flag.");
            Console.WriteLine();
            throw new ArgumentException("Scraping mode requires acknowledgment via --acknowledge-scraping flag");
        }
    }
}
