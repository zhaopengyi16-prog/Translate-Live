# Translate Live build and release guide

Last updated: 2026-09-14

## Release status

The repository is ready for public source review. It does not yet claim a signed or generally available binary release.

Current public version: `0.1.0.0`

### Caption replay amplification fix (working tree based on `9b669670`)

The current classroom database was inspected read-only using counts and text-shape relationships only; no caption text or credential was printed. The newest completed classroom contained no byte-identical source rows, but 20 of 33 rows belonged to seven near-revision clusters. This showed that progressive recognizer versions were acquiring new logical identities before they reached translation, WPF projection and SQLite.

The source review found four independent amplification paths in `LiveCaptionSegmenter`: an old sentence copied to the physical right edge was treated as trusted new speech; a final alternating between completed and draft forms could create a fresh identity on every cycle; strict short-tail growth after four seconds was declared new speech; and an unchanged but still-visible window did not refresh the retention evidence used by the recent identity ledger. A fifth pattern exposed adjacent old/new final rows for one progressive utterance. The repair removes right-edge placement as proof of a repeat, holds text-only repeats as bounded pending candidates, reuses the retained frontier for direct strict growth, collapses an adjacent strict-growth revision into that frontier, refreshes visible ledger evidence, and keeps `SegmentId`, `Sequence` and `CapturedAt` stable. A genuine repeat that establishes a new short-draft trajectory remains a separate identity.

Verification on a normal Windows x64 desktop with SDK 10.0.400 and isolated test data:

- before the repair, the completed/draft/final loop for one sentence produced **21 distinct identities** in 20 cycles; after the repair, the 40-cycle production-chain replay finishes with **1 identity / 1 provider request / 1 workspace row / 1 SQLite row**;
- all four initial failure tests failed before the code change and now pass: copied right-edge final, visible-ledger expiry, delayed short-tail growth, and adjacent progressive finals;
- an alternating two-voice synthetic replay with rolling-window rotations, controlled provider delays and 100 unchanged frames finishes with **12 identities / 12 requests / 12 workspace rows / 12 SQLite rows**;
- the full solution test run passes **256 tests, 0 failed, 0 skipped**;
- locked restore and Release build pass with **0 errors** and 304 existing nullable warnings;
- the real WPF timeline smoke passes follow cancellation, reading-anchor preservation, draft height, final visibility, font/resize and return-to-live checks;
- the Windows `--multi-voice-audio` smoke used two installed SAPI voices with a 300 ms overlap. Live Captions exposed 20 changed snapshots and 9 accepted revision events under 4 logical identities; microphone Off → On, caption-node rebind, restoration to Off and process cleanup all passed. Recognition merged some of the six intended spoken sentences, so this run validates non-amplifying identity behavior rather than ASR transcription accuracy;
- the published executable reached `startup.ui-ready` from the current output directory, accepted a normal window close, exited with code 0, removed its recovery marker and left no app-owned process;
- self-contained uncompressed win-x64 output: **409 files, 155,614,306 bytes** in `artifacts/dev-win-x64`;
- entry executable SHA-256: `d5ebb91eb427cdb12dcb079659e94ab31ff362be1130ff1d5b2f7f1beaf18cfb`;
- application assembly SHA-256: `dfe74b6b2f342adb4ed24a862471a063ad1b60a4b4c0ab5032f7319aabc71abf`.

The repair does not rewrite or text-deduplicate existing classroom history. Windows Live Captions still supplies flattened text without a native occurrence identifier, so an isolated same-text repeat with no draft trajectory remains intentionally pending instead of being guessed as either new speech or replay.

### First microphone caption startup fix (working tree based on `9b669670`)

