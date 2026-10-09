### Task 6：`RulesReloader` —— 轮询 + Channel 信号 + 消费循环

**Files:**
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/Reloading/ReloadReason.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/Reloading/RulesReloader.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/Options/ReloaderOptions.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/RulesReloaderRunner.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/ReloaderPollingTests.cs`

**Interfaces:**
- Consumes:
  - Task 4 的 `IRuleStore`、`RulesFingerprint`
  - Task 5 的 `RuleSessionFactoryProvider`
- Produces:
  - 类型 `RulesReloaderRunner.RunOnceAsync(IRuleStore, IRuleSessionFactoryProvider, CancellationToken)`
  - 类型 `RulesReloader : BackgroundService`（不连 NOTIFY，仅轮询；NOTIFY 在 Task 7 接入）
  - 类型 `ReloadReason` (enum: `Bootstrap`, `Poll`, `Notify`)
  - 类型 `ReloaderOptions` 含 `PollInterval`、`PollJitter`

- [ ] **Step 1: 写失败的测试：内置 Runner 编译并切换**

`Integration/ReloaderRunnerTests.cs`：

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class ReloaderRunnerTests
{
    private readonly PostgresFixture _pg;

    public ReloaderRunnerTests(PostgresFixture pg) => _pg = pg;

    private static IRuleDefinition MakeRule(string name) => new RuleBuilder()
        .Name(name)
        .LeftHandSide().Pattern(typeof(object), "o").Condition(o => true)
        .RightHandSide().Action(ctx => { })
        .Build();

    [Fact]
    public async Task RunOnceAsync_compiles_rules_and_swaps_factory()
    {
        await _pg.ResetAsync();
        var store = await MakeStoreAsync();
        var provider = new RuleSessionFactoryProvider();

        await store.SaveRuleAsync(MakeRule("r1"));
        await store.SaveRuleAsync(MakeRule("r2"));

        var factory = await RulesReloaderRunner.RunOnceAsync(store, provider, default);

        Assert.NotNull(factory);
        Assert.Same(factory, provider.Current);
    }

    private async Task<IRuleStore> MakeStoreAsync()
    {
        var factory = new TestDbContextFactory(_pg.ConnectionString);
        var opts = new NRules.Persistence.EntityFrameworkCore.Options.EFCoreStoreOptions
        {
            TypeResolver = new NRules.Json.TypeResolver(),
        };
        opts.TypeResolver.RegisterDefaultAliases();
        var serializer = RuleJsonConverterFactory.Create(opts);
        return new EFCoreRuleStore(factory, serializer, NullLogger<EFCoreRuleStore>.Instance);
    }
}
```

把 `TestDbContextFactory` 从 Task 4 那处移到公用文件 `TestDbContextFactory.cs`。

- [ ] **Step 2: 跑测试，期望红**

```bash
dotnet test ... --filter "FullyQualifiedName~ReloaderRunnerTests"
```

- [ ] **Step 3: 实现 `RulesReloaderRunner`**

`RulesReloaderRunner.cs`：

```csharp
using NRules;
using NRules.RuleModel;

namespace NRules.Persistence.EntityFrameworkCore;

public static class RulesReloaderRunner
{
    public static async Task<ISessionFactory> RunOnceAsync(
        IRuleStore store,
        IRuleSessionFactoryProvider provider,
        CancellationToken ct)
    {
        var rules = await store.LoadAllAsync(ct);
        var factory = new RuleCompiler().Compile(rules);
        provider.Swap(factory);
        return factory;
    }
}
```

- [ ] **Step 4: 跑测试，期望绿**

```bash
dotnet test ... --filter "FullyQualifiedName~ReloaderRunnerTests"
```

期望：`Passed: 1`

- [ ] **Step 5: 写轮询重载失败的测试**

`Integration/ReloaderPollingTests.cs`：

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NRules;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Persistence.EntityFrameworkCore.Reloading;
using NRules.RuleModel;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class ReloaderPollingTests
{
    private readonly PostgresFixture _pg;

    public ReloaderPollingTests(PostgresFixture pg) => _pg = pg;

    private async Task<IRuleStore> MakeStoreAsync() { /* 同 Runner 测试 */ }

    private static IRuleDefinition MakeRule(string name) => new RuleBuilder()
        .Name(name)
        .LeftHandSide().Pattern(typeof(object), "o").Condition(o => true)
        .RightHandSide().Action(ctx => { })
        .Build();

    [Fact]
    public async Task Polling_picks_up_insert_after_interval()
    {
        await _pg.ResetAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = await MakeStoreAsync();
        var provider = new RuleSessionFactoryProvider();
        var reloader = new RulesReloader(
            store,
            provider,
            Options.Create(new ReloaderOptions
            {
                PollInterval = TimeSpan.FromSeconds(10),
                PollJitter  = TimeSpan.Zero,
            }).Value,
            time,
            NullLoggerFactory.Instance);

        var cts = new CancellationTokenSource();
        await store.SaveRuleAsync(MakeRule("before-start"));
        var swaps = Channel.CreateUnbounded<ISessionFactory>();
        provider.FactorySwapped += (_, factory) => swaps.Writer.TryWrite(factory);

        await reloader.StartAsync(cts.Token);
        var initial = await swaps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(initial);

        await store.SaveRuleAsync(MakeRule("after-start"));
        time.Advance(TimeSpan.FromSeconds(10));
        var reloaded = await swaps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(initial, reloaded);

        await reloader.StopAsync(CancellationToken.None);
    }
}
```

- [ ] **Step 6: 跑轮询测试，期望红（找不到 ReloadReason/RulesReloader）**

- [ ] **Step 7: 实现 enum**

`Reloading/ReloadReason.cs`：

```csharp
namespace NRules.Persistence.EntityFrameworkCore.Reloading;

