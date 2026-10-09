using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace NRules.Persistence.EntityFrameworkCore.Reloading;

public sealed class PostgresNotifyListener : BackgroundService
{
    private readonly string _connectionString;
    private readonly string _channel;
    private readonly Action _onNotify;
    private readonly TimeProvider _time;
    private readonly ILogger<PostgresNotifyListener> _logger;
    private readonly TimeSpan _initialBackoff;
    private readonly TaskCompletionSource _listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task WaitUntilListeningAsync(CancellationToken ct = default) =>
        _listening.Task.WaitAsync(ct);

    public PostgresNotifyListener(
        string connectionString,
        Action onNotify,
        TimeProvider time,
        TimeSpan initialBackoff,
        ILogger<PostgresNotifyListener> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(onNotify);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialBackoff, TimeSpan.Zero);

        _connectionString = connectionString;
        _channel = "rules_changed";
        _onNotify = onNotify;
        _time = time;
        _initialBackoff = initialBackoff;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = _initialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                conn.Notification += (_, _) =>
                {
                    _logger.LogDebug("Received notify on {Channel}", _channel);
                    _onNotify();
                };
                await conn.OpenAsync(stoppingToken);

                await using (var cmd = new NpgsqlCommand($"LISTEN {_channel}", conn))
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                backoff = _initialBackoff;
                _listening.TrySetResult();

                while (!stoppingToken.IsCancellationRequested)
                    await conn.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Listen loop disconnected; reconnecting in {Backoff}",
                    backoff);
                await Task.Delay(backoff, _time, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }
}