The earlier classroom flow enabled the microphone and then called the general reset/resume path. `LiveCaptionSegmenter.Reset()` intentionally treats its next non-empty snapshot as an old-window seed, so the first microphone sentence was suppressed when it was the first snapshot after that second reset. The startup flow now captures the readable Live Captions baseline before changing the input, prepares the new capture epoch and classroom once, rebinds the caption node after the Windows microphone operation, and resumes without another reset. A second startup defect was found during Windows verification: on a fresh ready surface Windows may not create `CaptionsTextBlock` until speech occurs, so the baseline reader reported `captions-node-unavailable` and stopped before microphone activation. Readiness now uses positive shell evidence to classify that exact state as a confirmed empty baseline while preserving failures for preparation prompts, missing controls, stale nodes and UI Automation errors.

Verified on a normal Windows x64 desktop with .NET SDK 10.0.400 and `LECTURE_COPILOT_DATA_ROOT` set to a new temporary directory:

- the original first-sentence regression failed because the first post-enable snapshot produced zero segments; the repaired startup and production-chain regressions pass for an immediate first sentence, a long pause, a following second sentence, old visible text, an already-active microphone, mode switching, node rebuild, bounded failure, cancellation and late callbacks;
- the empty-surface Windows regression failed before the latest change with `PreSpeechBaselineReadable=False` and `captions-node-unavailable`; after the change the same pre-speech step reports `ConfirmedEmptySurface`, readable and empty, before any generated caption;
- locked restore and the full solution Release build succeeded with **0 errors**; the incremental verification build emitted **152 existing warnings**;
- the full Windows test project passed **250 tests, 0 failed, 0 skipped**, including five surface-readiness classifications;
- the real WPF timeline smoke passed queued-follow cancellation, reading-anchor preservation after height change, bounded draft height, live/final visibility, font/resize handling and return-to-live;
- the Live Captions desktop smoke began with no caption text node, confirmed the empty ready surface, detected fixed non-personal system speech (**0 to 50 caption characters**), changed the microphone **Off → On**, successfully rebound and read the post-enable caption node, restored it to **Off**, and confirmed process cleanup;
- the published executable reached `startup.ui-ready` from the path below, accepted a normal window close, exited with code 0, left no shutdown-recovery marker, and left no app-owned Translate Live or Live Captions process;
- self-contained win-x64 output: **409 files, 155,611,018 bytes** in `artifacts/dev-win-x64`;
- entry executable SHA-256: `d5ebb91eb427cdb12dcb079659e94ab31ff362be1130ff1d5b2f7f1beaf18cfb`.
- application assembly SHA-256: `03b278c7ab8abf1110e3c83d94767c9478e2d87bf4743c7e2095d5759e5222d4`.

The physical spoken-microphone scenario was not executed automatically: the available automation surface cannot provide or verify a real microphone waveform. The Windows smoke proves the OS toggle and rebuilt caption node, while the deterministic production replay proves the post-enable first snapshot, identity, timeline and isolated SQLite behavior. A person must still perform the short spoken check before treating this as a generally available binary release.

### Caption recording preview (after `430df950`)

This patch separates the fixed live-caption surface from complete classroom records. See [source review](SOURCE_REVIEW_2026-09-14.md) for root causes, remaining findings and limitations.

Verified in the current Linux review environment with the pinned .NET SDK 10.0.400:

- locked restore with `EnableWindowsTargeting=true`: successful; no lockfile or package changes;
- full solution Release cross-build: **0 errors, 304 warnings**, matching the warning count of the unmodified baseline cross-build;
- linked production-source portable test harness: **189 passed, 0 failed, 0 skipped**; uses actual logic/view-model/queue/repository classes and isolated SQLite, with a guard asserting no Windows host boundary is called;
- win-x64 self-contained cross-publish: **409 files, 155,556,893 bytes** in `artifacts/caption-recording-win-x64`;
- entry executable SHA-256: `1b4bbacadb4aefab8434494a925a488134b6c034a9006a6de2fc8f9679ad1b79`.
- source and publish scans: no high-confidence key-format matches or runtime settings, credentials, databases, logs, or dumps included; no actual user secrets were read for exact-value matching.

The portable harness is an additional Linux verification method; it does **not** execute the entire Windows-targeted test project. WPF, UI Automation, Live Captions, audio and DPAPI runtime checks cannot execute here. The full Windows test project and smoke executable cross-compile successfully; updated smoke assertions still require an isolated Windows desktop run. The preview is not a signed release or an installer.

