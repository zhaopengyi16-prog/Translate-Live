# Translate Live project guide

Last updated: 2026-09-14

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

- The fixed live-caption surface displays the current source and provisional translation. Provisional updates do not create classroom timeline rows or SQLite records. The old separate Draft badge is not shown; history virtualization and reading anchors remain active.
- `CaptionRecordingPolicy` admits an identity-resolved, punctuated sentence after a forward successor or 800 ms of unchanged observations. That interval affects classroom recording only; translation can start before admission. Removing punctuation or revising the source restarts this recording decision. Time never proves that equal text is a new occurrence.
- Ending a class flushes the latest unrecorded source, including unfinished words. An unfinished record is marked in the current UI; the marker is transient because this patch does not change the database schema. No punctuation or translation is invented. A successful provisional translation is reused; otherwise the source remains available with no translation.
- Accepted source records are saved before provider completion. Empty translations are allowed only by the capture snapshot path; normal manual create/edit validation remains strict. The same provider request can be promoted from provisional to final without cancellation and resubmission. Completed records keep revision authorization for the active class even after the small text-matching ledger is pruned.
- `SessionId`, capture epoch and revision are checked at queue acceptance, live display, history projection and context application. Stop closes translation ingress and waits for source writes and worker/persistence completion before changing classroom ownership. Shutdown retains the recovery marker on timeout or a detected history-write failure.
- A growing unpunctuated draft may reopen the immediately preceding final identity only when the prior window tail and retained ledger agree, no completed sentence precedes the draft in the new snapshot, and the existing long lexical-prefix rule proves strict growth. Appended different sentences and utterances beginning with a new short draft remain separate. Intermediate drafts are retained in the bounded revision ledger, so their post-finalization replay and punctuation-only rollback cannot create a new identity. Re-finalization refreshes the ledger with the actual revision number so old final snapshots cannot restore stale text. This remains a heuristic for an ASR source without native sentence IDs.
- Recognition drafts, translation submissions, provider results, workspace rows, overlay text, and persisted records carry the same stable segment identity. A revision can update that segment in place; it cannot create a second utterance or overwrite a newer revision.
- Each logical sentence fixes `CapturedAt` at first recognition. Revisions, provider completion, UI projection, and SQLite updates retain that value, while the stable recognition `Sequence` controls display order. Translation completion time is not used as the sentence timestamp.
- Structured persistence is keyed by `(SessionId, SegmentId)`. A newer revision updates that row in place, a stale revision is rejected again at the storage boundary, and a classroom reset clears the in-memory identity map only after queued persistence has drained.
- Text equality is not a global identity rule. Repeated snapshots for one segment are idempotent. A separately spoken identical sentence receives a distinct `SegmentId` only when a draft trajectory or position-supported forward append proves a new occurrence; an isolated `A -> B -> A` without that evidence remains bounded and pending.
- The identity resolver aligns every completed position in the whole accessibility snapshot one-to-one against the prior window and bounded history. Matching does not stop at the first changed row, so a strongly revised interior sentence cannot discard or replay the still-valid suffix. Same-slot and logical-neighbor anchors classify that changed row as a revision, while rotation and non-monotonic reorder such as `[A,B,C] -> [B,C,A]` reuse the established occurrences before any new `SegmentId` is allocated.
- An unmatched row before already aligned historical rows is not accepted as forward speech. It remains unresolved until a later frame provides a stable position, draft trajectory, or right-edge append. This prevents a temporary interior correction from becoming a new UI/database row while preserving genuine right-edge sentences.
- The rolling identity ledger retains at most 64 recent logical sentences for two minutes and up to eight earlier text revisions per sentence. A separate ledger retains 16 recent window-to-identity mappings, while at most eight unresolved repeat candidates remain pending. Position, neighboring context, revision history, and the active draft distinguish forward append, correction, and old-window rollback. Time only evicts evidence; it never promotes a pending repeat into new speech.
- The logical speech frontier is independent from the last visible accessibility row. A recycled old row therefore cannot replace a still-growing current sentence. If a temporarily punctuated frontier disappears from the completed portion and returns as a longer draft, it keeps the original `SegmentId`, `Sequence`, and `CapturedAt` even when older rows remain visible before it.
- A long completed sentence may continue growing when Live Captions inserts temporary punctuation before speech has actually ended. Only at a position-confirmed current tail, the segmenter compares a punctuation-insensitive lexical prefix (at least five Latin words/24 characters, or eight CJK characters); this keeps comma and sentence-ending edits on the same identity without lowering the global revision threshold or merging merely similar sentences.
- Short tail corrections use a separate four-second evidence window when the current window position agrees and a lexical prefix contains at least three Latin words or four CJK characters. Strict growth reopens the same identity; a temporary shortening must also retain at least 60% of the current tail. This window classifies recognizer revisions only and never delays translation output.
- Directional and zero-width format marks are removed during caption normalization. Completed occurrences are matched one-to-one, so two genuinely established equal utterances remain two identities when their window later reorders, while one recycled occurrence cannot multiply into several rows.
- A final-admission gate remains after source identity resolution, but it now performs only `SegmentId` and `Revision` idempotency. It never infers identity from equal text, elapsed time, draft revision numbers, or intervening sentences; all such evidence is resolved once at the snapshot source.
- An equal sentence with no draft trajectory or trustworthy right-edge append evidence remains an explicit bounded pending candidate. A later strict draft growth can promote that candidate to a new identity using its first observation time. Merely waiting does not promote it. A full retained window followed by a different sentence and then an equal right-edge occurrence is accepted as forward append evidence.
- The classroom session is captured together with each translation submission. A delayed provider result therefore remains bound to the classroom in which its caption was observed instead of consulting whichever classroom happens to be active later.
- When SQLite history loading overlaps a real-time persistence callback, the database row projection is rebound to the stable live `SegmentId`. The workspace removes the temporary projected identity rather than displaying the same database row twice.
- Recognition finalization, translation submission, and visual line wrapping are separate decisions. Closing punctuation belongs to its sentence; abbreviations, initials, decimals, version numbers, URLs, and truncated Live Captions windows are protected from eager finalization.
- `LiveCaptionSegmentationThresholds.DraftQuietTranslationDelay` is currently 450 ms. It permits a stable draft to enter translation after a short measured pause; it does not mark that draft final or impose a fixed wait on already-final sentences.
- The production resolver uses `splitLongDrafts: false`: an unpunctuated draft cannot become classroom sentences merely by crossing 72/120/180 characters. The legacy segmenter option remains available for callers that explicitly need reading units; WPF wrapping does not assign sentence identities.
- The timeline has explicit `FollowingLive`, `BrowsingHistory`, and `ReturningLive` states. Deferred follow requests recheck state before execution, and browsing preserves the first visible segment plus its viewport offset across translation and layout-height changes.

