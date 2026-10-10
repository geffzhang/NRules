# NRules PostgreSQL 持久化与热重载 —— 设计 spec

| 项目 | 值 |
| --- | --- |
| Spec 作者 | Claude |
| 日期 | 2026-10-09 |
| 仓库 | NRules (`develop` 分支) |
| 状态 | 待用户审阅 |
| 配套实现计划 | 由 `writing-plans` 技能产出 |

## 背景与目标

NRules 当前以 `RuleRepository` 扫描程序集发现规则、通过 `RuleCompiler.Compile(IRuleDefinition[])` 编译出 `ISessionFactory`。所有规则定义要么写在 C# 代码中（通过 Fluent DSL），要么通过 `samples/JsonRules` 演示的 `NRules.Json` 路径序列化为 JSON。但**没有**：
- 任何形式的"落库后再加载"官方机制
- 运行时热重载支持（新增/修改/删除规则需要进程重启）

运营/产品侧反复提出"业务规则改一次要发版"是 NRules 在企业场景下的最大劝退点。本 spec 解决两件事：
1. 把 `IRuleDefinition` 持久化到 PostgreSQL
2. 在应用运行中自动检测规则集变化并热重建 `ISessionFactory`，**不需要重启**

非目标（v1 不做）：
- 多租户隔离
- 规则版本历史 / 回滚（可以靠 `RuleVersion` 自增字段读出当前快照，但不做时间线查询）
- 规则编辑 UI / HTTP 管理接口（由调用方自行实现）
- 其他数据库（SQL Server / SQLite / MySQL）。v1 只支持 PostgreSQL；通过 EF Core 抽象层保留后续扩展的口子

## 关键决策一览

| 决策 | 选项 | 选定 |
| --- | --- | --- |
| 工程布局 | 库 / 样例 / 两者皆有 | **库 + 样例 + 集成测试** |
| 热重载触发 | 轮询 / LISTEN/NOTIFY / 两者 | **轮询默认 + LISTEN/NOTIFY 可选** |
| 并发令牌 | xmin / RowVersion / 无 | **Postgres `xmin`** |
| API 形态 | DI / 静态门面 / 两者 | **DI 扩展方法为主 + 低层静态门面** |

## 架构总览

### 新增工程

```
src/NRules/
├── NRules.Persistence.EntityFrameworkCore/
│   ├── NRules.Persistence.EntityFrameworkCore.csproj   （net10.0）
│   ├── RulesDbContext.cs
│   ├── Entities/RuleEntity.cs
│   ├── EFCoreRuleStore.cs
│   ├── IRuleStore.cs / IRuleSessionFactoryProvider.cs
│   ├── RuleSessionFactoryProvider.cs
│   ├── Serialization/RuleJsonConverterFactory.cs
│   ├── Reloading/
│   │   ├── RulesReloader.cs                （IHostedService）
│   │   ├── PostgresNotifyListener.cs
│   │   ├── RulesFingerprint.cs
│   │   └── ReloadReason.cs
│   ├── DependencyInjection/ServiceCollectionExtensions.cs
│   ├── Migrations/<timestamp>_Init.cs      （含 LISTEN/NOTIFY 触发器）
│   └── Options/{EFCoreStoreOptions,ReloaderOptions,PostgresNotifyOptions}.cs
└── Tests/
    └── NRules.Persistence.EntityFrameworkCore.Tests/
        ├── NRules.Persistence.EntityFrameworkCore.Tests.csproj
        ├── PostgresFixture.cs
        ├── Unit/{EFCoreRuleStoreTests,RulesFingerprintTests,RuleJsonConverterFactoryTests}.cs
        └── Integration/{ReloaderPollingTests,ReloaderNotifyTests,RuleStoreConcurrencyTests}.cs

samples/
└── Persistence.Postgres/
    ├── Persistence.Postgres.csproj
    ├── Persistence.Postgres.sln
    ├── Domain/{Customer,Order}.cs
    ├── RulesSeed.cs                               （Host.StartAsync 前迁移/灌入示例规则）
    ├── Program.cs
    ├── appsettings.json
    └── README.md                               （docker run + dotnet run 一条龙）
```

全部新工程加入 `src/NRules/NRules.sln`，库工程加入 `build.ps1` 的 `Build Core` 默认目标，样例加入 `Build Samples` 目标。

### 运行时结构