Regression coverage includes clipped sentence heads, unchanged-window replay and rotation, long draft growth, punctuation withdrawal, radical tail correction with stable neighbors, genuine repeats with growth/append evidence, provisional translations never entering history, same-request final promotion, original-only saving during a blocked provider, 80 queued finals beyond the matching ledger, old revisions and old classroom epochs, source-only placeholder protection, and explicit unfinished-source flush.

On Windows, use a fresh temporary `LECTURE_COPILOT_DATA_ROOT`, then run `scripts/build.ps1`, `scripts/test.ps1`, and the existing smoke executable before release. Verify actual system audio/microphone, subtitle-window rebuild, slow provider responses, class switching, stopping with unfinished speech, enlarged fonts and history reading anchors. Do not reuse the older Windows success statement below as evidence for this patch.

Remaining limits: source-only ambiguous repetitions are still heuristic; the unfinished badge is not persisted; an earlier unfinished sentence saved only at stop may reload after later complete records because the current schema lacks persisted recognition sequence. Separate credential-binding and SSE completion findings remain open in the source review.

### Previously reported Windows baseline (before this patch)

The repository previously recorded the following local baseline:

- Windows x64;
- .NET SDK 10.0.400;
- locked dependency restoration;
- Release build successful;
- 185 automated tests passing;
- self-contained win-x64 publish successful;
- Windows Live Captions system-audio and microphone smoke checks successful in an isolated data root.
- real WPF timeline smoke checks successful for queued-scroll cancellation, reading-anchor preservation, return-to-live, long-draft height, font enlargement, and window resizing.

Subtitle rollback regression evidence:

- the pre-fix production-segmenter replay `A → B long → B short → old A → old B short` failed its identity assertion and produced five logical identities;
- the repaired replay produces three valid revision events (`A`, `B`, and the shorter revision of `B`) across exactly two identities;
- old `A` and old `B` frames produce no translation submission;
- the isolated SQLite chain contains exactly two rows, and the `B` row is updated in place while retaining its first recognition timestamp.

Growing-final regression evidence:

- a five-frame workspace replay in which one sentence grows while commas and terminal punctuation change failed before the fix with three logical identities;
- the repaired production segmenter emits revisions `0` through `4` under one `SegmentId`;
- the production queue, workspace view model, and isolated SQLite repository finish with one visible row and one database row containing the latest revision and the original capture time;
- a long sentence with a shared opening but different later words remains a distinct sentence.

Final-to-draft continuity regression evidence:

- final → punctuationless growing draft → final retains one `SegmentId`, `Sequence`, and `CapturedAt` while advancing the revision;
- replaying recent intermediate drafts or the same tail with punctuation removed emits no new draft or final event and leaves the latest segment unchanged;
- a textually distinct appended sentence remains a new identity, while an identical or prefix-growing utterance is retained as a genuine repeat only after it establishes a new short-draft trajectory;
- the production segmenter, revision-aware queue, workspace view model, and isolated SQLite repository pass with and without intermediate punctuationless drafts;
- existing classroom rows are not rewritten or deduplicated by this change.

Rapid-continuity regression evidence:

- before the short-tail fix, `And you know? → And you know what → And you know what?` created a second identity, while `Can him remind your voice? → Can him remind your → original final` created a new draft that finalized as a duplicate;
- strict lexical-prefix growth now keeps the retained frontier identity without using elapsed time as evidence of new speech; the four-second threshold remains only for conservative short-tail rollback;
- a rapid 13-frame continuous-speech replay covering provisional punctuation, temporary shortening, restoration, and long growth finishes with one identity, its first capture time, one workspace row, and one isolated SQLite row;
- appended different sentences and repeated speech that begins from a new short draft remain separate identities; delayed strict growth of the retained frontier stays on that frontier.

Whole-window identity regression evidence:

