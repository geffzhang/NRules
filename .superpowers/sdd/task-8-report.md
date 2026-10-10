# Task 8: Dependency-injection extensions

## Implementation

- Added `AddNRulesEfCoreStore` to configure EF Core store options, construct the rule JSON serializer, register the `RulesDbContext` factory, and register the store and session-factory provider.
- Added `AddNRulesReloader` with the 30-second polling and 3-second jitter defaults, notify options, and a singleton `RulesReloader` shared with the hosted-service registration.
- Added `UsePostgresNotify` to apply notification options and enable notifications without requiring a connection string when notifications remain disabled.
- Added PostgreSQL DI smoke coverage for store/provider resolution, save-and-compile, hosted-service identity, notification opt-in configuration, and absence of notification connection settings when disabled. Both integration tests reset the shared fixture before work.

## TDD evidence

### RED

Command:

```text
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~DiSmokeTests" --no-restore
```

Before implementing the extensions, compilation failed because the required DI namespace/API was absent:

```text
DiSmokeTests.cs(5,46): error CS0234: 命名空间“NRules.Persistence.EntityFrameworkCore”中不存在类型或命名空间名“DependencyInjection”(是否缺少程序集引用?)
```

Exit code: 1.

### GREEN

Focused DI smoke integration tests:

```text
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~DiSmokeTests" --no-restore
```

```text
已通过! - 失败:     0，通过:     2，已跳过:     0，总计:     2
```

Warning-as-error build:

```text
dotnet build src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore -warnaserror
```

```text
已成功生成。
    0 个警告
    0 个错误
```

`git diff --check` reported no whitespace errors.

## Self-review

- The DI integration test confirms the `RulesReloader` resolved directly is the exact same instance exposed as `IHostedService`.
- The notify opt-in path configures both `ReloaderOptions.UseNotify` and `PostgresNotifyOptions.ConnectionString`; the disabled path resolves options without a connection string.
- The store smoke path uses the configured Npgsql context factory and JSON type aliases to persist and load a rule, then compiles it with `RulesReloaderRunner`.
- The reloader implementation and runner behavior from earlier tasks were not changed; the existing compatible constructor selection and post-publication subscriber handling remain intact.
- No dependency manifests or package versions needed changes.