```
业务代码
  ↓ provider.Current
IRuleSessionFactoryProvider（单例，volatile ISessionFactory）
  ↑ Swap()
RulesReloader : BackgroundService
  ├── PollingLoop ─┐
  └── NotifyLoop ──┴─→ Channel<ReloadReason>(Bounded=1, DropOldest, SingleReader)
                          ↓ consume
                    RulesReloaderRunner.RunOnceAsync()
                          ↓
                    EFCoreRuleStore.LoadAllAsync()
                          ↓
                    RuleCompiler.Compile()
                          ↓
                    ISessionFactory (next)
                          ↓
                    Interlocked.Exchange → Current

PostgreSQL
  ├── rules 表（jsonb + xmin + rule_version）
  └── rules_changed_trg → pg_notify('rules_changed', name)
```

边界清晰：
- `IRuleStore` —— 纯 CRUD，不知道热重载存在。可被 CLI 工具、迁移脚本、单元测试独立使用
- `IRuleSessionFactoryProvider` —— 消费者入口，不知道热重载触发源
- `RulesReloader` —— 后台编排，同时消费轮询和 NOTIFY 两个信号源

## 数据库 Schema

由 EF Core 的 init 迁移生成：

```sql
CREATE TABLE rules (
    name              varchar(200) PRIMARY KEY,
    description       text         NULL,
    priority          integer      NOT NULL DEFAULT 0,
    repeatability     smallint     NOT NULL DEFAULT 0,
    tags              text[]       NOT NULL DEFAULT '{}',
    properties        jsonb        NOT NULL DEFAULT '{}'::jsonb,
    rule_definition   jsonb        NOT NULL,
    rule_version      integer      NOT NULL DEFAULT 1,
    created_at        timestamptz  NOT NULL DEFAULT now(),
    updated_at        timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT "CK_rules_rule_definition_name_matches_name" CHECK (
        jsonb_typeof(rule_definition) = 'object'
        AND
        (rule_definition ? 'name')
        AND jsonb_typeof(rule_definition -> 'name') = 'string'
        AND rule_definition ->> 'name' = name
    )
);
-- xmin 是 Postgres 系统列，不可见于表定义但可作为并发令牌

CREATE OR REPLACE FUNCTION rules_set_version() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        NEW.rule_version := 1;
    ELSE
        NEW.rule_version := OLD.rule_version + 1;
        NEW.created_at := OLD.created_at;
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER rules_set_version_trg
BEFORE INSERT OR UPDATE ON rules
FOR EACH ROW EXECUTE FUNCTION rules_set_version();

CREATE OR REPLACE FUNCTION rules_notify() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        PERFORM pg_notify('rules_changed', OLD.name);
        RETURN OLD;
    ELSE
        PERFORM pg_notify('rules_changed', NEW.name);
        RETURN NEW;
    END IF;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER rules_changed_trg
AFTER INSERT OR UPDATE OR DELETE ON rules
FOR EACH ROW EXECUTE FUNCTION rules_notify();
```

### 关键字段说明

| 列 | 用途 |
| --- | --- |
| `name` | 自然键，等于 `IRuleDefinition.Name`。Fluent DSL 仓库里这是规则唯一身份；不引入代理键 |
| `rule_definition` (jsonb) | 整个 `IRuleDefinition` 用 `NRules.Json` 序列化后的字节 |
| `properties` (jsonb) | 同上，单列出来便于查询 |
| `tags` (text[]) | Postgres 原生数组，方便后续按 tag 过滤 |
| `rule_version` | 由数据库 BEFORE trigger 在每次 INSERT/UPDATE 时设置/递增，用作热重载指纹；涵盖 EF Core 与直接 SQL 写入 |
| `xmin` | Postgres 系统列，EF Core 配作 `IsConcurrencyToken()` |
| `created_at` / `updated_at` | 时间戳；由数据库默认值与 trigger 维护 |

`rule_definition` 是规则加载与编译的权威来源；`name` 必须与其中的 `IRuleDefinition.Name` 相同。`priority`、`repeatability`、`tags` 和 `properties` 是便于查询的投影列。应用存储 API 会从同一个规则定义更新这些列；直接 SQL 修改规则定义时，必须在同一事务中同步更新相关投影列。v1 不通过触发器解析任意规则 JSON 来维护投影。

### EF Core 关键映射

