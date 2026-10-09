using Moq;
using NRules;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Unit;

public class SessionFactoryProviderTests
{
    [Fact]
    public void Current_rejects_session_creation_before_first_swap()
    {
        var provider = new RuleSessionFactoryProvider();

        Assert.Throws<InvalidOperationException>(() =>
        {
            provider.Current.CreateSession();
        });
    }

    [Fact]
    public void Swap_replaces_current_factory_and_raises_event()
    {
        var provider = new RuleSessionFactoryProvider();
        var factory = new Mock<ISessionFactory>().Object;
        ISessionFactory? received = null;
        provider.FactorySwapped += (_, swappedFactory) => received = swappedFactory;

        provider.Swap(factory);

        Assert.Same(factory, provider.Current);
        Assert.Same(factory, received);
    }

    [Fact]
    public void Swap_called_twice_keeps_only_the_latest_factory()
    {
        var provider = new RuleSessionFactoryProvider();
        var firstFactory = new Mock<ISessionFactory>().Object;
        var secondFactory = new Mock<ISessionFactory>().Object;

        provider.Swap(firstFactory);
        provider.Swap(secondFactory);

        Assert.Same(secondFactory, provider.Current);
    }

    [Fact]
    public async Task WaitForReadyAsync_completes_after_first_swap()
    {
        var provider = new RuleSessionFactoryProvider();
        var factory = new Mock<ISessionFactory>().Object;
        var ready = provider.WaitForReadyAsync();

        provider.Swap(factory);

        Assert.Same(factory, await ready);
    }
}
