namespace NRules.Persistence.EntityFrameworkCore.Options;

public class ReloaderOptions
{
    public TimeSpan? PollInterval { get; set; }

    public TimeSpan? PollJitter { get; set; }

    public bool UseNotify { get; set; }
}