```csharp
modelBuilder.Entity<RuleEntity>(b =>
{
    b.ToTable("rules", table =>
        table.HasCheckConstraint(
            "CK_rules_rule_definition_name_matches_name",
            "jsonb_typeof(rule_definition) = 'object' AND (rule_definition ? 'name') AND jsonb_typeof(rule_definition -> 'name') = 'string' AND rule_definition ->> 'name' = name"));
    b.HasKey(r => r.Name);
    b.Property(r => r.Name).HasColumnName("name").HasColumnType("varchar(200)");
    b.Property(r => r.Description).HasColumnName("description");
    b.Property(r => r.Priority).HasColumnName("priority");
    b.Property(r => r.Repeatability).HasColumnName("repeatability");

    b.Property(r => r.Properties)
        .HasColumnName("properties")
        .HasColumnType("jsonb")
        .HasConversion(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonDocument.Parse(v, default));

    b.Property(r => r.RuleDefinition)
        .HasColumnName("rule_definition")
        .HasColumnType("jsonb")
        .HasConversion(
            v => JsonSerializer.Serialize(v, _serializerOptions),
            v => JsonDocument.Parse(v, default));

    b.Property(r => r.Tags)
        .HasColumnName("tags")
        .HasColumnType("text[]");

    b.Property(r => r.RuleVersion)
        .HasColumnName("rule_version")
        .ValueGeneratedOnAddOrUpdate();
    b.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
    b.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

    // xmin 并发令牌
    b.Property<uint>("xmin")
        .HasColumnName("xmin")
        .HasColumnType("xid")
        .ValueGeneratedOnAddOrUpdate()
        .IsConcurrencyToken();

    // updated_at 由数据库 trigger 刷新
});
```

列名通过 `HasColumnName` 显式映射为小写 snake_case，与迁移 SQL、触发器及直接 SQL 写入保持一致；不依赖额外命名约定包。

### 数据库触发器维护的列

- `rule_version`：INSERT 时设为 1；UPDATE 时在数据库内按旧值 +1。这样直接 SQL 编辑也会改变轮询指纹。
- `updated_at`：每次 INSERT/UPDATE 由同一 BEFORE trigger 写入数据库当前时间；应用不再维护这两列，避免重复递增或绕过拦截器。
- `rules_changed` 通知：AFTER trigger 对 INSERT/UPDATE/DELETE 发出；DELETE 使用 `OLD.name`。通知 channel 在 v1 固定为 `rules_changed`，必须由迁移创建对应触发器。

## 公共 API

### 核心接口（Core，不依赖 DI）

```csharp
public interface IRuleStore
{
    Task SaveRuleAsync(IRuleDefinition rule, CancellationToken ct = default);
    Task DeleteRuleAsync(string name, CancellationToken ct = default);
    Task<IRuleDefinition?> LoadAsync(string name, CancellationToken ct = default);
    /// <summary>整批加载；任一行无法反序列化或与键不匹配时整体失败，不返回部分规则集。</summary>
    Task<IReadOnlyList<IRuleDefinition>> LoadAllAsync(CancellationToken ct = default);
    /// <summary>返回 (聚合版本指纹, 规则数, 最高 rule_version)。轮询路径用它快速判断变更</summary>
    Task<RulesFingerprint> ComputeFingerprintAsync(CancellationToken ct = default);
}

public interface IRuleSessionFactoryProvider
{
    /// <summary>永远非 null；冷启动期间由内部空工厂拒绝创建 session</summary>
    ISessionFactory Current { get; }
    /// <summary>阻塞直到首加载完成</summary>
    Task<ISessionFactory> WaitForReadyAsync(CancellationToken ct = default);
    event EventHandler<ISessionFactory>? FactorySwapped;
    /// <summary>原子发布已成功编译的新工厂。</summary>
    void Swap(ISessionFactory next);
}

public readonly record struct RulesFingerprint(long Hash, int Count, int LatestVersion);
```

`Hash` 必须跨进程稳定：按 `name` 升序遍历 `(name, rule_version)`，对每行依次输入 4 字节大端名称字节长度、UTF-8 名称字节和 4 字节大端版本号，计算 SHA-256，并将摘要前 8 字节按大端有符号 `Int64` 解释为 `Hash`。`Count` 与 `LatestVersion` 单独返回。这样同一规则集可在不同进程中复用相同缓存键。

### DI 扩展（主路径）

