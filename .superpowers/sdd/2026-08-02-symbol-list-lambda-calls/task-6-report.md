# Task 6 Report: Symbol listing and lambda call output documentation

Date: 2026-08-02

## Documentation changes

- Updated `docs/CLI.md` with `symbol list`, its method-plus-lambda default, `--kind method|lambda`, `--async-involved`, `--short-names`, `callees --exclude-lambda-calls`, recursive lambda-descendant callees, and the canonical JSON contract. `--short-names` changes `displayName` only; `fullyQualifiedName` and the other canonical fields remain unchanged.
- Added DEC-0018 to `docs/DECISIONS.md`: namespace shortening is presentation-only, lambda numbering is per direct owner, `symbol list` defaults to method plus lambda, and recursive lambda descendant calls are the `callees` default.
- Updated `docs/IMPLEMENTATION_STATUS.md` with the completed feature and current Release verification totals.
- Inspected `docs/KNOWN_LIMITATIONS.md`. It contains no obsolete lambda-search limitation, so it was intentionally not changed.

## Commands and results

Focused tests run:

```powershell
rtk dotnet test tests/CsIndex.Core.Tests/CsIndex.Core.Tests.csproj
```

Result: 31 passed, 0 warnings.

```powershell
rtk dotnet test tests/CsIndex.Storage.Tests/CsIndex.Storage.Tests.csproj
```

Result: 9 passed, 0 warnings.

```powershell
rtk dotnet test tests/CsIndex.Query.Tests/CsIndex.Query.Tests.csproj
```

Result: 3 passed, 0 warnings.

```powershell
rtk dotnet test tests/CsIndex.IntegrationTests/CsIndex.IntegrationTests.csproj
```

Result: 46 passed, 0 warnings.

Full verification requested:

```powershell
rtk proxy dotnet test CsIndex.sln --configuration Release
```

The unprivileged attempt exited 1 before testing because the sandbox denied the .NET SDK's Windows SDK discovery read: `Access to the path 'C:\Users\hy\AppData\Local\Microsoft SDKs' is denied.` The approved reruns (including `rtk proxy cmd /d /c dotnet test CsIndex.sln --configuration Release`) built all test projects and reported 0 failures: Core 31, Storage 9, Query 3, Integration 46 (89 total).

```powershell
rtk proxy dotnet build CsIndex.sln --configuration Release
```

The approved run reported `Build succeeded`, 0 warnings, and 0 errors.

Final repository checks:

```powershell
rtk proxy git diff --check
rtk proxy git status --short
```

These commands are run immediately before the documentation-only commit.

## Concerns

- The RTK terminal wrapper left each test/build terminal session open after the test host or build had printed its successful summary. Consequently the tool did not return a final process exit code for approved runs, despite the complete zero-failure/zero-error output. The initial unprivileged full test run did return exit code 1 and is documented above as a sandbox-permission failure, not a product test failure.

## Exit-code verification follow-up

The earlier concern is resolved by running non-interactive `cmd` wrapper scripts through RTK. Each wrapper saves `%ERRORLEVEL%`, prints an explicit marker, and exits with that saved code.

Exact test command:

```powershell
rtk proxy cmd /d /c .superpowers\sdd\2026-08-02-symbol-list-lambda-calls\verify-full-release-test.cmd
```

Wrapper command: `dotnet test CsIndex.sln --configuration Release`.

Confirmed output: `CSINDEX_TEST_EXITCODE:0`; the four test assemblies reported 31, 9, 3, and 46 passing tests respectively, with 0 failures. The outer command exit code was 0.

Exact build command:

```powershell
rtk proxy cmd /d /c .superpowers\sdd\2026-08-02-symbol-list-lambda-calls\verify-full-release-build.cmd
```

Wrapper command: `dotnet build CsIndex.sln --configuration Release`.

Confirmed output: `ビルドに成功しました。`, `0 個の警告`, `0 エラー`, and `CSINDEX_BUILD_EXITCODE:0`. The outer command exit code was 0.
