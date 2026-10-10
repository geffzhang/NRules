# Task 10 — PostgreSQL `xmin` stale-write integration test

## Outcome

Added the focused integration test `RuleStoreConcurrencyTests.Save_stale_entity_throws_DbUpdateConcurrencyException`. It inserts a real rule through `EFCoreRuleStore`, loads that row in two independent contexts, saves a change in the first context, and asserts that saving the stale copy in the second context throws `DbUpdateConcurrencyException`.

No production code was changed. `RulesDbContext` already maps shadow property `xmin` to PostgreSQL `xmin`, marks it generated on add/update, and configures it as a concurrency token.

## TDD evidence

The requested test was added before running it. The exact snippet in the brief did not compile against the current DSL API, so its rule setup was adapted to the established repository usage: `RuleBuilder.Name` is a `void` method, and the condition/action require explicitly typed expression trees (`IContext` is in `NRules.RuleModel`).

Initial focused run (the brief's verbatim rule-builder chaining):

```text
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore --filter FullyQualifiedName~RuleStoreConcurrencyTests --logger "console;verbosity=normal"
```

```text
RuleStoreConcurrencyTests.cs(31,13): error CS0023: 运算符“.”无法应用于“void”类型的操作数
```

After splitting `Name` into its own call and typing the DSL lambdas, intermediate compile runs exposed the expression/API requirements (`CS8917`, then missing `IContext` namespace and statement-bodied expression tree). These were test-authoring compile errors, not a runtime concurrency failure.

**Behavioral RED was not observed:** once the test compiled, the first focused PostgreSQL execution passed. This is consistent with the requested scope being verification of the already-present Task 2 mapping. No mapping was disabled or otherwise changed merely to manufacture a failing run.

## GREEN verification

Focused test command:

```text
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore --filter FullyQualifiedName~RuleStoreConcurrencyTests --logger "console;verbosity=normal"
```

Output summary:

```text
Connected to Docker; PostgreSQL Testcontainers started and became ready.
Passed NRules.Persistence.EntityFrameworkCore.Tests.Integration.RuleStoreConcurrencyTests.Save_stale_entity_throws_DbUpdateConcurrencyException
Test run successful.
Total tests: 1
Passed: 1
```

Full persistence test project command:

```text
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore --logger "console;verbosity=minimal"
```

Output summary:

```text
Test run successful.
Failed: 0, Passed: 30, Skipped: 0, Total: 30
```

`git diff --check` reported no whitespace errors.

## Files changed

- `src/NRules/NRules.Persistence.EntityFrameworkCore.Tests/Integration/RuleStoreConcurrencyTests.cs` — new stale-entity concurrency integration test.
- `.superpowers/sdd/task-10-report.md` — this report.

Pre-existing untracked `docs/superpowers/` files were preserved and excluded from the commit.

## Self-review

- The test uses the shared PostgreSQL collection fixture and resets the table before execution.
- It exercises PostgreSQL and EF Core directly rather than mocking persistence or concurrency behavior.
- The two contexts independently load the same row before the first context updates it; the stale second write must throw the EF Core concurrency exception.
- Production mapping and unrelated files remain unchanged.
- The brief's exact DSL expression did not match the repository's current C# API; the test preserves the requested scenario and semantics using the existing typed-expression style.

## Concerns

The requested TDD behavioral RED phase could not be demonstrated without deliberately removing a production concurrency-token mapping that already works. The focused test passed at its first successful execution; initial failures were compile-time incompatibilities in the brief's supplied test snippet, corrected in the new test. No actual defect was found.