```csharp
// 用法
services.AddNRulesEfCoreStore(
    o =>
    {
        o.TypeResolver = new TypeResolver();
        o.TypeResolver.RegisterAlias("Customer", typeof(Customer));
        o.TypeResolver.RegisterAlias("Order", typeof(Order));
    },
    db => db.UseNpgsql(connStr));

services.AddNRulesReloader(opt =>
{
    opt.PollInterval = TimeSpan.FromSeconds(10);
    opt.PollJitter  = TimeSpan.FromSeconds(2);
});

// 可选：开 LISTEN/NOTIFY 加速
services.UsePostgresNotify(opt => opt.ConnectionString = connStr);
```

#### 选项类型

| 类型 | 关键字段 |
| --- | --- |
| `EFCoreStoreOptions` | `TypeResolver`, `ConfigureSerializer`；数据库 provider/连接串通过 `DbContextOptionsBuilder` 配置 |
| `ReloaderOptions` | `PollInterval` (默认 30s), `PollJitter` (默认 3s), `UseNotify` |
| `PostgresNotifyOptions` | `ConnectionString`, `ReconnectBackoff` (默认 1s 起、30s 上限)；channel 固定为 `rules_changed` |

### 低层静态门面（用于控制台脚本、迁移工具、测试夹具）

```csharp
public static class RulesReloaderRunner
{
    public static async Task<ISessionFactory> RunOnceAsync(
        IRuleStore store,
        IRuleSessionFactoryProvider provider,
        CancellationToken ct);
}
```

### Null Object

```csharp
using System;
using NRules.Diagnostics;
using NRules.Extensibility;

internal sealed class NullSessionFactory : ISessionFactory
{
    public static readonly NullSessionFactory Instance = new();
    private static InvalidOperationException NotReady() =>
        new("Rules have not been loaded yet. Wait for IRuleSessionFactoryProvider.WaitForReadyAsync().");

    public IEventProvider Events => throw NotReady();
    public IDependencyResolver DependencyResolver { get => throw NotReady(); set => throw NotReady(); }
    public IActionInterceptor? ActionInterceptor { get => throw NotReady(); set => throw NotReady(); }
    public ISession CreateSession() => throw NotReady();
    public ISession CreateSession(Action<ISession> initializationAction) => throw NotReady();
    public ReteGraph GetSchema() => throw NotReady();
}
```

## 热重载机制细节

### 信号聚合

```csharp
internal sealed class RulesReloader : BackgroundService
{
    private readonly Channel<ReloadReason> _signals = Channel.CreateBounded<ReloadReason>(
        new BoundedChannelOptions(capacity: 1)
        {
            FullMode  = BoundedChannelFullMode.DropOldest, // 关键：高频信号只保留最新
            SingleReader = true,
            SingleWriter = false
        });
}
```

### 三类信号

| 来源 | 写入原因 |
| --- | --- |
| `ReloadReason.Bootstrap` | 服务启动后立即来一次首加载 |
| `ReloadReason.Poll` | 轮询循环观察到指纹变化 |
| `ReloadReason.Notify` | `PostgresNotifyListener` 收到 `pg_notify` |

### 重建设置（消费循环）

```csharp
private async Task RunConsumeLoop(CancellationToken stop)
{
    await foreach (var reason in _signals.Reader.ReadAllAsync(stop))
    {
        try
        {
            var fingerprint = await _store.ComputeFingerprintAsync(stop);
            if (reason != ReloadReason.Bootstrap && fingerprint == _lastFingerprint) continue;
            var rules = await _store.LoadAllAsync(stop);
            var next  = new RuleCompiler().Compile(rules);
            _provider.Swap(next);
            _lastFingerprint = fingerprint;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RulesReloader rebuild failed; keeping previous factory");
            // 不替换，保持旧 Current
        }
    }
}
```

### 工厂原子切换

```csharp
public sealed class RuleSessionFactoryProvider : IRuleSessionFactoryProvider
{
    private ISessionFactory _current = NullSessionFactory.Instance;
    private readonly TaskCompletionSource<ISessionFactory> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ISessionFactory Current => Volatile.Read(ref _current)!;
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
```

**为什么用 `Interlocked.Exchange`：** 单原子发布新工厂，读路径用 `Volatile.Read`，消费者每次读取都能看到一个完整的工厂引用。它只保证引用切换原子，不自动解决旧工厂的释放时机；如果工厂实现可释放接口，必须保证释放不会早于已取得该工厂的消费者使用结束。

