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
  -> position-aware caption stabilization
  -> stable SessionId / SegmentId / Revision identity
  -> revision-aware translation task queue
  -> selected provider
  -> workspace and overlay
  -> session-oriented SQLite history
  -> optional independent summary model
~~~

### Subtitle-chain invariants

- A growing unpunctuated draft may reopen the immediately preceding final identity only when the prior window tail and retained ledger agree, no completed sentence precedes the draft in the new snapshot, and the existing long lexical-prefix rule proves strict growth. Appended different sentences and utterances beginning with a new short draft remain separate. Intermediate drafts are retained in the bounded revision ledger, so their post-finalization replay and punctuation-only rollback cannot create a new identity. Re-finalization refreshes the ledger with the actual revision number so old final snapshots cannot restore stale text. This remains a heuristic for an ASR source without native sentence IDs.
- Recognition drafts, translation submissions, provider results, workspace rows, overlay text, and persisted records carry the same stable segment identity. A revision can update that segment in place; it cannot create a second utterance or overwrite a newer revision.
- Each logical sentence fixes `CapturedAt` at first recognition. Revisions, provider completion, UI projection, and SQLite updates retain that value, while the stable recognition `Sequence` controls display order. Translation completion time is not used as the sentence timestamp.
- Structured persistence is keyed by `(SessionId, SegmentId)`. A newer revision updates that row in place, a stale revision is rejected again at the storage boundary, and a classroom reset clears the in-memory identity map only after queued persistence has drained.
- Text equality is not a global identity rule. Repeated snapshots for one segment are idempotent. A new draft, intervening sentence, or an append outside the short accessibility-burst window preserves a separately spoken identical sentence with a distinct `SegmentId`.
- The rolling-window ledger retains at most 24 recent logical sentences for two minutes and up to four earlier text revisions per sentence. Position, neighboring sentence context, revision history, and the active draft distinguish forward append, correction, and old-window rollback. Time only evicts evidence; it never turns an identical snapshot into proof of new speech.
- A long completed sentence may continue growing when Live Captions inserts temporary punctuation before speech has actually ended. Only at a position-confirmed current tail, the segmenter compares a punctuation-insensitive lexical prefix (at least five Latin words/24 characters, or eight CJK characters); this keeps comma and sentence-ending edits on the same identity without lowering the global revision threshold or merging merely similar sentences.
- Short tail corrections use a separate four-second evidence window when the current window position agrees and a lexical prefix contains at least three Latin words or four CJK characters. Strict growth reopens the same identity; a temporary shortening must also retain at least 60% of the current tail. This window classifies recognizer revisions only and never delays translation output.
- Windows Live Captions can repeat the same completed tail row while its accessibility layout is recycled under continuous speech. Directional and zero-width format marks are removed during caption normalization. An adjacent, exactly equal completed tail appended again within three seconds, without a new draft identity, is treated as the same accessibility row and is not translated or persisted again. The window is a classifier only; it adds no wait to normal captions.
- A second bounded final-admission gate sits after recognition segmentation and before translation, workspace projection, overlay history, and SQLite persistence. It rejects a rapid run of exact completed candidates when Live Captions assigns each recycled candidate a different local identity but supplies no fresh draft or intervening-final evidence. The admitted identity is reused for the current overlay, so the protection is upstream data classification rather than a visual-only list filter.
- The final-admission gate adds no translation delay. A newer revision of the same identity is admitted, and identical speech remains distinct when a fresh draft identity, an intervening different final, a quiet gap beyond the burst window, or a classroom reset provides forward-progress evidence. A rapid direct repeat with none of those signals is inherently ambiguous and is deliberately collapsed in favor of preventing duplicate translation and storage.
- Recognition finalization, translation submission, and visual line wrapping are separate decisions. Closing punctuation belongs to its sentence; abbreviations, initials, decimals, version numbers, URLs, and truncated Live Captions windows are protected from eager finalization.
- `LiveCaptionSegmentationThresholds.DraftQuietTranslationDelay` is currently 450 ms. It permits a stable draft to enter translation after a short measured pause; it does not mark that draft final or impose a fixed wait on already-final sentences.
- Unpunctuated speech uses centrally defined 72/120/180-character minimum, preferred, and hard reading-unit boundaries. Commas, semicolons, colons, then word boundaries are preferred; CJK text falls back to a deterministic character boundary. The pieces retain all source text and stable sequence identity.
- The timeline has explicit `FollowingLive`, `BrowsingHistory`, and `ReturningLive` states. Deferred follow requests recheck state before execution, and browsing preserves the first visible segment plus its viewport offset across translation and layout-height changes.

Windows Live Captions does not expose a native sentence identifier. A single isolated sentence that is textually identical to a recent sentence can therefore be ambiguous when there is no draft growth or neighboring-window evidence. While bounded evidence exists, Translate Live treats that isolated frame as rollback. A rapid adjacent exact append without draft evidence is also treated as accessibility recycling; a repeated utterance is retained when a new draft grows, a different sentence intervenes, or the append occurs outside the three-second burst. This is a deliberate, bounded trade-off rather than global text de-duplication.

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

The current local baseline has 167 passing automated tests. The regression suite includes the production caption segmenter, bounded final-admission gate, revision-aware translation queue, stable-time UI projection, overlay context exclusion, punctuation-changing final growth, short-tail continuity, final-to-draft continuity, stale intermediate-draft rollback, punctuation-only rollback, accessibility duplicate-burst suppression within one snapshot and across successive snapshots, distinct-identity same-text burst suppression before translation/UI/SQLite, invisible-format normalization, genuine-repeat counterexamples, rapid continuous long-speech replay, and isolated SQLite upsert behavior. The smoke-test executable also supports `--system-audio` for a fixed non-personal audio capture and `--timeline-ui` for the real WPF timeline control. Windows audio, microphone, UI Automation, and window-lifecycle behavior still require a real desktop-session smoke test in addition to compilation.

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
