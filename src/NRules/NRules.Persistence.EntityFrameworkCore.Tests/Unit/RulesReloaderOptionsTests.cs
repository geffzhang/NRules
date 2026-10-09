using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Persistence.EntityFrameworkCore.Reloading;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Unit;

public class RulesReloaderOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_non_positive_poll_interval(int seconds)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RulesReloader(
                Mock.Of<IRuleStore>(),
                new RuleSessionFactoryProvider(),
                new ReloaderOptions
                {
                    PollInterval = TimeSpan.FromSeconds(seconds),
                },
                TimeProvider.System,
                NullLoggerFactory.Instance));

        Assert.Equal("PollInterval", exception.ParamName);
        Assert.Contains("greater than zero", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
