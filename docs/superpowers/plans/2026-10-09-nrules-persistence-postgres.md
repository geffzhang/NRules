# NRules.Persistence.EntityFrameworkCore（PostgreSQL + 热重载）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 NRules 提供一个 Postgres 持久化 + 运行时热重载规则的库，以及一个端到端样例和 Testcontainers 集成测试套件。

**Architecture:**
- 一个 EF Core 库 `NRules.Persistence.EntityFrameworkCore`，使用 `NRules.Json` 把 `IRuleDefinition` 序列化为 `jsonb` 落到 `rules` 表
- 一个后台 `BackgroundService` 同时消费**轮询指纹**与 **Postgres LISTEN/NOTIFY** 两路信号，通过 `Channel<ReloadReason>(Bounded=1, DropOldest)` 折叠高频信号
- 一个 volatile 单例 `IRuleSessionFactoryProvider`，用 `Interlocked.Exchange` 原子发布新工厂；消费者代码只 `provider.Current.CreateSession()`
- 并发安全用 Postgres 系统列 `xmin` 作为 EF Core 并发令牌 —— 零额外字段、DB 端强制

**Tech Stack:**
- .NET 10（库、测试项目与样例；EF Core 10 / Npgsql EF Core 10 不支持 net8.0 目标）
- EF Core 10.x + Npgsql.EntityFrameworkCore.PostgreSQL 10.x
- Microsoft.Extensions.Hosting + Microsoft.Extensions.DependencyInjection
- Testcontainers.PostgreSql + Microsoft.Extensions.TimeProvider.Testing
- xUnit + Moq
- NRules (locally built 包) + NRules.Json

**Spec:** [`docs/superpowers/specs/2026-10-09-nrules-persistence-postgres-design.md`](../specs/2026-10-09-nrules-persistence-postgres-design.md)

## Global Constraints

- **Central package versions** managed in `Directory.Packages.props`；新增 EF Core / Npgsql / Testcontainers 等包必须先在那里声明
- **CentralPackageFloatingVersionsEnabled=true**；使用稳定主版本浮动范围 `Version="10.*"`（Testcontainers 使用 `4.*`），不限定 prerelease 版本
- **`TreatWarningsAsErrors=true`** (继承自 `Directory.Build.props`)，所有代码必须能零警告编译
- **Nullable reference types enabled** 在新工程显式打开
- **Implicit usings enabled** 在新库、测试和样例工程显式设置为 `<ImplicitUsings>enable</ImplicitUsings>`；仓库未全局启用
- **C# latest** 在新工程显式打开
- **强名称签名** 遵循现有库项目配置：显式设置 `SignAssembly=true`，并将 `AssemblyOriginatorKeyFile` 指向仓库根目录的 `SigningKey.snk`
- **库的 TargetFramework**：`net10.0`，与 EF Core 10 / Npgsql EF Core 10 的支持目标一致
- **测试工程的 TargetFramework**：`net10.0`（Testcontainers.PostgreSql + FakeTimeProvider 在 net10 上最稳）
- **命名空间**：`NRules.Persistence.EntityFrameworkCore.*`
- **公共 API 入口在 Core 子集**（`IRuleStore`、`IRuleSessionFactoryProvider`、`RulesReloaderRunner`），**不**依赖 Microsoft.Extensions.DependencyInjection；DI 扩展方法单独放在 `DependencyInjection` 子集
- **测试约定**：单元测试用 `Theory`/`Fact`；集成测试标记 `[Trait("Category","Integration")]`，可选择性跑
- **提交风格**：Conventional Commits（`feat:` / `test:` / `chore:` / `docs:` / `build:`）

---

### Task 1：搭建库工程骨架并接入解决方案

**Files:**
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/NRules.Persistence.EntityFrameworkCore.csproj`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/_placeholder.cs`
- Modify: `Directory.Packages.props`（追加 EF Core 等新包版本）
- Modify: `src/NRules/NRules.sln`（添加新项目）
- Modify: `build.ps1`（追加新 component 条目使它能 `Build All` 时一并构建）

**Interfaces:**
- Produces: 库项目 `NRules.Persistence.EntityFrameworkCore`（空库）
- 无 — 仅为后续 Task 准备工程脚手架

- [ ] **Step 1: 在 `Directory.Packages.props` 中追加新包版本**

打开 `E:/GitHub/NRules/Directory.Packages.props`，在现有 `<ItemGroup>` 内追加：

```xml
<PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.*" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.*" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.*" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Configuration.Json" Version="10.*" />
<PackageVersion Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="10.*" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.*" />
<PackageVersion Include="Testcontainers.PostgreSql" Version="4.*" />
<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.*" />
```

- [ ] **Step 2: 创建库 csproj**

`src/NRules/NRules.Persistence.EntityFrameworkCore/NRules.Persistence.EntityFrameworkCore.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>NRules.Persistence.EntityFrameworkCore</RootNamespace>
    <AssemblyName>NRules.Persistence.EntityFrameworkCore</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <!-- 包元数据继承自 Directory.Build.props -->
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="NRules.Json" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: 创建临时占位符让 csproj 不空**

`src/NRules/NRules.Persistence.EntityFrameworkCore/_placeholder.cs`：

```csharp
namespace NRules.Persistence.EntityFrameworkCore;

internal static class PlaceholderAssemblyMarker { }
```

- [ ] **Step 4: 把项目加进 NRules.sln**

运行（在仓库根）：

```bash
dotnet sln src/NRules/NRules.sln add src/NRules/NRules.Persistence.EntityFrameworkCore/NRules.Persistence.EntityFrameworkCore.csproj
```

期望：`Project NRules.Persistence.EntityFrameworkCore.csproj added to the solution.`

- [ ] **Step 5: 编辑 `build.ps1`**

打开 `build.ps1`，在 `$components` 字典里添加一项（在 `NRules.Integration.DependencyInjection` 之后、`Samples.GettingStarted` 之前）：

```powershell
'NRules.Persistence' = @{
    name = 'NRules.Persistence.EntityFrameworkCore'
    solution_file = 'src\NRules\NRules.sln'
    package = @{
        bin = @{
            artifacts = @('net10.0')
            'net10.0' = @{
                include = @(
                    "NRules.Persistence.EntityFrameworkCore\bin\$configuration\net10.0"
                )
            }
        }
        nuget = @(
            'NRules.Persistence.EntityFrameworkCore'
        )
    }
};
```

然后在 `$integration = ...` 那行**之前**新增：

```powershell
$persistence = @('NRules.Persistence')
```

在 `if ($component -eq "Core")` 分支中，紧接 `$componentList += $core` 追加：

```powershell
$componentList += $persistence
```

并在 `if ($component -eq "All")` 分支中也追加：

```powershell
$componentList += $persistence
```

**注意**：可以单独跑 `./build.sh Build NRules.Persistence` 进行 CI 局部构建。

- [ ] **Step 6: 编译验证**

```bash
cd E:/GitHub/NRules && dotnet build src/NRules/NRules.Persistence.EntityFrameworkCore/NRules.Persistence.EntityFrameworkCore.csproj -nologo
```

期望：`Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 7: 提交**

```bash
cd E:/GitHub/NRules
git add Directory.Packages.props \
        src/NRules/NRules.Persistence.EntityFrameworkCore/ \
        src/NRules/NRules.sln \
        build.ps1
git commit -m "chore: scaffold NRules.Persistence.EntityFrameworkCore library"
```

---

### Task 2：定义 `RuleEntity` 与 `RulesDbContext`

**Files:**
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/Entities/RuleEntity.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/RulesDbContext.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/RulesDbContextDesignTimeFactory.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/Migrations/<timestamp>_InitRules.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/Migrations/RulesDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: Task 1 的库项目
- Produces:
  - 类型 `NRules.Persistence.EntityFrameworkCore.Entities.RuleEntity`
  - 类型 `NRules.Persistence.EntityFrameworkCore.RulesDbContext`
  - EF Core migration 为数据库 schema 与触发器提供唯一版本化来源

