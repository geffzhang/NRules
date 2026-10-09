namespace NRules.Persistence.EntityFrameworkCore.Options;

public class PostgresNotifyOptions
{
    public string? ConnectionString { get; set; }

    public TimeSpan ReconnectBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