### 轮询循环

```csharp
private async Task RunPollingLoop(CancellationToken stop)
{
    var period = _options.PollInterval + RandomJitter(_options.PollJitter);
    while (!stop.IsCancellationRequested)
    {
        await Task.Delay(period, _time, stop);
        await _signals.Writer.WriteAsync(ReloadReason.Poll, stop);
    }
}
```

### LISTEN/NOTIFY 监听

- 单独一条 `NpgsqlConnection`：先执行 `LISTEN rules_changed`，再循环 `await conn.WaitAsync(stop)`；Notification 回调写入 `_signals.Writer.TryWrite(ReloadReason.Notify)`。`WaitAsync` 是异步等待操作，不作为可释放资源使用。
- 连接断开 → 指数退避（1s → 30s 上限）重连，期间不影响轮询路径
- listener 未连接时不阻塞轮询/消费启动；`RulesReloader.WaitForNotifyReadyAsync` 仅用于观察监听连接状态，不是 readiness bootstrap 的前置条件
- 依赖 `Microsoft.Extensions.Hosting.IHostedService` 生命周期
- NOTIFY 只是低延迟提示；所有变更仍以数据库版本指纹为准，轮询作为断线兜底。

### 失败语义

| 场景 | 行为 |
| --- | --- |
| 单条规则 JSON 反序列化失败或定义名与主键不匹配 | 整批加载失败并记录规则名；不编译或发布部分规则集，保留当前工厂，修复数据后由后续轮询/通知重试 |
| `RuleCompiler.Compile` 整体抛错 | 旧工厂保留，新候选工厂丢弃；不提交已处理指纹，后续轮询/通知可重试 |
| `FactorySwapped` 订阅处理器在原子发布后抛错 | 新工厂已发布；记录为通知处理器错误并提交已处理指纹，避免误报为重载失败或重复编译 |
| `DbUpdateConcurrencyException` | 调用 `IRuleStore.SaveRuleAsync` 直接抛出，由调用方处理；库内不自动重试 |
| Postgres 暂时不可达 | 记录重载失败并保留 `Current`；后续轮询周期重新读取，v1 不额外增加数据库错误退避策略 |
| LISTEN/NOTIFY 连接断开 | 后台重连（指数退避）；轮询不受影响 |

## 测试策略

### 单元测试

| 用例 | 目标 |
| --- | --- |
| `EFCoreRuleStore_SaveAndLoad_RoundTrip` | 序列化往返，等价性 |
| `EFCoreRuleStore_Delete_RemovesRow` | 删除路径 |
| `RulesFingerprint_Stable_AcrossQueries` | 同样的内容产生同样的指纹 |
| `RuleJsonConverterFactory_RegisterAliases_ResolveBack` | 别名机制不回环 |
| `Provider_CurrentBeforeFirstSwap_ThrowsDescriptiveError` | 冷启动错误友好 |

### 集成测试（Testcontainers.PostgreSql）

| 用例 | 目标 |
| --- | --- |
| `Reloader_PicksUpInsert_AndSwapsFactory` | 启动后 INSERT 触发版本指纹变化，推进 FakeTimeProvider 至轮询周期，验证 `FactorySwapped` |
| `Reloader_NotifiesImmediately_WhenNotifyEnabled` | 等待监听器完成 LISTEN，再直接 SQL 更新；断言收到通知并在规定时限内替换工厂 |
| `Reloader_KeepsPreviousFactory_WhenRuleCannotBeDeserialized` | 注入破坏的 JSON 行，验证整批加载失败、当前工厂不变；修复行后可重试切换 |
| `Concurrent_SaveRule_ThrowsDbUpdateConcurrencyException` | xmin 并发令牌真实生效 |
| `Rule_Jsonb_Array_Tags_PersistsAndQueries` | text[] 列正常工作 |

测试夹具用 `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider` 加速轮询循环。

## 端到端样例 `samples/Persistence.Postgres`

**目的：** 让用户在 30 秒内看到完整链路：
1. 启动一个 Postgres 容器
2. `dotnet run` 在启动 hosted reloader 前执行迁移并灌入两条规则
3. 程序消费示例 Order，并保持运行以接受后续评估
4. README 演示用 `psql` 更新 `rule_definition` 中的 `priority` → 数据库维护版本并通知 → 按 Enter 用新工厂再次评估