- [ ] **Step 1: 写失败的单元测试（DbContext 配置能跑通）**

先创建测试工程文件并添加到 solution，再创建测试源码。测试工程使用 Task 1 已配置的 Central Package Management：

`src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Moq" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <ProjectReference Include="..\NRules.Persistence.EntityFrameworkCore\NRules.Persistence.EntityFrameworkCore.csproj" />
  </ItemGroup>
</Project>
```

运行 `dotnet sln src/NRules/NRules.sln add src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj`。

`src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Unit/RulesDbContextSchemaTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Entities;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Unit;

public class RulesDbContextSchemaTests
{
    [Fact]
    public void Can_build_DbContext_with_default_schema()
    {
        var options = new DbContextOptionsBuilder<RulesDbContext>()
            .UseInMemoryDatabase(nameof(Can_build_DbContext_with_default_schema))
            .Options;

        using var ctx = new RulesDbContext(options);
        Assert.Equal("rules", ctx.Model.FindEntityType(typeof(RuleEntity))!.GetTableName());
    }
}
```

（这一步先仅用 InMemory 验证 schema mapping 配置正确；真实 Postgres 集成在 Task 3 引入 Testcontainers）

- [ ] **Step 2: 跑测试，确认失败（红）**

```bash
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~RulesDbContextSchemaTests" -nologo
```

期望：编译失败，因为 `RuleEntity` 不存在。

- [ ] **Step 3: 实现 `RuleEntity`**

`src/NRules/NRules.Persistence.EntityFrameworkCore/Entities/RuleEntity.cs`：

```csharp
using System.Text.Json;
using NRules.RuleModel;

namespace NRules.Persistence.EntityFrameworkCore.Entities;

/// <summary>
/// EF Core entity mapping to the Postgres <c>rules</c> table.
/// </summary>
public class RuleEntity
{
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public int Priority { get; set; }
    public RuleRepeatability Repeatability { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>JSONB column: PropertyMap of the rule.</summary>
    public JsonDocument Properties { get; set; } = default!;

    /// <summary>JSONB column: full IRuleDefinition serialized with NRules.Json.</summary>
    public JsonDocument RuleDefinition { get; set; } = default!;

    public int RuleVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 4: 实现 `RulesDbContext`**

`src/NRules/NRules.Persistence.EntityFrameworkCore/RulesDbContext.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Entities;

namespace NRules.Persistence.EntityFrameworkCore;

public class RulesDbContext : DbContext
{
    public RulesDbContext(DbContextOptions<RulesDbContext> options) : base(options) { }

    public DbSet<RuleEntity> Rules => Set<RuleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var e = modelBuilder.Entity<RuleEntity>();
    e.ToTable("rules", table =>
        table.HasCheckConstraint(
            "CK_rules_rule_definition_name_matches_name",
            "jsonb_typeof(rule_definition) = 'object' AND (rule_definition ? 'name') AND jsonb_typeof(rule_definition -> 'name') = 'string' AND rule_definition ->> 'name' = name"));
    e.HasKey(r => r.Name);
        e.Property(r => r.Name).HasColumnName("name").HasColumnType("varchar(200)");
        e.Property(r => r.Description).HasColumnName("description");
        e.Property(r => r.Priority).HasColumnName("priority");

        e.Property(r => r.Repeatability)
            .HasColumnName("repeatability")
            .HasConversion<short>()
            .HasColumnType("smallint");
        e.Property(r => r.Priority).HasDefaultValue(0);

        e.Property(r => r.Properties)
            .HasColumnName("properties")
            .HasColumnType("jsonb")
            .IsRequired();

        e.Property(r => r.RuleDefinition)
            .HasColumnName("rule_definition")
            .HasColumnType("jsonb")
            .IsRequired();

        e.Property(r => r.Tags)
            .HasColumnName("tags")
            .HasColumnType("text[]")
            .HasDefaultValue(Array.Empty<string>())
            .Metadata.SetValueComparer(
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<string[]>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                    v => v.ToArray()));

        e.Property(r => r.RuleVersion).HasColumnName("rule_version").HasDefaultValue(1);
        e.Property(r => r.RuleVersion).ValueGeneratedOnAddOrUpdate();
        e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        e.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").HasDefaultValueSql("now()");

        // Postgres xmin concurrency token
        e.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
```

- [ ] **Step 5: 添加 design-time DbContext factory**

实现 `IDesignTimeDbContextFactory<RulesDbContext>`，从必需的环境变量 `NRULES_DESIGN_CONNECTION_STRING` 读取连接串；未设置时抛出清晰异常。生成迁移只需 provider 配置，不应在设计时主动连接数据库。

- [ ] **Step 6: 添加初始 EF Core migration**

确保已安装 `dotnet-ef` 10.x tool。在 PowerShell 中设置 `NRULES_DESIGN_CONNECTION_STRING` 后，通过 `dotnet ef migrations add InitRules --project src/NRules/NRules.Persistence.EntityFrameworkCore --startup-project src/NRules/NRules.Persistence.EntityFrameworkCore` 为 `RulesDbContext` 生成 migration 与 model snapshot，然后编辑 migration 的 `Up`/`Down`：表结构由 EF migration 管理；同一 migration 通过 `migrationBuilder.Sql(...)` 创建/删除触发器函数及触发器。`Up` 中的 PostgreSQL SQL 至少实现：

```sql
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
    END IF;
    PERFORM pg_notify('rules_changed', NEW.name);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER rules_changed_trg
AFTER INSERT OR UPDATE OR DELETE ON rules
FOR EACH ROW EXECUTE FUNCTION rules_notify();
```

`Down` 先删除两个 trigger 和函数，再删除表。channel 固定为 `rules_changed`。不要在测试 fixture 或样例中复制第二份 DDL。

- [ ] **Step 7: 确认测试工程已在 Step 1 创建并加入 solution**

- [ ] **Step 9: 跑测试，期望通过（绿）**

```bash
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~RulesDbContextSchemaTests" -nologo
```

期望：`Passed: 1`

- [ ] **Step 10: 提交**

```bash
git add src/NRules/NRules.Persistence.EntityFrameworkCore/Entities/RuleEntity.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore/RulesDbContext.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/ \
        src/NRules/NRules.sln
