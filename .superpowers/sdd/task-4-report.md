# Task 4 report: `IRuleStore` and `EFCoreRuleStore`

## Implementation

- Added `IRuleStore`, `RulesFingerprint`, and the `EFCoreStoreOptions` serializer configuration surface.
- Added JSON serializer option creation using camel-case property names and NRules' rule converters.
- Implemented PostgreSQL-backed rule insert/update/delete/load operations and the name/version-based fingerprint.
- Kept the serialized `IRuleDefinition` as the authoritative payload; all relational metadata is populated from that same definition.
- Enforced fail-closed loads: malformed rules or a JSON/row name mismatch are logged and throw instead of returning a partial ruleset.
- Added CRUD, metadata round-trip, update/version, fingerprint, and fail-closed PostgreSQL integration tests. Every test that writes calls `ResetAsync()`.

PostgreSQL `jsonb` does not preserve JSON object property order, while the existing NRules rule converters consume properties in a defined order. The CRUD round-trip exposed this at first. Added an internal normalizer that restores the converter-defined property order before deserializing the stored JSON. The database JSON remains authoritative; this normalization only restores the expected order and does not source rule values from relational columns.

Project references to the repository's `NRules.Json`, `NRules.Fluent`, runtime, and `NRules.RuleModel` projects are used to test against the source projects' matching assembly identities instead of potentially stale local NuGet package builds.

## TDD and validation

### RED

Command:

```text
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~EFCoreRuleStoreCrudTests" -nologo
```

After correcting the test builder to the actual `RuleBuilder` API and adding required project references, compilation failed as expected because `EFCoreRuleStore`, its options namespace, and its serializer namespace did not yet exist.

### GREEN

The same focused command passed after implementation: **5 passed, 0 failed**. The first execution caught JSONB key reordering breaking NRules deserialization; the normalizer was added and the test passed on rerun.

### Other checks

- `dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj -nologo` — **11 passed, 0 failed** (includes existing migration/constraint checks).
- `dotnet build src\NRules\NRules.Persistence.EntityFrameworkCore\NRules.Persistence.EntityFrameworkCore.csproj --no-restore -nologo` — **0 warnings, 0 errors**.
- `dotnet build src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore -nologo` — **0 warnings, 0 errors**.
- `git diff --check` — passed.

## Self-review

- The JSON object order normalizer is deliberately internal to persistence and needed because PostgreSQL `jsonb` canonicalizes object keys. The integration tests exercise a rule with an expression, metadata, tags, and properties through PostgreSQL before compiling the loaded result.
- The fingerprint implementation follows the task brief's sorted-name/rule-version fold and returns row count and maximum version (zero for an empty table).
- No unrelated tracked files were changed. Existing untracked documentation and scratch artifacts were left untouched.