样例代码保持 ≤ 200 行；不演示并发编辑 UI。

## 包元数据与依赖

### 新增到 `Directory.Packages.props`

```xml
<PackageVersion Include="Microsoft.EntityFrameworkCore"          Version="10.*" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.*" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design"   Version="10.*" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Configuration.Json" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="10.*" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.*" />
<PackageVersion Include="Testcontainers.PostgreSql"             Version="4.*" />
<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.*" />
```

### 库 csproj

- `TargetFramework`: `net10.0`（EF Core 10 与 Npgsql EF Core 10 的支持目标）
- `Nullable: enable`
- `ImplicitUsings: enable`（新工程显式设置；仓库未全局启用）
- `TreatWarningsAsErrors`: 继承自 `Directory.Build.props`
- `PackageId`: `NRules.Persistence.EntityFrameworkCore`
- 依赖 `NRules.Json`（运行时）
- 依赖 `Microsoft.EntityFrameworkCore`、`Microsoft.EntityFrameworkCore.Relational`、`Npgsql.EntityFrameworkCore.PostgreSQL`、`Microsoft.Extensions.Hosting.Abstractions`

### 测试 csproj

- `TargetFramework`: `net10.0`
- 包含 `Microsoft.NET.Test.Sdk`、`xunit`、`xunit.runner.visualstudio`、`Testcontainers.PostgreSql`、`Microsoft.Extensions.TimeProvider.Testing`、`Moq`
- 集成测试标记 `[Trait("Category","Integration")]`，CI 可单独跑

## 风险与缓解

| 风险 | 缓解 |
| --- | --- |
| Postgres 长时间不可达期间规则被外部修改，启动恢复后不一致 | 重连后下一轮轮询会重建一次；首加载前提供 `WaitForReadyAsync` 让消费者选择等待 |
| `RuleCompiler.Compile` 在高并发规则集下开销大 | `BoundedChannel(DropOldest)` 合并等待中的信号；消费中的编译不取消，完成后再处理队列中的新信号 |
| 序列化 `IRuleDefinition` 中的 Action 引用了未加载程序集 | 整批加载失败并保留最后一个有效工厂；修复数据或加载所需程序集后由后续轮询/通知重试；部署侧必须保证域程序集已加载 |
| LISTEN/NOTIFY 触发器被运维禁用 | 默认轮询仍工作；`UsePostgresNotify` 是 opt-in |
| 直接 SQL 写入绕过应用拦截器 | 版本号、更新时间和通知都由数据库触发器维护；迁移是 schema/触发器的唯一来源 |
| xmin 不显式可见，DBA 可能误删迁移 | migration 文件保留 xmin 映射文档和 trigger SQL；README 说明不要删除系统列映射或迁移 |

## 自审清单

| 项 | 结果 |
| --- | --- |
| 占位符 (TBD/TODO) | 无 |
| 内部一致性 | 各段对接口名、列名、数据库触发器责任、channel 与选项类定义一致 |
| 范围聚焦 | 是 —— 仅"Postgres 持久化 + 热重载"，不引入 HTTP 层 / 多租户 / SQL Server 适配 |
| 歧义 | 否 —— 关键决策都有明确选项说明 |

## 后续可加项（v1 不做；后续 PR / v2 候选）

按"扩展性 ROI"从高到低排序。设计层面为这些项已经做了预留，调用方在 v1 就能通过扩展点接入，无需库再变更。

### 高优先级（经常被问到）

