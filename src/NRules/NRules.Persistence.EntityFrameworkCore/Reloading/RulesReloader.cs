using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NRules.Persistence.EntityFrameworkCore.Options;

namespace NRules.Persistence.EntityFrameworkCore.Reloading;

public class RulesReloader : BackgroundService
{
    private readonly IRuleStore _store;
    private readonly IRuleSessionFactoryProvider _provider;
    private readonly ReloaderOptions _options;
    private readonly PostgresNotifyOptions _notifyOptions;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _time;
    private readonly ILogger<RulesReloader> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Channel<ReloadReason> _signals;
    private RulesFingerprint _lastFingerprint;
    private Task _notifyReady = Task.CompletedTask;

    public RulesReloader(
        IRuleStore store,
        IRuleSessionFactoryProvider provider,
        ReloaderOptions options,
        TimeProvider time,
        ILoggerFactory loggerFactory)
        : this(store, provider, options, time, loggerFactory, new PostgresNotifyOptions())
    {
    }

    public RulesReloader(
        IRuleStore store,
        IRuleSessionFactoryProvider provider,
        ReloaderOptions options,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        PostgresNotifyOptions notifyOptions)
    {
        _store = store;
        _provider = provider;
        _options = options;
        _notifyOptions = notifyOptions;
        _pollInterval = options.PollInterval ?? TimeSpan.FromSeconds(30);
        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.PollInterval),
                options.PollInterval,
                "PollInterval must be greater than zero.");
        }

        _time = time;
        _logger = loggerFactory.CreateLogger<RulesReloader>();
        _loggerFactory = loggerFactory;

        _signals = Channel.CreateBounded<ReloadReason>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    internal void Signal(ReloadReason reason) =>
        _signals.Writer.TryWrite(reason);

    public Task WaitForNotifyReadyAsync(CancellationToken ct = default) =>
        _notifyReady.WaitAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _signals.Writer.TryWrite(ReloadReason.Bootstrap);

        PostgresNotifyListener? listener = null;
        if (_options.UseNotify)
        {
            var connectionString = _notifyOptions.ConnectionString
                ?? throw new InvalidOperationException(
                    "UsePostgresNotify requires PostgresNotifyOptions.ConnectionString.");
            listener = new PostgresNotifyListener(
                connectionString,
                onNotify: () => _signals.Writer.TryWrite(ReloadReason.Notify),
                _time,
                _notifyOptions.ReconnectBackoff,
                _loggerFactory.CreateLogger<PostgresNotifyListener>());
            await listener.StartAsync(stoppingToken);
            _notifyReady = listener.WaitUntilListeningAsync(stoppingToken);
        }

        try
        {
            var pollTask = RunPollingLoopAsync(stoppingToken);
            var consumeTask = RunConsumeLoopAsync(stoppingToken);
            await Task.WhenAll(pollTask, consumeTask);
        }
        finally
        {
            if (listener is not null)
                await listener.StopAsync(CancellationToken.None);
        }
    }

    private async Task RunPollingLoopAsync(CancellationToken stop)
    {
        var jitter = _options.PollJitter ?? TimeSpan.FromSeconds(3);

        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(_pollInterval + RandomJitter(jitter), _time, stop);
            _signals.Writer.TryWrite(ReloadReason.Poll);
        }
    }

    private async Task RunConsumeLoopAsync(CancellationToken stop)
    {
        await foreach (var reason in _signals.Reader.ReadAllAsync(stop))
        {
            try
            {
                var fingerprint = await _store.ComputeFingerprintAsync(stop);
                if (reason != ReloadReason.Bootstrap && fingerprint == _lastFingerprint)
                    continue;

                var rules = await _store.LoadAllAsync(stop);
                var factory = new NRules.RuleCompiler().Compile(rules);
                try
                {
                    _provider.Swap(factory);
                }
                catch (Exception exception) when (ReferenceEquals(_provider.Current, factory))
                {
                    _logger.LogError(
                        exception,
                        "FactorySwapped event handler failed after factory publication; treating reload as successful");
                }

                _lastFingerprint = fingerprint;
                _logger.LogInformation(
                    "Rules rebuilt; count={Count} latest={Latest}",
                    fingerprint.Count,
                    fingerprint.LatestVersion);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Rules rebuild failed; keeping previous factory");
            }
        }
    }

    private static TimeSpan RandomJitter(TimeSpan max)
    {
        if (max <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var milliseconds = Random.Shared.NextDouble() * max.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
