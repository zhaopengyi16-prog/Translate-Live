# Translate Live build and release guide

Last updated: 2026-09-14

## Release status

The repository is ready for public source review. It does not yet claim a signed or generally available binary release.

Current public version: `0.1.0.0`

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
- an explicit appended sentence remains a new identity, and an identical utterance that starts from a new short draft is retained as a genuine repeat;
- the production segmenter, revision-aware queue, workspace view model, and isolated SQLite repository pass with and without intermediate punctuationless drafts;
- existing classroom rows are not rewritten or deduplicated by this change.

Rapid-continuity regression evidence:

- before the short-tail fix, `And you know? → And you know what → And you know what?` created a second identity, while `Can him remind your voice? → Can him remind your → original final` created a new draft that finalized as a duplicate;
- a four-second, position-supported lexical-prefix window now keeps those recognizer revisions on the current identity without adding translation latency;
- a rapid 13-frame continuous-speech replay covering provisional punctuation, temporary shortening, restoration, and long growth finishes with one identity, its first capture time, one workspace row, and one isolated SQLite row;
- appended different sentences, repeated speech that begins from a new short draft, and short-prefix growth outside the evidence window remain separate identities.

Whole-window identity regression evidence:

- before this fix, the production replay `[A,B,C] -> [B,C,A]` treated the rotated old `A` as forward speech and finished with four logical identities, four translation requests, four workspace rows, and four isolated SQLite rows;
- the repaired replay first aligns the complete accessibility window and finishes with exactly three identities, three requests, three workspace rows, and three SQLite rows (`4/4/4/4 -> 3/3/3/3`);
- repeated rotation in either direction, head truncation, and historical revisions reuse their existing occurrence identities one-to-one; two genuinely established equal utterances remain two identities when the window later reorders;
- a punctuation-finalized current sentence that reappears without punctuation and keeps growing retains its original `SegmentId`, `Sequence`, and `CapturedAt`, including when older completed rows remain visible;
- an isolated `A -> B -> A` without a draft trajectory or trusted append position is held as a bounded pending candidate. Waiting alone never promotes it; strict draft growth can establish a new occurrence with the pending candidate's first observation time, while expired evidence closes without emitting speech;
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
