# Task 6 Report: Rules Reloader

## Implementation

- Added `RulesReloaderRunner.RunOnceAsync` to load all persisted rules, compile one factory, and publish it only after successful compilation.
- Added `RulesReloader` as a `BackgroundService`, with bootstrap and poll signals, a single-reader bounded wake channel, TimeProvider-driven polling with jitter, and fingerprint-based no-op skipping.
- Added `ReloadReason` (`Bootstrap`, `Poll`, `Notify`) and `ReloaderOptions` (`PollInterval`, `PollJitter`, `UseNotify`). The internal `Signal` hook is available for the Task 7 notification listener; this task does not implement LISTEN/NOTIFY.
- Reload failures are logged without replacing the current factory or advancing the last successful fingerprint, allowing a later poll or signal to retry.
- Added PostgreSQL integration coverage for the runner and polling refresh. Moved `TestDbContextFactory` to the shared test project root for reuse.

## TDD Evidence

### RED

Runner test command:

```powershell
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~ReloaderRunnerTests" --no-restore
```

After fixing test-only compilation issues, the test failed to compile as expected because `RulesReloaderRunner` did not exist (`CS0103`).

Polling test command:

```powershell
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~ReloaderPollingTests" --no-restore
```

The test failed to compile as expected because the `Reloading` namespace did not yet exist (`CS0234`).

### GREEN

- Runner test: passed 1, failed 0.
- Polling test: passed 1, failed 0.
- Combined focused reloader tests: passed 2, failed 0.
- Entire persistence test project: passed 18, failed 0, skipped 0.

## Warning-as-error Build

```powershell
dotnet build src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore -warnaserror -nologo
```

Result: build succeeded, 0 warnings, 0 errors.

## Self-review

- Bootstrap always loads and publishes a factory; poll signals skip work when the fingerprint is unchanged.
- The published factory and last-successful fingerprint are updated only after loading and compilation succeed, preserving the old factory on errors and permitting retries.
- Channel capacity is one with drop-oldest semantics because signals are wake-ups; the consumer always reads current store state.
- Poll intervals use the injected `TimeProvider`; non-positive jitter is treated as zero.
- `Notify` and the internal signal hook are present for the next task, with no listener or database notification handling added here.
- Changes are limited to Task 6 source and tests. Existing untracked documentation and ignored scratch files were not staged.

## Commit

`7bdc8cfc feat: add RulesReloader polling loop and runner`
`08ab126f fix: reject non-positive reloader poll intervals`

## Review Follow-up: Positive Poll Interval

The review identified that a zero interval makes `Task.Delay` complete synchronously, preventing the host's `ExecuteAsync` path from progressing to the consumer loop. `RulesReloader` now resolves the missing-value default to 30 seconds and throws `ArgumentOutOfRangeException` from its constructor for configured zero or negative intervals, before either loop starts. `PollJitter` retains the intentional behavior that null selects 3 seconds and non-positive values disable jitter; this is documented on `ReloaderOptions`. The Task 6 brief's implementation guidance was updated to match.

### TDD RED (verbatim test result)

Command:

```powershell
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~RulesReloaderOptionsTests" --no-restore -nologo
```

```text
[xUnit.net 00:00:00.44]     NRules.Persistence.EntityFrameworkCore.Tests.Unit.RulesReloaderOptionsTests.Constructor_rejects_non_positive_poll_interval(seconds: -1) [FAIL]
[xUnit.net 00:00:00.46]     NRules.Persistence.EntityFrameworkCore.Tests.Unit.RulesReloaderOptionsTests.Constructor_rejects_non_positive_poll_interval(seconds: 0) [FAIL]
  失败 NRules.Persistence.EntityFrameworkCore.Tests.Unit.RulesReloaderOptionsTests.Constructor_rejects_non_positive_poll_interval(seconds: -1) [91 ms]
  错误消息:
   Assert.Throws() Failure: No exception was thrown
Expected: typeof(System.ArgumentOutOfRangeException)
  堆栈跟踪:
     at NRules.Persistence.EntityFrameworkCore.Tests.Unit.RulesReloaderOptionsTests.Constructor_rejects_non_positive_poll_interval(Int32 seconds) in E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\Unit\RulesReloaderOptionsTests.cs:line 17
   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
   at System.Reflection.MethodBaseInvoker.InvokeDirectByRefWithFewArgs(Object obj, Span`1 copyOfArgs, BindingFlags invokeAttr)
 失败 NRules.Persistence.EntityFrameworkCore.Tests.Unit.RulesReloaderOptionsTests.Constructor_rejects_non_positive_poll_interval(seconds: 0) [1 ms]
 错误消息:
   Assert.Throws() Failure: No exception was thrown