git commit -m "feat: add RuleEntity and RulesDbContext with xmin concurrency token"
```

---

### Task 3：`Testcontainers` PostgresFixture

**Files:**
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/PostgresFixture.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/PostgresCollection.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/MigrationAppliesTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `RulesDbContext`
- Produces:
  - `PostgresFixture : IAsyncLifetime` —— 启动 `testcontainers/postgres:16-alpine`，通过 EF Core migration 初始化 schema 与触发器
  - `[CollectionDefinition("Postgres")]` 让多测试类共享一个容器
- 集合共享数据库；为避免跨测试残留，每个使用 `PostgresFixture` 的测试必须先调用 `await _pg.ResetAsync()`。该方法只清空 `rules` 表，不重建 schema 或容器。

- [ ] **Step 1: 写失败的初始化迁移应用测试**

`Integration/MigrationAppliesTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class MigrationAppliesTests
{
    private readonly PostgresFixture _pg;

    public MigrationAppliesTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Schema_apply_succeeds_and_table_is_queryable()
    {
        await _pg.ResetAsync();
        await using var ctx = _pg.NewDbContext();
        var migrations = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Contains(migrations, migration => migration.EndsWith("_InitRules", StringComparison.Ordinal));
        var rows = await ctx.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM \"rules\"").ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0] >= 0);
    }
}
```

- [ ] **Step 2: 跑测试，期望失败**

```bash
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~MigrationAppliesTests" -nologo
```

期望：DI 找不到 `PostgresFixture` —— 容器还没写。

- [ ] **Step 3: 实现 `PostgresFixture`**

`PostgresFixture.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace NRules.Persistence.EntityFrameworkCore.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlBuilder _builder = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("nrules_test")
        .WithUsername("postgres")
        .WithPassword("postgres");

    private PostgreSqlContainer _container = default!;

    public string ConnectionString => _container.GetConnectionString();

    public RulesDbContext NewDbContext()
    {
        var opts = new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new RulesDbContext(opts);
    }

    public async Task InitializeAsync()
    {
        _container = _builder.Build();
        await _container.StartAsync();
        await using var ctx = NewDbContext();
        await ctx.Database.MigrateAsync();
    }

    public async Task ResetAsync()
    {
        await using var ctx = NewDbContext();
        await ctx.Database.ExecuteSqlRawAsync("TRUNCATE TABLE rules");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

}
```

- [ ] **Step 4: 实现 collection 注解**

`PostgresCollection.cs`：

```csharp
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests;

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
```

- [ ] **Step 5: 跑测试，期望通过**

```bash
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~MigrationAppliesTests" -nologo
```

期望：Testcontainers 启动容器约 10-20s，测试通过。

- [ ] **Step 6: 提交**

```bash
git add src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/PostgresFixture.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/PostgresCollection.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/MigrationAppliesTests.cs
git commit -m "test: add PostgresFixture with Testcontainers and bootstrap DDL"
```

---

### Task 4：`IRuleStore` + `EFCoreRuleStore` 的 CRUD

**Files:**
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/IRuleStore.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/EFCoreRuleStore.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/Serialization/RuleJsonConverterFactory.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/Options/EFCoreStoreOptions.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/EFCoreRuleStoreCrudTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `RulesDbContext`、`RuleEntity`；Task 3 的 `PostgresFixture`
- Produces:
  - 类型 `IRuleStore`（含 `SaveRuleAsync` / `DeleteRuleAsync` / `LoadAsync` / `LoadAllAsync` / `ComputeFingerprintAsync`）
  - 类型 `EFCoreRuleStore : IRuleStore`（依赖 `IDbContextFactory<RulesDbContext>` + `EFCoreStoreOptions`）
  - 类型 `RuleJsonConverterFactory`，把 `EFCoreStoreOptions` 编译为 `JsonSerializerOptions`
  - 类型 `EFCoreStoreOptions`（含 `ConfigureSerializer`、`TypeResolver`）

- [ ] **Step 1: 写 `IRuleStore` 接口**

`IRuleStore.cs`：

```csharp
using NRules.RuleModel;

namespace NRules.Persistence.EntityFrameworkCore;

