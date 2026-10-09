# Task 3: Testcontainers PostgreSQL fixture

## TDD verification

### RED

Added `MigrationAppliesTests` before implementing the fixture, then ran:

```text
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~MigrationAppliesTests" -nologo
```

Expected failure: compilation could not find `PostgresFixture` (`CS0246`) at the constructor and field declaration. This confirmed the test could not run before the fixture existed.

### GREEN

Implemented the shared fixture/collection and ran the focused migration test:

```text
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~MigrationAppliesTests" -nologo
```

Result: passed, 1 test; the migration initialized PostgreSQL and the `rules` table was queryable.

Ran both PostgreSQL integration tests against the shared collection:

```text
dotnet test src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "Category=Integration" -nologo
```

Result: passed, 2 tests.

Ran the warning-as-error build:

```text
dotnet build src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore -warnaserror -nologo
```

Result: succeeded with 0 warnings and 0 errors.
