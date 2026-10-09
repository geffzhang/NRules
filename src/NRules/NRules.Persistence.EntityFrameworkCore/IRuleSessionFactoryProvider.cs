using NRules;

namespace NRules.Persistence.EntityFrameworkCore;

public interface IRuleSessionFactoryProvider
{
    ISessionFactory Current { get; }

    Task<ISessionFactory> WaitForReadyAsync(CancellationToken ct = default);

    event EventHandler<ISessionFactory>? FactorySwapped;

    void Swap(ISessionFactory next);
}