public enum ReloadReason { Bootstrap, Poll, Notify }
```

- [ ] **Step 8: 实现 `ReloaderOptions`**

`Options/ReloaderOptions.cs`：

```csharp
namespace NRules.Persistence.EntityFrameworkCore.Options;

public class ReloaderOptions
{
    public TimeSpan? PollInterval { get; set; }
    public TimeSpan? PollJitter { get; set; }
    public bool UseNotify { get; set; }
}
```

- [ ] **Step 9: 实现 `RulesReloader`（polling only 此处）**

`Reloading/RulesReloader.cs`：

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NRules.Persistence.EntityFrameworkCore.Options;

namespace NRules.Persistence.EntityFrameworkCore.Reloading;

public class RulesReloader : BackgroundService
{
    private readonly IRuleStore _store;
    private readonly IRuleSessionFactoryProvider _provider;
    private readonly ReloaderOptions _options;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _time;
    private readonly ILogger<RulesReloader> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Channel<ReloadReason> _signals;

    private RulesFingerprint _lastFingerprint;

    public RulesReloader(
        IRuleStore store,
        IRuleSessionFactoryProvider provider,
        ReloaderOptions options,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        _store    = store;
        _provider = provider;
        _options  = options;
        var interval = options.PollInterval ?? TimeSpan.FromSeconds(30);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options.PollInterval),
                options.PollInterval,
                "PollInterval must be greater than zero.");
        _pollInterval = interval;
        _time     = time;
        _loggerFactory = loggerFactory;
        _logger   = loggerFactory.CreateLogger<RulesReloader>();

        _signals = Channel.CreateBounded<ReloadReason>(new BoundedChannelOptions(1)
        {
            FullMode      = BoundedChannelFullMode.DropOldest,
            SingleReader  = true,
            SingleWriter  = false,
        });
    }

    internal void Signal(ReloadReason reason) =>
        _signals.Writer.TryWrite(reason);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _signals.Writer.TryWrite(ReloadReason.Bootstrap);

        var pollTask = RunPollingLoopAsync(stoppingToken);
        var consumeTask = RunConsumeLoopAsync(stoppingToken);

        await Task.WhenAll(pollTask, consumeTask);
    }

    private async Task RunPollingLoopAsync(CancellationToken stop)
    {
        var jitter   = _options.PollJitter   ?? TimeSpan.FromSeconds(3);

        while (!stop.IsCancellationRequested)
        {
            await _time.Delay(_pollInterval + RandomJitter(jitter), stop);
            _signals.Writer.TryWrite(ReloadReason.Poll);
        }
    }

    private async Task RunConsumeLoopAsync(CancellationToken stop)
    {
        await foreach (var reason in _signals.Reader.ReadAllAsync(stop))
        {
            try
            {
                var fp = await _store.ComputeFingerprintAsync(stop);
                if (reason != ReloadReason.Bootstrap && fp == _lastFingerprint) continue;
                var rules = await _store.LoadAllAsync(stop);
                var factory = new NRules.RuleCompiler().Compile(rules);
                _provider.Swap(factory);
                _lastFingerprint = fp;
                _logger.LogInformation(
                    "Rules rebuilt; count={Count} latest={Latest}",
                    fp.Count, fp.LatestVersion);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rules rebuild failed; keeping previous factory");
            }
        }
    }

    private static TimeSpan RandomJitter(TimeSpan max)
    {
        if (max <= TimeSpan.Zero) return TimeSpan.Zero;
        var ms = Random.Shared.NextDouble() * max.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(ms);
    }
}
```

`PollInterval` is strictly positive when specified; the constructor rejects zero or negative values before starting either background loop. A missing value keeps the 30-second default. `PollJitter` defaults to 3 seconds; zero or negative values intentionally disable jitter.

- [ ] **Step 10: 跑测试，期望绿**

```bash
dotnet test ... --filter "FullyQualifiedName~ReloaderPollingTests"
```

期望：`Passed: 1`

- [ ] **Step 11: 提交**

```bash
git add src/NRules/NRules.Persistence.EntityFrameworkCore/Reloading/ \
        src/NRules/NRules.Persistence.EntityFrameworkCore/RulesReloaderRunner.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore/Options/ReloaderOptions.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/ReloaderRunnerTests.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/ReloaderPollingTests.cs
git commit -m "feat: RulesReloader polling loop and runner with fingerprint-based skip"
```

---