| 项 | 说明 | 设计的预留点 |
| --- | --- | --- |
| **规则版本历史 / 回滚** | `rule_versions` 副表记录每次 UPDATE 前一版本（由版本触发器同步写），支持 `RollbackAsync(name, version)` | 复用数据库版本触发器，确保直接 SQL 与应用写入使用同一事务 |
| **REST 管理接口** | `POST /rules`、`PUT /rules/{name}`、`DELETE /rules/{name}`、`GET /rules`、`POST /rules/{name}/rollback` | 库外层项目，依赖 `IRuleStore` 已经具备全部能力 |
| **SQL Server / MySQL / SQLite 适配** | 同一 `IRuleStore` 接口，多个 `SqlServerRuleStore` / `MySqlRuleStore` / `SqliteRuleStore`。此时并发令牌模型需更换（SQL Server 用 `rowversion`，MySQL 用 `timestamp` 列），不能直接复用 `xmin` | v1 用 EF Core provider 抽象隔离，但需要新增 `IConcurrencyTokenStrategy` |
| **多租户隔离** | 表上加 `tenant_id` 列 / schema-per-tenant / DB-per-tenant 三种切片策略；`IRuleStore` 接受 `tenantId` 参数 | v1 的 `Name` 自然键在多租户下变成 `(tenant_id, name)` 复合键，可现在就把 PK 设为复合键以避免未来迁移 |
| **规则签名校验** | 每条规则附带 `signature` 列（私钥签名 `IRuleDefinition` 序列化字节），`ComputeFingerprintAsync` 同时校验签名 → 拒绝未授权下发 | `RuleEntity` 增加一列；签名 / 校验在 `SaveRuleAsync` / `Compile` 入口各做一次 |

### 中优先级（提效）

| 项 | 说明 |
| --- | --- |
| **灰度 / A/B** | `IRuleSessionFactoryProvider` 扩展成 `IRuleSessionFactoryProvider<TKey>`，按 key（用户/会话）选工厂；需要双套 `IRuleStore` 引用 |
| **增量编译** | 不再全量 `RuleCompiler.Compile(rules)`，而是按规则名差量更新 Rete 网络节点。`RuleCompiler` 当前没有公开增量 API，需要先在 `NRules` 主仓库加 `IRuleCompiler.CompileDelta(IRuleDefinition new, IRuleDefinition old)`。**这是依赖 NRules 上游的改动** |
| **编译缓存** | 同 `X` 条规则集 → 缓存编译结果，命中直接复用 `ISessionFactory`。`ComputeFingerprintAsync` 返回的 `Hash` 就是天然缓存键 |
| **规则导入 / 导出** | 批量 `SaveRulesAsync(IEnumerable<IRuleDefinition>)` + `ZipExportAsync()` / `ZipImportAsync(zipStream)` |
| **规则依赖分析** | `RuleDefinitionConverter` 已经是可遍历的 AST，加 `RuleDependencyAnalyzer` 提取 `LeftHandSide` 引用的所有域类型 / Tag，反向回答"这条规则会响应哪些类型的变化" |
| **LISTEN/NOTIFY 触发器自动创建标志位** | 在某条规则上手动 disable 触发器，跳过 NOTIFY 路径；可避免批量导入（迁移 / 测试夹具灌数据）期间无意义地扇出事件 |

### 低优先级（生态）

| 项 | 说明 |
| --- | --- |
| **与 NRules.Debugger.Visualizer 集成** | 现存可视化插件目前只读 fluent DSL。把 `IRuleDefinition` 反序列化为模型后即可复用现有 visit 逻辑 |
| **OpenTelemetry 指标** | `nrules.rules.count` / `nrules.rebuild.duration` / `nrules.rebuild.trigger{kind=notify/poll}` / `nrules.serialize.failures`。依赖 `ActivitySource` |
| **Health Check** | `services.AddHealthChecks().AddNRulesStore()` 检查 (a) DB 可达 (b) `provider.Current` 不是 `NullSessionFactory` |
| **WASM / Blazor 客户端预览** | 把规则 JSON 拿到浏览器侧用 NRules.Json 反序列化 + 一个简化版 Rete 解释器预览执行，供规则作者在提交前本地试运行 |
| **规则统计使用追踪** | 每次 `Fire()` 计数每条规则的命中次数，间歇写回 `rule_stats` 表，配合规则可视化 |
| **加密静态字面量** | `IRuleDefinition` 中 Action 里的常量字符串经常含敏感词；`RuleJsonConverterFactory` 接受一个可选的 `IFieldEncryptor` 钩子，行落库前加密、读取时解密 |

### 升级路径注意事项

- 上面"高优先级"中的任何一项在 v1 release 之后做，都意味着 v1.x 的 bug fix 周期内并存两套 schema；建议 v2.0 之前完成**所有四项**以避免兼容包袱
- "中优先级"中的"增量编译"是唯一需要改 NRules 主仓库的项，应该单独建一个 issue 跟踪上游 PR
- 引入"多租户"时务必在 v1 就把 PK 重定义为 `(tenant_id, name)` —— 否则 v2 再做要写一次性迁移脚本
