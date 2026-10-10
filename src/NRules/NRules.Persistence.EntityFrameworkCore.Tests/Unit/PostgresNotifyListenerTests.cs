using Microsoft.Extensions.Logging.Abstractions;
using NRules.Persistence.EntityFrameworkCore.Reloading;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Unit;

public class PostgresNotifyListenerTests
{
    [Fact]
    public async Task Initial_retry_delay_is_capped_at_thirty_seconds()
    {
        var time = new RecordingTimeProvider();
        var listener = new PostgresNotifyListener(
            "Host=127.0.0.1;Port=1;Database=postgres;Username=postgres;Password=postgres;Timeout=1",
            () => { },
            time,
            TimeSpan.FromHours(1),
            NullLogger<PostgresNotifyListener>.Instance);

        await listener.StartAsync(CancellationToken.None);
        try
        {
            var firstDelay = await time.FirstTimer.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromSeconds(30), firstDelay);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly TimeProvider _system = TimeProvider.System;

        public TaskCompletionSource<TimeSpan> FirstTimer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => _system.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => _system.LocalTimeZone;

        public override long TimestampFrequency => _system.TimestampFrequency;

        public override long GetTimestamp() => _system.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            FirstTimer.TrySetResult(dueTime);
            return _system.CreateTimer(callback, state, dueTime, period);
        }
    }
}