Windows Live Captions does not expose a native sentence identifier. A single isolated sentence that is textually identical to a recent sentence can therefore be ambiguous when there is no draft growth or neighboring-window evidence. Translate Live keeps that occurrence pending rather than admitting or permanently classifying it from text alone. Strict draft growth or a complete-window right-edge append can establish a new occurrence; elapsed time cannot. Pending evidence is capacity- and retention-bounded, and expiry closes it without emitting a translation or database row. This is an explicit trade-off rather than global text de-duplication.

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

The current local baseline has 185 passing automated tests. The regression suite includes the production identity resolver, whole-window one-to-one alignment beyond an interior mismatch, position-anchored radical corrections, rotation and reorder rollback, 80-sentence rolling-window stress, duplicate occurrence accounting, bounded and non-promoting pending candidates, snapshot-history capacity and expiry, final-to-draft continuation inside a full window, revision-aware translation queues, stable-time UI projection, history/live identity rebinding, overlay context exclusion, punctuation-changing final growth, short-tail continuity, stale intermediate rollback, long-reading-unit integrity, controlled provider delay, and isolated SQLite upsert behavior. A production-chain replay also proves that 18 accepted revisions of one sentence update a ten-sentence workspace and SQLite session in place instead of expanding their row counts. The smoke-test executable supports `--system-audio` for a fixed non-personal audio capture and `--timeline-ui` for the real WPF timeline control. Windows audio, microphone, UI Automation, and window-lifecycle behavior still require a real desktop-session smoke test in addition to compilation.

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