public interface IRuleStore
{
    Task SaveRuleAsync(IRuleDefinition rule, CancellationToken ct = default);
    Task DeleteRuleAsync(string name, CancellationToken ct = default);
    Task<IRuleDefinition?> LoadAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<IRuleDefinition>> LoadAllAsync(CancellationToken ct = default);
    Task<RulesFingerprint> ComputeFingerprintAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2: 写 `RulesFingerprint` record 与 options 占位**

`RulesFingerprint.cs`（新文件，与 `IRuleStore` 同目录）：

```csharp
public readonly record struct RulesFingerprint(long Hash, int Count, int LatestVersion);
```

`Options/EFCoreStoreOptions.cs`：

```csharp
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using NRules.Json;

namespace NRules.Persistence.EntityFrameworkCore.Options;

public class EFCoreStoreOptions
{
    [Required] public TypeResolver TypeResolver { get; set; } = default!;
    public Action<JsonSerializerOptions>? ConfigureSerializer { get; set; }
}
```

- [ ] **Step 3: 写 `RuleJsonConverterFactory`**

`Serialization/RuleJsonConverterFactory.cs`：

```csharp
using System.Text.Json;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore.Options;

namespace NRules.Persistence.EntityFrameworkCore.Serialization;

public static class RuleJsonConverterFactory
{
    public static JsonSerializerOptions Create(EFCoreStoreOptions opt)
    {
        var j = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        opt.ConfigureSerializer?.Invoke(j);
        RuleSerializer.Setup(j, opt.TypeResolver);
        return j;
    }

    public static JsonSerializerOptions Create(TypeResolver typeResolver)
    {
        var j = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        RuleSerializer.Setup(j, typeResolver);
        return j;
    }
}
```

- [ ] **Step 4: 写 CRUD 测试（红）**

`Integration/EFCoreRuleStoreCrudTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class EFCoreRuleStoreCrudTests
{
    private readonly PostgresFixture _pg;

    public EFCoreRuleStoreCrudTests(PostgresFixture pg) => _pg = pg;

    private IRuleDefinition MakeRule(string name) => new RuleBuilder()
        .Name(name).Priority(42)
        .LeftHandSide().Pattern(typeof(object), "o").Condition(o => true)
        .RightHandSide().Action(ctx => { })
        .Build();

    private EFCoreRuleStore NewStore()
    {
        var factory = new TestDbContextFactory(_pg.ConnectionString);
        var opts = new EFCoreStoreOptions
        {
            TypeResolver = new TypeResolver(),
        };
        opts.TypeResolver.RegisterDefaultAliases();
        var serializer = RuleJsonConverterFactory.Create(opts);
        return new EFCoreRuleStore(factory, serializer, NullLogger<EFCoreRuleStore>.Instance);
    }

    [Fact]
    public async Task SaveRule_Then_LoadAll_Roundtrips_Equivalent_Definition()
    {
        await _pg.ResetAsync();
        var store = NewStore();
        var rule = MakeRule("rule-A");

        await store.SaveRuleAsync(rule);
        var all = await store.LoadAllAsync();

        Assert.Single(all);
        Assert.Equal(rule.Name, all[0].Name);
        Assert.Equal(rule.Priority, all[0].Priority);
        Assert.NotNull(new RuleCompiler().Compile(all));
    }

    [Fact]
    public async Task DeleteRule_Removes_Row()
    {
        await _pg.ResetAsync();
        var store = NewStore();
        var rule = MakeRule("rule-B");
        await store.SaveRuleAsync(rule);

        await store.DeleteRuleAsync("rule-B");

        Assert.Null(await store.LoadAsync("rule-B"));
    }

    [Fact]
    public async Task Compute_Fingerprint_Is_Stable_Across_Equal_Inputs()
    {
        await _pg.ResetAsync();
        var store = NewStore();
        await store.SaveRuleAsync(MakeRule("rule-C"));

        var f1 = await store.ComputeFingerprintAsync();
        var f2 = await store.ComputeFingerprintAsync();

        Assert.Equal(f1.Hash, f2.Hash);
        Assert.Equal(-3564688325343655662L, f1.Hash);
        Assert.Equal(1, f1.Count);
        Assert.Equal(1, f1.LatestVersion);
    }
}

internal sealed class TestDbContextFactory : IDbContextFactory<RulesDbContext>
{
    private readonly DbContextOptions<RulesDbContext> _options;
    public TestDbContextFactory(string conn)
        => _options = new DbContextOptionsBuilder<RulesDbContext>().UseNpgsql(conn).Options;
    public RulesDbContext CreateDbContext() => new(_options);
}
```

- [ ] **Step 5: 跑测试，确认红**

```bash
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~EFCoreRuleStoreCrudTests" -nologo
```

期望：编译失败 —— `EFCoreRuleStore` 不存在。

- [ ] **Step 6: 实现 `EFCoreRuleStore`**

`EFCoreRuleStore.cs`：

```csharp
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NRules.Persistence.EntityFrameworkCore.Entities;
using NRules.RuleModel;

namespace NRules.Persistence.EntityFrameworkCore;

public class EFCoreRuleStore : IRuleStore
{
    private readonly IDbContextFactory<RulesDbContext> _factory;
    private readonly JsonSerializerOptions _serializer;
    private readonly ILogger<EFCoreRuleStore> _logger;

    public EFCoreRuleStore(
        IDbContextFactory<RulesDbContext> factory,
        JsonSerializerOptions serializer,
        ILogger<EFCoreRuleStore> logger)
    {
        _factory = factory;
        _serializer = serializer;
        _logger = logger;
    }

    public async Task SaveRuleAsync(IRuleDefinition rule, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(rule, _serializer);
        var existing = await ctx.Rules.FindAsync(new object?[] { rule.Name }, ct);

        if (existing is null)
        {
            var entity = new RuleEntity
            {
                Name          = rule.Name,
                Description   = rule.Description,
                Priority      = rule.Priority,
                Repeatability = rule.Repeatability,
                Tags          = rule.Tags.ToArray(),
                Properties    = JsonDocument.Parse(JsonSerializer.Serialize(rule.Properties, _serializer)),
                RuleDefinition = JsonDocument.Parse(json),
            };
            await ctx.Rules.AddAsync(entity, ct);
        }
        else
        {
            existing.Description   = rule.Description;
            existing.Priority      = rule.Priority;
            existing.Repeatability = rule.Repeatability;
            existing.Tags          = rule.Tags.ToArray();
            existing.Properties    = JsonDocument.Parse(
                JsonSerializer.Serialize(rule.Properties, _serializer));
            existing.RuleDefinition = JsonDocument.Parse(json);
        }

        // PostgreSQL triggers maintain RuleVersion and UpdatedAt for both EF and direct SQL writes.
        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteRuleAsync(string name, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.Rules.FindAsync(new object?[] { name }, ct);
        if (existing is null) return;
        ctx.Rules.Remove(existing);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IRuleDefinition?> LoadAsync(string name, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all.FirstOrDefault(r => r.Name == name);
    }

    public async Task<IReadOnlyList<IRuleDefinition>> LoadAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.Rules.AsNoTracking().ToListAsync(ct);
        var result = new List<IRuleDefinition>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                var def = JsonSerializer.Deserialize<IRuleDefinition>(
                    row.RuleDefinition.RootElement.GetRawText(), _serializer);
                if (def is null)
                    throw new InvalidOperationException("Deserialization returned null.");
                if (!string.Equals(def.Name, row.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Definition name '{def.Name}' does not match row name '{row.Name}'.");
                result.Add(def);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot load rule {Rule}; aborting the ruleset load", row.Name);
                throw new InvalidOperationException(
                    $"Cannot load rule '{row.Name}'; no partial ruleset will be returned.", ex);
            }
        }
        return result;
    }

    public async Task<RulesFingerprint> ComputeFingerprintAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.Rules.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new { r.Name, r.RuleVersion })
            .ToListAsync(ct);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> valueBuffer = stackalloc byte[sizeof(int)];
        foreach (var r in rows)
        {
            var nameBytes = Encoding.UTF8.GetBytes(r.Name);
            BinaryPrimitives.WriteInt32BigEndian(valueBuffer, nameBytes.Length);
            hash.AppendData(valueBuffer);
            hash.AppendData(nameBytes);
            BinaryPrimitives.WriteInt32BigEndian(valueBuffer, r.RuleVersion);
            hash.AppendData(valueBuffer);
        }

        var digest = hash.GetHashAndReset();
        var fingerprintHash = BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, sizeof(long)));
        return new RulesFingerprint(
            fingerprintHash, rows.Count, rows.Count == 0 ? 0 : rows.Max(r => r.RuleVersion));
    }
}
```

- [ ] **Step 7: 跑测试，期望绿**

```bash
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~EFCoreRuleStoreCrudTests" -nologo
```

期望：`Passed: 3`

- [ ] **Step 8: 提交**

```bash
git add src/NRules/NRules.Persistence.EntityFrameworkCore/IRuleStore.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore/RulesFingerprint.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore/EFCoreRuleStore.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore/Options/ \
        src/NRules/NRules.Persistence.EntityFrameworkCore/Serialization/ \
        src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/EFCoreRuleStoreCrudTests.cs
git commit -m "feat: IRuleStore and EFCoreRuleStore with CRUD and fingerprint"
```

---

### Task 5：`IRuleSessionFactoryProvider` + `NullSessionFactory` + 原子切换

**Files:**
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/IRuleSessionFactoryProvider.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/RuleSessionFactoryProvider.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/NullSessionFactory.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Unit/SessionFactoryProviderTests.cs`

**Interfaces:**
- Consumes: `ISessionFactory`（从 NRules 包）
- Produces:
  - 类型 `IRuleSessionFactoryProvider`：属性 `Current`（volatile，永远非 null）、事件 `FactorySwapped`、方法 `WaitForReadyAsync`
  - 类型 `RuleSessionFactoryProvider : IRuleSessionFactoryProvider` 含 `Swap(ISessionFactory next)`
  - 类型 `NullSessionFactory : ISessionFactory`（静态 `Instance`）

- [ ] **Step 1: 写失败的单元测试**

`Unit/SessionFactoryProviderTests.cs`：

```csharp
using Moq;
using NRules;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Unit;

public class SessionFactoryProviderTests
{
    [Fact]
    public void Current_rejects_session_creation_before_first_swap()
    {
        var p = new RuleSessionFactoryProvider();
        Assert.Throws<InvalidOperationException>(() => p.Current.CreateSession());
    }

    [Fact]
    public void Swap_replaces_previous_and_raises_event()
    {
        var p = new RuleSessionFactoryProvider();
        var factoryMock = new Mock<ISessionFactory>();
        ISessionFactory? received = null;
        p.FactorySwapped += (_, f) => received = f;

        p.Swap(factoryMock.Object);

        Assert.Same(factoryMock.Object, p.Current);
        Assert.Same(factoryMock.Object, received);
    }

    [Fact]
    public void Swap_called_twice_keeps_only_the_latest()
    {
        var p = new RuleSessionFactoryProvider();
        var mock1 = new Mock<ISessionFactory>();
        var mock2 = new Mock<ISessionFactory>();

        p.Swap(mock1.Object);
        p.Swap(mock2.Object);

        Assert.Same(mock2.Object, p.Current);
    }

    [Fact]
    public async Task WaitForReadyAsync_completes_after_first_swap()
    {
        var p = new RuleSessionFactoryProvider();
        var factory = new Mock<ISessionFactory>().Object;
        var ready = p.WaitForReadyAsync();

        p.Swap(factory);

        Assert.Same(factory, await ready);
    }
}
```

- [ ] **Step 2: 跑测试，确认红**

```bash
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~SessionFactoryProviderTests" -nologo
```

期望：`type or namespace name 'RuleSessionFactoryProvider' could not be found`

- [ ] **Step 3: 实现 `NullSessionFactory`**

`NullSessionFactory.cs`：

```csharp
using System;
using NRules.Diagnostics;
using NRules.Extensibility;
using NRules;

namespace NRules.Persistence.EntityFrameworkCore;

internal sealed class NullSessionFactory : ISessionFactory
{
    public static readonly NullSessionFactory Instance = new();