- before this fix, the production replay `[A,B,C] -> [B,C,A]` treated the rotated old `A` as forward speech and finished with four logical identities, four translation requests, four workspace rows, and four isolated SQLite rows;
- the repaired replay first aligns the complete accessibility window and finishes with exactly three identities, three requests, three workspace rows, and three SQLite rows (`4/4/4/4 -> 3/3/3/3`);
- repeated rotation in either direction, head truncation, and historical revisions reuse their existing occurrence identities one-to-one; two genuinely established equal utterances remain two identities when the window later reorders;
- a punctuation-finalized current sentence that reappears without punctuation and keeps growing retains its original `SegmentId`, `Sequence`, and `CapturedAt`, including when older completed rows remain visible;
- an isolated `A -> B -> A` or copied right-edge `A` without a draft trajectory is held as a bounded pending candidate. Waiting or right-edge placement alone never promotes it; strict growth from that pending trajectory can establish a new occurrence with its first observation time, while expired evidence closes without emitting speech;
- the final-admission gate now validates only resolved `SegmentId` and `Revision`. It no longer creates text aliases or uses a three-second timeout, intervening text, or revision number as a second identity classifier;
- Unicode direction and zero-width format marks exposed by UI Automation do not change normalized caption identity text, and no fixed delay is added before ordinary final captions are submitted.

Continuous-window correction regression evidence:

- before the full-position alignment fix, rewriting the fifth row of a ten-sentence Live Captions window stopped matching at that row, retained only five window identities, and assigned the correction a new `SegmentId`;
- the repaired resolver keeps all ten window identities and emits the changed row as revision `1` of its original identity;
- a progressive twelve-sentence replay with 24 complete rewrites of one interior sentence, a full-window rotation, another interior correction, and one genuine append finishes with 13 logical identities for 13 spoken sentences;
- an 80-sentence continuous replay using a ten-row rolling window and periodic rotations produces exactly 80 identities and no replay identities;
- the production queue, workspace view model, and isolated SQLite repository process ten spoken sentences plus 18 accepted revisions as ten workspace rows and ten database rows. Revision events update their established identity instead of adding scrollback rows;
- accessibility duplicate rows that repeat an already aligned occurrence are excluded from position-based rewrite handling, preserving the existing duplicate-burst regressions.

Downstream identity evidence:

- the classroom session identifier is captured when a caption enters the translation queue, so a delayed provider result cannot be written to a classroom selected later;
- stale translation revisions and stale final revisions cannot overwrite a newer revision of the same identity;
- when SQLite history projection interleaves with the corresponding real-time event, the temporary projected UI identity is rebound to the stable live identity instead of creating a second timeline row;
- existing classroom history is not text-deduplicated, rewritten, or deleted by this change.

## Supported release target

The first supported binary target is `win-x64`. The project declares `win-arm64`, but ARM64 must not be advertised as verified until it has an independent build and desktop integration run.

## Required toolchain

- Windows 11
- PowerShell 7 or Windows PowerShell
- .NET SDK 10.0.400
- Windows Live Captions and at least one installed speech-recognition language

The SDK version is fixed by `global.json`. Dependencies are fixed by the root and test-project `packages.lock.json` files.

## Standard verification

Run from the repository root:

~~~powershell
./scripts/build.ps1
./scripts/test.ps1
./scripts/publish-dev.ps1
~~~

Equivalent core commands:

~~~powershell
dotnet restore LiveCaptionsTranslator.sln --locked-mode
dotnet build LiveCaptionsTranslator.sln -c Release --no-restore -m:1 -nodeReuse:false
dotnet test LiveCaptionsTranslator.sln -c Release --no-build --no-restore -m:1 -nodeReuse:false
dotnet publish LiveCaptionsTranslator.csproj -c Release -r win-x64 --self-contained true --no-restore -m:1 -nodeReuse:false -p:PublishSingleFile=false -o artifacts/dev-win-x64
~~~

The current self-contained output is intentionally multi-file. Do not silently switch to single-file publishing without rechecking WPF startup, native SQLite loading, UI Automation, and Windows Live Captions behavior.

## Windows integration checks

Compilation is not sufficient evidence for the capture path. Before a binary release:

