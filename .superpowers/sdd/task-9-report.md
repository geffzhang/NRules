# Task 9 Report: Fail-Closed Rule Reload

## Changes

- Added `EFCoreRuleStoreResilienceTests.RunOnce_preserves_current_factory_when_rule_cannot_be_loaded`.
- The integration test resets the shared PostgreSQL fixture, publishes a valid initial factory, inserts a malformed persisted rule definition (`{"name":"broken"}`) using `RuleEntity`, and verifies the failed reload leaves the published factory unchanged.
- It then removes the malformed row and verifies a retry publishes and exposes a new factory.
- No production code changes were needed: the existing store and reload runner already provide the required fail-closed behavior.

## Verification

- Focused: `dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter FullyQualifiedName~EFCoreRuleStoreResilienceTests --verbosity minimal`
  - Passed: 1; failed: 0; skipped: 0.
- Full persistence test project: `dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --verbosity minimal`
  - Passed: 29; failed: 0; skipped: 0.
- Warning-as-error build: `dotnet build src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore --warnaserror --verbosity minimal`
  - Build succeeded; 0 warnings; 0 errors.

## Self-review

- This is test-first behavioral verification of existing behavior; the focused integration test passed without changing production code, as anticipated by the task brief.
- The malformed row satisfies the database name constraint but cannot be deserialized as a complete rule, and the test exercises the actual PostgreSQL-backed store.
- The test asserts both factory identity preservation on failure and successful factory replacement after repair.
- Production reload semantics—including fingerprinting, subscriber exception handling, poll interval validation, and notify reconnect limits—were left untouched.
