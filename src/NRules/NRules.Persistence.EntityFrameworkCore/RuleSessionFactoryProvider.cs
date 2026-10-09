using NRules;

namespace NRules.Persistence.EntityFrameworkCore;

public sealed class RuleSessionFactoryProvider : IRuleSessionFactoryProvider
{
    private ISessionFactory _current = NullSessionFactory.Instance;
    private readonly TaskCompletionSource<ISessionFactory> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ISessionFactory Current => Volatile.Read(ref _current);

    public event EventHandler<ISessionFactory>? FactorySwapped;

    public Task<ISessionFactory> WaitForReadyAsync(CancellationToken ct = default) =>
        _ready.Task.WaitAsync(ct);

    public void Swap(ISessionFactory next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Interlocked.Exchange(ref _current, next);
        _ready.TrySetResult(next);
        FactorySwapped?.Invoke(this, next);
    }
}