1. Set `LECTURE_COPILOT_DATA_ROOT` to a new temporary directory.
2. Start the published executable in a normal interactive Windows desktop session.
3. Verify Windows Live Captions reaches the **working** state.
4. Play a fixed, non-personal audio sentence and confirm a caption node becomes available.
5. Verify microphone mode changes the Live Captions microphone state from Off to On.
6. End the class and confirm one isolated session is saved.
7. Close Translate Live and confirm no app-owned Live Captions process remains.
8. Confirm the production data directory was not modified.

The smoke-test project reports only state, availability, and character counts; it must not print captured content.

Targeted desktop commands after a Release build:

~~~powershell
$env:LECTURE_COPILOT_DATA_ROOT = Join-Path $env:TEMP ("TranslateLive-Smoke-" + [guid]::NewGuid().ToString("N"))
tests/LiveCaptionsTranslator.SmokeTests/bin/Release/net10.0-windows/LiveCaptionsTranslator.SmokeTests.exe --system-audio
tests/LiveCaptionsTranslator.SmokeTests/bin/Release/net10.0-windows/LiveCaptionsTranslator.SmokeTests.exe --multi-voice-audio
tests/LiveCaptionsTranslator.SmokeTests/bin/Release/net10.0-windows/LiveCaptionsTranslator.SmokeTests.exe --timeline-ui
~~~

The timeline smoke uses the production `TranscriptTimeline` control and `TranscriptSessionViewModel`. It is not a substitute for a visible human usability review, but it verifies actual WPF layout and dispatcher behavior rather than only testing a scroll helper.

## Security and privacy gate

Before every public push or release:

- inspect `git status` and the exact staged file list;
- verify that `Data/`, `Logs/`, `Backups/`, `Recovery/`, `.tools/`, build outputs, and databases are ignored;
- scan staged files for provider-key patterns, private keys, tokens, and connection strings;
- confirm no settings, DPAPI credential blobs, SQLite databases, JSONL logs, crash dumps, or exports are staged;
- inspect screenshots for names, course content, account details, and other personal data;
- confirm every bundled image, font, and icon has redistribution permission;
- build and test from a clean clone of the exact commit to be pushed.

Never publish a real API key even if it has already been revoked. Do not use classroom recordings or transcripts as release fixtures.

## Packaging

The developer publish script writes:

`artifacts/dev-win-x64/LectureCopilot.Dev.exe`

The internal filename remains `LectureCopilot.Dev.exe` to preserve compatibility with:

- `%LOCALAPPDATA%\LectureCopilot.Dev`;
- existing DPAPI-protected credentials;
- the single-instance mutex;
- saved settings and classroom history.

The user-facing product name remains **Translate Live**.

## Versioning

Use four-part assembly versions and matching Git tags:

`vMAJOR.MINOR.PATCH.REVISION`

For the initial public preview, use the `0.1.x.x` line. Update both `Properties/AssemblyInfo.cs` and `Properties/AssemblyInfo.tt` in the same change.

## GitHub release checklist

- [ ] Clean GitHub Actions run on the release commit
- [ ] win-x64 clean-clone build and tests
- [ ] Windows Live Captions computer-audio smoke test
- [ ] Windows Live Captions microphone smoke test
- [ ] isolated settings, credential migration, and corrupted-ciphertext tests
- [ ] no sensitive or personal files in the Git tree
- [ ] third-party license and attribution review
- [ ] release notes describing data migrations and known limitations
- [ ] SHA-256 checksum for every binary archive
- [ ] code-signing status stated clearly

## Known blockers for a stable binary release

- no code-signing certificate or installer;
- no independently verified ARM64 integration run;
- Windows Live Captions UI Automation may change across Windows updates;
- executable and data-directory renaming requires a deliberate compatibility migration;
- GitHub release automation should be added only after the packaging names and signing process are settled.

## Upstream attribution

Translate Live is derived from [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator) and retains the Apache License 2.0 license and upstream author attribution. Translate Live modifications and maintenance are credited to QuBe.
