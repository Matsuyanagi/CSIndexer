# Taskfile Build and Publish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a root `taskfile.yml` that builds the solution and publishes the CLI as a self-contained Windows x64 multi-file folder.

**Architecture:** Keep orchestration in one Taskfile v3 file. The `build` task delegates to `dotnet build` for the solution, while `publish` delegates directly to `dotnet publish` for the CLI project without a redundant dependency on `build`.

**Tech Stack:** Task CLI 3.50.0 / Taskfile schema v3, .NET 10 SDK, MSBuild

## Global Constraints

- Create the entrypoint as repository-root `taskfile.yml`.
- Required task names are exactly `build` and `publish`.
- Default configuration is `Release`.
- Default runtime identifier is `win-x64`.
- Publish `src/CsIndex.Cli/CsIndex.Cli.csproj` with `--self-contained true`.
- Do not enable `PublishSingleFile`; publish must retain dependency files.
- Default publish directory is `artifacts/publish/win-x64`.
- `CONFIGURATION`, `RID`, and `PUBLISH_DIR` must be overridable Task variables.
- Do not add a publish profile, task-level cache, test task, packaging task, or multi-RID loop.

---

### Task 1: Root build and publish Taskfile

**Files:**
- Create: `taskfile.yml`
- Reference: `CsIndex.sln`
- Reference: `src/CsIndex.Cli/CsIndex.Cli.csproj`
- Reference: `docs/superpowers/specs/2026-07-22-taskfile-build-publish-design.md`

**Interfaces:**
- Consumes: Task CLI variable overrides `CONFIGURATION=<value>`, `RID=<value>`, and `PUBLISH_DIR=<path>`
- Produces: `task build` and `task publish`; default publish artifact `artifacts/publish/win-x64/csindex.exe` plus runtime/dependency files

- [ ] **Step 1: Verify the Taskfile is absent**

Run:

```powershell
rtk task --list
```

Expected: non-zero exit and a message that no Taskfile was found. If a Taskfile has appeared since planning, stop and review it rather than overwriting it.

- [ ] **Step 2: Create the minimal Taskfile v3 implementation**

Create `taskfile.yml` with exactly this content:

```yaml
version: '3'

vars:
  CONFIGURATION: '{{.CONFIGURATION | default "Release"}}'
  RID: '{{.RID | default "win-x64"}}'
  PUBLISH_DIR: '{{.PUBLISH_DIR | default (printf "artifacts/publish/%s" .RID)}}'

tasks:
  build:
    desc: Build the solution
    cmds:
      - dotnet build CsIndex.sln --configuration {{.CONFIGURATION}}

  publish:
    desc: Publish the CLI as a self-contained folder
    cmds:
      - >-
        dotnet publish src/CsIndex.Cli/CsIndex.Cli.csproj
        --configuration {{.CONFIGURATION}}
        --runtime {{.RID}}
        --self-contained true
        --output "{{.PUBLISH_DIR}}"
```

- [ ] **Step 3: Validate discovery and the default commands without executing them**

Run:

```powershell
rtk task --list
rtk task --dry build
rtk task --dry publish
```

Expected:

- list output contains `build` and `publish` with their descriptions;
- build dry-run contains `dotnet build CsIndex.sln --configuration Release`;
- publish dry-run contains the CLI project, `--configuration Release`, `--runtime win-x64`, `--self-contained true`, and `--output "artifacts/publish/win-x64"`.

- [ ] **Step 4: Validate variable overrides without producing artifacts**

Run:

```powershell
rtk task --dry build CONFIGURATION=Debug
rtk task --dry publish CONFIGURATION=Debug RID=win-x64 PUBLISH_DIR=artifacts/custom-publish
```

Expected:

- build dry-run uses `--configuration Debug`;
- publish dry-run uses `--configuration Debug`, the supplied RID, and `--output "artifacts/custom-publish"`.

- [ ] **Step 5: Execute the default build task**

Run:

```powershell
rtk task build
```

Expected: Task exits 0; `dotnet build` reports 9 projects, 0 errors, and 0 warnings.

- [ ] **Step 6: Execute and inspect the default publish task**

Run:

```powershell
rtk task publish
rtk powershell -NoProfile -Command "Test-Path -LiteralPath 'artifacts/publish/win-x64/csindex.exe'"
rtk powershell -NoProfile -Command "Test-Path -LiteralPath 'artifacts/publish/win-x64/coreclr.dll'"
rtk powershell -NoProfile -Command "Test-Path -LiteralPath 'artifacts/publish/win-x64/csindex.dll'"
rtk powershell -NoProfile -Command "(Get-ChildItem -LiteralPath 'artifacts/publish/win-x64' -File).Count"
```

Expected:

- publish exits 0;
- all three `Test-Path` commands print `True`;
- file count is greater than 1, proving a normal dependency folder rather than a single-file artifact;
- `coreclr.dll` confirms that the .NET runtime is included.

- [ ] **Step 7: Smoke-test the published executable**

Run:

```powershell
rtk artifacts/publish/win-x64/csindex.exe --help
```

Expected: exit 0 and CSIndexer help text.

- [ ] **Step 8: Inspect the complete change**

Run:

```powershell
rtk git diff --check
rtk git diff -- taskfile.yml
rtk git status --short
```

Expected: no whitespace errors; only `taskfile.yml` is an uncommitted tracked change; `artifacts/` remains ignored.

- [ ] **Step 9: Commit the Taskfile**

```powershell
rtk git add taskfile.yml
rtk git commit -m "build: add Taskfile build and publish tasks"
```

- [ ] **Step 10: Verify the committed state**

Run:

```powershell
rtk git status --short --branch
rtk task --list
```

Expected: clean `feature/async_info` worktree and both required tasks listed.
