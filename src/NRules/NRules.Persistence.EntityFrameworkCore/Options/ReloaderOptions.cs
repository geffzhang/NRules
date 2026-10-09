namespace NRules.Persistence.EntityFrameworkCore.Options;

public class ReloaderOptions
{
    /// <summary>
    /// Gets or sets the polling interval. Null selects the 30-second default; a configured value must be positive.
    /// </summary>
    public TimeSpan? PollInterval { get; set; }

    /// <summary>
    /// Gets or sets the maximum random delay added to each polling interval. Null selects 3 seconds; zero or negative values disable jitter.
    /// </summary>
    public TimeSpan? PollJitter { get; set; }

    public bool UseNotify { get; set; }
}