Expected: typeof(System.ArgumentOutOfRangeException)
堆栈跟踪:
     at NRules.Persistence.EntityFrameworkCore.Tests.Unit.RulesReloaderOptionsTests.Constructor_rejects_non_positive_poll_interval(Int32 seconds) in E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\Unit\RulesReloaderOptionsTests.cs:line 17
   at InvokeStub_RulesReloaderOptionsTests.Constructor_rejects_non_positive_poll_interval(Object, Span`1)
   at System.Reflection.MethodBaseInvoker.InvokeWithOneArg(Object obj, BindingFlags invokeAttr, Binder binder, Object[] parameters)
失败!  - 失败:     2，通过:     0，已跳过:     0，总计:     2，持续时间: 98 ms - NRules.Persistence.EntityFrameworkCore.Tests.dll (net10.0)
```

### Final verification output (verbatim)

Focused reloader tests:

Command:

```powershell
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~ReloaderRunnerTests|FullyQualifiedName~ReloaderPollingTests|FullyQualifiedName~RulesReloaderOptionsTests" --no-restore -nologo
```

```text
NRules.RuleModel -> E:\GitHub\NRules\src\NRules\NRules.RuleModel\bin\Debug\netstandard2.1\NRules.RuleModel.dll
NRules.Json -> E:\GitHub\NRules\src\NRules\NRules.Json\bin\Debug\netstandard2.1\NRules.Json.dll
NRules -> E:\GitHub\NRules\src\NRules\NRules\bin\Debug\netstandard2.1\NRules.dll
NRules.Persistence.EntityFrameworkCore -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.dll
NRules.Fluent -> E:\GitHub\NRules\src\NRules\NRules.Fluent\bin\Debug\netstandard2.1\NRules.Fluent.dll
NRules.Persistence.EntityFrameworkCore.Tests -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll
E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll (.NETCoreApp,Version=v10.0)的测试运行
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:     4，已跳过:     0，总计:     4，持续时间: 10 s - NRules.Persistence.EntityFrameworkCore.Tests.dll (net10.0)
```

Full persistence test project:

Command:

```powershell
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore -nologo
```

```text
NRules.RuleModel -> E:\GitHub\NRules\src\NRules\NRules.RuleModel\bin\Debug\netstandard2.1\NRules.RuleModel.dll
NRules -> E:\GitHub\NRules\src\NRules\NRules\bin\Debug\netstandard2.1\NRules.dll
NRules.Json -> E:\GitHub\NRules\src\NRules\NRules.Json\bin\Debug\netstandard2.1\NRules.Json.dll
NRules.Persistence.EntityFrameworkCore -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.dll
NRules.Fluent -> E:\GitHub\NRules\src\NRules\NRules.Fluent\bin\Debug\netstandard2.1\NRules.Fluent.dll
NRules.Persistence.EntityFrameworkCore.Tests -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll
E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll (.NETCoreApp,Version=v10.0)的测试运行
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:    20，已跳过:     0，总计:    20，持续时间: 9 s - NRules.Persistence.EntityFrameworkCore.Tests.dll (net10.0)
```

Warning-as-error build:

Command:

```powershell
dotnet build src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore -warnaserror -nologo
```

```text
NRules.RuleModel -> E:\GitHub\NRules\src\NRules\NRules.RuleModel\bin\Debug\netstandard2.1\NRules.RuleModel.dll
NRules -> E:\GitHub\NRules\src\NRules\NRules\bin\Debug\netstandard2.1\NRules.dll
NRules.Json -> E:\GitHub\NRules\src\NRules\NRules.Json\bin\Debug\netstandard2.1\NRules.Json.dll
NRules.Persistence.EntityFrameworkCore -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.dll
NRules.Fluent -> E:\GitHub\NRules\src\NRules\NRules.Fluent\bin\Debug\netstandard2.1\NRules.Fluent.dll
NRules.Persistence.EntityFrameworkCore.Tests -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:03.38
```

## Re-review Follow-up: Subscriber Exceptions After Publication

`RuleSessionFactoryProvider.Swap` atomically updates `Current` and completes readiness before synchronously invoking `FactorySwapped`. The reloader now catches a swap exception only when `Current` is reference-equal to the compiled factory, logs it explicitly as a `FactorySwapped` handler failure, and continues the successful reload path so `_lastFingerprint` advances. If the provider did not publish that factory, the exception still reaches the existing rebuild-failure handler. The provider/event API contract is unchanged.

The regression test uses the real provider, a valid compiled rule, a throwing event handler and an unchanged fingerprint. It verifies readiness and `Current` point to the published factory; after the next poll the fingerprint has been checked again but rules are not reloaded or swapped again; the handler failure is logged and is not mislabeled as a rebuild failure.

### TDD RED (verbatim test result)

Command:

```powershell
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~ReloaderSubscriberFailureTests" --no-restore -nologo
```

```text
NRules.RuleModel -> E:\GitHub\NRules\src\NRules\NRules.RuleModel\bin\Debug\netstandard2.1\NRules.RuleModel.dll
NRules.Json -> E:\GitHub\NRules\src\NRules\NRules.Json\bin\Debug\netstandard2.1\NRules.Json.dll
NRules -> E:\GitHub\NRules\src\NRules\NRules\bin\Debug\netstandard2.1\NRules.dll
NRules.Persistence.EntityFrameworkCore -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.dll
NRules.Fluent -> E:\GitHub\NRules\src\NRules\NRules.Fluent\bin\Debug\netstandard2.1\NRules.Fluent.dll
NRules.Persistence.EntityFrameworkCore.Tests -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll
E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll (.NETCoreApp,Version=v10.0)的测试运行
总共 1 个测试文件与指定模式相匹配。
[xUnit.net 00:00:00.52]     NRules.Persistence.EntityFrameworkCore.Tests.Unit.ReloaderSubscriberFailureTests.Throwing_factory_swapped_handler_keeps_publication_successful_and_skips_unchanged_fingerprint [FAIL]
 失败 NRules.Persistence.EntityFrameworkCore.Tests.Unit.ReloaderSubscriberFailureTests.Throwing_factory_swapped_handler_keeps_publication_successful_and_skips_unchanged_fingerprint [204 ms]
 错误消息:
  Assert.Same() Failure: Values are not the same instance
Expected: SessionFactory { ActionInterceptor = null, DependencyResolver = DependencyResolver { }, Events = EventAggregator { TraceEnabled = False } }
Actual:   SessionFactory { ActionInterceptor = null, DependencyResolver = DependencyResolver { }, Events = EventAggregator { TraceEnabled = False } }
 堆栈跟踪:
    at NRules.Persistence.EntityFrameworkCore.Tests.Unit.ReloaderSubscriberFailureTests.Throwing_factory_swapped_handler_keeps_publication_successful_and_skips_unchanged_fingerprint() in E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\Unit\ReloaderSubscriberFailureTests.cs:line 67
--- End of stack trace from previous location ---

失败!  - 失败:     1，通过:     0，已跳过:     0，总计:     1，持续时间: 204 ms - NRules.Persistence.EntityFrameworkCore.Tests.dll (net10.0)
```

### Final verification output (verbatim)

Focused reloader tests:

Command:

```powershell
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~Reloader" --no-restore -nologo
```

```text
NRules.RuleModel -> E:\GitHub\NRules\src\NRules\NRules.RuleModel\bin\Debug\netstandard2.1\NRules.RuleModel.dll
NRules -> E:\GitHub\NRules\src\NRules\NRules\bin\Debug\netstandard2.1\NRules.dll
NRules.Json -> E:\GitHub\NRules\src\NRules\NRules.Json\bin\Debug\netstandard2.1\NRules.Json.dll
NRules.Persistence.EntityFrameworkCore -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.dll
NRules.Fluent -> E:\GitHub\NRules\src\NRules\NRules.Fluent\bin\Debug\netstandard2.1\NRules.Fluent.dll
NRules.Persistence.EntityFrameworkCore.Tests -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll
E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll (.NETCoreApp,Version=v10.0)的测试运行
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:     5，已跳过:     0，总计:     5，持续时间: 10 s - NRules.Persistence.EntityFrameworkCore.Tests.dll (net10.0)
```

Full persistence test project:

Command:

```powershell
dotnet test src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore -nologo
```

```text
NRules.RuleModel -> E:\GitHub\NRules\src\NRules\NRules.RuleModel\bin\Debug\netstandard2.1\NRules.RuleModel.dll
NRules -> E:\GitHub\NRules\src\NRules\NRules\bin\Debug\netstandard2.1\NRules.dll
NRules.Json -> E:\GitHub\NRules\src\NRules\NRules.Json\bin\Debug\netstandard2.1\NRules.Json.dll
NRules.Persistence.EntityFrameworkCore -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.dll
NRules.Fluent -> E:\GitHub\NRules\src\NRules\NRules.Fluent\bin\Debug\netstandard2.1\NRules.Fluent.dll
NRules.Persistence.EntityFrameworkCore.Tests -> E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll
E:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll (.NETCoreApp,Version=v10.0)的测试运行
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:    21，已跳过:     0，总计:    21，持续时间: 8 s - NRules.Persistence.EntityFrameworkCore.Tests.dll (net10.0)
```

Warning-as-error build:

Command:

```powershell
dotnet build src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\NRules.Persistence.EntityFrameworkCore.Tests.csproj --no-restore -warnaserror -nologo
```

```text
NRules.RuleModel -> e:\GitHub\NRules\src\NRules\NRules.RuleModel\bin\Debug\netstandard2.1\NRules.RuleModel.dll
NRules.Json -> e:\GitHub\NRules\src\NRules\NRules.Json\bin\Debug\netstandard2.1\NRules.Json.dll
NRules -> e:\GitHub\NRules\src\NRules\NRules\bin\Debug\netstandard2.1\NRules.dll
NRules.Persistence.EntityFrameworkCore -> e:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.dll
NRules.Fluent -> e:\GitHub\NRules\src\NRules\NRules.Fluent\bin\Debug\netstandard2.1\NRules.Fluent.dll
NRules.Persistence.EntityFrameworkCore.Tests -> e:\GitHub\NRules\src\NRules\NRules.Persistence.EntityFrameworkCore.Tests\bin\Debug\net10.0\NRules.Persistence.EntityFrameworkCore.Tests.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:05.51
```

Follow-up fix commit: `821b9530 fix: handle reloader swap subscriber failures`