    private NullSessionFactory() { }

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

- [ ] **Step 4: 实现 `IRuleSessionFactoryProvider`**

`IRuleSessionFactoryProvider.cs`：

```csharp
using NRules;

namespace NRules.Persistence.EntityFrameworkCore;

public interface IRuleSessionFactoryProvider
{
    ISessionFactory Current { get; }
    Task<ISessionFactory> WaitForReadyAsync(CancellationToken ct = default);
    event EventHandler<ISessionFactory>? FactorySwapped;
    void Swap(ISessionFactory next);
}
```

- [ ] **Step 5: 实现 `RuleSessionFactoryProvider`**

`RuleSessionFactoryProvider.cs`：

```csharp
using NRules;

namespace NRules.Persistence.EntityFrameworkCore;

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

- [ ] **Step 6: 跑测试，期望绿**

```bash
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~SessionFactoryProviderTests" -nologo
```

期望：`Passed: 4`

- [ ] **Step 7: 提交**

```bash
git add src/NRules/NRules.Persistence.EntityFrameworkCore/IRuleSessionFactoryProvider.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore/RuleSessionFactoryProvider.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore/NullSessionFactory.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Unit/SessionFactoryProviderTests.cs
git commit -m "feat: IRuleSessionFactoryProvider with atomic swap and Null Object"
```

---

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
using Microsoft.Extensions.Logging;
using System.Diagnostics;

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
        try
        {
            provider.Swap(factory);
        }
        catch (Exception ex) when (ReferenceEquals(provider.Current, factory))
        {
            Trace.TraceError(
                "FactorySwapped notification handler failed after factory publication: {0}", ex);
        }
        return factory;
    }

    public static async Task<ISessionFactory> RunOnceAsync(
        IRuleStore store,
        IRuleSessionFactoryProvider provider,
        ILogger logger,
        CancellationToken ct)
    {
        var rules = await store.LoadAllAsync(ct);
        var factory = new RuleCompiler().Compile(rules);
        try
        {
            provider.Swap(factory);
        }
        catch (Exception ex) when (ReferenceEquals(provider.Current, factory))
        {
            logger.LogError(ex, "FactorySwapped notification handler failed after factory publication");
        }
        return factory;
    }
}
```

三参数兼容入口将事件回调错误写到 `Trace.TraceError`；需要应用日志管道时调用 `ILogger` 重载。仅当异常发生时已确认 `provider.Current` 是刚编译的工厂，才将其视为发布后的回调错误并返回该工厂；未发布时继续抛出原异常。

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

缺省 `PollInterval` 为 30 秒；显式配置必须大于零，并在启动后台循环前校验。`PollJitter` 缺省为 3 秒，配置为零或负值时禁用 jitter。

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
        var pollInterval = options.PollInterval ?? TimeSpan.FromSeconds(30);
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options.PollInterval), "PollInterval must be greater than zero.");

        _store    = store;
        _provider = provider;
        _options  = options;
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
        var interval = _options.PollInterval ?? TimeSpan.FromSeconds(30);
        var jitter   = _options.PollJitter   ?? TimeSpan.FromSeconds(3);

        while (!stop.IsCancellationRequested)
        {
            await _time.Delay(interval + RandomJitter(jitter), stop);
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

`ISessionFactoryProvider.Swap` 原子发布后若 `FactorySwapped` 订阅处理器抛错，`Current` 已是新工厂。此情况必须按通知处理器失败单独记录，并仍提交该规则集指纹；不可误报为重载失败或在下一次轮询重复编译。若工厂尚未发布，则沿用常规失败路径并保留旧指纹。

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

### Task 7：`PostgresNotifyListener` 与 LISTEN/NOTIFY 路径

**Files:**
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/Reloading/PostgresNotifyListener.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/Options/PostgresNotifyOptions.cs`
- Modify: `src/NRules/NRules.Persistence.EntityFrameworkCore/Reloading/RulesReloader.cs`（接入 listen + 暴露 `UseNotify`）
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/ReloaderNotifyTests.cs`

**Interfaces:**
- Consumes: Task 6 的 `RulesReloader`、`ReloadReason`
- Produces:
  - 类型 `PostgresNotifyListener : BackgroundService`，使用独立连接，构造参数包括 `ConnectionString`、通知回调、`TimeProvider` 与重连退避
  - 类型 `PostgresNotifyOptions`
  - 调整：`UsePostgresNotify` 启用 `ReloaderOptions.UseNotify` 并传递连接串；`RulesReloader` 启动并管理 listener 生命周期

- [ ] **Step 1: 写失败的测试（NOTIFY 推送路径）**

`Integration/ReloaderNotifyTests.cs`：

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NRules;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Persistence.EntityFrameworkCore.Reloading;
using NRules.RuleModel;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
public class ReloaderNotifyTests
{
    private readonly PostgresFixture _pg;
    public ReloaderNotifyTests(PostgresFixture pg) => _pg = pg;

    private async Task<IRuleStore> MakeStoreAsync() { /* 同 Runner 测试 */ }

    private static IRuleDefinition MakeRule(string name) => new RuleBuilder()
        .Name(name)
        .LeftHandSide().Pattern(typeof(object), "o").Condition(o => true)
        .RightHandSide().Action(ctx => { })
        .Build();

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Direct_sql_update_rebuilds_factory_without_waiting_for_poll()
    {
        await _pg.ResetAsync();
        var store = await MakeStoreAsync();
        await store.SaveRuleAsync(MakeRule("notify-rule"));
        await store.SaveRuleAsync(MakeRule("survivor-rule"));

        var provider = new RuleSessionFactoryProvider();
        var swaps = Channel.CreateUnbounded<ISessionFactory>();
        provider.FactorySwapped += (_, factory) => swaps.Writer.TryWrite(factory);
        var reloader = new RulesReloader(
            store,
            provider,
            new ReloaderOptions
            {
                UseNotify = true,
                PollInterval = TimeSpan.FromMinutes(1),
                PollJitter = TimeSpan.Zero,
            },
            TimeProvider.System,
            NullLoggerFactory.Instance,
            new PostgresNotifyOptions
            {
                ConnectionString = _pg.ConnectionString,
            });

        await reloader.StartAsync(CancellationToken.None);
        try
        {
            await reloader.WaitForNotifyReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var initial = await swaps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            await using var conn = new Npgsql.NpgsqlConnection(_pg.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "UPDATE rules SET rule_definition = rule_definition WHERE name = 'notify-rule'",
                conn);
            await cmd.ExecuteNonQueryAsync();

            var reloaded = await swaps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(initial, reloaded);

            cmd.CommandText = "DELETE FROM rules WHERE name = 'notify-rule'";
            await cmd.ExecuteNonQueryAsync();
            var afterDelete = await swaps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(reloaded, afterDelete);
            Assert.Single(await store.LoadAllAsync());
        }
        finally
        {
            await reloader.StopAsync(CancellationToken.None);
        }
    }
}
```

- [ ] **Step 2: 跑测试，确认红**

- [ ] **Step 3: 实现 `PostgresNotifyOptions`**

`Options/PostgresNotifyOptions.cs`：

```csharp
namespace NRules.Persistence.EntityFrameworkCore.Options;

public class PostgresNotifyOptions
{
    public string? ConnectionString { get; set; }
    public TimeSpan ReconnectBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
```

The listener caps `ReconnectBackoff` at 30 seconds when it is constructed, so even the first retry delay cannot exceed the retry maximum when a larger value is configured.

- [ ] **Step 4: 实现 `PostgresNotifyListener`**

`Reloading/PostgresNotifyListener.cs`：

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace NRules.Persistence.EntityFrameworkCore.Reloading;

public sealed class PostgresNotifyListener : BackgroundService
{
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

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
        _connectionString = connectionString;
        _channel = "rules_changed";
        _onNotify = onNotify;
        _time = time;
        _initialBackoff = initialBackoff <= MaximumBackoff ? initialBackoff : MaximumBackoff;
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Listen loop disconnected; reconnecting in {Backoff}", backoff);
                await _time.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(
                    Math.Min(MaximumBackoff.TotalSeconds, backoff.TotalSeconds * 2));
            }
        }
    }
}
```

- [ ] **Step 5: 跑测试，期望绿**

```bash
dotnet test ... --filter "FullyQualifiedName~ReloaderNotifyTests"
```

期望：`Passed: 1`

- [ ] **Step 6: 接入 `RulesReloader` 让 `UseNotify` 路径同时启 `PostgresNotifyListener`**

修改 `RulesReloader.cs`：
在 `RulesReloader` 中注入 `PostgresNotifyOptions` 并在 `ExecuteAsync` 开头追加：

新增字段 `_notifyOptions`、`_notifyReady`，以及 `WaitForNotifyReadyAsync(CancellationToken)` 供需要确认监听已连接的调用方/测试等待。`StartAsync` listener 后只保存 readiness task，不要等待它再启动轮询与消费循环；数据库不可达时轮询仍必须工作。

```csharp
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
```

- `RulesReloader` 内部新增一个 `Microsoft.Extensions.Logging.ILoggerFactory` 字段。在构造函数里再做一次参数调整：

```csharp
public RulesReloader(
    IRuleStore store,
    IRuleSessionFactoryProvider provider,
    ReloaderOptions options,
    TimeProvider time,
    ILoggerFactory loggerFactory,
    PostgresNotifyOptions notifyOptions)
{
    _logger = loggerFactory.CreateLogger<RulesReloader>();
    _loggerFactory = loggerFactory;
    _notifyOptions = notifyOptions;
    // ... 其余不变
}
```
- 在 `RulesReloader` 中添加：

```csharp
private readonly PostgresNotifyOptions _notifyOptions;
private Task _notifyReady = Task.CompletedTask;

public Task WaitForNotifyReadyAsync(CancellationToken ct = default) =>
    _notifyReady.WaitAsync(ct);
```

- 同步更新 Task 6 的轮询测试构造参数：传 `NullLoggerFactory.Instance` 和默认 `new PostgresNotifyOptions()`。

- [ ] **Step 7: 跑 Tests Polling + Notify 全跑一遍**

```bash
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/ -nologo
```

期望：所有 unit + polling + notify 测试都通过。

- [ ] **Step 8: 提交**

```bash
git add src/NRules/NRules.Persistence.EntityFrameworkCore/Reloading/PostgresNotifyListener.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore/Options/PostgresNotifyOptions.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore/Reloading/RulesReloader.cs \
        src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/ReloaderNotifyTests.cs
git commit -m "feat: PostgresNotifyListener and dual-source reload signals"
```

---

### Task 8：DI 扩展方法

**Files:**
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/DiSmokeTests.cs`

**Interfaces:**
- Consumes: 全部前置 Task
- Produces:
  - 扩展方法 `AddNRulesEfCoreStore(IServiceCollection, Action<EFCoreStoreOptions>, Action<DbContextOptionsBuilder>)`
  - 扩展方法 `AddNRulesReloader(IServiceCollection, Action<ReloaderOptions>?)`
  - 扩展方法 `UsePostgresNotify(IServiceCollection, Action<PostgresNotifyOptions>?)`

- [ ] **Step 1: 写失败的 DI 集成测试**

`Integration/DiSmokeTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class DiSmokeTests
{
    private readonly PostgresFixture _pg;
    public DiSmokeTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Full_DI_graph_can_resolve_store_and_provider()
    {
        await _pg.ResetAsync();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();

        services.AddNRulesEfCoreStore(
            o =>
            {
                o.TypeResolver = new TypeResolver();
                o.TypeResolver.RegisterDefaultAliases();
            },
            db => db.UseNpgsql(_pg.ConnectionString));

        services.AddNRulesReloader();

        await using var sp = services.BuildServiceProvider();
        var store    = sp.GetRequiredService<IRuleStore>();
        var provider = sp.GetRequiredService<IRuleSessionFactoryProvider>();

        await store.SaveRuleAsync(new NRules.RuleModel.Builders.RuleBuilder()
            .Name("d1").LeftHandSide().Pattern(typeof(object), "o").Condition(o => true)
            .RightHandSide().Action(ctx => { }).Build());

        var factory = await RulesReloaderRunner.RunOnceAsync(store, provider, default);
        Assert.NotNull(factory);
    }
}
```

- [ ] **Step 2: 跑测试，确认红**

- [ ] **Step 3: 实现扩展方法**

`DependencyInjection/ServiceCollectionExtensions.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Persistence.EntityFrameworkCore.Reloading;
using NRules.Persistence.EntityFrameworkCore.Serialization;

namespace NRules.Persistence.EntityFrameworkCore.DependencyInjection;

public static class NRulesEfCoreServiceCollectionExtensions
{
    public static IServiceCollection AddNRulesEfCoreStore(
        this IServiceCollection services,
        Action<EFCoreStoreOptions> configure,
        Action<DbContextOptionsBuilder>? configureDb = null)
    {
        services.AddOptions<EFCoreStoreOptions>().Configure(configure);
        services.AddSingleton(sp => RuleJsonConverterFactory.Create(sp.GetRequiredService<IOptions<EFCoreStoreOptions>>().Value));

        services.AddDbContextFactory<RulesDbContext>(b =>
            configureDb?.Invoke(b));

        services.AddSingleton<IRuleStore, EFCoreRuleStore>();
        services.AddSingleton<IRuleSessionFactoryProvider, RuleSessionFactoryProvider>();
        return services;
    }

    public static IServiceCollection AddNRulesReloader(
        this IServiceCollection services,
        Action<ReloaderOptions>? configure = null)
    {
        services.AddOptions<ReloaderOptions>()
            .Configure(o =>
            {
                configure?.Invoke(o);
                o.PollInterval ??= TimeSpan.FromSeconds(30);
                o.PollJitter   ??= TimeSpan.FromSeconds(3);
            });

        services.AddOptions<PostgresNotifyOptions>();
        services.AddSingleton<RulesReloader>(sp => new RulesReloader(
            sp.GetRequiredService<IRuleStore>(),
            sp.GetRequiredService<IRuleSessionFactoryProvider>(),
            sp.GetRequiredService<IOptions<ReloaderOptions>>().Value,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IOptions<PostgresNotifyOptions>>().Value));
        services.AddHostedService(sp => sp.GetRequiredService<RulesReloader>());
        return services;
    }

    public static IServiceCollection UsePostgresNotify(
        this IServiceCollection services,
        Action<PostgresNotifyOptions>? configure = null)
    {
        services.AddOptions<PostgresNotifyOptions>().Configure(o =>
        {
            configure?.Invoke(o);
        });
        services.Configure<ReloaderOptions>(o => o.UseNotify = true);
        return services;
    }
}
```

- [ ] **Step 4: 跑测试，期望绿**

```bash
dotnet test ... --filter "FullyQualifiedName~DiSmokeTests"
```

- [ ] **Step 5: 提交**

```bash
git add src/NRules/NRules.Persistence.EntityFrameworkCore/DependencyInjection/ \
        src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/DiSmokeTests.cs
git commit -m "feat: DI extensions AddNRulesEfCoreStore/AddNRulesReloader/UsePostgresNotify"
```

---

### Task 9：失败容忍测试 —— 坏规则不发布部分规则集

**Files:**
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/EFCoreRuleStoreResilienceTests.cs`
- Modify: `src/NRules/NRules.Persistence.EntityFrameworkCore/EFCoreRuleStore.cs`（已具备 try/catch 日志；本 Task 单纯验证）

**Interfaces:**
- Consumes: Task 4 的 `EFCoreRuleStore`
- Produces: 测试覆盖 resilience 行为

- [ ] **Step 1: 写失败验证测试**

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Entities;
using NRules.Persistence.EntityFrameworkCore.Serialization;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class EFCoreRuleStoreResilienceTests
{
    private readonly PostgresFixture _pg;
    public EFCoreRuleStoreResilienceTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task RunOnce_preserves_current_factory_when_rule_cannot_be_loaded()
    {
        await _pg.ResetAsync();
        var factory = new TestDbContextFactory(_pg.ConnectionString);
        var opts = new NRules.Persistence.EntityFrameworkCore.Options.EFCoreStoreOptions
        {
            TypeResolver = new NRules.Json.TypeResolver(),
        };
        opts.TypeResolver.RegisterDefaultAliases();
        var serializer = RuleJsonConverterFactory.Create(opts);
        var store = new EFCoreRuleStore(factory, serializer, NullLogger<EFCoreRuleStore>.Instance);
        var provider = new RuleSessionFactoryProvider();

        var good = new RuleBuilder().Name("good")
            .LeftHandSide().Pattern(typeof(object), "o").Condition(o => true)
            .RightHandSide().Action(ctx => { }).Build();
        await store.SaveRuleAsync(good);
        var original = await RulesReloaderRunner.RunOnceAsync(store, provider, default);

        // 手工塞入有效 JSON 格式但无法反序列化为规则的行。
        await using var ctx = factory.CreateDbContext();
        ctx.Rules.Add(new RuleEntity
        {
            Name = "broken",
            // Satisfies the name constraint but is missing required rule-definition fields.
            RuleDefinition = JsonDocument.Parse("{\"name\":\"broken\"}"),
            Properties = JsonDocument.Parse("{}"),
            Tags = Array.Empty<string>(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RulesReloaderRunner.RunOnceAsync(store, provider, default));
        Assert.Contains("broken", error.Message);
        Assert.Same(original, provider.Current);

        ctx.Rules.Remove(await ctx.Rules.FindAsync("broken")
            ?? throw new InvalidOperationException("Broken test row was not found."));
        await ctx.SaveChangesAsync();

        var retried = await RulesReloaderRunner.RunOnceAsync(store, provider, default);
        Assert.NotSame(original, retried);
        Assert.Same(retried, provider.Current);
    }
}
```

- [ ] **Step 2: 跑测试，期望通过**

验证坏行导致整批加载失败且旧工厂未变；删除坏行后重试成功并切换工厂。

- [ ] **Step 3: 提交**

```bash
git add src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/EFCoreRuleStoreResilienceTests.cs
git commit -m "test: preserve current factory when a stored rule cannot load"
```

---

### Task 10：并发冲突测试（xmin 真实生效）

**Files:**
- Create: `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/RuleStoreConcurrencyTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `RuleEntity.xmin`

- [ ] **Step 1: 写测试**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Serialization;
using NRules.RuleModel.Builders;
using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests.Integration;

[Collection("Postgres")]
[Trait("Category", "Integration")]
public class RuleStoreConcurrencyTests
{
    private readonly PostgresFixture _pg;
    public RuleStoreConcurrencyTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Save_stale_entity_throws_DbUpdateConcurrencyException()
    {
        await _pg.ResetAsync();
        var factory = new TestDbContextFactory(_pg.ConnectionString);
        var opts = new NRules.Persistence.EntityFrameworkCore.Options.EFCoreStoreOptions
        {
            TypeResolver = new NRules.Json.TypeResolver(),
        };
        opts.TypeResolver.RegisterDefaultAliases();
        var serializer = RuleJsonConverterFactory.Create(opts);
        var store = new EFCoreRuleStore(factory, serializer, NullLogger<EFCoreRuleStore>.Instance);

        var rule = new RuleBuilder().Name("c1")
            .LeftHandSide().Pattern(typeof(object), "o").Condition(o => true)
            .RightHandSide().Action(ctx => { }).Build();
        await store.SaveRuleAsync(rule);

        // 两个并发 ctx 读出同一行
        await using var ctx1 = factory.CreateDbContext();
        await using var ctx2 = factory.CreateDbContext();
        var a = await ctx1.Rules.FindAsync("c1");
        var b = await ctx2.Rules.FindAsync("c1");

        a!.Priority = 100;
        await ctx1.SaveChangesAsync();

        b!.Priority = 200;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await ctx2.SaveChangesAsync());
    }
}
```

- [ ] **Step 2: 跑测试，期望绿**

- [ ] **Step 3: 提交**

```bash
git add src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/RuleStoreConcurrencyTests.cs
git commit -m "test: verify xmin concurrency token throws on stale write"
```

---

### Task 11：端到端样例 `samples/Persistence.Postgres`

**Files:**
- Create: `samples/Persistence.Postgres/Persistence.Postgres.csproj`
- Create: `samples/Persistence.Postgres/Persistence.Postgres.sln`
- Create: `samples/Persistence.Postgres/Domain/Customer.cs`
- Create: `samples/Persistence.Postgres/Domain/Order.cs`
- Create: `samples/Persistence.Postgres/RulesSeed.cs`
- Create: `samples/Persistence.Postgres/Program.cs`
- Create: `samples/Persistence.Postgres/appsettings.json`
- Create: `samples/Persistence.Postgres/README.md`
- Modify: `build.ps1`（追加样例 component）

**Interfaces:**
- Consumes: 全部前置 Task；可执行样例依赖 NRules、Npgsql、EF Core

- [ ] **Step 1: 创建 csproj**

`Persistence.Postgres.csproj`：

> 本仓库样例引用当前源码项目 `NRules.Fluent` 与 `NRules.Json`，而不是 NuGet 包：中央包版本在本地解析为 1.0.4，与本仓库程序集版本 1.0.0 不兼容。此样例验证仓库源码的端到端集成，不验证已发布包的消费路径。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>NRules.Samples.Persistence.Postgres</RootNamespace>
    <AssemblyName>NRules.Samples.Persistence.Postgres</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\NRules\NRules.Fluent\NRules.Fluent.csproj" />
    <ProjectReference Include="..\..\src\NRules\NRules.Json\NRules.Json.csproj" />
    <ProjectReference Include="..\..\src\NRules\NRules.Persistence.EntityFrameworkCore\NRules.Persistence.EntityFrameworkCore.csproj" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" />
    <PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 创建 sln**

```bash
dotnet new sln -n Persistence.Postgres
dotnet sln Persistence.Postgres.sln add Persistence.Postgres.csproj
```

- [ ] **Step 3: 创建 Domain**

`Domain/Customer.cs`：

```csharp
namespace NRules.Samples.Persistence.Postgres.Domain;

public class Customer
{
    public string Name { get; }
    public Customer(string name) => Name = name;
    public override string ToString() => $"Customer({Name})";
}
```

`Domain/Order.cs`：

```csharp
namespace NRules.Samples.Persistence.Postgres.Domain;

public class Order
{
    public int Id { get; }
    public Customer Customer { get; }
    public double Amount { get; }
    public Order(int id, Customer customer, double amount)
    {
        Id = id;
        Customer = customer;
        Amount = amount;
    }
    public override string ToString() => $"Order({Id}, {Amount})";
}
```

- [ ] **Step 4: 创建 `RulesSeed`**

`RulesSeed.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore;
using NRules.RuleModel;
using NRules.RuleModel.Builders;

namespace NRules.Samples.Persistence.Postgres;

public static class RulesSeed
{
    public static async Task EnsureSeededAsync(IRuleStore store, CancellationToken ct)
    {
        if ((await store.LoadAllAsync(ct)).Count > 0) return;

        await store.SaveRuleAsync(LargeOrder(), ct);
        await store.SaveRuleAsync(VipOrder(), ct);
    }

    private static IRuleDefinition LargeOrder() => new RuleBuilder()
        .Name("LargeOrderRule").Priority(10)
        .LeftHandSide().Pattern(typeof(Domain.Order), "o").Condition(o => o.Amount > 100)
        .RightHandSide().Action(ctx => Console.WriteLine($"[LargeOrderRule] {ctx.Resolve<Domain.Order>().Id}"))
        .Build();

    private static IRuleDefinition VipOrder() => new RuleBuilder()
        .Name("VipOrderRule").Priority(20)
        .LeftHandSide().Pattern(typeof(Domain.Order), "o").Condition(o => o.Customer.Name.StartsWith("VIP"))
        .RightHandSide().Action(ctx => Console.WriteLine($"[VipOrderRule] {ctx.Resolve<Domain.Order>().Id}"))
        .Build();
}
```

- [ ] **Step 5: 创建 `Program.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Samples.Persistence.Postgres.Domain;

var builder = Host.CreateApplicationBuilder();

builder.Configuration.AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables();

var conn = builder.Configuration.GetConnectionString("Rules")
    ?? throw new InvalidOperationException("Connection string 'Rules' not configured");

builder.Services.AddNRulesEfCoreStore(
    o =>
    {
        o.TypeResolver = new TypeResolver();
        o.TypeResolver.RegisterDefaultAliases();
        o.TypeResolver.RegisterAlias("Customer", typeof(Customer));
        o.TypeResolver.RegisterAlias("Order", typeof(Order));
        o.TypeResolver.RegisterAlias("Context", typeof(NRules.IContext));
    },
    db => db.UseNpgsql(conn));

builder.Services.AddNRulesReloader(o =>
{
    o.PollInterval = TimeSpan.FromSeconds(5);
});

builder.Services.UsePostgresNotify(o => o.ConnectionString = conn);
builder.Services.AddSingleton(TimeProvider.System);

await using var host = builder.Build();

// Migrate and seed before the hosted reloader performs its bootstrap load.
await using (var scope = host.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RulesDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    var store = scope.ServiceProvider.GetRequiredService<IRuleStore>();
    await RulesSeed.EnsureSeededAsync(store, CancellationToken.None);
}

await host.StartAsync();

var provider = host.Services.GetRequiredService<IRuleSessionFactoryProvider>();
// Block until the Bootstrap signal has been consumed and a real factory is in place.
// Otherwise provider.Current is still NullSessionFactory at this point.
var factory = await provider.WaitForReadyAsync();
var session = factory.CreateSession();

var customer = new Customer("VIP John");
session.Insert(customer);
session.Insert(new Order(1, customer, 200));

session.Fire();

Console.WriteLine("Rules are live. Update the rule in PostgreSQL, then press Enter to evaluate again; Ctrl+C exits.");
var stoppingToken = host.Services
    .GetRequiredService<IHostApplicationLifetime>()
    .ApplicationStopping;
try
{
    while (await Console.In.ReadLineAsync(stoppingToken) is not null)
    {
        session = provider.Current.CreateSession();
        session.Insert(customer);
        session.Insert(new Order(1, customer, 200));
        session.Fire();
    }
}
catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
{
}

await host.StopAsync();
```

- [ ] **Step 6: 创建 `appsettings.json`**

```json
{
  "ConnectionStrings": {
    "Rules": "Host=localhost;Database=nrules_demo;Username=postgres;Password=postgres"
  }
}
```

- [ ] **Step 7: 创建 README**

`README.md`：

````markdown
# NRules Postgres Persistence 样例

## 准备

```bash
docker run -d --name nrules-pg -p 5432:5432 \
  -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=nrules_demo postgres:16-alpine

# 等待容器就绪
until docker exec nrules-pg pg_isready; do sleep 1; done
```

## 运行

> 该连接串使用仅供本地演示的默认密码 `postgres`，不得用于生产环境。

```bash
dotnet run
```

预期输出形如：
```
[VipOrderRule] 1
[LargeOrderRule] 1
```

## 热重载演示

另起终端：
```bash
docker exec -i nrules-pg psql -U postgres -d nrules_demo <<EOF
UPDATE rules
SET priority = 99,
    rule_definition = jsonb_set(rule_definition, '{priority}', '99'::jsonb)
WHERE name = 'LargeOrderRule';
EOF
```

数据库触发器会递增 `rule_version` 并发送 NOTIFY；监听器或下一次轮询触发重建。等待重建日志后回到样例终端按 Enter，用新 session 执行规则。全过程**无需重启**。
````

- [ ] **Step 8: 跑 dotnet build**

```bash
dotnet build samples/Persistence.Postgres/Persistence.Postgres.csproj -nologo
```

期望：`Build succeeded`。

- [ ] **Step 9: 跑 dotnet run 在容器里（人工验证）**

（此步需要本地有 docker，用户也可跳过）

```bash
docker run -d --name nrules-pg -p 5432:5432 \
  -e POSTGRES_PASSWORD=postgres \
  -e POSTGRES_DB=nrules_demo postgres:16-alpine
until docker exec nrules-pg pg_isready; do sleep 1; done
cd samples/Persistence.Postgres && dotnet run
```

预期看到 `[VipOrderRule]` 与 `[LargeOrderRule]` 输出。

- [ ] **Step 10: 在 `build.ps1` 注册样例**

在 `$components` 字典中追加（紧跟 `Samples.JsonRules`）：

```powershell
'Samples.Persistence' = @{
    name = 'Persistence.Postgres'
    solution_file = 'samples\Persistence.Postgres\Persistence.Postgres.sln'
};
```

确保在 `$samples` 数组收集的 `Where-Object { $_.StartsWith("Samples.") }` 内自动包含。

- [ ] **Step 11: 提交**

```bash
git add samples/Persistence.Postgres/ build.ps1
git commit -m "feat: end-to-end sample Persistence.Postgres with README"
```

---

### Task 12：最终验收 —— 全套测试 + build.ps1

**Files:**
- 无新增
- 全仓库测试 + 端到端构建

- [ ] **Step 1: 跑全套测试**

```bash
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/ -nologo
```

期望：所有 unit + integration 测试通过。

- [ ] **Step 2: 跑 `Build Core`**

```bash
pwsh ./build.ps1 Build Core
```

期望：含 `NRules.Persistence` 在内全部 core 组件构建成功。

- [ ] **Step 3: 跑 `Build Samples`**

```bash
pwsh ./build.ps1 Build Samples
```

期望：`Samples.Persistence` 构建成功。

- [ ] **Step 4: 跑 `Build All`**

```bash
pwsh ./build.ps1 Build All
```

期望：所有组件均构建成功。

- [ ] **Step 5: 提交（一次性汇总提交）**

如果有任何遗漏（注释调整、错字），现在 commit：

```bash
git status
# 如有变更：
# git add -A
# git commit -m "chore: final cleanup before review"
```

---

## 自审清单

| 项 | 状态 |
| --- | --- |
| Spec 覆盖：核心接口（IRuleStore, IRuleSessionFactoryProvider） | Task 4、Task 5 |
| Spec 覆盖：xmin 并发令牌 | Task 2、Task 10 验证 |
| Spec 覆盖：CRUD | Task 4 |
| Spec 覆盖：轮询触发 | Task 6 |
| Spec 覆盖：LISTEN/NOTIFY 触发 | Task 7 |
| Spec 覆盖：DI 扩展 | Task 8 |
| Spec 覆盖：原子切换 + Null Object | Task 5 |
| Spec 覆盖：失败容忍 | Task 9 |
| Spec 覆盖：样例 | Task 11 |
| Spec 覆盖：build 系统接入 | Task 1、Task 11、Task 12 |
| 占位符扫描 | 无（除注释文字） |
| 类型/方法名一致性 | `IRuleStore.ComputeFingerprintAsync`、`IRuleSessionFactoryProvider.Current`/`Swap`、`RuleSessionFactoryProvider.Swap`、`RulesReloaderRunner.RunOnceAsync` 全程对齐 |
| 范围聚焦 | 仅 Postres 持久化 + 热重载，未引入 HTTP/UI |
