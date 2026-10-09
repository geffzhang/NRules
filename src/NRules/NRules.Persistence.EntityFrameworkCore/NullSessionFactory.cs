using NRules;
using NRules.Diagnostics;
using NRules.Extensibility;
using NRules.Rete;

namespace NRules.Persistence.EntityFrameworkCore;

internal sealed class NullSessionFactory : ISessionFactory
{
    public static readonly NullSessionFactory Instance = new();

    private NullSessionFactory()
    {
    }

    private static InvalidOperationException NotReady() =>
        new("Rules have not been loaded yet. Wait for IRuleSessionFactoryProvider.WaitForReadyAsync().");

    public IEventProvider Events => throw NotReady();

    public IDependencyResolver DependencyResolver
    {
        get => throw NotReady();
        set => throw NotReady();
    }

    public IActionInterceptor? ActionInterceptor
    {
        get => throw NotReady();
        set => throw NotReady();
    }

    public ISession CreateSession() => throw NotReady();

    public ISession CreateSession(Action<ISession> initializationAction) => throw NotReady();

    public ReteGraph GetSchema() => throw NotReady();
}
