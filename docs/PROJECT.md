# Translate Live project guide

Last updated: 2026-09-11

## Product scope

Translate Live is a Windows 11 desktop application for real-time bilingual captions, class-session recording, review, and summary generation. The repository is an open-source development preview maintained by QuBe and derived from `SakiRinn/LiveCaptions-Translator`.

Current scope:

- Windows Live Captions integration for computer audio and microphone capture;
- real-time translation through cloud or self-hosted providers;
- session-based local transcript storage and CRUD operations;
- an independent model configuration for class summaries;
- a configurable subtitle overlay;
- local credential protection and recoverable settings migration.

The project does not currently provide an installer, code signing, or an independent ASR engine.

## Baselines

| Baseline | Status | Source |
|---|---|---|
| Software | Confirmed | `LiveCaptionsTranslator.sln`, `LiveCaptionsTranslator.csproj`, `src/` |
| Hardware/interfaces | Not applicable | Windows audio, UI Automation, and Live Captions are operating-system dependencies |
| Functional requirements | Found—authority is project-maintainer direction | README, this guide, and existing automated tests |
| Shared architecture profile | Not assessed | No external profile is bound to this repository |

## Technology

| Area | Current implementation |
|---|---|
| UI | C#, WPF, WPF-UI 4.0.1 |
| Target framework | `net10.0-windows` |
| SDK | .NET SDK 10.0.400, pinned by `global.json` |
| Platforms | win-x64 and win-arm64 configured; win-x64 is the verified release target |
| Speech-to-text | Windows Live Captions through UI Automation |
| Local data | Microsoft.Data.Sqlite 10.0.11 |
| Export | CsvHelper 33.0.1 |
| Credentials | Windows DPAPI `CurrentUser` |
| Tests | MSTest plus a Windows integration smoke-test executable |

## Entry points and runtime connection

| Component | Present | Build included | Runtime connection |
|---|---|---|---|
| WPF application | `src/App.xaml(.cs)` | Confirmed by the main project | Confirmed by WPF startup |
| Main workspace | `src/pages/LectureWorkspacePage.*` | Confirmed | Created by `MainWindow` navigation |
| Live Captions capture | `src/utils/LiveCaptionsHandler.cs` | Confirmed | Started by `Translator.ConnectLiveCaptionsAsync` |
| Translation queue | `src/models/TranslationTaskQueue.cs` | Confirmed | Driven by `Translator.TranslateLoop` |
| History storage | `src/utils/HistoryLogger.cs` and repository classes | Confirmed | Initialized by `App.OnStartup` |
| Summary service | `src/services/LectureSummaryService.cs` | Confirmed | Invoked from the workspace summary action |
| Unit tests | `tests/LiveCaptionsTranslator.Tests` | Confirmed by the solution | Executed by `scripts/test.ps1` and CI |
| Windows smoke tests | `tests/LiveCaptionsTranslator.SmokeTests` | Confirmed by the solution | Manually run on a Windows desktop session |

## Runtime flow

~~~text
App startup
  +-- resolve isolated or LocalAppData paths
  +-- load ordinary settings
  +-- load/migrate DPAPI-protected credentials
  +-- initialize SQLite and recovery journal
  +-- show the WPF shell
  +-- start capture, translation, and display workers

Windows audio or microphone
  -> Windows Live Captions
  -> UI Automation capture
  -> caption stabilization
  -> translation task queue
  -> selected provider
  -> workspace and overlay
  -> session-oriented SQLite history
  -> optional independent summary model
~~~

## Source boundaries

- `src/apis/`: translation-provider clients, request/response parsing, and native API declarations.
- `src/services/`: class sessions, caption sources, microphone control, summaries, and orchestration.
- `src/models/` and `src/viewmodels/`: settings, queue state, persistent records, and UI state.
- `src/pages/`, `src/windows/`, and `src/controls/`: WPF presentation and bindings.
- `src/utils/`: application paths, storage, diagnostics, Live Captions lifecycle, and text policies.
- `tests/`: isolated logic tests and Windows-only integration checks.

Generated, local, or protected areas such as `bin/`, `obj/`, `artifacts/`, `.artifacts/`, `.tools/`, `.m0-validation/`, and runtime data directories are not source.

## Data and security boundaries

Production data is stored under `%LOCALAPPDATA%\LectureCopilot.Dev` for compatibility with earlier local builds.

- `Data/setting.json` contains ordinary settings only.
- `Data/credentials.dat` and its backup contain DPAPI `CurrentUser` ciphertext.
- `Data/translation_history.db` contains personal transcripts, translations, summaries, and session metadata.
- `Logs/app-*.jsonl` must contain metadata-only diagnostics.
- `Backups/` and `Recovery/` must never be committed or shipped.

Automated UI and integration tests must set `LECTURE_COPILOT_DATA_ROOT` to a temporary directory. Production credentials and classroom databases are not valid test inputs.

## Live Captions integration

Translate Live keeps the Windows Live Captions window rendered but parks it outside the desktop and removes it from the taskbar. Fully hiding the window can suspend its UI Automation caption tree on some Windows versions.

The application distinguishes:

- **ready**: the Live Captions process and controls are available;
- **working**: the user has started a class and Windows is actively capturing;
- **disconnected**: the system window is unavailable and requires explicit reconnection.

Closing the app restores a pre-existing user-owned Live Captions window or terminates only the instance started by Translate Live.

## Build and verification

~~~powershell
./scripts/build.ps1
./scripts/test.ps1
./scripts/publish-dev.ps1
~~~

WPF build operations must stay serial with `-m:1 -nodeReuse:false`. Dependency restoration must use the committed lock files.

The current local baseline has 105 passing unit tests. Windows audio, microphone, UI Automation, and window-lifecycle behavior require a real desktop-session smoke test in addition to compilation.

## Upstream and license

- Upstream repository: <https://github.com/SakiRinn/LiveCaptions-Translator>
- Upstream release line represented by the imported source: `v1.7.1300.1822`
- Upstream release commit shown by GitHub: `d6d8394`
- License: Apache License 2.0
- Upstream authors: SakiRinn and other contributors
- Translate Live modifications and maintenance: QuBe

The imported working tree did not retain its original Git metadata, so the exact historical ancestry cannot be proven solely from the local repository. Attribution and the upstream link are therefore kept explicitly in the README, application, project metadata, and license.

## Public release blockers

- produce a signed installer or clearly document unsigned-build behavior;
- validate ARM64 separately before publishing ARM64 binaries;
- run CI from a clean GitHub-hosted Windows environment;
- create a repeatable release workflow only after the executable naming and packaging policy is finalized;
- replace any local-only artwork with assets whose redistribution rights are explicit.
